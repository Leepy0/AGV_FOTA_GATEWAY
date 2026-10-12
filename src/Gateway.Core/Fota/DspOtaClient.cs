using System.Threading.Channels;
using System.Text.Json;
using Gateway.Core.Bridge;

namespace Gateway.Core.Fota;

public sealed class DspOtaException(string message) : Exception(message);

/// <summary>
/// 브리지가 보고하는 오류 코드(err)의 운용자용 한국어 안내 — 메인보드(DSP_OTA_RESULT)·브리지 자체 OTA(OTA_RESULT) 공통.
/// 코드는 AGV_Wifi_Bridge main/dsp_ota.c·ota.c 의 보고 그대로.
/// </summary>
public static class BridgeErrors
{
    /// <summary>오류 코드의 안내 문구. 모르는 코드면 null</summary>
    public static string? Hint(string? err)
    {
        var e = (err ?? "").Trim();
        return e switch
        {
            _ when e.StartsWith("app_ng") => "메인보드 앱이 업데이트 진입을 거부 — AGV 가 정지 상태인지 확인",
            "app_no_reply" => "메인보드 앱이 응답하지 않음(앱 프레임도 없음) — 정지 확인 후 다시",
            "app_no_fwup" => "메인보드 앱은 동작하나 업데이트 진입을 지원하지 않음(구 앱) — JTAG 로 부트로더와 새 앱을 설치해야 함",
            "uart_busy" => "브리지 → 메인보드 송신 대기열이 가득 참 — 잠시 뒤 다시",
            "url_host" => "URL 호스트가 브리지에 설정된 서버 IP 와 다름 — PublicBaseUrl 은 브리지 server IP 와 같은 IPv4 여야 함 (비우면 자동)",
            "url_too_long" => "URL 이 브리지 한도를 넘음 — 펌웨어 파일명을 줄여 다시 올리기",
            "bad_url" => "URL 형식 오류 — http:// 로 시작하고 255자 이하여야 함 (PublicBaseUrl 확인)",
            "bad_request" => "브리지가 명령 JSON 을 읽지 못함 — 서버·브리지 버전 확인",
            "ver_invalid" => "메인보드 버전 형식 오류 (1~8자 영문·숫자·._-) — dsp_pack.py --ver 확인",
            "image_bad" or "image_crc" or "stage_invalid" or "crc_mismatch" or "stage_block" => "이미지 검증 실패 — dsp_pack.py 로 다시 만들어 올리기",
            "no_bootloader" => "부트로더 응답 없음 — 메인보드 전원·배선 확인 (복구 적용 중이면 안내에 따라 메인보드 전원을 껐다 켜기)",
            "boot_proto" => "부트로더 프로토콜 버전이 맞지 않음 — 부트로더 갱신 필요 (메인보드 앱은 그대로)",
            "esp_ota_busy" => "브리지 자체 업데이트(OTA) 진행 중이라 메인보드 적용 거부 — 브리지 OTA 가 끝난 뒤 다시",
            "mainboard_programming" => "브리지가 메인보드를 굽는 중이라 브리지 OTA 거부 — 메인보드 적용이 끝난 뒤 다시",
            "mainboard_staging" => "브리지가 메인보드 이미지를 받는 중(스테이징)이라 브리지 OTA 거부 — 스테이징이 끝난 뒤 다시",
            "no_mem" => "브리지 메모리 부족으로 시작하지 못함 — 잠시 뒤 다시, 반복되면 브리지 재부팅",
            "busy" or "not_ready" => "브리지가 앞선 메인보드 명령을 처리 중 — 잠시 뒤 다시",
            "no_partition" or "nopart" => "브리지에 dspfw 파티션이 없음 — USB 로 한 번 전체 굽기 필요",
            "ver_mismatch" => "구운 뒤 메인보드 앱이 보고한 버전이 이미지와 다름",
            "not_staged" => "브리지에 받아 둔(스테이징) 이미지가 없음 (브리지 재굽기·스테이징 중 재부팅 등) — 새 작업으로 다시 배포",
            "job_mismatch" => "브리지에 받아 둔 이미지가 다른 작업의 것 (다른 작업이 덮어씀) — 새 작업으로 다시 배포",
            "erase" or "verify" or "write" or "write_timeout" or "program" or "verify_block" or "write_header" or "stage_read" => "메인보드 굽기 실패 — 다시 적용 (앱이 지워졌으면 부트로더가 업데이트 모드에서 기다림)",
            _ when e.StartsWith("http_") || e.StartsWith("begin") || e.StartsWith("perform") => "브리지가 서버에서 파일을 받지 못함 — 서버 HTTP 포트·방화벽·PublicBaseUrl 확인",
            _ when e.StartsWith("flash_") => "브리지 플래시 쓰기 오류 — 다시 시도, 반복되면 브리지 점검",
            _ when e.StartsWith("finish") || e.StartsWith("project mismatch") => "브리지 이미지 검증 실패 — build/agv_wifi_bridge.bin 을 올렸는지 확인",
            _ => null,
        };
    }

