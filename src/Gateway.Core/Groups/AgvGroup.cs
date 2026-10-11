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
    /// <summary>단계가 전부 빈 소속의 경로. 트리에서도 같은 이름의 제품군 노드로 모인다 (값이 "미분류" 인 제품군과 같은 노드)</summary>
    public const string Unclassified = "미분류";
    /// <summary>경로 구분자. 단계 값 안의 '/' 는 전각 '／' 로 바꿔 두므로 값과 겹치지 않는다</summary>
    public const string Separator = " / ";
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
    public string Path => Depth == 0 ? Unclassified : string.Join(Separator, Enumerable.Range(0, Depth).Select(i => Norm(this[i])));
    /// <summary>앞 depth 단계만의 경로 (트리 노드 경로)</summary>
    public string PathTo(int depth) => depth <= 0 ? "" : string.Join(Separator, Enumerable.Range(0, Math.Min(depth, Levels)).Select(i => Norm(this[i])));

    /// <summary>트리 노드 이름: 빈 값은 (미지정), '/' 는 '／' (예전 DB 값도 경로가 겹치지 않게)</summary>
    public static string Norm(string? v) => string.IsNullOrWhiteSpace(v) ? Unassigned : Clean(v);
    /// <summary>저장 값 정리: 앞뒤 공백 제거 + '/' → '／' (화면·CSV·ACS·API 입력 공통)</summary>
    public static string Clean(string? v) => (v ?? "").Trim().Replace('/', '／');
    public static AgvGroup Trimmed(string? pf, string? fl, string? ln, string? pr, string? rt) =>
        new(Clean(pf), Clean(fl), Clean(ln), Clean(pr), Clean(rt));
    /// <summary>모든 단계에 <see cref="Clean"/> 을 적용한 사본</summary>
    public AgvGroup Cleaned() => Trimmed(ProductFamily, Floor, Line, Process, Route);
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
    /// <summary>소속 목록 → 트리. 빈 단계는 (미지정) 노드로, 전부 비면 "미분류" 노드(제품군 단계)로</summary>
    public static GroupNode Build(IEnumerable<AgvAssignment> items)
    {
        var root = new GroupNode { Level = -1, Name = "전체", Path = "" };
        foreach (var a in items.OrderBy(x => x.Agv))
        {
            var node = root;
            int depth = a.Group.Depth;
            // 전부 빈 소속은 표시 경로("미분류")와 같은 노드에 둔다 — 호기 현황의 '구역' 링크·배포 대상이 이 경로로 찾는다
            if (depth == 0) node = Child(root, 0, AgvGroup.Unclassified);
            for (int lv = 0; lv < depth; lv++) node = Child(node, lv, AgvGroup.Norm(a.Group[lv]));
            node.Agvs.Add(a.Agv);
        }
        Finish(root);
        return root;
    }

    static GroupNode Child(GroupNode node, int lv, string name)
    {
        var child = node.Children.FirstOrDefault(c => c.Name == name);
        if (child is null)
        {
            child = new GroupNode { Level = lv, Name = name, Path = node.Path.Length == 0 ? name : node.Path + AgvGroup.Separator + name };
            node.Children.Add(child);
        }
        return child;
    }

    /// <summary>같은 Path 를 가진 노드가 둘 이상인 경로 (정상이면 빈 목록 — 단계 값의 '/' 를 '／' 로 바꾸므로 생기지 않아야 함)</summary>
    public static IReadOnlyList<string> DuplicatePaths(GroupNode root) =>
        root.Descendants().GroupBy(n => n.Path).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

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
        foreach (var (ln, raw) in Records(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = Split(line);
            if (!int.TryParse(f[0], out var agv))
            {
                if (ln == 1 || f[0].Equals("agv", StringComparison.OrdinalIgnoreCase)) continue;   // 머리글
                errors.Add($"{ln}행: 호기 번호가 아님 '{f[0]}'"); continue;
            }
            if (agv is < 1 or > 99) { errors.Add($"{ln}행: 호기 번호는 1~99 ({agv})"); continue; }
            string At(int i) => i < f.Length ? Unguard(f[i]) : "";
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

    /// <summary>엑셀이 수식으로 읽는 첫 글자 (= + - @ 탭 CR)</summary>
    static bool FormulaStart(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    /// <summary>내보내기 값: 수식 첫 글자면 앞에 ' (엑셀 수식 실행 방지), 쉼표·따옴표·줄바꿈이 있으면 큰따옴표로 묶음</summary>
    static string Q(string s)
    {
        if (s.Length > 0 && FormulaStart(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\t', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>가져오기 값: 내보내기에서 붙인 ' 를 뗌 (' 뒤가 수식 첫 글자일 때만)</summary>
    static string Unguard(string s) => s.Length > 1 && s[0] == '\'' && FormulaStart(s[1]) ? s[1..] : s;

    /// <summary>줄 단위로 나눔 — 단 큰따옴표로 묶인 값 안의 줄바꿈은 값으로 둔다. (시작 행 번호, 내용)</summary>
    static IEnumerable<(int line, string text)> Records(string text)
    {
        var sb = new System.Text.StringBuilder();
        int ln = 1, start = 1;
        bool q = false, fieldStart = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n') ln++;
            if (q)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append(c); i++; } else q = false; }
                sb.Append(c);   // 원문 그대로 ("" 는 두 글자 다) — 값 해석은 Split 이 한다
                continue;
            }
            if (c == '\n')
            {
                yield return (start, sb.ToString().TrimEnd('\r'));
                sb.Clear(); start = ln; fieldStart = true;
                continue;
            }
            if (c == '"' && fieldStart) q = true;
            if (c is ',' or '\t') fieldStart = true;
            else if (c != ' ') fieldStart = false;
            sb.Append(c);
        }
        if (sb.Length > 0) yield return (start, sb.ToString().TrimEnd('\r'));
    }

    /// <summary>따옴표 밖에 탭이 있으면 탭 구분 (엑셀에서 복사해 붙인 경우)</summary>
    static bool HasBareTab(string line)
    {
        bool q = false;
        foreach (var c in line) { if (c == '"') q = !q; else if (c == '\t' && !q) return true; }
        return false;
    }

    /// <summary>탭 구분이면 탭으로, 아니면 쉼표로. 쉼표 모드는 큰따옴표 묶음("" 이스케이프, 값 안의 줄바꿈) 지원</summary>
    static string[] Split(string line)
    {
        if (HasBareTab(line)) return line.Split('\t').Select(x => x.Trim()).ToArray();
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
            else if (c == '"' && string.IsNullOrWhiteSpace(sb.ToString())) q = true;   // 값 첫머리의 따옴표만 묶음 시작
            else if (c == ',') { fields.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString().Trim());
        return fields.ToArray();
    }
}
