using Gateway.Core.Groups;
using Gateway.Server.Data;
using Gateway.Server.Services;

namespace Gateway.Core.Tests;

/// <summary>호기 소속 저장소 (INT-B1): DB 쓰기가 성공한 뒤에만 메모리를 바꾸고, 기동 때 다 읽지 못하면 ACS 병합을 막는다</summary>
public class AgvGroupStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gw-grp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    static void Exec(GatewayDb db, string sql) { using var c = db.Open(); GatewayDb.Exec(c, sql); }
    static AgvAssignment Acs(int agv, string pf) => new() { Agv = agv, Group = new AgvGroup(pf), UpdatedAt = DateTimeOffset.UtcNow };
    const string FailWrites = """
        CREATE TRIGGER no_ins BEFORE INSERT ON agv_groups BEGIN SELECT RAISE(FAIL, 'disk full'); END;
        CREATE TRIGGER no_del BEFORE DELETE ON agv_groups BEGIN SELECT RAISE(FAIL, 'disk full'); END;
        """;

    [Fact]
    public void Failed_db_write_leaves_memory_unchanged()
    {
        var db = new GatewayDb(_dir);
        var st = new AgvGroupStore(_dir, db);
        st.Set(7, new AgvGroup("A", "2F"));
        int changed = 0;
        st.Changed += () => changed++;
        Exec(db, FailWrites);

        Assert.ThrowsAny<Exception>(() => st.Set(7, new AgvGroup("B")));
        Assert.ThrowsAny<Exception>(() => st.Set(8, new AgvGroup("B")));
        Assert.ThrowsAny<Exception>(() => st.Remove(7));
        Assert.ThrowsAny<Exception>(() => st.Import([Acs(9, "C"), Acs(10, "C")]));
        Assert.ThrowsAny<Exception>(() => st.MergeFromAcs([Acs(11, "D")], false, false));
        Assert.Equal(0, changed);
        Assert.Equal(new[] { 7 }, st.Snapshot().Select(a => a.Agv));
        Assert.Equal("A", st.Get(7)!.Group.ProductFamily);
        Assert.Equal(new[] { 7 }, st.AgvsUnder(""));                    // 트리도 그대로
        Assert.False(st.Remove(99));                                     // 없는 호기는 DB 를 건드리지 않고 false

        Exec(db, "DROP TRIGGER no_ins; DROP TRIGGER no_del;");
        Assert.Equal(2, st.Import([Acs(9, "C"), Acs(10, "C")]));
        Assert.True(st.Remove(7));
        var re = new AgvGroupStore(_dir, db);                            // 메모리와 DB 가 같은지
        Assert.Equal(new[] { 9, 10 }, re.Snapshot().Select(a => a.Agv));
        Assert.Equal("import", re.Get(9)!.Source);
    }

    [Fact]
    public void Import_is_all_or_nothing()
    {
        var db = new GatewayDb(_dir);
        var st = new AgvGroupStore(_dir, db);
        Exec(db, "CREATE TRIGGER no_10 BEFORE INSERT ON agv_groups WHEN NEW.agv = 10 BEGIN SELECT RAISE(FAIL, 'bad'); END");
        Assert.ThrowsAny<Exception>(() => st.Import([Acs(9, "C"), Acs(10, "C")]));
        Assert.Equal(0, st.Count);
        Assert.Equal(0, db.Count("agv_groups"));
    }

    [Fact]
    public void Partial_load_blocks_acs_merge_and_keeps_unread_manual_rows()
    {
        var db = new GatewayDb(_dir);
        var st = new AgvGroupStore(_dir, db);
        st.Set(1, new AgvGroup("수동1"));
        st.Set(7, new AgvGroup("수동7"));
        Exec(db, "UPDATE agv_groups SET updated_at='깨진 값' WHERE agv=7");   // 7호기 행을 읽지 못하게

        var re = new AgvGroupStore(_dir, db);
        Assert.True(re.LoadIncomplete);
        Assert.Contains("7호기", re.StartupError);
        Assert.Equal(new[] { 1 }, re.Snapshot().Select(a => a.Agv));     // 읽을 수 있는 행은 읽는다
        var ex = Assert.Throws<InvalidOperationException>(() => re.MergeFromAcs([Acs(7, "ACS값"), Acs(8, "ACS값")], false, true));
        Assert.Contains("반영하지 않음", ex.Message);
        using (var c = db.Open())
        using (var cmd = GatewayDb.Cmd(c, "SELECT product_family, source FROM agv_groups WHERE agv=7"))
        using (var r = cmd.ExecuteReader())
        {
            Assert.True(r.Read());
            Assert.Equal(("수동7", "manual"), (r.GetString(0), r.GetString(1)));   // 덮어쓰지 않음
        }
        Assert.Equal(2, db.Count("agv_groups"));

        // 화면에서 직접 고치는 건 된다 (운용자가 명시적으로 덮어씀)
        re.Set(7, new AgvGroup("다시"));
        Assert.Equal("다시", re.Get(7)!.Group.ProductFamily);
    }

    [Fact]
    public void Failed_json_migration_blocks_acs_merge()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "agv_groups.json"), "{ 깨진 json");
        var db = new GatewayDb(_dir);
        var st = new AgvGroupStore(_dir, db);
        Assert.True(st.LoadIncomplete);
        Assert.Throws<InvalidOperationException>(() => st.MergeFromAcs([Acs(7, "A")], false, false));
        Assert.True(File.Exists(Path.Combine(_dir, "agv_groups.json")));   // 원본 그대로
    }

    [Fact]
    public void Merge_writes_db_then_memory_when_load_was_complete()
    {
        var db = new GatewayDb(_dir);
        var st = new AgvGroupStore(_dir, db);
        Assert.False(st.LoadIncomplete);
        st.Set(5, new AgvGroup("수동"));
        var (changed, removed, held) = st.MergeFromAcs([Acs(5, "ACS"), Acs(6, "ACS")], false, true);
        Assert.Equal((1, 0, 0), (changed, removed, held.Count));
        var re = new AgvGroupStore(_dir, db);
        Assert.Equal("수동", re.Get(5)!.Group.ProductFamily);
        Assert.Equal("acs", re.Get(6)!.Source);
    }
}
