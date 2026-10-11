using System.Text.Json;
using Gateway.Core.Groups;
using Gateway.Server.Data;
using Microsoft.Data.Sqlite;

namespace Gateway.Server.Services;

/// <summary>
/// 호기 소속 저장소 (SQLite agv_groups 표. 예전 agv_groups.json 은 첫 실행 때 옮긴다). ACS 에서 받은 값(acs)과 화면·CSV 로 넣은 값(manual/import)을 함께 둔다.
/// 트리는 바뀔 때마다 다시 만든다 (호기 수백 대 규모라 비용 없음).
/// </summary>
public sealed class AgvGroupStore
{
    readonly GatewayDb _db;
    readonly object _lock = new();
    readonly Dictionary<int, AgvAssignment> _items = new();
    GroupNode? _tree;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public event Action? Changed;

    /// <summary>기동 때 DB 읽기·예전 JSON 이전에서 난 오류 (없으면 null). 서비스는 그대로 뜨고, AcsInventoryPullService 가 로그에 남기며 구역 화면에 보인다</summary>
    public string? StartupError { get; private set; }

    public AgvGroupStore(string dataDir, GatewayDb db)
    {
        _db = db;
        try
        {
            using var c = _db.Open();
            using var cmd = GatewayDb.Cmd(c, "SELECT agv, name, product_family, floor, line, process, route, source, updated_at, note FROM agv_groups");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                _items[r.GetInt32(0)] = new AgvAssignment
                {
                    Agv = r.GetInt32(0), Name = r.GetString(1), Group = new AgvGroup(r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)),
                    Source = r.GetString(7), UpdatedAt = GatewayDb.Dto(r.GetString(8)), Note = r.GetString(9),
                };
        }
        catch (Exception e) { StartupError = "소속 DB 읽기 실패: " + e.Message; return; }
        var json = Path.Combine(dataDir, "agv_groups.json");
        if (File.Exists(json)) MigrateJson(json);
    }

    /// <summary>예전 agv_groups.json → DB. 한 트랜잭션으로 넣고 커밋된 뒤에만 .migrated 로 이름을 바꾼다.
    /// 어떤 실패든 예외를 밖으로 내지 않고 원본을 그대로 둔다 (다음 기동 때 다시 시도 — DB 에 이미 있는 호기는 건너뜀)</summary>
    void MigrateJson(string json)
    {
        List<AgvAssignment> add;
        try
        {
            var old = JsonSerializer.Deserialize<List<AgvAssignment>>(File.ReadAllText(json), Json) ?? new();
            add = old.Where(a => a is not null && a.Agv is >= 1 and <= 99 && !_items.ContainsKey(a.Agv))
                     .GroupBy(a => a.Agv).Select(g => g.Last())
                     .Select(a => new AgvAssignment
                     {
                         Agv = a.Agv, Name = a.Name ?? "", Source = string.IsNullOrEmpty(a.Source) ? "manual" : a.Source, UpdatedAt = a.UpdatedAt, Note = a.Note ?? "",
                         Group = AgvGroup.Trimmed(a.Group?.ProductFamily, a.Group?.Floor, a.Group?.Line, a.Group?.Process, a.Group?.Route),
                     }).ToList();
        }
        catch (Exception e) { StartupError = $"예전 소속 파일 읽기 실패 — 원본 그대로 둠 ({json}): {e.Message}"; return; }
        try
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            foreach (var a in add) Insert(c, tx, a);
            tx.Commit();
        }
        catch (Exception e) { StartupError = $"예전 소속 {add.Count}대 DB 반영 실패 — 원본 그대로 두고 다음 기동 때 다시 시도 ({json}): {e.Message}"; return; }
        lock (_lock) { foreach (var a in add) _items[a.Agv] = a; _tree = null; }
        try { File.Move(json, json + ".migrated", true); }
        catch (Exception e) { StartupError = $"예전 소속 {add.Count}대 반영함 · 파일 이름 바꾸기 실패 (다음 기동 때 다시 시도, 이미 있는 호기는 건너뜀) ({json}): {e.Message}"; }
    }

    static void Insert(SqliteConnection c, SqliteTransaction? tx, AgvAssignment a)
    {
        using var cmd = GatewayDb.Cmd(c, """
            INSERT OR REPLACE INTO agv_groups(agv, name, product_family, floor, line, process, route, source, updated_at, note)
            VALUES($agv, $name, $pf, $fl, $ln, $pr, $rt, $src, $up, $note)
            """, ("$agv", a.Agv), ("$name", a.Name), ("$pf", a.Group.ProductFamily), ("$fl", a.Group.Floor), ("$ln", a.Group.Line),
            ("$pr", a.Group.Process), ("$rt", a.Group.Route), ("$src", a.Source), ("$up", GatewayDb.Ts(a.UpdatedAt)), ("$note", a.Note));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    void Write(AgvAssignment a)
    {
        _tree = null;
        using var c = _db.Open();
        Insert(c, null, a);
    }

    void Delete(int agv)
    {
        _tree = null;
        using var c = _db.Open();
        GatewayDb.Exec(c, "DELETE FROM agv_groups WHERE agv=$agv", ("$agv", agv));
    }

    public int Count { get { lock (_lock) return _items.Count; } }
    public AgvAssignment? Get(int agv) { lock (_lock) return _items.TryGetValue(agv, out var a) ? a : null; }
    public IReadOnlyList<AgvAssignment> Snapshot() { lock (_lock) return _items.Values.OrderBy(a => a.Agv).ToList(); }
    public GroupNode Tree { get { lock (_lock) return _tree ??= GroupTree.Build(_items.Values); } }
    public IReadOnlyList<int> AgvsUnder(string path) => GroupTree.AgvsUnder(Tree, path);

    /// <summary>화면·API 에서 한 대 설정 (출처 manual)</summary>
    public AgvAssignment Set(int agv, AgvGroup group, string? name = null, string note = "", string source = "manual")
    {
        var a = new AgvAssignment { Agv = agv, Group = group.Cleaned(), Name = name ?? Get(agv)?.Name ?? "", Note = note, Source = source, UpdatedAt = DateTimeOffset.UtcNow };
        lock (_lock) { _items[agv] = a; Write(a); }
        Notify();
        return a;
    }

    public bool Remove(int agv)
    {
        bool ok;
        lock (_lock) { ok = _items.Remove(agv); if (ok) Delete(agv); }
        if (ok) Notify();
        return ok;
    }

    /// <summary>CSV 가져오기: 목록의 호기만 덮어씀 (출처 import)</summary>
    public int Import(IEnumerable<AgvAssignment> items)
    {
        int n = 0;
        lock (_lock)
        {
            foreach (var a in items) { a.Source = "import"; a.Group = a.Group.Cleaned(); _items[a.Agv] = a; Write(a); n++; }
        }
        if (n > 0) Notify();
        return n;
    }

    /// <summary>ACS 응답 반영 (규칙은 <see cref="AcsMerge.Plan"/>). 반환 = (추가·변경, 삭제, 삭제 보류 호기)</summary>
    public (int changed, int removed, IReadOnlyList<int> held) MergeFromAcs(IReadOnlyList<AgvAssignment> fromAcs, bool overrideManual, bool removeMissing)
    {
        AcsMergePlan plan;
        lock (_lock)
        {
            foreach (var a in fromAcs) a.Group = a.Group.Cleaned();
            plan = AcsMerge.Plan(_items, fromAcs, overrideManual, removeMissing);
            foreach (var a in plan.Upserts) { _items[a.Agv] = a; Write(a); }
            foreach (var k in plan.Removes) { _items.Remove(k); Delete(k); }
        }
        if (plan.Upserts.Count + plan.Removes.Count > 0) Notify();
        return (plan.Upserts.Count, plan.Removes.Count, plan.Held);
    }

    /// <summary>트리에서 같은 경로가 둘 이상인 노드 (정상이면 빈 목록 — 구역 화면에 경고로 보임)</summary>
    public IReadOnlyList<string> DuplicatePaths => GroupTree.DuplicatePaths(Tree);

    void Notify() { try { Changed?.Invoke(); } catch { } }
}
