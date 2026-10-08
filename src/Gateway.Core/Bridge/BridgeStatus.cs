using System.Text.Json;

namespace Gateway.Core.Bridge;

/// <summary>브리지 heartbeat(STATUS 0x10) JSON. 모르는 필드는 무시하고 Raw 에 원문을 둔다.</summary>
public sealed record BridgeStatus(
    string Fw, string Part, string Ota, long UpSeconds, int Rssi, int Channel, string Bssid,
    long Disconnects, long MainRx, long MainTx, long RfidRx, long UartErrors, long NetOffline, long NetErrors,
    long Heap, string Cfg, bool SetupAp, string Raw)
{
    public static BridgeStatus? Parse(ReadOnlySpan<byte> json)
    {
        var raw = System.Text.Encoding.UTF8.GetString(json);
        try
        {
            using var d = JsonDocument.Parse(raw);
            var r = d.RootElement;
            return new BridgeStatus(
                Str(r, "fw"), Str(r, "part"), Str(r, "ota"), Num(r, "up"), (int)Num(r, "rssi"), (int)Num(r, "ch"), Str(r, "bssid"),
                Num(r, "disc"), Num(r, "m_rx"), Num(r, "m_tx"), Num(r, "r_rx"), Num(r, "u_err"), Num(r, "n_off"), Num(r, "n_err"),
                Num(r, "heap"), Str(r, "cfg"), Num(r, "ap") != 0, raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string Str(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static long Num(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
