using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Gateway.Core.Acs;
using Gateway.Core.Agv;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

/// <summary>
/// ACS(교통제어) TCP 소켓 중계 — 뼈대. 기존 ZigBee 게이트웨이가 ACS 와 주고받던 규격을 확인해 맞춘다.
///  현재 가정: 소켓 위에 AGV 프레임(STX…ETX)이 그대로 흐른다.
///   - ACS → 소켓 → 프레임 분리 → AGV ID(헤더 4~5번째 자리) 로 호기 선택 → MAIN_DOWN.
///     단 F(업데이트 진입)·R(브리지용 응답)은 ACS 가 보낼 종류가 아니므로 버린다(AcsRelayPolicy.IsBlockedDown)
///   - 브리지 MAIN_UP 중 E·A 만(화이트리스트, AgvFrame.IsForAcs) → 소켓에 그대로 기록. 새 종류(P 위치 등)는 ACS 에 보내지 않는다
///  Mode=Client 면 이 서버가 ACS 에 접속(재접속 반복), Server 면 리스너를 계속 열어 두고 ACS 의 접속을 받는다
///  (한 번에 한 연결 — 새 접속이 오면 기존 연결을 닫고 교체. AllowedRemotes 가 있으면 그 IP 만).
///  연결 소켓에는 TCP keepalive 를 걸어, 상대가 FIN/RST 없이 사라져도(정전 등) 약 20초 안에 끊김을 알아챈다.
/// </summary>
public sealed class AcsRelayService : BackgroundService
{
    const int KeepAliveTimeSec = 10, KeepAliveIntervalSec = 3, KeepAliveRetry = 3;
    static readonly TimeSpan WarnEvery = TimeSpan.FromSeconds(10);

    readonly AcsOptions _opt;
    readonly BridgeUdpService _bridge;
    readonly ILogger<AcsRelayService> _log;
    // 상향 프레임은 순서대로 한 루프가 보낸다. ACS 가 막히면 오래된 것부터 버림 (무한 적체 방지)
    readonly Channel<byte[]> _tx = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    public long Dropped;
    volatile NetworkStream? _stream;
    DateTime _lastBlockedWarn, _lastRejectWarn;

    public bool Enabled => _opt.Enabled;
    public bool Connected => _stream is not null;
    public long ToAcs, FromAcs;
    /// <summary>버린 하향 F·R 프레임 수</summary>
    public long BlockedDown;
    /// <summary>Server 모드에서 허용 목록에 없어 닫은 접속 수</summary>
    public long RejectedRemotes;
    public DateTime? ConnectedAt { get; private set; }
    public string? RemoteEndPoint { get; private set; }

    public AcsRelayService(IOptions<GatewayOptions> opt, BridgeUdpService bridge, ILogger<AcsRelayService> log)
    {
        _opt = opt.Value.Acs; _bridge = bridge; _log = log;
    }

    bool IsServer => _opt.Mode.Equals("Server", StringComparison.OrdinalIgnoreCase);
    string[] Allowed => _opt.AllowedRemotes ?? [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled) { _log.LogInformation("ACS relay disabled"); return; }
        _bridge.MainUpForAcs += OnMainUp;
        try
        {
            if (IsServer) await ServerLoop(ct);
            else await ClientLoop(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _bridge.MainUpForAcs -= OnMainUp; }
    }

