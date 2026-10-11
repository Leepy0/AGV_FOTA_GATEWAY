using System.Threading.Channels;
using System.Text.Json;
using Gateway.Core.Bridge;

namespace Gateway.Core.Fota;

public sealed class DspOtaException(string message) : Exception(message);

/// <summary>DSP_OTA_RESULT 한 건: {job, stage, pct?, ver?, err?}</summary>
public sealed record DspOtaResult(long Job, string Stage, int Pct, string Ver, string Err)
{
    public static DspOtaResult? Parse(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            return new DspOtaResult(
                r.TryGetProperty("job", out var j) && j.TryGetInt64(out var jv) ? jv : 0,
                r.TryGetProperty("stage", out var s) ? s.GetString() ?? "" : "",
                r.TryGetProperty("pct", out var p) && p.TryGetInt32(out var pv) ? pv : -1,
                r.TryGetProperty("ver", out var v) ? v.GetString() ?? "" : "",
                r.TryGetProperty("err", out var e) ? e.GetString() ?? "" : "");
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// 메인보드(F2808) OTA — B안 (AGV_Wifi_Bridge/docs/DSP_OTA_DESIGN.md).
///  1. 스테이징: DSP_OTA_START {url, ver, crc32, size, job, apply:0} → 브리지가 HTTP 로 받아 보관 → "download"(pct) → "staged"
///  2. 적용:     DSP_OTA_APPLY {job[, recover:1]} → "start" → "enter" → "erase" → "progress"(pct) → "verify" → "done"{ver} / "fail"{err}
/// 브리지가 메인보드를 부트로더로 보내 UART 로 직접 굽는다. 적용 중 메인보드 앱은 멈추므로 AGV 정지 상태에서만.
/// UDP 유실 대비: 명령 뒤 수신 흔적(보고·heartbeat)이 없으면 다시 보내고, 최종 보고가 유실돼도 heartbeat(staged·dsp·dsp_st)로 판정한다.
/// 브리지 0.5.2 는 최종 보고를 3회 반복하므로 같은 보고가 거듭 와도 한 번만 처리한다.
/// </summary>
public sealed class DspOtaClient : IDisposable
{
    readonly IAgvLink _link;
    readonly Func<int, BridgeStatus?> _status;
    readonly AgvSubscription _sub;
    readonly int _agv;
    readonly OtaTimeouts _t;
    string? _lastJs;                // 직전에 처리한 보고 원문 (반복 보고 무시)

    public static readonly TimeSpan StageTimeout = OtaTimeouts.Default.DspStageIdle;
    public static readonly TimeSpan ApplyIdleTimeout = OtaTimeouts.Default.DspApplyIdle;

    public DspOtaClient(IAgvLink link, Func<int, BridgeStatus?> status, int agv, OtaTimeouts? timeouts = null)
    {
        _link = link; _status = status; _agv = agv; _t = timeouts ?? OtaTimeouts.Default;
        _sub = link.Subscribe(agv);
    }

    public void Dispose() => _sub.Dispose();

    // 브리지의 최소 JSON 파서는 \uXXXX 를 풀지 못하므로 + & < > ' 와 비ASCII 는 그대로 둔다 (" \ 제어문자만 이스케이프)
    static readonly JsonSerializerOptions JsOpt = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string Js(string s) => JsonSerializer.Serialize(s, JsOpt);

    /// <summary>이미지를 브리지에 받아 두게 한다. "staged" 보고 또는 heartbeat 의 staged.job==job 까지 기다린다</summary>
    public async Task StageAsync(string url, DspfwInfo img, long job, Action<DspOtaResult>? progress = null, CancellationToken ct = default)
    {
        var json = $"{{\"url\":{Js(url)},\"ver\":{Js(img.Version)},\"crc32\":{img.Crc32},\"size\":{img.Size},\"job\":{job},\"apply\":0}}";
        if (!_link.SendDspOtaStart(_agv, json)) throw new DspOtaException("브리지 endpoint 없음 (heartbeat 미수신)");
        await WaitAsync(job, "staged", _t.DspStageIdle, () => _link.SendDspOtaStart(_agv, json), null, progress, ct);
    }

    /// <summary>
    /// 스테이징된 이미지를 메인보드에 굽는다. "done" 의 ver(앱이 보고한 새 버전, 앞 8자)를 돌려준다.
    /// done 보고가 유실되면 heartbeat 가 dsp==기대 버전(앞 8자)·dsp_st=idle·staged=null 일 때 완료로 본다.
    /// 기대 버전은 expectedVersion, 없으면 이 job 의 start 보고나 heartbeat staged.ver 에서 본 스테이징 버전. recover=true 면 "recover":1 (앱 무응답 시 부트로더 직행 복구).
    /// </summary>
    public async Task<string> ApplyAsync(long job, Action<DspOtaResult>? progress = null, CancellationToken ct = default,
                                         string? expectedVersion = null, bool recover = false)
    {
        var json = recover ? $"{{\"job\":{job},\"recover\":1}}" : $"{{\"job\":{job}}}";
        if (!_link.SendDspOtaApply(_agv, json)) throw new DspOtaException("브리지 endpoint 없음");
        var r = await WaitAsync(job, "done", _t.DspApplyIdle, () => _link.SendDspOtaApply(_agv, json), expectedVersion, progress, ct);
        return r.Ver;
    }

    /// <summary>heartbeat 의 dsp 버전이 기대값(앞 8자)이 될 때까지 (브리지가 적용 후 앱에 버전을 다시 묻는다)</summary>
    public async Task<BridgeStatus> WaitVersionAsync(string expected, TimeSpan timeout, CancellationToken ct = default)
    {
        var t0 = DateTime.UtcNow;
        BridgeStatus? last = null;
        while (true)
        {
            last = _status(_agv);
            if (last is not null && DspVersion.Matches(last.Dsp, expected)) return last;
            if (DateTime.UtcNow - t0 >= timeout) break;
            await Task.Delay(_t.Poll, ct);
        }
        throw new DspOtaException($"heartbeat 의 메인보드 버전이 {expected} 가 아님 (현재 {last?.Dsp ?? "?"})");
    }

    /// <summary>
    /// 같은 job 의 보고와 heartbeat 를 함께 본다. "fail" 이면 예외, 목표 단계면 반환. 이 job 의 보고·진행 표시가 idle 만큼 끊기면 시간 초과.
    /// 수신 흔적이 Resend 동안 없으면 resend() 로 다시 보낸다(최대 MaxResends). 재전송 뒤에는 job 0 의 fail(not_staged 등)과 busy 를
    /// 앞서 보낸 명령이 처리 중인 흔적으로 보고 무시한다.
    /// </summary>
    async Task<DspOtaResult> WaitAsync(long job, string target, TimeSpan idle, Func<bool> resend, string? expected,
                                       Action<DspOtaResult>? progress, CancellationToken ct)
    {
        bool apply = target == "done";
        var lastSend = DateTime.UtcNow;
        var lastActivity = lastSend;
        int resends = 0;
        bool received = false;      // 브리지가 명령을 받은 흔적 (재전송 중단)
        bool sawJob = false;        // 적용: 이 job 이 스테이징돼 있었거나 처리된 흔적 (heartbeat 완료 판정의 전제)
        BridgeStatus? lastSt = null;
        var reader = _sub.DspOtaResults.Reader;
        while (true)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(_t.Poll);
                try { if (!await reader.WaitToReadAsync(cts.Token)) throw new DspOtaException("구독 종료"); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            while (reader.TryRead(out var js))
            {
                var r = DspOtaResult.Parse(js);
                if (r is null || (r.Job != 0 && job != 0 && r.Job != job)) continue;      // 다른 job — 대기 시계도 그대로
                lastActivity = DateTime.UtcNow;
                received = true;
                if (r.Job == job && job != 0)
                {
                    sawJob = true;
                    if (apply && r.Stage == "start" && r.Ver.Length > 0) expected ??= r.Ver;   // start 에 스테이징 버전이 실려 옴
                }
                if (js == _lastJs) continue;                                            // 반복 보고 (브리지 0.5.2 는 최종 보고 3회)
                _lastJs = js;
                if (apply && r.Stage is "staged" or "download") continue;               // 스테이징 쪽 보고가 늦게 도착 (반복 보고·재전송한 START)
                if (r.Stage == "fail" && resends > 0 && (r.Job == 0 || r.Err == "busy")) continue;  // 재전송한 명령의 중복 처리 결과
                progress?.Invoke(r);
                if (r.Stage == "fail") throw new DspOtaException($"브리지 보고 실패: {r.Err}{(r.Ver.Length > 0 ? $" (ver {r.Ver})" : "")}");
                if (r.Stage == target) return r;
            }

            var st = _status(_agv);
            bool fresh = st is not null && !ReferenceEquals(st, lastSt);
            if (st is not null)
            {
                lastSt = st;
                if (st.StagedJob == job && job != 0)
                {
                    if (!apply) return Done(new DspOtaResult(job, "staged", 100, st.StagedVer ?? "", ""));
                    sawJob = true;
                    expected ??= st.StagedVer;
                }
                if (fresh && st.DspState == (apply ? "programming" : "downloading"))
                {
                    received = true;
                    if (apply) sawJob = true;
                    lastActivity = DateTime.UtcNow;
                }
                if (apply && sawJob && st.DspState == "idle" && st.StagedJob is null && DspVersion.Matches(st.Dsp, expected))
                    return Done(new DspOtaResult(job, "done", 100, st.Dsp, ""));
            }

            var now = DateTime.UtcNow;
            if (now - lastActivity >= idle)
                throw new DspOtaException($"브리지 보고 없음 ({idle.TotalSeconds:F0}초, '{target}' 대기 중{(resends > 0 ? $", 재전송 {resends}회" : "")})");
            if (!received && resends < _t.MaxResends && now - lastSend >= _t.Resend)
            {
                resends++;
                lastSend = now;
                resend();
                progress?.Invoke(new DspOtaResult(job, "resend", -1, "", ""));
            }
        }

        DspOtaResult Done(DspOtaResult r) { progress?.Invoke(r); return r; }
    }
}
