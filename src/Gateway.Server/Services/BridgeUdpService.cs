using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Gateway.Core.Agv;
using Gateway.Core.Bridge;
using Gateway.Core.Fota;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

/// <summary>
/// 브리지 UDP 서버 (AGV_Wifi_Bridge 규격). 호기별 endpoint 를 기억했다가 그곳으로 하향 송신한다.
///  - MAIN_UP: 모두 구독자(OTA 클라이언트 등)에게, ACS 중계는 E·A(OTA 응답 제외)만 (AgvFrame.IsForAcs)
///  - STATUS/INFO: 레지스트리 갱신   - OTA_RESULT/DSP_OTA_RESULT: 구독자   - CFG_RESULT/PONG: 로그
/// </summary>
public sealed class BridgeUdpService : BackgroundService, IAgvLink
{
    readonly GatewayOptions _opt;
    readonly AgvRegistry _reg;
    readonly ILogger<BridgeUdpService> _log;
    readonly ConcurrentDictionary<int, List<AgvSubscription>> _subs = new();
    readonly ConcurrentDictionary<int, ushort> _downSeq = new();
    UdpClient? _udp;
    /// <summary>UDP 포트를 못 열었을 때의 사유 (화면 표시). null = 정상</summary>
    public string? BindError { get; private set; }

    /// <summary>ACS 로 넘길 상향 프레임 (OTA 응답 제외)</summary>
    public event Action<int, byte[]>? MainUpForAcs;
    /// <summary>화면용 최근 프레임 로그</summary>
    public event Action<string>? Trace;