    /// <summary>메시지 뒤에 오류 코드 안내를 붙인다 (모르는 코드면 그대로)</summary>
    public static string WithHint(string message, string? err) => Hint(err) is { } h ? $"{message} — {h}" : message;
}

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
    /// 같은 job 의 보고와 heartbeat 를 함께 본다. "fail" 이면 예외, 목표 단계면 반환. 이 job 의 보고·진행 표시가 idle 만큼 끊기면 시간 초과
    /// (복구 적용의 wait_power·retry 보고도 보고이므로 받을 때마다 대기 시한이 늘어난다).
    /// 수신 흔적이 Resend 동안 없으면 resend() 로 다시 보낸다(최대 MaxResends). 적용 대기 중 늦게 온 스테이징 보고(staged·download)는
    /// 수신 흔적이 아니다. 재전송 뒤의 busy 는 앞서 보낸 명령이 처리 중인 흔적으로 보고 무시하고, job 0 의 fail(not_staged)은 실패로 본다.
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
                // 스테이징 쪽 보고가 늦게 도착 (최종 보고 반복·재전송한 START) — APPLY 가 닿은 흔적이 아니다 (재전송을 막지 않게 수신 흔적보다 먼저)
                if (apply && r.Stage is "staged" or "download") continue;
                lastActivity = DateTime.UtcNow;
                received = true;
                if (r.Job == job && job != 0)
                {
                    sawJob = true;
                    if (apply && r.Stage == "start" && r.Ver.Length > 0) expected ??= r.Ver;   // start 에 스테이징 버전이 실려 옴
                }
                // 반복 보고 (브리지 0.5.2 는 최종 보고 3회). wait_power 처럼 같은 내용을 주기적으로 보내는 진행 보고는 매번 넘긴다
                if (r.Stage is "staged" or "done" or "fail")
                {
                    if (js == _lastJs) continue;
                    _lastJs = js;
                }
                if (r.Stage == "fail" && resends > 0 && r.Err == "busy") continue;      // 재전송한 명령이 앞선 명령 처리 중에 닿음
                // 재전송 뒤 job 0 실패(not_staged): 브리지 0.5.2 는 이미 끝난 같은 job 의 APPLY 에 최근 결과(done)를 다시 보내므로
                // 실제 스테이징 소실이다. 단 heartbeat 가 이미 이 job 의 완료를 보이면(최근 결과 보관 시간이 지난 경우 등) 완료로 본다
                if (apply && r.Stage == "fail" && resends > 0 && r.Job == 0 && sawJob && HeartbeatDone(_status(_agv), expected) is { } hb)
                    return Done(hb);
                progress?.Invoke(r);
                if (r.Stage == "fail") throw new DspOtaException(BridgeErrors.WithHint($"브리지 보고 실패: {r.Err}{(r.Ver.Length > 0 ? $" (ver {r.Ver})" : "")}", r.Err));
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
                if (apply && sawJob && HeartbeatDone(st, expected) is { } hb) return Done(hb);
            }

            var now = DateTime.UtcNow;
            // 명령이 닿은 흔적이 없으면 재전송을 다 쓴 뒤에만 포기한다 (시간 초과가 마지막 재전송보다 먼저 오지 않게)
            if (now - lastActivity >= idle && (received || resends >= _t.MaxResends))
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
        DspOtaResult? HeartbeatDone(BridgeStatus? st, string? exp) =>
            st is not null && st.DspState == "idle" && st.StagedJob is null && DspVersion.Matches(st.Dsp, exp)
                ? new DspOtaResult(job, "done", 100, st.Dsp, "") : null;
    }
}
