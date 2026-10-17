using System.Text.Json;

namespace Gateway.Core.Bridge;

/// <summary>
/// 브리지 <c>cfg json</c> 응답(브리지 0.6.0+)을 읽은 설정 사본. Values 는 "cfg" 객체의 값을 글자로 (숫자도 글자) — 비교·표시용.
/// pass 는 브리지가 길이만 알려 주므로 pass_len 으로만 들어온다 (되읽기 불가).
/// </summary>
public sealed record CfgSnapshot(
    string Fw, string Idf, string Mac, string Name, string Part, string Ota, string State, bool Differs,
    IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Keys, string Raw)
{
    /// <summary>호기마다 달라야 하는 키 — 드리프트 비교·일괄 변경에서 제외 (agv 번호·고정 IP·비밀번호 길이)</summary>
    public static readonly string[] PerAgvKeys = ["agv", "ip", "pass_len"];

    /// <summary>모든 호기가 같아야 하는 키 — 기준값과 비교하는 대상</summary>
    public static readonly string[] FleetKeys =
        ["ssid", "mask", "gw", "dns", "dns2", "server", "server_port", "local_port", "rfid_dst", "rfid_port", "main_baud", "rfid_baud", "hb_ms"];

    /// <summary>일괄 변경(set)으로 보낼 수 있는 키 = FleetKeys + 비밀번호. pass 는 되읽을 수 없어 비교하지 않는다</summary>
    public static readonly string[] BulkKeys = [.. FleetKeys, "pass"];

    public string Get(string key) => Values.TryGetValue(key, out var v) ? v : "";

    /// <summary>응답이 JSON 객체이고 cfg 객체가 있으면 사본, 아니면 null (구 펌웨어의 "unknown command" 텍스트·{"err":..} 포함)</summary>
    public static CfgSnapshot? Parse(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith('{')) return null;
        try
        {
            using var d = JsonDocument.Parse(t);
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("cfg", out var cfg) || cfg.ValueKind != JsonValueKind.Object) return null;
            var vals = new Dictionary<string, string>();
            foreach (var p in cfg.EnumerateObject()) vals[p.Name] = Text(p.Value);
            var keys = new List<string>();
            if (r.TryGetProperty("keys", out var k) && k.ValueKind == JsonValueKind.Array)
                foreach (var e in k.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) keys.Add(e.GetString() ?? "");
            return new CfgSnapshot(Str(r, "fw"), Str(r, "idf"), Str(r, "mac"), Str(r, "name"), Str(r, "part"), Str(r, "ota"), Str(r, "state"),
                                   r.TryGetProperty("differs", out var df) && df.ValueKind == JsonValueKind.True, vals, keys, t);
        }
        catch (JsonException) { return null; }
    }

    static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static string Text(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
}

public sealed record CfgDiff(string Key, string Expected, string Actual);

public static class CfgDrift
{
    /// <summary>기준값(키→값)과 다른 FleetKeys. 기준에 없는 키·이 펌웨어가 모르는 키는 건너뛴다</summary>
    public static List<CfgDiff> Compare(IReadOnlyDictionary<string, string> baseline, CfgSnapshot s)
    {
        var r = new List<CfgDiff>();
        foreach (var k in CfgSnapshot.FleetKeys)
            if (baseline.TryGetValue(k, out var want) && s.Values.TryGetValue(k, out var have) && !string.Equals(want, have, StringComparison.Ordinal))
                r.Add(new CfgDiff(k, want, have));
        return r;
    }

    /// <summary>수집된 사본들의 키별 최빈값 → 기준값 후보. 같은 횟수면 먼저 본 값</summary>
    public static Dictionary<string, string> MostCommon(IEnumerable<CfgSnapshot> snaps)
    {
        var list = snaps.ToList();
        var r = new Dictionary<string, string>();
        foreach (var k in CfgSnapshot.FleetKeys)
        {
            var best = list.Where(s => s.Values.ContainsKey(k)).GroupBy(s => s.Values[k]).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (best is not null) r[k] = best.Key;
        }
        return r;
    }

    /// <summary>
    /// 보드를 바꿔 끼운 뒤 BLE·콘솔에 붙여 넣을 set 명령들. 비밀번호는 되읽을 수 없어 빠진다.
    /// agv 번호와 고정 IP 는 포함한다 (새 보드의 기본값이 아니라 이 호기의 값으로).
    /// </summary>
    public static List<string> RestoreCommands(CfgSnapshot s)
    {
        var r = new List<string>();
        foreach (var k in new[] { "agv", "ssid", "ip", "mask", "gw", "dns", "dns2", "server", "server_port", "local_port", "rfid_dst", "rfid_port", "main_baud", "rfid_baud", "hb_ms" })
            if (s.Values.TryGetValue(k, out var v)) r.Add($"set {k} {v}");
        return r;
    }

    /// <summary>호기를 batchSize 대씩 나눈다 (일괄 변경이 한 번에 건드리는 호기 수 제한)</summary>
    public static List<List<int>> Batches(IEnumerable<int> agvs, int batchSize)
    {
        batchSize = Math.Max(1, batchSize);
        var all = agvs.Distinct().OrderBy(a => a).ToList();
        var r = new List<List<int>>();
        for (int i = 0; i < all.Count; i += batchSize) r.Add(all.GetRange(i, Math.Min(batchSize, all.Count - i)));
        return r;
    }
}
