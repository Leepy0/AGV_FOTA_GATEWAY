using System.Text.Json;

namespace Gateway.Core.Bridge;

/// <summary>
/// 서버 ↔ 브리지 시각 동기 (브리지 0.8.0+). 서버가 기준 시계다.
///  - 브리지가 TIME_REQ {"t0": 자기 가동 ms} 를 보내면 서버가 TIME {"t0","t1","t2"} 로 답한다. t1 = 서버가 요청을 받은 UTC unix ms, t2 = 보낸 UTC unix ms.
///    브리지는 왕복 지연 rtt = (받은 가동 ms − t0) − (t2 − t1) 를 구해 시각 = t2 + rtt/2 로 맞춘다 (NTP 한 번 교환).
///  - 서버가 먼저 TIME {"t2"} 만 보내기도 한다 (편차가 클 때·화면의 "시각 동기"). 이때는 한 방향 지연만큼 늦다.
///  - 브리지 heartbeat 의 "ts"(UTC unix ms)와 서버가 받은 시각의 차이가 편차(skew). 한 방향 지연(수 ms)이 섞여 있다.
/// </summary>
public static class BridgeClock
{
    /// <summary>이보다 이른 시각은 브리지가 아직 동기되지 않은 값(1970 근처)으로 본다 (2023-11)</summary>
    public const long MinPlausibleUnixMs = 1_700_000_000_000;

    public static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>TIME_REQ 본문에서 t0. 형식이 틀리면 null</summary>
    public static long? ParseRequest(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var d = JsonDocument.Parse(payload.ToArray());
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("t0", out var t) && t.TryGetInt64(out var v) && v >= 0 ? v : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>TIME 본문. t0 가 null 이면 서버가 먼저 보내는 형태 {"t2":..}</summary>
    public static byte[] BuildReply(long? t0, long t1UnixMs, long t2UnixMs)
        => System.Text.Encoding.ASCII.GetBytes(t0 is { } t ? $"{{\"t0\":{t},\"t1\":{t1UnixMs},\"t2\":{t2UnixMs}}}" : $"{{\"t2\":{t2UnixMs}}}");

    /// <summary>브리지 시각 − 서버 시각 (ms). 양수 = 브리지가 앞섬</summary>
    public static long SkewMs(long bridgeUnixMs, long serverUnixMs) => bridgeUnixMs - serverUnixMs;

    public static bool OverThreshold(long skewMs, long thresholdMs) => Math.Abs(skewMs) > thresholdMs;

    /// <summary>화면용: "+3 ms" · "-1.2 s" · "+2.0 분"</summary>
    public static string FormatSkew(long ms)
    {
        var a = Math.Abs(ms);
        var sign = ms >= 0 ? "+" : "-";
        return a < 1000 ? $"{sign}{a} ms" : a < 60_000 ? $"{sign}{a / 1000.0:0.0} s" : $"{sign}{a / 60_000.0:0.0} 분";
    }
}
