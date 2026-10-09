using System.Text.Json;
using Gateway.Core.Groups;

namespace Gateway.Server.Services;

/// <summary>
/// 호기 소속 저장소 ({DataDir}/agv_groups.json). ACS 에서 받은 값(acs)과 화면·CSV 로 넣은 값(manual/import)을 함께 둔다.
/// 트리는 바뀔 때마다 다시 만든다 (호기 수백 대 규모라 비용 없음).
/// </summary>
public sealed class AgvGroupStore
{
    readonly string _path;
    readonly object _lock = new();
    readonly Dictionary<int, AgvAssignment> _items = new();
    GroupNode? _tree;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public event Action? Changed;

    public AgvGroupStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "agv_groups.json");
        if (File.Exists(_path))
        {
            try
            {
                foreach (var a in JsonSerializer.Deserialize<List<AgvAssignment>>(File.ReadAllText(_path), Json) ?? new())
                    _items[a.Agv] = a;
            }
            catch (JsonException) { }
        }
    }

    public int Count { get { lock (_lock) return _items.Count; } }
    public AgvAssignment? Get(int agv) { lock (_lock) return _items.TryGetValue(agv, out var a) ? a : null; }
    public IReadOnlyList<AgvAssignment> Snapshot() { lock (_lock) return _items.Values.OrderBy(a => a.Agv).ToList(); }
    public GroupNode Tree { get { lock (_lock) return _tree ??= GroupTree.Build(_items.Values); } }
    public IReadOnlyList<int> AgvsUnder(string path) => GroupTree.AgvsUnder(Tree, path);

    /// <summary>화면·API 에서 한 대 설정 (출처 manual)</summary>
    public AgvAssignment Set(int agv, AgvGroup group, string? name = null, string note = "", string source = "manual")
    {
        var a = new AgvAssignment { Agv = agv, Group = group, Name = name ?? Get(agv)?.Name ?? "", Note = note, Source = source, UpdatedAt = DateTimeOffset.UtcNow };
        lock (_lock) { _items[agv] = a; Save(); }
        Notify();
        return a;
    }

    public bool Remove(int agv)
    {
        bool ok;
        lock (_lock) { ok = _items.Remove(agv); if (ok) Save(); }
        if (ok) Notify();
        return ok;
    }

    /// <summary>CSV 가져오기: 목록의 호기만 덮어씀 (출처 import)</summary>
    public int Import(IEnumerable<AgvAssignment> items)
    {
        int n = 0;
        lock (_lock)
        {
            foreach (var a in items) { a.Source = "import"; _items[a.Agv] = a; n++; }
            if (n > 0) Save();
        }
        if (n > 0) Notify();
        return n;
    }

    /// <summary>ACS 응답 반영. 반환 = (추가·변경, 삭제)</summary>
    public (int changed, int removed) MergeFromAcs(IReadOnlyList<AgvAssignment> fromAcs, bool overrideManual, bool removeMissing)
    {
        int changed = 0, removed = 0;
        lock (_lock)
        {
            var seen = new HashSet<int>();
            foreach (var a in fromAcs)
            {
                seen.Add(a.Agv);
                if (_items.TryGetValue(a.Agv, out var cur))
                {
                    if (cur.Source != "acs" && !overrideManual) continue;          // 직접 넣은 값은 유지
                    if (cur.Group == a.Group && cur.Name == a.Name && cur.Source == "acs") continue;
                }
                a.Source = "acs";
                _items[a.Agv] = a; changed++;
            }
            if (removeMissing)
                foreach (var k in _items.Where(kv => kv.Value.Source == "acs" && !seen.Contains(kv.Key)).Select(kv => kv.Key).ToList())
                { _items.Remove(k); removed++; }
            if (changed + removed > 0) Save();
        }
        if (changed + removed > 0) Notify();
        return (changed, removed);
    }

    void Save()
    {
        _tree = null;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_items.Values.OrderBy(a => a.Agv).ToList(), Json)); }
        catch (Exception) { }
    }
    void Notify() { try { Changed?.Invoke(); } catch { } }
}
