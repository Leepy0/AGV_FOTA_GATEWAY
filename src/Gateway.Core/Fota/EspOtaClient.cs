using System.Text.Json;
using Gateway.Core.Bridge;

namespace Gateway.Core.Fota;

public sealed class EspOtaException(string message) : Exception(message);

/// <summary>
/// 브리지(ESP32) 자체 OTA — OTA_START 에 URL 을 보내고 OTA_RESULT(start/progress/done/fail/busy) 를 기다린 뒤,
/// 재부팅한 새 펌웨어의 heartbeat(ota=pending) 를 보고 PING 으로 확정(valid)시킨다.
/// </summary>
public sealed class EspOtaClient
{
    readonly IAgvLink _link;
    readonly Func<int, BridgeStatus?> _status;     // 최신 heartbeat 조회 (서버의 AgvRegistry)

    public EspOtaClient(IAgvLink link, Func<int, BridgeStatus?> status) { _link = link; _status = status; }

    public async Task<string> RunAsync(int agv, string url, string expectedVersion, Action<string>? progress, CancellationToken ct)
    {
        using var sub = _link.Subscribe(agv);
        var before = _status(agv)?.Fw ?? "?";
        if (!_link.SendOtaStart(agv, url)) throw new EspOtaException("브리지 endpoint 없음");
        progress?.Invoke($"OTA_START {url} (현재 fw {before})");

        // 다운로드 결과
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            var rest = deadline - DateTime.UtcNow;
            if (rest <= TimeSpan.Zero) throw new EspOtaException("OTA_RESULT 없음 (3분)");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(rest);
            string js;
            try { js = await sub.OtaResults.Reader.ReadAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            string stage = "", err = "";
            try
            {
                using var d = JsonDocument.Parse(js);
                stage = d.RootElement.TryGetProperty("stage", out var s) ? s.GetString() ?? "" : "";
                err = d.RootElement.TryGetProperty("err", out var e) ? e.GetString() ?? "" : "";
            }
            catch (JsonException) { }
            progress?.Invoke($"OTA {js}");
            if (stage == "done") break;
            if (stage == "fail") throw new EspOtaException($"브리지 OTA 실패: {err}");
            if (stage == "busy") throw new EspOtaException("브리지가 이미 OTA 진행 중");
        }

        // 재부팅 후 새 펌웨어 heartbeat (ota=pending) → PING 으로 확정
        progress?.Invoke("재부팅 대기");
        var st = await WaitStatusAsync(agv, s => s.Ota == "pending" || (s.Fw != before && s.Ota == "valid"), TimeSpan.FromSeconds(90), ct)
                 ?? throw new EspOtaException("재부팅 후 heartbeat 없음 (90초) — 180초 뒤 이전 펌웨어로 롤백됨");
        if (st.Ota == "pending")
        {
            _link.SendPing(agv);
            st = await WaitStatusAsync(agv, s => s.Ota == "valid", TimeSpan.FromSeconds(15), ct)
                 ?? throw new EspOtaException("확정(valid) 안 됨");
        }
        if (!string.IsNullOrEmpty(expectedVersion) && st.Fw != expectedVersion)
            throw new EspOtaException($"버전 불일치: 기대 {expectedVersion}, 실제 {st.Fw}");
        progress?.Invoke($"완료 fw {st.Fw} part {st.Part} ota {st.Ota}");
        return st.Fw;
    }

    async Task<BridgeStatus?> WaitStatusAsync(int agv, Func<BridgeStatus, bool> pred, TimeSpan timeout, CancellationToken ct)
    {
        var t0 = DateTime.UtcNow;
        while (DateTime.UtcNow - t0 < timeout)
        {
            await Task.Delay(500, ct);
            var s = _status(agv);
            if (s is not null && pred(s)) return s;
        }
        return null;
    }
}
