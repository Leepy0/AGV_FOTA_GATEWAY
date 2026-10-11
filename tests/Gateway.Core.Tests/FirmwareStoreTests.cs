using System.Text.Json;
using Gateway.Core.Fota;
using Gateway.Server.Data;
using Gateway.Server.Services;

namespace Gateway.Core.Tests;

/// <summary>펌웨어 저장소: DB 실패 시 메모리·폴더 정합(S-06), 삭제와 lock 순서(S-08), 버전·파일명 규칙(C-02·C-09), 옛 index.json 이전(S-05), 변경 알림(U-10)</summary>
public class FirmwareStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gw-fw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    (GatewayDb db, FirmwareStore st) New() { var db = new GatewayDb(_dir); return (db, new FirmwareStore(_dir, db)); }

    static Task<FirmwareEntry> AddEsp(FirmwareStore st, string ver = "0.5.2", string name = "agv_wifi_bridge.bin") =>
        st.AddAsync(FirmwareKind.Esp, "", "", "", [("esp", name, new MemoryStream(EspAppImage.Build(ver, EspAppImage.BridgeProject)))]);

    static void Exec(GatewayDb db, string sql) { using var c = db.Open(); GatewayDb.Exec(c, sql); }

    string FwDirs() => string.Join(",", Directory.GetDirectories(Path.Combine(_dir, "firmware")).Select(Path.GetFileName));

    [Fact]
    public async Task Add_then_reload_and_changed_event()
    {
        var (db, st) = New();
        int changed = 0;
        st.Changed += () => changed++;
        var e = await AddEsp(st);
        Assert.Equal(1, changed);
        Assert.Equal("0.5.2", e.Version);
        Assert.True(File.Exists(st.PathOf(e, e.Files[0])));
        var again = new FirmwareStore(_dir, db);
        Assert.Equal(e.Id, Assert.Single(again.List()).Id);
    }

    [Fact]
    public async Task Add_db_failure_cleans_folder_and_keeps_memory_empty()
    {
        var (db, st) = New();
        Exec(db, "DROP TABLE firmware");
        await Assert.ThrowsAnyAsync<Exception>(() => AddEsp(st));
        Assert.Empty(st.List());
        Assert.Equal("", FwDirs());   // 고아 폴더 없음
    }

    [Fact]
    public async Task Delete_db_failure_keeps_entry_in_memory()
    {
        var (db, st) = New();
        var e = await AddEsp(st);
        Exec(db, "DROP TABLE firmware");
        Assert.ThrowsAny<Exception>(() => st.Delete(e.Id));
        Assert.NotNull(st.Get(e.Id));                       // DB 와 어긋나지 않게 메모리는 그대로
        Assert.True(File.Exists(st.PathOf(e, e.Files[0]))); // 파일도 그대로
    }

    [Fact]
    public async Task Delete_removes_row_memory_and_folder_then_notifies()
    {
        var (db, st) = New();
        var e = await AddEsp(st);
        int changed = 0;
        st.Changed += () => changed++;
        Assert.True(st.Delete(e.Id));
        Assert.Null(st.Get(e.Id));
        Assert.Equal(1, changed);
        Assert.Equal("", FwDirs());
        Assert.Empty(new FirmwareStore(_dir, db).List());   // DB 에서도 지워짐
        Assert.False(st.Delete(e.Id));                       // 없으면 false, 알림 없음
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Changed_is_raised_outside_the_store_lock()
    {
        // JobService 가 작업 lock 안에서 Delete 를 부르고, 화면이 그 알림에서 다른 스레드로 목록을 다시 읽어도 막히지 않아야 한다
        var (_, st) = New();
        var listed = new List<bool>();
        st.Changed += () => listed.Add(Task.Run(() => st.List()).Wait(TimeSpan.FromSeconds(5)));
        var e = await AddEsp(st);
        Assert.True(st.Delete(e.Id));
        Assert.Equal([true, true], listed);
    }

    [Fact]
    public void InUseBy_counts_every_unfinished_job()
    {
        Assert.True(FirmwareStore.InUseBy(new Job { State = JobState.Scheduled }));
        Assert.True(FirmwareStore.InUseBy(new Job { State = JobState.Running }));
        Assert.False(FirmwareStore.InUseBy(new Job { State = JobState.Done }));
        Assert.False(FirmwareStore.InUseBy(new Job { State = JobState.Failed }));
        Assert.False(FirmwareStore.InUseBy(new Job { State = JobState.Cancelled }));
    }

    [Fact]
    public async Task Dsp_version_longer_than_8_is_rejected()
    {
        var (_, st) = New();
        // 9자 버전 (앱 버전 응답은 8자까지라 굽고 나서도 '버전 다름'이 됨) — 업로드 단계에서 거부
        var img = DspfwImage.BuildApp(new ushort[] { 1, 2, 3 }, "1.2.3-rc1");   // 형식은 정상, 버전만 규칙 위반
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            st.AddAsync(FirmwareKind.Dsp, "", "", "", [("dspfw", "a.dspfw", new MemoryStream(img))]));
        Assert.Empty(st.List());
        Assert.Equal("", FwDirs());
    }

    [Theory]
    [InlineData("agv_wifi_bridge.bin", "agv_wifi_bridge.bin")]
    [InlineData("../../evil name.bin", "evil_name.bin")]
    [InlineData("한글.dspfw", "dspfw")]
    public void SafeName_cleans(string input, string expected) => Assert.Equal(expected, FirmwareStore.SafeName(input));

    [Fact]
    public void SafeName_limits_length_and_keeps_extension()
    {
        var n = FirmwareStore.SafeName(new string('a', 300) + ".dspfw");
        Assert.True(n.Length <= FirmwareStore.MaxFileName);
        Assert.EndsWith(".dspfw", n);
        // 스테이징 URL 최장 예: http://255.255.255.255:65535/fw/{id 22자}/{파일명} ≤ 199
        var url = $"http://255.255.255.255:65535/fw/20261011-120000-abcdef/{n}";
        Assert.True(url.Length <= 199, url.Length.ToString());
    }

    [Fact]
    public async Task Long_upload_name_is_shortened_on_disk()
    {
        var (_, st) = New();
        var e = await AddEsp(st, name: new string('b', 200) + ".bin");
        Assert.True(e.Files[0].FileName.Length <= FirmwareStore.MaxFileName);
        Assert.True(File.Exists(st.PathOf(e, e.Files[0])));
    }

    [Fact]
    public void Old_index_json_is_migrated_and_renamed()
    {
        var idx = Path.Combine(_dir, "firmware", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(idx)!);
        File.WriteAllText(idx, JsonSerializer.Serialize(new[] { OldEntry("a"), OldEntry("b") }));
        var (_, st) = New();
        Assert.Equal(2, st.List().Count);
        Assert.False(File.Exists(idx));
        Assert.True(File.Exists(idx + ".migrated"));
    }

    [Fact]
    public void Old_index_json_failure_keeps_original_and_does_not_throw()
    {
        var idx = Path.Combine(_dir, "firmware", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(idx)!);
        File.WriteAllText(idx, JsonSerializer.Serialize(new[] { OldEntry("a") }));
        var db = new GatewayDb(_dir);
        // 표는 읽히지만 쓰기는 실패하게 (삽입 거부 트리거)
        Exec(db, "CREATE TRIGGER no_ins BEFORE INSERT ON firmware BEGIN SELECT RAISE(FAIL, 'disk full'); END");
        var st = new FirmwareStore(_dir, db);
        Assert.Empty(st.List());
        Assert.True(File.Exists(idx));               // 원본 유지 → 다음 기동 때 다시 시도
        Assert.False(File.Exists(idx + ".migrated"));
        Exec(db, "DROP TRIGGER no_ins");
        Assert.Single(new FirmwareStore(_dir, db).List());
        Assert.True(File.Exists(idx + ".migrated"));
    }

    static FirmwareEntry OldEntry(string id) => new()
    {
        Id = id, Kind = FirmwareKind.Esp, Version = "0.4." + id.Length, Project = "agv_wifi_bridge", UploadedAt = DateTime.UtcNow,
        Files = [new FirmwareFile { Slot = "esp", FileName = "agv_wifi_bridge.bin", Size = 1, Sha256 = "00" }],
    };
}
