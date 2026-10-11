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
///  - MAIN_UP: 모두 구독자(OTA 클라이언트 등)에게, ACS 중계는 완전한 E·A 프레임만 (AgvFrame.IsForAcs)
///  - STATUS/INFO: 레지스트리 갱신(서로 없는 필드는 유지), cfg=trial·ota=pending 이면 자동 PING
///  - OTA_RESULT/DSP_OTA_RESULT: 구독자   - CFG_RESULT/PONG: 로그 (요청 seq 반송이라 seq 검사 제외)
/// 호기 번호 충돌: 살아 있는 endpoint 가 있는데 다른 주소에서 같은 번호가 오면 endpoint 를 바꾸지 않고 그 패킷을 버린다(BridgeOrigin).
/// 충돌 중에는 OTA·DSP·CFG 송신을 거부한다. MAIN_DOWN(ACS 중계)은 기존 endpoint 로 계속.
/// 마지막 접속 DB 기록은 별도 타이머(PersistLoop)가 모아서 한다 — 수신 루프에서 DB I/O 없음.
/// </summary>
public sealed class BridgeUdpService : BackgroundService, IAgvLink
{
    readonly GatewayOptions _opt;
    readonly AgvRegistry _reg;
    readonly ILogger<BridgeUdpService> _log;
    readonly ConcurrentDictionary<int, List<AgvSubscription>> _subs = new();
    readonly ConcurrentDictionary<int, ushort> _downSeq = new();
    UdpClient? _udp;
    long _acsDropped;
    int _dbFails;
    DateTime _dbWarnedAt, _badAgvWarnedAt;
    /// <summary>UDP 포트를 못 열었을 때의 사유 (화면 표시). null = 정상</summary>
    public string? BindError { get; private set; }

    /// <summary>ACS 로 넘길 상향 프레임 (OTA 응답 제외)</summary>
    public event Action<int, byte[]>? MainUpForAcs;
    /// <summary>화면용 최근 프레임 로그</summary>
    public event Action<string>? Trace;
    /// <summary>ETX·BCC 가 깨져 ACS 로 넘기지 않은 E·A 프레임 수</summary>
    public long AcsDroppedFrames => Interlocked.Read(ref _acsDropped);

    /// <summary>자동 확정 PING 최소 간격 (호기당)</summary>
    const int AutoPingIntervalSec = 5;
    /// <summary>충돌·범위 밖 번호 경고 최소 간격</summary>
    const int WarnIntervalSec = 10;

    public BridgeUdpService(IOptions<GatewayOptions> opt, AgvRegistry reg, ILogger<BridgeUdpService> log)
    {
        _opt = opt.Value; _reg = reg; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var persist = Task.Run(() => PersistLoop(ct), ct);
        try { await ReceiveAsync(ct); }
        finally { try { await persist; } catch (OperationCanceledException) { } }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        _reg.MarkAllSeen();
        FlushRegistry();   // 종료 직전 마지막 기록
    }

    /// <summary>마지막 접속 DB 기록 — 2초마다 모아서. DB 가 잠겨 있어도 수신 루프는 멈추지 않는다</summary>
    async Task PersistLoop(CancellationToken ct)
    {
        using var t = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try { while (await t.WaitForNextTickAsync(ct)) FlushRegistry(); }
        catch (OperationCanceledException) { }
    }

    void FlushRegistry()
    {
        try
        {
            _reg.Flush();
            if (_dbFails > 0) { _log.LogInformation("마지막 접속 DB 기록 재개 (실패 {N}회 뒤)", _dbFails); _dbFails = 0; }
        }
        catch (Exception e)
        {
            _dbFails++;
            if ((DateTime.UtcNow - _dbWarnedAt).TotalSeconds >= 60)
            {
                _dbWarnedAt = DateTime.UtcNow;
                _log.LogWarning("마지막 접속 DB 기록 실패 (연속 {N}회, 미기록 {P}대): {Msg} — 다른 프로그램이 gateway.db 를 잠갔는지 확인 (DB Browser 는 읽기 전용으로 열 것)",
                    _dbFails, _reg.PendingWrites, e.Message);
            }
        }
    }

    async Task ReceiveAsync(CancellationToken ct)
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
        if (!BridgeOrigin.IsValidAgv(h.Agv))
        {
            if ((DateTime.UtcNow - _badAgvWarnedAt).TotalSeconds >= WarnIntervalSec)
            {
                _badAgvWarnedAt = DateTime.UtcNow;
                Trace?.Invoke($"호기 번호 {h.Agv} 은 범위(1~99) 밖 — {ep} 의 {BridgePacket.TypeName(h.Type)} 버림 (브리지 AGV ID 설정 확인)");
            }
            return;
        }
        var pl = plSpan.ToArray();
        var a = _reg.GetOrAdd(h.Agv);
        var now = DateTime.UtcNow;
        var isStatus = h.Type is BridgePacket.Status or BridgePacket.Info;
        var fresh = isStatus ? BridgeStatus.Parse(pl) : null;

