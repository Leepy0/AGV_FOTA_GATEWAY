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

    /// <summary>기동 때 소속을 다 읽지 못함 (DB 읽기 실패·읽지 못한 행·예전 JSON 미반영). 이때 ACS 병합은 막는다 —
    /// 메모리에 없는 수동 행을 ACS 값으로 덮어쓰거나 이전을 건너뛰게 만들 수 있어서. 원인을 고치고 서버를 다시 시작하면 풀린다</summary>
    public bool LoadIncomplete { get; private set; }

    public AgvGroupStore(string dataDir, GatewayDb db)
    {
        _db = db;
        var bad = new List<string>();
        try
        {
            using var c = _db.Open();
            using var cmd = GatewayDb.Cmd(c, "SELECT agv, name, product_family, floor, line, process, route, source, updated_at, note FROM agv_groups");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    _items[r.GetInt32(0)] = new AgvAssignment
                    {
                        Agv = r.GetInt32(0), Name = r.GetString(1), Group = new AgvGroup(r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)),
                        Source = r.GetString(7), UpdatedAt = GatewayDb.Dto(r.GetString(8)), Note = r.GetString(9),
                    };
                }
                catch (Exception e) when (e is not SqliteException)
                {
                    bad.Add($"{(r.IsDBNull(0) ? "?" : r.GetValue(0))}호기({e.Message})");   // 한 행이 깨져도 나머지는 읽는다
                }
            }
        }
        catch (Exception e) { Fail("소속 DB 읽기 실패: " + e.Message); return; }
        if (bad.Count > 0) Fail($"소속 {bad.Count}행 읽기 실패: {string.Join(", ", bad.Take(5))}");
        var json = Path.Combine(dataDir, "agv_groups.json");
        if (File.Exists(json)) MigrateJson(json);
    }

    /// <summary>로드 실패 기록 — ACS 병합을 막는다</summary>
    void Fail(string why)
    {
        LoadIncomplete = true;
        StartupError = (StartupError is null ? "" : StartupError + " · ") + why + " — ACS 소속 반영을 멈춤 (원인을 고치고 서버 재시작)";
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
        catch (Exception e) { Fail($"예전 소속 파일 읽기 실패 — 원본 그대로 둠 ({json}): {e.Message}"); return; }
        try
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            foreach (var a in add) Insert(c, tx, a);
            tx.Commit();
        }
        catch (Exception e) { Fail($"예전 소속 {add.Count}대 DB 반영 실패 — 원본 그대로 두고 다음 기동 때 다시 시도 ({json}): {e.Message}"); return; }
        lock (_lock) { foreach (var a in add) _items[a.Agv] = a; _tree = null; }
        try { File.Move(json, json + ".migrated", true); }
        catch (Exception e) { StartupError = (StartupError is null ? "" : StartupError + " · ") + $"예전 소속 {add.Count}대 반영함 · 파일 이름 바꾸기 실패 (다음 기동 때 다시 시도, 이미 있는 호기는 건너뜀) ({json}): {e.Message}"; }
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

    /// <summary>DB 에 한 트랜잭션으로 쓰기·지우기. 실패하면 예외 (메모리는 호출자가 커밋 뒤에만 바꾼다)</summary>
    void Save(IEnumerable<AgvAssignment> upserts, IEnumerable<int> removes)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var a in upserts) Insert(c, tx, a);
        foreach (var k in removes)
        {
            using var cmd = GatewayDb.Cmd(c, "DELETE FROM agv_groups WHERE agv=$agv", ("$agv", k));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public int Count { get { lock (_lock) return _items.Count; } }
    public AgvAssignment? Get(int agv) { lock (_lock) return _items.TryGetValue(agv, out var a) ? a : null; }
    public IReadOnlyList<AgvAssignment> Snapshot() { lock (_lock) return _items.Values.OrderBy(a => a.Agv).ToList(); }
    public GroupNode Tree { get { lock (_lock) return _tree ??= GroupTree.Build(_items.Values); } }
    public IReadOnlyList<int> AgvsUnder(string path) => GroupTree.AgvsUnder(Tree, path);

    /// <summary>화면·API 에서 한 대 설정 (출처 manual). DB 쓰기가 실패하면 예외 — 메모리는 그대로</summary>
    public AgvAssignment Set(int agv, AgvGroup group, string? name = null, string note = "", string source = "manual")
    {
        AgvAssignment a;
        lock (_lock)
        {
            a = new AgvAssignment { Agv = agv, Group = group.Cleaned(), Name = name ?? (_items.TryGetValue(agv, out var cur) ? cur.Name : ""), Note = note, Source = source, UpdatedAt = DateTimeOffset.UtcNow };
            Save([a], []);
            _items[agv] = a; _tree = null;
        }
        Notify();
        return a;
    }

    /// <summary>한 대 지우기. 없으면 false, DB 지우기가 실패하면 예외 — 메모리는 그대로</summary>
    public bool Remove(int agv)
    {
        lock (_lock)
        {
            if (!_items.ContainsKey(agv)) return false;
            Save([], [agv]);
            _items.Remove(agv); _tree = null;
        }
        Notify();
        return true;
    }

    /// <summary>CSV 가져오기: 목록의 호기만 덮어씀 (출처 import). 한 트랜잭션 — 실패하면 예외, 아무것도 반영 안 됨</summary>
    public int Import(IEnumerable<AgvAssignment> items)
    {
        var list = items.ToList();
        foreach (var a in list) { a.Source = "import"; a.Group = a.Group.Cleaned(); }
        if (list.Count == 0) return 0;
        lock (_lock)
        {
            Save(list, []);
            foreach (var a in list) _items[a.Agv] = a;
            _tree = null;
        }
        Notify();
        return list.Count;
    }

    /// <summary>ACS 응답 반영 (규칙은 <see cref="AcsMerge.Plan"/>). 반환 = (추가·변경, 삭제, 삭제 보류 호기).
    /// 기동 때 소속을 다 읽지 못했으면(<see cref="LoadIncomplete"/>) InvalidOperationException. DB 쓰기가 실패하면 예외 — 메모리는 그대로</summary>
    public (int changed, int removed, IReadOnlyList<int> held) MergeFromAcs(IReadOnlyList<AgvAssignment> fromAcs, bool overrideManual, bool removeMissing)
    {
        if (LoadIncomplete) throw new InvalidOperationException("소속 저장소를 다 읽지 못해 ACS 소속을 반영하지 않음: " + StartupError);
        AcsMergePlan plan;
        lock (_lock)
        {
            foreach (var a in fromAcs) a.Group = a.Group.Cleaned();
            plan = AcsMerge.Plan(_items, fromAcs, overrideManual, removeMissing);
            if (plan.Upserts.Count + plan.Removes.Count > 0)
            {
                Save(plan.Upserts, plan.Removes);
                foreach (var a in plan.Upserts) _items[a.Agv] = a;
                foreach (var k in plan.Removes) _items.Remove(k);
                _tree = null;
            }
        }
        if (plan.Upserts.Count + plan.Removes.Count > 0) Notify();
        return (plan.Upserts.Count, plan.Removes.Count, plan.Held);
    }

    /// <summary>트리에서 같은 경로가 둘 이상인 노드 (정상이면 빈 목록 — 구역 화면에 경고로 보임)</summary>
    public IReadOnlyList<string> DuplicatePaths => GroupTree.DuplicatePaths(Tree);

    void Notify() { try { Changed?.Invoke(); } catch { } }
}
