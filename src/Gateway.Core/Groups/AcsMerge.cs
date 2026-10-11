namespace Gateway.Core.Groups;

/// <summary>ACS 소속 응답을 저장소에 반영할 계획. Held = 한 번에 너무 많이 빠져서 삭제를 보류한 호기</summary>
public sealed record AcsMergePlan(List<AgvAssignment> Upserts, List<int> Removes, List<int> Held);

/// <summary>ACS 소속 반영 규칙 (AgvGroupStore.MergeFromAcs 가 적용). 저장 없이 계산만 — 시험용으로 분리</summary>
public static class AcsMerge
{
    /// <summary>이 수 이상, 그리고 현재 acs 출처의 절반 이상이 한 번에 빠지면 삭제 보류 (ACS 일시 오류·형식 변경 대비)</summary>
    public const int MassRemoveMin = 2;

    public static AcsMergePlan Plan(IReadOnlyDictionary<int, AgvAssignment> current, IReadOnlyList<AgvAssignment> fromAcs, bool overrideManual, bool removeMissing)
    {
        var upserts = new List<AgvAssignment>();
        var seen = new HashSet<int>();
        foreach (var a in fromAcs)
        {
            seen.Add(a.Agv);
            if (current.TryGetValue(a.Agv, out var cur))
            {
                if (cur.Source != "acs" && !overrideManual) continue;          // 직접 넣은 값은 유지
                if (cur.Group == a.Group && cur.Name == a.Name && cur.Source == "acs") continue;
            }
            a.Source = "acs";
            upserts.Add(a);
        }
        var missing = removeMissing
            ? current.Values.Where(v => v.Source == "acs" && !seen.Contains(v.Agv)).Select(v => v.Agv).OrderBy(x => x).ToList()
            : new List<int>();
        int acsNow = current.Values.Count(v => v.Source == "acs");
        if (missing.Count >= MassRemoveMin && missing.Count * 2 >= acsNow) return new(upserts, new(), missing);
        return new(upserts, missing, new());
    }
}