    async Task ClientLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var c = new TcpClient { NoDelay = true };
                try { await c.ConnectAsync(_opt.Host, _opt.Port, ct); }
                catch { c.Dispose(); throw; }
                await RunSession(c, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { _log.LogWarning("ACS: {Msg}", e.Message); }
            await Task.Delay(TimeSpan.FromSeconds(_opt.ReconnectSec), ct);
        }
    }

    // 리스너는 서비스 수명 동안 열어 둔다. ACS 가 재접속하면(옛 연결이 반쯤 끊겨 남아 있어도) 새 연결로 교체
    async Task ServerLoop(CancellationToken ct)
    {
        foreach (var bad in AcsRelayPolicy.InvalidEntries(Allowed))
            _log.LogWarning("Acs.AllowedRemotes: IP 로 해석되지 않는 항목 '{Entry}' 무시", bad);
        while (!ct.IsCancellationRequested)
        {
            var l = new TcpListener(IPAddress.Any, _opt.Port);
            Task? session = null;
            CancellationTokenSource? sessCts = null;
            try
            {
                l.Start();
                _log.LogInformation("ACS 접속 대기 :{Port}{Allow}", _opt.Port,
                    Allowed.Any(s => !string.IsNullOrWhiteSpace(s)) ? $" (허용 {string.Join(", ", Allowed)})" : "");
                while (true)
                {
                    var c = await l.AcceptTcpClientAsync(ct);
                    var ep = c.Client.RemoteEndPoint as IPEndPoint;
                    if (!AcsRelayPolicy.IsAllowedRemote(ep?.Address, Allowed))
                    {
                        Interlocked.Increment(ref RejectedRemotes);
                        if (Due(ref _lastRejectWarn)) _log.LogWarning("ACS 접속 거부: {Ep} 는 Acs.AllowedRemotes 에 없음 (누적 {N}건)", ep, Interlocked.Read(ref RejectedRemotes));
                        c.Dispose();
                        continue;
                    }
                    if (session is not null)
                    {
                        if (!session.IsCompleted) _log.LogInformation("ACS 새 접속 {Ep} — 기존 연결 {Old} 을 닫고 교체", ep, RemoteEndPoint);
                        await StopSession(session, sessCts!);
                    }
                    sessCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    session = RunSessionLogged(c, sessCts.Token);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { _log.LogWarning("ACS listen :{Port}: {Msg}", _opt.Port, e.Message); }
            finally
            {
                l.Stop();
                if (session is not null) await StopSession(session, sessCts!);
            }
            await Task.Delay(TimeSpan.FromSeconds(_opt.ReconnectSec), ct);
        }
    }

    static async Task StopSession(Task session, CancellationTokenSource cts)
    {
        cts.Cancel();
        try { await session; } catch { }
        cts.Dispose();
    }

    async Task RunSessionLogged(TcpClient c, CancellationToken ct)
    {
        try { await RunSession(c, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { _log.LogWarning("ACS: {Msg}", e.Message); }
    }

    /// <summary>한 연결의 수명. 끊기거나 ct 가 취소되면 끝나고 client 를 닫는다</summary>
    async Task RunSession(TcpClient client, CancellationToken ct)
    {
        using var owned = client;
        client.NoDelay = true;
        SetKeepAlive(client.Client);
        await using var ns = client.GetStream();
        while (_tx.Reader.TryRead(out _)) { }   // 끊겨 있던 동안 쌓인 옛 프레임은 버림
        RemoteEndPoint = client.Client.RemoteEndPoint?.ToString();
        _stream = ns; ConnectedAt = DateTime.UtcNow;
        _log.LogInformation("ACS connected {Ep}", RemoteEndPoint);
        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var tx = TxLoop(ns, linkCts);
            try { await RxLoop(ns, linkCts.Token); }
            finally { linkCts.Cancel(); try { await tx; } catch { } }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("ACS 송신 실패로 연결 종료");   // TxLoop 가 끊은 경우
        }
        finally { _stream = null; ConnectedAt = null; RemoteEndPoint = null; }
    }

    void SetKeepAlive(Socket s)
    {
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveTimeSec);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveIntervalSec);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveRetry);
        }
        catch (Exception e) { _log.LogDebug("ACS keepalive 설정 실패: {Msg}", e.Message); }
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
                if (AcsRelayPolicy.IsBlockedDown(fr))
                {
                    Interlocked.Increment(ref BlockedDown);
                    if (Due(ref _lastBlockedWarn)) _log.LogWarning("ACS 하향 F/R 프레임 버림 (메인보드로 넘기지 않음, 누적 {N}건): {F}", Interlocked.Read(ref BlockedDown), AgvFrame.Fmt(fr));
                    continue;
                }
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

    // 쓰기가 실패하면 연결을 끝낸다 (RxLoop 도 함께 멈추고 재접속·재대기로)
    async Task TxLoop(NetworkStream ns, CancellationTokenSource link)
    {
        try
        {
            await foreach (var frame in _tx.Reader.ReadAllAsync(link.Token))
            {
                await ns.WriteAsync(frame, link.Token);
                Interlocked.Increment(ref ToAcs);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _log.LogWarning("ACS tx: {Msg}", e.Message); try { link.Cancel(); } catch (ObjectDisposedException) { } }
    }

    // 경고 로그 빈도 제한 (10초에 한 번)
    static bool Due(ref DateTime last)
    {
        var now = DateTime.UtcNow;
        if (now - last < WarnEvery) return false;
        last = now;
        return true;
    }
}
