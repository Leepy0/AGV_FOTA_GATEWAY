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
///  2. 적용:     DSP_OTA_APPLY {job} → "start" → "enter" → "erase" → "progress"(pct) → "verify" → "done"{ver} / "fail"{err}
/// 브리지가 메인보드를 부트로더로 보내 UART 로 직접 굽는다. 적용 중 메인보드 앱은 멈추므로 AGV 정지 상태에서만.
/// </summary>
public sealed class DspOtaClient : IDisposable
{
    readonly IAgvLink _link;
    readonly Func<int, BridgeStatus?> _status;
    readonly AgvSubscription _sub;
    readonly int _agv;

    public static readonly TimeSpan StageTimeout = TimeSpan.FromMinutes(3);     // 다운로드 전체
    public static readonly TimeSpan ApplyIdleTimeout = TimeSpan.FromSeconds(90); // 단계 보고가 이만큼 끊기면 실패 (erase 1~2초×섹터, 쓰기 수십 초)

    public DspOtaClient(IAgvLink link, Func<int, BridgeStatus?> status, int agv)
    {
        _link = link; _status = status; _agv = agv;
        _sub = link.Subscribe(agv);
    }

    public void Dispose() => _sub.Dispose();

    static string Js(string s) => JsonSerializer.Serialize(s);

    /// <summary>이미지를 브리지에 받아 두게 한다. "staged" 까지 기다린다</summary>
    public async Task StageAsync(string url, DspfwInfo img, long job, Action<DspOtaResult>? progress = null, CancellationToken ct = default)
    {
        var json = $"{{\"url\":{Js(url)},\"ver\":{Js(img.Version)},\"crc32\":{img.Crc32},\"size\":{img.Size},\"job\":{job},\"apply\":0}}";
        if (!_link.SendDspOtaStart(_agv, json)) throw new DspOtaException("브리지 endpoint 없음 (heartbeat 미수신)");
        await WaitAsync(job, "staged", StageTimeout, progress, ct);
    }

    /// <summary>스테이징된 이미지를 메인보드에 굽는다. "done" 의 ver(앱이 보고한 새 버전)를 돌려준다</summary>
    public async Task<string> ApplyAsync(long job, Action<DspOtaResult>? progress = null, CancellationToken ct = default)
    {
        if (!_link.SendDspOtaApply(_agv, $"{{\"job\":{job}}}")) throw new DspOtaException("브리지 endpoint 없음");
        var r = await WaitAsync(job, "done", ApplyIdleTimeout, progress, ct);
        return r.Ver;
    }

    /// <summary>heartbeat 의 dsp 버전이 기대값이 될 때까지 (브리지가 적용 후 앱에 버전을 다시 묻는다)</summary>
    public async Task<BridgeStatus> WaitVersionAsync(string expected, TimeSpan timeout, CancellationToken ct = default)
    {
        var t0 = DateTime.UtcNow;
        BridgeStatus? last = null;
        while (DateTime.UtcNow - t0 < timeout)
        {
            last = _status(_agv);
            if (last is not null && last.Dsp == expected) return last;
            await Task.Delay(500, ct);
        }
        throw new DspOtaException($"heartbeat 의 메인보드 버전이 {expected} 가 아님 (현재 {last?.Dsp ?? "?"})");
    }

    // 같은 job 의 결과만 본다. "fail" 이면 예외, 목표 stage 가 오면 반환. 보고가 idle 만큼 끊기면 시간 초과
    async Task<DspOtaResult> WaitAsync(long job, string target, TimeSpan idle, Action<DspOtaResult>? progress, CancellationToken ct)
    {
        while (true)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(idle);
            string js;
            try { js = await _sub.DspOtaResults.Reader.ReadAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DspOtaException($"브리지 보고 없음 ({idle.TotalSeconds:F0}초, '{target}' 대기 중)"); }
            catch (ChannelClosedException) { throw new DspOtaException("구독 종료"); }
            var r = DspOtaResult.Parse(js);
            if (r is null || (r.Job != 0 && job != 0 && r.Job != job)) continue;
            progress?.Invoke(r);
            if (r.Stage == "fail") throw new DspOtaException($"브리지 보고 실패: {r.Err}{(r.Ver.Length > 0 ? $" (ver {r.Ver})" : "")}");
            if (r.Stage == target) return r;
        }
    }
}
