using System.Text.Json;
using Gateway.Core.Fota;
using Gateway.Server.Data;
using Gateway.Server.Services;

namespace Gateway.Server.Tests;

/// <summary>
/// 펌웨어 저장소: DB 실패 시 메모리·폴더 정합(S-06), 삭제와 lock 순서(S-08), 버전·파일명 규칙(C-02·C-09), 옛 index.json 이전(S-05), 변경 알림(U-10).
/// (서버 클래스라 Gateway.Server.Tests 로 옮김 — INT-A11)
/// </summary>
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
    public void IsActive_counts_every_unfinished_job_including_awaiting_approval()
    {
        // 펌웨어 '사용 중' 판정 = Job.IsActive 하나 (화면·REST·JobService 공통)
        Assert.True(new Job { State = JobState.Scheduled }.IsActive);
        Assert.True(new Job { State = JobState.Running }.IsActive);
        Assert.True(new Job { State = JobState.AwaitingApproval }.IsActive);
        Assert.False(new Job { State = JobState.Done }.IsActive);
        Assert.False(new Job { State = JobState.Failed }.IsActive);
        Assert.False(new Job { State = JobState.Cancelled }.IsActive);
    }

    static Task<FirmwareEntry> AddPkg(FirmwareStore st, byte[] pkg, string name = "AGV_Main_2.0.0.dsppkg", string version = "") =>
        st.AddAsync(FirmwareKind.Dsp, version, "", "", [("pkg", name, new MemoryStream(pkg))]);

    [Fact]
    public async Task Dsp_version_longer_than_8_is_rejected()
    {
        var (_, st) = New();
        // 9자 버전 (앱 버전 응답은 8자까지라 굽고 나서도 '버전 다름'이 됨) — 업로드 단계에서 거부
        var img = DspfwImage.BuildApp(new ushort[] { 1, 2, 3 }, "1.2.3-rc1");   // 형식은 정상, 버전만 규칙 위반
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AddPkg(st, DspPackageBuilder.Build(img)));
        Assert.Contains("8자 이하", ex.Message);
        Assert.Empty(st.List());
        Assert.Equal("", FwDirs());
    }

    [Fact]
    public async Task Bad_dspfw_in_package_message_names_format_crc_and_block_structure()
    {
        var (_, st) = New();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AddPkg(st, BrokenDspfwPackage()));
        Assert.Contains("블록 구조", ex.Message);
        Assert.Contains("dsp_pack.py bundle", ex.Message);
        Assert.Equal("", FwDirs());
    }

    /// <summary>manifest 는 깨진 바이트로 계산 (sha·크기는 맞고 .dspfw 자체가 틀림)</summary>
    static byte[] BrokenDspfwPackage()
    {
        var img = JobTestKit.DspImage("2.0.0");
        img[100] ^= 1;
        var outB = DspPackageBuilder.CoffOut(DspPackageBuilder.DefaultLinkTime);
        var mapB = System.Text.Encoding.Latin1.GetBytes(DspPackageBuilder.Map("app.out", DspPackageBuilder.DefaultLinkTime));
        var m = DspPackageBuilder.Manifest(img, "app_2.0.0.dspfw", outB, "app.out", mapB, "app.map", "time");
        m["ver"] = "2.0.0";
        return DspPackageBuilder.Zip([("manifest.json", DspPackageBuilder.ManifestBytes(m)), ("app_2.0.0.dspfw", img), ("app.out", outB), ("app.map", mapB)]);
    }

    // ── 메인보드 패키지(.dsppkg): .out·.map 을 늘 함께 보관 ──

    [Theory]
    [InlineData("app_2.0.0.dspfw")]
    [InlineData("app.bin")]
    [InlineData("app.zip")]
    public async Task Bare_dspfw_or_other_file_for_dsp_is_rejected_with_package_hint(string name)
    {
        var (_, st) = New();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            st.AddAsync(FirmwareKind.Dsp, "", "", "", [("dspfw", name, new MemoryStream(JobTestKit.DspImage("2.0.0")))]));
        Assert.Contains(".dsppkg", ex.Message);
        Assert.Contains("dsp_pack.py bundle", ex.Message);
        Assert.Empty(st.List());
        Assert.Equal("", FwDirs());
    }

    [Fact]
    public async Task Package_upload_stores_dspfw_out_map_and_package_with_slots()
    {
        var (db, st) = New();
        var pkg = JobTestKit.DspPackage("2.0.0", "AGV_Main");
        var (e, warns) = await st.AddCheckedAsync(FirmwareKind.Dsp, "", "", "", [("pkg", "AGV_Main_2.0.0.dsppkg", new MemoryStream(pkg))]);
        Assert.Empty(warns);
        Assert.Equal(("2.0.0", 16, "time", true), (e.Version, e.PkgId.Length, e.LinkCheck, e.HasOriginals));
        Assert.Equal(["dspfw", "out", "map", "pkg"], e.Files.Select(f => f.Slot));
        Assert.Equal(["AGV_Main_2.0.0.dspfw", "AGV_Main.out", "AGV_Main.map", "AGV_Main_2.0.0.dsppkg"], e.Files.Select(f => f.FileName));
        Assert.Equal("dspfw", e.Primary!.Slot);
        Assert.Equal(JobTestKit.DspImage("2.0.0"), File.ReadAllBytes(st.PathOf(e, e.Primary)));   // 브리지가 받는 파일 = 패키지 안 .dspfw 그대로
        Assert.Equal(pkg, File.ReadAllBytes(st.PathOf(e, e.FileOf("pkg")!)));
        foreach (var f in e.Files)
            Assert.Equal(f.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(st.PathOf(e, f)))));
        Assert.Equal(DspPackage.Read(pkg).Package!.PkgId, e.PkgId);

        // DB: firmware 행에는 주 파일(이전 서버도 읽음), 나머지는 firmware_files
        using (var c = db.Open())
        {
            using (var cmd = GatewayDb.Cmd(c, "SELECT slot, file_name, pkg_id, link_check FROM firmware WHERE id=$id", ("$id", e.Id)))
            using (var r = cmd.ExecuteReader())
            {
                Assert.True(r.Read());
                Assert.Equal(("dspfw", "AGV_Main_2.0.0.dspfw", e.PkgId, "time"), (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            }
            using (var cmd = GatewayDb.Cmd(c, "SELECT group_concat(slot, ',') FROM (SELECT slot FROM firmware_files WHERE firmware_id=$id ORDER BY slot)", ("$id", e.Id)))
                Assert.Equal("map,out,pkg", cmd.ExecuteScalar());
        }

        // 다시 읽어도 같은 파일·슬롯·원본 표시
        var again = Assert.Single(new FirmwareStore(_dir, db).List());
        Assert.Equal(e.Files.Select(f => (f.Slot, f.FileName, f.Size, f.Sha256)), again.Files.Select(f => (f.Slot, f.FileName, f.Size, f.Sha256)));
        Assert.Equal((e.PkgId, "time", true), (again.PkgId, again.LinkCheck, again.HasOriginals));
        Assert.Contains("2.0.0", st.VersionsWithOriginals());
    }

    [Fact]
    public async Task Package_warnings_and_same_package_duplicate_are_reported_not_rejected()
    {
        var (_, st) = New();
        var skipped = DspPackageBuilder.Build(JobTestKit.DspImage("2.0.0"), tzOffsetSec: 12345, linkCheck: "skipped");
        var (a, w1) = await st.AddCheckedAsync(FirmwareKind.Dsp, "", "", "", [("pkg", "a.dsppkg", new MemoryStream(skipped))]);
        Assert.Equal("skipped", a.LinkCheck);
        Assert.True(a.HasOriginals);
        Assert.Contains(w1, w => w.Contains("건너뛰"));
        var (b, w2) = await st.AddCheckedAsync(FirmwareKind.Dsp, "", "", "", [("pkg", "b.dsppkg", new MemoryStream(skipped))]);
        Assert.Equal(a.PkgId, b.PkgId);
        Assert.Contains(w2, w => w.Contains($"같은 패키지(ID {a.PkgId})"));
        Assert.Equal(2, st.List().Count);
    }

    [Fact]
    public async Task Package_rejections_leave_no_folder_or_row()
    {
        var (_, st) = New();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AddPkg(st, JobTestKit.DspPackage("2.0.0"), version: "2.0.1"));
        Assert.Contains("패키지 버전 2.0.0", ex.Message);
        var noMap = DspPackageBuilder.Zip([("manifest.json", "{}"u8.ToArray())]);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AddPkg(st, noMap, "x.dsppkg"));
        Assert.Contains("x.dsppkg: 패키지에 .dspfw 이 없습니다", ex.Message);
        Assert.Empty(st.List());
        Assert.Equal("", FwDirs());
    }

    [Fact]
    public async Task Short_package_name_is_replaced_and_url_stays_short()
    {
        var (_, st) = New();
        var e = await AddPkg(st, JobTestKit.DspPackage("2.0.0", new string('a', 48)), "한글.dsppkg");   // .dspfw 항목 이름 60자
        Assert.All(e.Files, f => Assert.True(f.FileName.Length <= FirmwareStore.MaxFileName, f.FileName));
        Assert.EndsWith(".dsppkg", e.FileOf("pkg")!.FileName);
        Assert.True(e.FileOf("pkg")!.FileName.Length > 10);
        var url = $"http://255.255.255.255:65535/fw/{e.Id}/{e.Primary!.FileName}";
        Assert.True(url.Length <= 199, url.Length.ToString());
    }

    [Fact]
    public async Task Delete_removes_both_tables_and_folder()
    {
        var (db, st) = New();
        var e = await AddPkg(st, JobTestKit.DspPackage("2.0.0"));
        Assert.Equal(3, db.Count("firmware_files"));
        Assert.True(st.Delete(e.Id));
        Assert.Equal(0, db.Count("firmware_files"));
        Assert.Equal(0, db.Count("firmware"));
        Assert.Equal("", FwDirs());
        Assert.Empty(new FirmwareStore(_dir, db).List());
    }

    [Fact]
    public async Task Failed_second_table_insert_rolls_back_the_firmware_row()
    {
        var (db, st) = New();
        Exec(db, "CREATE TRIGGER no_files BEFORE INSERT ON firmware_files BEGIN SELECT RAISE(FAIL, 'disk full'); END");
        await Assert.ThrowsAnyAsync<Exception>(() => AddPkg(st, JobTestKit.DspPackage("2.0.0")));
        Assert.Empty(st.List());
        Assert.Equal(0, db.Count("firmware"));   // 한 트랜잭션 — firmware 행도 남지 않음
        Assert.Equal("", FwDirs());
    }

    [Fact]
    public async Task Esp_entry_keeps_single_file_and_no_originals_flag()
    {
        var (db, st) = New();
        var e = await AddEsp(st);
        Assert.Equal(("esp", "", false), (e.Primary!.Slot, e.PkgId, e.HasOriginals));
        Assert.Single(e.Files);
        Assert.Equal(0, db.Count("firmware_files"));
    }

    [Theory]
    [InlineData("agv_wifi_bridge.bin", "agv_wifi_bridge.bin")]
    [InlineData("../../evil name.bin", "evil_name.bin")]
    [InlineData("한글.dspfw", "dspfw")]
    [InlineData("AGV Main 1.2.3.dsppkg", "AGV_Main_1.2.3.dsppkg")]
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
