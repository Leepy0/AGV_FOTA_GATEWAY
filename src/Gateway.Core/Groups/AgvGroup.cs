namespace Gateway.Core.Groups;

/// <summary>
/// 호기의 소속: 제품군 → 층 → 라인 → 공정(구역) → 대상 자재(라우트) 5단계. 값이 비면 그 단계는 "미지정".
/// ACS 가 주는 분류를 그대로 담는 그릇이고, 단계 이름은 <see cref="LevelNames"/> 로 고정이다 (화면·CSV·API 공통).
/// </summary>
public sealed record AgvGroup(string ProductFamily = "", string Floor = "", string Line = "", string Process = "", string Route = "")
{
    public const int Levels = 5;
    public static readonly string[] LevelNames = ["제품군", "층", "라인", "공정", "라우트"];
    public static readonly string[] LevelKeys = ["productFamily", "floor", "line", "process", "route"];
    public const string Unassigned = "(미지정)";
    public static readonly AgvGroup Empty = new();

    public string this[int level] => level switch
    {
        0 => ProductFamily, 1 => Floor, 2 => Line, 3 => Process, 4 => Route, _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public AgvGroup With(int level, string value) => level switch
    {
        0 => this with { ProductFamily = value }, 1 => this with { Floor = value }, 2 => this with { Line = value },
        3 => this with { Process = value }, 4 => this with { Route = value }, _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public bool IsEmpty => Depth == 0;
    /// <summary>값이 있는 가장 깊은 단계 + 1 (0 = 전부 미지정)</summary>
    public int Depth { get { for (int i = Levels - 1; i >= 0; i--) if (!string.IsNullOrWhiteSpace(this[i])) return i + 1; return 0; } }

    /// <summary>표시용 경로 "A / 2F / L1 / 조립 / 자재X" (비어 있는 중간 단계는 (미지정), 끝의 빈 단계는 생략)</summary>
    public string Path => Depth == 0 ? "미분류" : string.Join(" / ", Enumerable.Range(0, Depth).Select(i => Norm(this[i])));
    /// <summary>앞 depth 단계만의 경로 (트리 노드 경로)</summary>
    public string PathTo(int depth) => depth <= 0 ? "" : string.Join(" / ", Enumerable.Range(0, Math.Min(depth, Levels)).Select(i => Norm(this[i])));

    public static string Norm(string? v) => string.IsNullOrWhiteSpace(v) ? Unassigned : v.Trim();
    public static AgvGroup Trimmed(string? pf, string? fl, string? ln, string? pr, string? rt) =>
        new((pf ?? "").Trim(), (fl ?? "").Trim(), (ln ?? "").Trim(), (pr ?? "").Trim(), (rt ?? "").Trim());
}

/// <summary>호기 한 대의 소속 정보 (ACS 에서 받았거나 화면·CSV 로 직접 넣은 것)</summary>
public sealed class AgvAssignment
{
    public int Agv { get; set; }
    /// <summary>ACS 쪽 호기 이름·ID (있으면 그대로 보관, 매칭은 Agv 번호로)</summary>
    public string Name { get; set; } = "";
    public AgvGroup Group { get; set; } = AgvGroup.Empty;
    /// <summary>acs | manual | import</summary>
    public string Source { get; set; } = "manual";
    public DateTimeOffset UpdatedAt { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>소속 트리의 노드. Level 0 = 제품군 … 4 = 라우트. 루트는 Level -1</summary>
public sealed class GroupNode
{
    public int Level { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public List<GroupNode> Children { get; } = new();
    /// <summary>이 노드에서 분류가 끝나는 호기 (더 깊은 단계가 미지정)</summary>
    public List<int> Agvs { get; } = new();
    /// <summary>이 노드 아래 전체 호기 (정렬·중복 제거)</summary>
    public List<int> AllAgvs { get; } = new();
    public int Count => AllAgvs.Count;
    public string LevelName => Level < 0 ? "전체" : AgvGroup.LevelNames[Level];

    public IEnumerable<GroupNode> Descendants()
    {
        foreach (var c in Children) { yield return c; foreach (var d in c.Descendants()) yield return d; }
    }
}

public static class GroupTree
{
    /// <summary>소속 목록 → 트리. 빈 단계는 (미지정) 노드로, 전부 비면 루트 직속</summary>
    public static GroupNode Build(IEnumerable<AgvAssignment> items)
    {
        var root = new GroupNode { Level = -1, Name = "전체", Path = "" };
        foreach (var a in items.OrderBy(x => x.Agv))
        {
            var node = root;
            int depth = a.Group.Depth;
            for (int lv = 0; lv < depth; lv++)
            {
                var name = AgvGroup.Norm(a.Group[lv]);
                var child = node.Children.FirstOrDefault(c => c.Name == name);
                if (child is null)
                {
                    child = new GroupNode { Level = lv, Name = name, Path = node.Path.Length == 0 ? name : node.Path + " / " + name };
                    node.Children.Add(child);
                }
                node = child;
            }
            node.Agvs.Add(a.Agv);
        }
        Finish(root);
        return root;
    }

    static void Finish(GroupNode n)
    {
        n.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var all = new SortedSet<int>(n.Agvs);
        foreach (var c in n.Children) { Finish(c); all.UnionWith(c.AllAgvs); }
        n.AllAgvs.Clear(); n.AllAgvs.AddRange(all);
    }

    public static GroupNode? Find(GroupNode root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return root;
        return root.Descendants().FirstOrDefault(n => n.Path == path.Trim());
    }

    /// <summary>경로 아래 전체 호기 (없는 경로면 빈 목록)</summary>
    public static IReadOnlyList<int> AgvsUnder(GroupNode root, string path) => Find(root, path)?.AllAgvs ?? [];
}

/// <summary>CSV 가져오기·내보내기: agv,제품군,층,라인,공정,라우트[,이름]. 첫 줄이 머리글이면 건너뜀, # 주석</summary>
public static class GroupCsv
{
    public const string Header = "agv,제품군,층,라인,공정,라우트,이름";

    public static List<AgvAssignment> Parse(string text, DateTimeOffset now, out List<string> errors)
    {
        errors = new();
        var list = new List<AgvAssignment>();
        int ln = 0;
        foreach (var raw in text.Split('\n'))
        {
            ln++;
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = Split(line);
            if (!int.TryParse(f[0], out var agv))
            {
                if (ln == 1 || f[0].Equals("agv", StringComparison.OrdinalIgnoreCase)) continue;   // 머리글
                errors.Add($"{ln}행: 호기 번호가 아님 '{f[0]}'"); continue;
            }
            if (agv is < 1 or > 99) { errors.Add($"{ln}행: 호기 번호는 1~99 ({agv})"); continue; }
            string At(int i) => i < f.Length ? f[i] : "";
            list.Add(new AgvAssignment { Agv = agv, Group = AgvGroup.Trimmed(At(1), At(2), At(3), At(4), At(5)), Name = At(6), Source = "import", UpdatedAt = now });
        }
        return list;
    }

    public static string Format(IEnumerable<AgvAssignment> items)
    {
        var sb = new System.Text.StringBuilder(Header + "\n");
        foreach (var a in items.OrderBy(x => x.Agv))
            sb.Append(a.Agv).Append(',').Append(Q(a.Group.ProductFamily)).Append(',').Append(Q(a.Group.Floor)).Append(',').Append(Q(a.Group.Line))
              .Append(',').Append(Q(a.Group.Process)).Append(',').Append(Q(a.Group.Route)).Append(',').Append(Q(a.Name)).Append('\n');
        return sb.ToString();
    }

    static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>탭 구분이면 탭으로, 아니면 쉼표로. 쉼표 모드는 큰따옴표 묶음("" 이스케이프) 지원</summary>
    static string[] Split(string line)
    {
        if (line.Contains('\t')) return line.Split('\t').Select(x => x.Trim()).ToArray();
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { fields.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString().Trim());
        return fields.ToArray();
    }
}
