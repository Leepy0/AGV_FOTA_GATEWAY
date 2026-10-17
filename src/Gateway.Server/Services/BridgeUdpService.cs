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
public sealed class BridgeUdpService : BackgroundService, IAgvLink, ICfgChannel
{
    readonly GatewayOptions _opt;
    readonly AgvRegistry _reg;
    readonly ILogger<BridgeUdpService> _log;
    readonly ConcurrentDictionary<int, List<AgvSubscription>> _subs = new();
    readonly ConcurrentDictionary<int, ushort> _downSeq = new();
    /// <summary>응답을 기다리는 설정 명령 — (호기, 요청 seq). CFG_RESULT 가 seq 를 그대로 반송한다</summary>
    readonly ConcurrentDictionary<(int Agv, ushort Seq), TaskCompletionSource<string>> _cfgWait = new();
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
    /// <summary>heartbeat·INFO 로 호기 상태가 갱신된 뒤 (호기, 직전 상태, 새 상태). 수신 루프에서 불리므로 가볍게 끝내야 한다 (DB·네트워크 금지)</summary>
    public event Action<AgvState, BridgeStatus?, BridgeStatus>? StatusUpdated;
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
        // 수신 루프가 예상치 못하게 끝나도 기록 루프를 멈추고 마지막 기록까지 기다린다 (서비스가 조용히 멈춘 채 남지 않게)
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var persist = Task.Run(() => PersistLoop(stop.Token), CancellationToken.None);
        try { await ReceiveAsync(ct); }
        finally { stop.Cancel(); try { await persist; } catch (OperationCanceledException) { } }
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
            {
                var text = AgvFrame.Fmt(pl);
                Trace?.Invoke($"[{h.Agv:D2}] RFID> {text}");
                // Wi-Fi 분석의 위치 대용: 마지막으로 읽은 태그 (아는 호기만 — 엉뚱한 번호로 호기를 만들지 않는다)
                if (_reg.Get(h.Agv) is { } ag && text.Length > 0)
                {
                    ag.LastTag = text.Length > 40 ? text[..40] : text;
                    ag.LastTagAt = DateTime.UtcNow;
                }
            }
        }
    }

    void Handle(byte[] b, IPEndPoint ep)
    {
        var recvMs = BridgeClock.NowUnixMs();   // 시각 동기의 t1·편차 계산용 — 패킷을 받은 바로 그 시각
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
        if (ep.Equals(a.ConflictEndPoint)) a.ConflictEndPoint = null;   // 충돌로 보던 주소로 정상 이전 — 충돌 표시를 남기지 않음
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
                    var prev = origin == OriginDecision.Move ? null : a.Status;
                    a.Status = BridgeStatus.Merge(fresh, prev, h.Type == BridgePacket.Info);
                    a.StatusRaw = a.Status.Raw;
                    if (h.Type == BridgePacket.Status)
                    {
                        UpdateClock(a, fresh, recvMs, now);   // ts 는 heartbeat 에만 있다 (합친 상태의 ts 는 옛 값일 수 있음)
                        try { StatusUpdated?.Invoke(a, prev, a.Status); } catch (Exception e) { _log.LogWarning(e, "StatusUpdated 처리기 오류"); }
                    }
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
                var cfgText = BridgePacket.CfgResultText(pl);
                // 응답을 기다리는 요청이면 깨운다 (진단·설정 수집은 길이가 큰 JSON 이라 로그에는 앞부분만)
                if (_cfgWait.TryRemove((h.Agv, h.Seq), out var waiter)) waiter.TrySetResult(cfgText);
                Trace?.Invoke($"[{h.Agv:D2}] CFG> {(cfgText.Length > 300 ? cfgText[..300] + $"… ({cfgText.Length}자)" : cfgText)}");
                break;
            case BridgePacket.TimeReq:
                OnTimeRequest(a, h, pl, recvMs);
                break;
            case BridgePacket.Pong:
                break;
            default:
                Trace?.Invoke($"[{h.Agv:D2}] {BridgePacket.TypeName(h.Type)} {AgvFrame.Fmt(pl)}");
                break;
        }
    }

    /// <summary>브리지의 시각 요청에 즉시 답한다 — t1(받은 시각)과 t2(보내는 시각)를 함께 줘서 브리지가 왕복 지연을 뺀다</summary>
    void OnTimeRequest(AgvState a, BridgeHeader h, byte[] pl, long recvMs)
    {
        if (BridgeClock.ParseRequest(pl) is not { } t0)
        {
            Trace?.Invoke($"[{h.Agv:D2}] TIME_REQ 형식 오류 — 무시 ({AgvFrame.Fmt(pl)})");
            return;
        }
        if (!Send(h.Agv, BridgePacket.Time, BridgeClock.BuildReply(t0, recvMs, BridgeClock.NowUnixMs()))) return;
        a.LastTimeReply = DateTime.UtcNow;
        var n = Interlocked.Increment(ref a.TimeReplies);
        Trace?.Invoke($"[{h.Agv:D2}] TIME 동기 응답 (누적 {n})");
    }

    /// <summary>
    /// heartbeat 의 ts 로 시각 편차를 기록한다. ClockAutoSync 이면 편차가 임계를 넘거나(시각 동기를 아는 브리지가 아직 미동기이면 부팅 30초 뒤부터)
    /// 서버가 먼저 시각을 보낸다 — 호기당 1분에 1회까지.
    /// </summary>
    void UpdateClock(AgvState a, BridgeStatus st, long recvMs, DateTime now)
    {
        if (!st.HasClock) { a.ClockSkewMs = null; return; }   // 시각 동기를 모르는 구 펌웨어 — 보내도 소용없다
        if (st.ClockSynced)
        {
            a.ClockSkewMs = BridgeClock.SkewMs(st.TsMs, recvMs);
            a.ClockSkewAt = now;
            if (_opt.ClockAutoSync && BridgeClock.OverThreshold(a.ClockSkewMs.Value, _opt.ClockSkewWarnMs))
                PushTime(a, now, $"편차 {BridgeClock.FormatSkew(a.ClockSkewMs.Value)} > {_opt.ClockSkewWarnMs} ms");
        }
        else
        {
            a.ClockSkewMs = null;
            // 브리지는 부팅하면 스스로 요청한다. 한동안 답이 없으면(요청 유실·서버 재시작) 서버가 보낸다
            if (_opt.ClockAutoSync && st.UpSeconds >= 30) PushTime(a, now, "브리지 시각 미동기");
        }
    }

    void PushTime(AgvState a, DateTime now, string why)
    {
        if ((now - a.LastTimePush).TotalSeconds < 60) return;
        a.LastTimePush = now;
        if (SendTime(a.Id)) Trace?.Invoke($"[{a.Id:D2}] 시각 보정 전송 ({why})");
    }

    /// <summary>서버 시각을 브리지에 보낸다 (화면의 "시각 동기"·편차 자동 보정). 시각 동기를 모르는 구 펌웨어에는 보내지 않는다</summary>
    public bool SendTime(int agv)
    {
        if (_reg.Get(agv)?.Status is not { HasClock: true }) return false;
        return Send(agv, BridgePacket.Time, BridgeClock.BuildReply(null, 0, BridgeClock.NowUnixMs()));
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

    bool Send(int agv, byte type, ReadOnlySpan<byte> payload) => SendCore(agv, type, payload, null);

    /// <summary>beforeSend: 이번 패킷의 seq 를 알려 준다 (보내기 직전 — 응답 대기를 먼저 등록하려고)</summary>
    bool SendCore(int agv, byte type, ReadOnlySpan<byte> payload, Action<ushort>? beforeSend)
    {
        var a = _reg.Get(agv);
        var ep = a?.EndPoint;
        if (a is null || ep is null || _udp is null) return false;
        if (BlockedInConflict(type) && a.InConflict(_opt.OfflineAfterSec))
        {
            Trace?.Invoke($"[{agv:D2}] 호기 번호 충돌 중 ({a.EndPoint} / {a.ConflictEndPoint}) — {BridgePacket.TypeName(type)} 송신 거부");
            return false;
        }
        // 패킷 한도를 넘는 payload 는 예외(BridgePacket.Build) 대신 거부 — 호출 경로(화면·REST)가 검사를 빠뜨려도 처리기 밖으로 예외가 나가지 않게
        if (payload.Length > BridgePacket.PayloadMax)
        {
            _log.LogWarning("[{Agv:D2}] {Type} payload {Len}B > {Max}B — 송신 안 함", agv, BridgePacket.TypeName(type), payload.Length, BridgePacket.PayloadMax);
            Trace?.Invoke($"[{agv:D2}] {BridgePacket.TypeName(type)} payload {payload.Length}B 가 {BridgePacket.PayloadMax}B 를 넘어 송신 안 함");
            return false;
        }
        var seq = _downSeq.AddOrUpdate(agv, 1, (_, s) => (ushort)(s + 1));
        beforeSend?.Invoke(seq);
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

    /// <summary>
    /// 설정 명령을 보내고 CFG_RESULT 를 기다린다. 오프라인·번호 충돌 중·payload 한도 초과는 바로 Error,
    /// 시간 안에 응답이 없으면 Error="응답 없음". 명령 문자열은 로그에 남기지 않는다 (set pass 등 비밀 포함 가능).
    /// </summary>
    public async Task<CfgReply> RequestCfgAsync(int agv, string cmd, TimeSpan timeout, CancellationToken ct = default)
    {
        var a = _reg.Get(agv);
        if (a is null || a.EndPoint is null) return new(null, "호기 주소를 모릅니다 (heartbeat 없음)");
        if (!a.Online(_opt.OfflineAfterSec)) return new(null, "오프라인");
        if (a.InConflict(_opt.OfflineAfterSec)) return new(null, "호기 번호 충돌 중");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        (int, ushort)? key = null;
        var sent = SendCore(agv, BridgePacket.CfgCmd, Encoding.UTF8.GetBytes(cmd), seq => { key = (agv, seq); _cfgWait[(agv, seq)] = tcs; });
        if (!sent)
        {
            if (key is { } k0) _cfgWait.TryRemove(k0, out _);
            return new(null, "보내지 못했습니다 (주소·충돌·길이 한도)");
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cts.Token));
            cts.Cancel();
            return done == tcs.Task ? new(await tcs.Task, null) : new(null, "응답 없음 (시간 초과)");
        }
        catch (OperationCanceledException) { return new(null, "취소됨"); }
        finally { if (key is { } k) _cfgWait.TryRemove(k, out _); }
    }

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