    public BridgeUdpService(IOptions<GatewayOptions> opt, AgvRegistry reg, ILogger<BridgeUdpService> log)
    {
        _opt = opt.Value; _reg = reg; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 포트가 이미 쓰이고 있으면(벤치 도구 등) 서비스를 죽이지 말고 10초마다 다시 시도 — 화면에 사유를 보여준다
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _opt.UdpPort));
                BindError = null;
                break;
            }
            catch (SocketException e)
            {
                BindError = $"UDP {_opt.UdpPort} 포트를 열 수 없음: {e.Message} (다른 프로그램이 사용 중?)";
                _log.LogError("{Err}", BindError);
                try { await Task.Delay(10000, ct); } catch (OperationCanceledException) { return; }
            }
        }
        if (_udp is null) return;
        if (OperatingSystem.IsWindows()) _udp.Client.IOControl(-1744830452, [0], null);   // ICMP unreachable 예외 방지
        _log.LogInformation("bridge udp :{Port}", _opt.UdpPort);
        var rfid = _opt.RfidPort > 0 ? Task.Run(() => RfidLoop(ct), ct) : Task.CompletedTask;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException e) { _log.LogDebug("udp rx: {Msg}", e.Message); continue; }
            try { Handle(r.Buffer, r.RemoteEndPoint); }
            catch (Exception e) { _log.LogWarning(e, "packet from {Ep}", r.RemoteEndPoint); }
        }
        await rfid;
    }

    async Task RfidLoop(CancellationToken ct)
    {
        UdpClient u;
        try { u = new UdpClient(new IPEndPoint(IPAddress.Any, _opt.RfidPort)); }
        catch (SocketException e) { _log.LogWarning("RFID 포트 {Port} 를 열 수 없음: {Msg} — RFID 로그 없이 계속", _opt.RfidPort, e.Message); return; }
        using var _ = u;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await u.ReceiveAsync(ct); } catch (OperationCanceledException) { break; } catch (SocketException) { continue; }
            if (BridgePacket.TryParse(r.Buffer, out var h, out var pl) && h.Type == BridgePacket.Rfid)
                Trace?.Invoke($"[{h.Agv:D2}] RFID> {AgvFrame.Fmt(pl)}");
        }
    }

    void Handle(byte[] b, IPEndPoint ep)
    {
        if (!BridgePacket.TryParse(b, out var h, out var plSpan))
        {
            Trace?.Invoke($"BAD packet from {ep} ({b.Length}B)");
            return;
        }
        var pl = plSpan.ToArray();
        var a = _reg.GetOrAdd(h.Agv);
        a.EndPoint = ep;
        a.LastSeen = DateTime.UtcNow;
        int loss = AgvRegistry.CheckSeq(a, h.Type, h.Seq);
        if (loss > 0) Interlocked.Add(ref a.SeqLoss, loss);

        switch (h.Type)
        {
            case BridgePacket.Status:
            case BridgePacket.Info:
                a.StatusRaw = Encoding.UTF8.GetString(pl);
                a.Status = BridgeStatus.Parse(pl) ?? a.Status;
                if (h.Type == BridgePacket.Info) Trace?.Invoke($"[{h.Agv:D2}] INFO {a.StatusRaw}");
                _reg.NotifyChanged();
                break;
            case BridgePacket.DspOtaResult:
                var dj = Encoding.UTF8.GetString(pl);
                Trace?.Invoke($"[{h.Agv:D2}] DSP  {dj}");
                foreach (var s in Subscribers(h.Agv)) s.DspOtaResults.Writer.TryWrite(dj);
                break;
            case BridgePacket.MainUp:
                Interlocked.Increment(ref a.MainUp);
                a.LastFrame = AgvFrame.Fmt(pl);
                Trace?.Invoke($"[{h.Agv:D2}] MAIN> {a.LastFrame}{(loss > 0 ? $"  [LOSS {loss}]" : "")}");
                foreach (var s in Subscribers(h.Agv)) s.Frames.Writer.TryWrite(pl);
                if (AgvFrame.IsForAcs(pl)) MainUpForAcs?.Invoke(h.Agv, pl);   // E·A 만 ACS 로 (화이트리스트)
                _reg.NotifyChanged();
                break;
            case BridgePacket.OtaResult:
                var js = Encoding.UTF8.GetString(pl);
                Trace?.Invoke($"[{h.Agv:D2}] OTA  {js}");
                foreach (var s in Subscribers(h.Agv)) s.OtaResults.Writer.TryWrite(js);
                break;
            case BridgePacket.CfgResult:
                Trace?.Invoke($"[{h.Agv:D2}] CFG> {Encoding.UTF8.GetString(pl).TrimEnd('\n')}");
                break;
            case BridgePacket.Pong:
                break;
            default:
                Trace?.Invoke($"[{h.Agv:D2}] {BridgePacket.TypeName(h.Type)} {AgvFrame.Fmt(pl)}");
                break;
        }
    }

    AgvSubscription[] Subscribers(int agv)
    {
        if (!_subs.TryGetValue(agv, out var list)) return [];
        lock (list) return list.ToArray();
    }

    bool Send(int agv, byte type, ReadOnlySpan<byte> payload)
    {
        var ep = _reg.Get(agv)?.EndPoint;
        if (ep is null || _udp is null) return false;
        var seq = _downSeq.AddOrUpdate(agv, 1, (_, s) => (ushort)(s + 1));
        var pkt = BridgePacket.Build(type, agv, seq, payload);
        try { _udp.Send(pkt, pkt.Length, ep); } catch (SocketException e) { _log.LogWarning("udp tx {Agv}: {Msg}", agv, e.Message); return false; }
        return true;
    }

    // ───── IAgvLink ─────
    public bool SendMainDown(int agv, ReadOnlySpan<byte> frame)
    {
        if (frame.Length > AgvFrame.BridgeFrameMax) return false;
        var a = _reg.Get(agv);
        if (a is not null) Interlocked.Increment(ref a.MainDown);
        Trace?.Invoke($"[{agv:D2}] MAIN< {AgvFrame.Fmt(frame)}");
        return Send(agv, BridgePacket.MainDown, frame);
    }

    public bool SendOtaStart(int agv, string url) => Send(agv, BridgePacket.OtaStart, Encoding.ASCII.GetBytes(url));
    public bool SendDspOtaStart(int agv, string json) { Trace?.Invoke($"[{agv:D2}] DSP< START {json}"); return Send(agv, BridgePacket.DspOtaStart, Encoding.UTF8.GetBytes(json)); }
    public bool SendDspOtaApply(int agv, string json) { Trace?.Invoke($"[{agv:D2}] DSP< APPLY {json}"); return Send(agv, BridgePacket.DspOtaApply, Encoding.UTF8.GetBytes(json)); }
    public bool SendInfoRequest(int agv) => Send(agv, BridgePacket.InfoReq, ReadOnlySpan<byte>.Empty);
    public bool SendPing(int agv) => Send(agv, BridgePacket.Ping, BitConverter.GetBytes(DateTime.UtcNow.Ticks));
    public bool SendCfg(int agv, string cmd) => Send(agv, BridgePacket.CfgCmd, Encoding.UTF8.GetBytes(cmd));

    public AgvSubscription Subscribe(int agv)
    {
        var list = _subs.GetOrAdd(agv, _ => new List<AgvSubscription>());
        AgvSubscription? sub = null;
        sub = new AgvSubscription(() => { lock (list) list.Remove(sub!); });
        lock (list) list.Add(sub);
        return sub;
    }

    public string? LocalAddressToward(int agv)
    {
        var ep = _reg.Get(agv)?.EndPoint;
        if (ep is null) return null;
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(ep);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch (SocketException) { return null; }
    }
}
