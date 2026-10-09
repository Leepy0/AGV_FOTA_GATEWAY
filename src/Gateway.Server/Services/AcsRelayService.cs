using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Gateway.Core.Agv;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

/// <summary>
/// ACS(교통제어) TCP 소켓 중계 — 뼈대. 기존 ZigBee 게이트웨이가 ACS 와 주고받던 규격을 확인해 맞춘다.
///  현재 가정: 소켓 위에 AGV 프레임(STX…ETX)이 그대로 흐른다.
///   - ACS → 소켓 → 프레임 분리 → AGV ID(헤더 4~5번째 자리) 로 호기 선택 → MAIN_DOWN
///   - 브리지 MAIN_UP 중 E·A 만(화이트리스트, AgvFrame.IsForAcs) → 소켓에 그대로 기록. 새 종류(P 위치 등)는 ACS 에 보내지 않는다
///  Mode=Client 면 이 서버가 ACS 에 접속(재접속 반복), Server 면 ACS 의 접속을 기다린다(한 연결).
/// </summary>
public sealed class AcsRelayService : BackgroundService
{
    readonly AcsOptions _opt;
    readonly BridgeUdpService _bridge;
    readonly ILogger<AcsRelayService> _log;
    // 상향 프레임은 순서대로 한 루프가 보낸다. ACS 가 막히면 오래된 것부터 버림 (무한 적체 방지)
    readonly Channel<byte[]> _tx = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    public long Dropped;
    NetworkStream? _stream;

    public bool Enabled => _opt.Enabled;
    public bool Connected => _stream is not null;
    public long ToAcs, FromAcs;
    public DateTime? ConnectedAt { get; private set; }

    public AcsRelayService(IOptions<GatewayOptions> opt, BridgeUdpService bridge, ILogger<AcsRelayService> log)
    {
        _opt = opt.Value.Acs; _bridge = bridge; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled) { _log.LogInformation("ACS relay disabled"); return; }
        _bridge.MainUpForAcs += OnMainUp;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var client = _opt.Mode.Equals("Server", StringComparison.OrdinalIgnoreCase)
                        ? await AcceptAsync(ct) : await ConnectAsync(ct);
                    await using var ns = client.GetStream();
                    _stream = ns; ConnectedAt = DateTime.UtcNow;
                    _log.LogInformation("ACS connected {Ep}", client.Client.RemoteEndPoint);
                    while (_tx.Reader.TryRead(out _)) { }   // 끊겨 있던 동안 쌓인 옛 프레임은 버림
                    using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var tx = TxLoop(ns, linkCts.Token);
                    try { await RxLoop(ns, ct); }
                    finally { linkCts.Cancel(); try { await tx; } catch { } }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { _log.LogWarning("ACS: {Msg}", e.Message); }
                finally { _stream = null; ConnectedAt = null; }
                await Task.Delay(TimeSpan.FromSeconds(_opt.ReconnectSec), ct);
            }
        }
        finally { _bridge.MainUpForAcs -= OnMainUp; }
    }

    async Task<TcpClient> ConnectAsync(CancellationToken ct)
    {
        var c = new TcpClient { NoDelay = true };
        await c.ConnectAsync(_opt.Host, _opt.Port, ct);
        return c;
    }

    async Task<TcpClient> AcceptAsync(CancellationToken ct)
    {
        var l = new TcpListener(IPAddress.Any, _opt.Port);
        l.Start();
        try { var c = await l.AcceptTcpClientAsync(ct); c.NoDelay = true; return c; }
        finally { l.Stop(); }
    }

    async Task RxLoop(NetworkStream ns, CancellationToken ct)
    {
        var buf = new byte[2048];
        var rest = Array.Empty<byte>();
        while (!ct.IsCancellationRequested)
        {
            int n = await ns.ReadAsync(buf, ct);
            if (n <= 0) throw new IOException("ACS closed");
            foreach (var fr in AgvFrame.Split(buf.AsSpan(0, n), ref rest))
            {
                Interlocked.Increment(ref FromAcs);
                int agv = AgvFrame.PeekAgv(fr);
                if (agv < 0) { _log.LogWarning("ACS frame without AGV id: {F}", AgvFrame.Fmt(fr)); continue; }
                if (!_bridge.SendMainDown(agv, fr)) _log.LogWarning("ACS → AGV {Agv}: 브리지 미접속", agv);
            }
        }
    }

    void OnMainUp(int agv, byte[] frame)
    {
        if (_stream is null) return;
        if (_tx.Reader.Count >= 512) Interlocked.Increment(ref Dropped);
        _tx.Writer.TryWrite(frame);
    }

    async Task TxLoop(NetworkStream ns, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _tx.Reader.ReadAllAsync(ct))
            {
                await ns.WriteAsync(frame, ct);
                Interlocked.Increment(ref ToAcs);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _log.LogWarning("ACS tx: {Msg}", e.Message); }
    }
}
