using System.Text.Json;

namespace Gateway.Core.Bridge;

/// <summary>
/// 브리지 heartbeat(STATUS 0x10) 또는 INFO(0x12) JSON. 모르는 필드는 무시하고 Raw 에 원문을 둔다.
/// 메인보드 필드(브리지 0.5.0+): dsp(앱 버전), dspb(부트로더 버전), dsp_build, dsp_st(idle/downloading/staged/programming),
/// staged {job, ver} 또는 null.
/// </summary>
public sealed record BridgeStatus(
    string Fw, string Part, string Ota, long UpSeconds, int Rssi, int Channel, string Bssid,
    long Disconnects, long MainRx, long MainTx, long RfidRx, long UartErrors, long NetOffline, long NetErrors,
    long Heap, string Cfg, bool SetupAp,
    string Dsp, string DspBoot, string DspBuild, string DspState, long? StagedJob, string? StagedVer,
    string Mac, string Ip, string Raw)
{
    public static BridgeStatus? Parse(ReadOnlySpan<byte> json)
    {
        var raw = System.Text.Encoding.UTF8.GetString(json);
        try
        {
            using var d = JsonDocument.Parse(raw);
            var r = d.RootElement;
            long? stagedJob = null; string? stagedVer = null;
            if (r.TryGetProperty("staged", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                stagedJob = Num(st, "job");
                stagedVer = Str(st, "ver");
            }
            return new BridgeStatus(
                Str(r, "fw"), Str(r, "part"), Str(r, "ota"), Num(r, "up"), (int)Num(r, "rssi"), (int)Num(r, "ch"), Str(r, "bssid"),
                Num(r, "disc"), Num(r, "m_rx"), Num(r, "m_tx"), Num(r, "r_rx"), Num(r, "u_err"), Num(r, "n_off"), Num(r, "n_err"),
                Num(r, "heap"), Str(r, "cfg"), Num(r, "ap") != 0 || Num(r, "setup") != 0,
                Str(r, "dsp"), Str(r, "dspb"), Str(r, "dsp_build"), Str(r, "dsp_st"), stagedJob, stagedVer,
                Str(r, "mac"), Str(r, "ip"), raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>메인보드 앱 버전을 아는 브리지인지 (0.5.0 미만·응답 없는 구 앱은 "" 또는 "?")</summary>
    public bool HasDsp => !string.IsNullOrEmpty(Dsp) && Dsp != "?";

    static string Str(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static long Num(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
