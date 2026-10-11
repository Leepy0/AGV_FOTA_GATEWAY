using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.Core.Groups;

namespace Gateway.Core.Acs;

// ══════════════════════════════════════════════════════════════════════════════════════
//  ACS → 서버: 호기 소속(제품군·층·라인·공정·라우트) 받기 — 스키마 반영 지점
//  ACS 응답 JSON 의 필드 이름은 AcsInventoryFieldMap (appsettings "AcsInventory.Fields") 으로 맞춘다.
//  "location.floor" 처럼 점으로 중첩 필드를 가리킬 수 있고, 호기 번호는 숫자 또는 "AGV-07" 같은 이름에서 정규식으로 뽑는다.
//  구조가 더 다르면(호기별이 아니라 구역별 목록 등) 이 파일의 Parse 만 고친다.
// ══════════════════════════════════════════════════════════════════════════════════════

/// <summary>ACS 응답 필드 이름 매핑 (임시 기본값 — 월요일 스키마로 교체)</summary>
public sealed class AcsInventoryFieldMap
{
    /// <summary>호기 목록이 들어 있는 속성 경로. 비우면 자동: 최상위 배열, 또는 agvs/items/data/result/list</summary>
    public string Root { get; set; } = "";
    public string Agv { get; set; } = "agvId";
    public string Name { get; set; } = "name";
    public string ProductFamily { get; set; } = "productFamily";
    public string Floor { get; set; } = "floor";
    public string Line { get; set; } = "line";
    public string Process { get; set; } = "process";
    public string Route { get; set; } = "route";
    /// <summary>Agv 값이 문자열일 때 번호를 뽑는 정규식 (첫 캡처 그룹). "AGV-07" → 7</summary>
    public string AgvIdRegex { get; set; } = @"(?<!\d)(\d{1,2})\s*$";
}

/// <param name="RootFound">호기 목록 배열을 찾았는지 (false 면 오류 응답·형식 변경 — 반영하면 안 됨)</param>
public sealed record AcsInventoryParseResult(List<AgvAssignment> Items, List<string> Warnings, int TotalRecords, bool RootFound = true)
{
    /// <summary>반영하면 안 되는 응답이면 그 이유 (목록 못 찾음·빈 목록·전부 해석 실패), 반영해도 되면 null.
    /// 이런 응답을 그대로 반영하면 RemoveMissing 때문에 acs 출처 소속이 전부 지워진다.</summary>
    public string? RejectReason =>
        !RootFound ? "호기 목록을 찾지 못함" + (Warnings.Count > 0 ? $" ({Warnings[0]})" : "")
        : Items.Count == 0 ? (TotalRecords == 0 ? "호기 목록이 비어 있음" : $"{TotalRecords}건 모두 호기 번호 해석 실패 (Fields 매핑 확인)")
        : null;
}

public static class AcsInventoryMapper
{
    static readonly string[] AutoRoots = ["agvs", "items", "data", "result", "results", "list", "vehicles"];

    /// <summary>문서·시험용 예시 응답 (임시 형식)</summary>
    public const string SampleJson = """
        {
          "agvs": [
            { "agvId": 7,        "name": "AGV-07", "productFamily": "A제품", "floor": "2F", "line": "L1", "process": "조립", "route": "자재X" },
            { "agvId": "AGV-12", "name": "AGV-12", "productFamily": "A제품", "floor": "2F", "line": "L1", "process": "검사", "route": "자재Y" },
            { "agvId": 23,       "name": "AGV-23", "productFamily": "B제품", "floor": "3F", "line": "L4", "process": "포장", "route": "" }
          ]
        }
        """;

    public static AcsInventoryParseResult Parse(string json, AcsInventoryFieldMap map, DateTimeOffset now)
    {
        var items = new List<AgvAssignment>();
        var warnings = new List<string>();
        using var doc = JsonDocument.Parse(json);
        var root = FindRoot(doc.RootElement, map.Root);
        if (root is null || root.Value.ValueKind != JsonValueKind.Array)
        {
            warnings.Add(string.IsNullOrEmpty(map.Root) ? "호기 목록(배열)을 찾지 못함 — AcsInventory.Fields.Root 지정 필요" : $"'{map.Root}' 가 배열이 아님");
            return new(items, warnings, 0, RootFound: false);
        }
        var rx = new Regex(map.AgvIdRegex);
        int total = 0;
        var seen = new HashSet<int>();
        foreach (var rec in root.Value.EnumerateArray())
        {
            total++;
            if (rec.ValueKind != JsonValueKind.Object) { warnings.Add($"{total}번째 항목이 객체가 아님"); continue; }
            var idText = Str(rec, map.Agv);
            if (!TryAgv(idText, rx, out var agv)) { warnings.Add($"{total}번째: 호기 번호를 알 수 없음 ({map.Agv}='{idText}')"); continue; }
            if (!seen.Add(agv)) { warnings.Add($"호기 {agv:D2} 중복 — 뒤의 것을 씀"); items.RemoveAll(x => x.Agv == agv); }
            items.Add(new AgvAssignment
            {
                Agv = agv, Name = Str(rec, map.Name), Source = "acs", UpdatedAt = now,
                Group = AgvGroup.Trimmed(Str(rec, map.ProductFamily), Str(rec, map.Floor), Str(rec, map.Line), Str(rec, map.Process), Str(rec, map.Route)),
            });
        }
        return new(items, warnings, total);
    }

    static JsonElement? FindRoot(JsonElement root, string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) return Get(root, path);
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in AutoRoots)
            if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array) return v;
        // 객체 안에 배열이 하나뿐이면 그것
        var arrays = root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).ToList();
        return arrays.Count == 1 ? arrays[0].Value : null;
    }

    /// <summary>"a.b.c" 경로로 값 찾기 (대소문자 무시)</summary>
    static JsonElement? Get(JsonElement e, string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            JsonElement? next = null;
            foreach (var p in e.EnumerateObject())
                if (p.Name.Equals(part, StringComparison.OrdinalIgnoreCase)) { next = p.Value; break; }
            if (next is null) return null;
            e = next.Value;
        }
        return e;
    }

    static string Str(JsonElement rec, string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var v = Get(rec, path);
        return v is null ? "" : v.Value.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString() ?? "",
            JsonValueKind.Number => v.Value.GetRawText(),
            JsonValueKind.True => "true", JsonValueKind.False => "false",
            _ => "",
        };
    }

    static bool TryAgv(string s, Regex rx, out int agv)
    {
        agv = 0;
        if (int.TryParse(s, out var n)) { agv = n; return n is >= 1 and <= 99; }
        var m = rx.Match(s);
        if (!m.Success || m.Groups.Count < 2 || !int.TryParse(m.Groups[1].Value, out n)) return false;
        agv = n;
        return n is >= 1 and <= 99;
    }
}