        var knownMac = a.Status?.Mac ?? "";
        var origin = BridgeOrigin.Decide(a.EndPoint, a.Online(_opt.OfflineAfterSec), knownMac, ep, fresh?.Mac ?? "");
        if (origin == OriginDecision.Conflict)
        {
            OnConflict(a, ep, h, BridgeOrigin.MacDiffers(knownMac, fresh?.Mac ?? ""));
            return;
        }
        if (origin == OriginDecision.Move)
        {
            if (a.EndPoint is not null) Trace?.Invoke($"[{h.Agv:D2}] 주소 변경 {a.EndPoint}→{ep}");
            AgvRegistry.ResetSeq(a);   // 다른 장치일 수 있다 — seq 기준 새로
        }
        else if (fresh is not null && a.Status is { } prev && fresh.UpSeconds < prev.UpSeconds)
            AgvRegistry.ResetSeq(a);   // 가동 시간이 줄었다 = 브리지 재부팅, seq 가 0 부터 다시 시작
        a.EndPoint = ep;
        a.LastSeen = now;
        int loss = BridgeSeq.Tracked(h.Type) ? AgvRegistry.CheckSeq(a, h.Type, h.Seq) : 0;
        if (loss > 0) Interlocked.Add(ref a.SeqLoss, loss);

        switch (h.Type)
        {
            case BridgePacket.Status:
            case BridgePacket.Info:
                if (fresh is null) a.StatusRaw = Encoding.UTF8.GetString(pl);   // JSON 이 아니면 원문만 보관
                else
                {
                    // INFO 와 heartbeat 가 서로의 필드(IP·MAC ↔ RSSI·카운터)를 지우지 않게 합친다. 주소가 바뀌었으면 이전 값은 버린다
                    a.Status = BridgeStatus.Merge(fresh, origin == OriginDecision.Move ? null : a.Status, h.Type == BridgePacket.Info);
                    a.StatusRaw = a.Status.Raw;
                }
                if (h.Type == BridgePacket.Info) Trace?.Invoke($"[{h.Agv:D2}] INFO {Encoding.UTF8.GetString(pl)}");
                _reg.Touch(a);
                AutoConfirmPing(a, now);
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
                if (AgvFrame.IsForAcs(pl)) MainUpForAcs?.Invoke(h.Agv, pl);   // 완전한 E·A 만 ACS 로 (화이트리스트)
                else if (AgvFrame.IsAcsKind(pl))
                {
                    var n = Interlocked.Increment(ref _acsDropped);
                    Trace?.Invoke($"[{h.Agv:D2}] ACS 중계 제외 — ETX/BCC 가 맞지 않는 조각 (누적 {n})");
                }
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

    /// <summary>같은 호기 번호를 다른 장치가 쓰고 있음 — 기존 endpoint·상태는 그대로 두고 기록·경고만 (빈도 제한)</summary>
    void OnConflict(AgvState a, IPEndPoint ep, BridgeHeader h, bool macDiffers)
    {
        a.ConflictAt = DateTime.UtcNow;
        a.ConflictEndPoint = ep;
        var n = Interlocked.Increment(ref a.ConflictCount);
        if ((a.ConflictAt - a.ConflictWarnedAt).TotalSeconds >= WarnIntervalSec)
        {
            a.ConflictWarnedAt = a.ConflictAt;
            var msg = $"[{h.Agv:D2}] 주소 충돌 {a.EndPoint}→{ep}{(macDiffers ? " (MAC 다름)" : "")} — 같은 호기 번호를 쓰는 장치가 둘. 기존 주소로 계속 중계, 새 주소 패킷({BridgePacket.TypeName(h.Type)} 등)은 버림 (누적 {n})";
            Trace?.Invoke(msg);
            _log.LogWarning("{Msg}", msg);
        }
        _reg.NotifyChanged();
    }

    /// <summary>원격 설정 시험 적용(cfg=trial)·브리지 OTA 미확정(ota=pending)은 서버 하향 패킷을 받아야 확정된다 — 호기당 5초에 1회 PING</summary>
    void AutoConfirmPing(AgvState a, DateTime now)
    {
        if (a.Status is not { NeedsConfirmPing: true } st || a.InConflict(_opt.OfflineAfterSec)) return;
        if ((now - a.LastAutoPing).TotalSeconds < AutoPingIntervalSec) return;
        a.LastAutoPing = now;
        if (SendPing(a.Id)) Trace?.Invoke($"[{a.Id:D2}] 자동 PING (cfg={st.Cfg}, ota={st.Ota}) — 확정용");
    }

    AgvSubscription[] Subscribers(int agv)
    {
        if (!_subs.TryGetValue(agv, out var list)) return [];
        lock (list) return list.ToArray();
    }

    bool Send(int agv, byte type, ReadOnlySpan<byte> payload)
    {
        var a = _reg.Get(agv);
        var ep = a?.EndPoint;
        if (a is null || ep is null || _udp is null) return false;
        if (BlockedInConflict(type) && a.InConflict(_opt.OfflineAfterSec))
        {
            Trace?.Invoke($"[{agv:D2}] 호기 번호 충돌 중 ({a.EndPoint} / {a.ConflictEndPoint}) — {BridgePacket.TypeName(type)} 송신 거부");
            return false;
        }
        var seq = _downSeq.AddOrUpdate(agv, 1, (_, s) => (ushort)(s + 1));
        var pkt = BridgePacket.Build(type, agv, seq, payload);
        try { _udp.Send(pkt, pkt.Length, ep); } catch (SocketException e) { _log.LogWarning("udp tx {Agv}: {Msg}", agv, e.Message); return false; }
        return true;
    }

    /// <summary>충돌 중 거부하는 하향: 엉뚱한 장치에 가면 위험한 OTA·메인보드 업데이트·설정 변경. MAIN_DOWN·PING·INFO_REQ 는 보낸다</summary>
    static bool BlockedInConflict(byte type) =>
        type is BridgePacket.OtaStart or BridgePacket.DspOtaStart or BridgePacket.DspOtaApply or BridgePacket.CfgCmd;

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
