using System.Diagnostics;
using Gateway.Core.Fota;
using Gateway.Server.Data;
using Gateway.Server.Services;

namespace Gateway.Server.Tests;

/// <summary>
/// 저장 실패 재시도(R4·R10), 펌웨어 lock 밖 DB(R5), 적용 대기 전환과 취소의 엇갈림(R6), 취소 뒤 결과 판정(R7)
/// </summary>
public class JobSaveAndCancelTests
{
    static void Sql(JobTestKit k, string sql) { using var c = k.Db.Open(); GatewayDb.Exec(c, sql); }

    static string FailOnState(string state) =>
        $"CREATE TRIGGER fail_{state} BEFORE INSERT ON jobs WHEN NEW.state='{state}' BEGIN SELECT RAISE(FAIL, 'database is locked'); END";

    static async Task Until(Func<bool> cond, string what, int ms = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    // ── R4: 마지막 저장이 실패해도 Tick 이 다시 써서 재시작 뒤 되살아나지 않음 ──

    [Fact]
    public void Cancelled_schedule_does_not_resurrect_after_failed_save_and_restart()
    {
        using var k = new JobTestKit();
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], DateTime.UtcNow.AddHours(1), true);
        Sql(k, FailOnState("Cancelled"));
        Assert.True(k.Svc.Cancel(j.Id));
        Assert.Equal(JobState.Cancelled, j.State);
        Assert.Equal(1, k.Svc.UnsavedCount);
        k.Svc.Tick();                                    // 아직 잠김 — 재시도 실패해도 계속
        Assert.Equal(1, k.Svc.UnsavedCount);
        Sql(k, "DROP TRIGGER fail_Cancelled");
        k.Svc.Tick();
        Assert.Equal(0, k.Svc.UnsavedCount);
        var again = k.Restart().Get(j.Id)!;
        Assert.Equal(JobState.Cancelled, again.State);
    }

    [Fact]
    public async Task Final_done_state_is_written_after_failed_save()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp("2.0.0");
        Sql(k, FailOnState("Done"));
        var j = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate);
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.Done, j.State);
        Assert.Equal(1, k.Svc.UnsavedCount);
        Sql(k, "DROP TRIGGER fail_Done");
        k.Svc.Tick();
        var again = k.Restart().Get(j.Id)!;
        Assert.Equal(JobState.Done, again.State);        // '서버 재시작' 실패로 바뀌지 않음
        Assert.Equal(TargetState.Done, again.Results[0].State);
    }

    [Fact]
    public async Task Awaiting_approval_transition_is_written_after_failed_save()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp();
        Sql(k, FailOnState("AwaitingApproval"));
        var j = k.Svc.Create("", fw.Id, [7], null, true);
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.AwaitingApproval, j.State);
        Sql(k, "DROP TRIGGER fail_AwaitingApproval");
        k.Svc.Tick();
        var again = k.Restart().Get(j.Id)!;
        Assert.Equal(JobState.AwaitingApproval, again.State);   // 스테이징이 버려지지 않음
        Assert.Equal(TargetState.Staged, again.Results[0].State);
    }

    [Fact]
    public void Jobs_json_is_kept_until_every_migrated_job_is_saved()
    {
        // S-05: 이전한 작업을 DB 에 못 쓴 채 원본 이름을 바꾸면, 재시도 전에 서비스가 멈출 때 옛 작업 기록이 사라진다
        using var k = new JobTestKit();
        var json = Path.Combine(k.Dir, "jobs.json");
        var old = new[] { new Job { Id = "old1", Name = "옛 작업", FirmwareId = "f", Kind = FirmwareKind.Dsp, FirmwareVersion = "1.0.0",
                                    Targets = [7], State = JobState.Done, CreatedAt = DateTime.UtcNow.AddDays(-1) } };
        File.WriteAllText(json, System.Text.Json.JsonSerializer.Serialize(old));
        Sql(k, "CREATE TRIGGER no_insert BEFORE INSERT ON jobs BEGIN SELECT RAISE(FAIL, 'database is locked'); END");
        k.Restart();
        Assert.True(File.Exists(json));
        Assert.False(File.Exists(json + ".migrated"));
        Sql(k, "DROP TRIGGER no_insert");
        k.Restart();   // 다음 기동: DB 에 없는 작업을 다시 옮기고 그때 원본 이름을 바꾼다
        Assert.False(File.Exists(json));
        Assert.True(File.Exists(json + ".migrated"));
        Assert.Equal(JobState.Done, k.Restart().Get("old1")?.State);
    }

    [Fact]
    public void Failed_delete_is_retried_so_record_does_not_come_back()
    {
        using var k = new JobTestKit();
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], DateTime.UtcNow.AddHours(1), true);
        k.Svc.Cancel(j.Id);
        Sql(k, "CREATE TRIGGER no_delete BEFORE DELETE ON jobs BEGIN SELECT RAISE(FAIL, 'database is locked'); END");
        Assert.True(k.Svc.Delete(j.Id));
        Assert.Equal(1, k.Svc.UnsavedCount);
        Sql(k, "DROP TRIGGER no_delete");
        k.Svc.Tick();
        Assert.Equal(0, k.Svc.UnsavedCount);
        Assert.Null(k.Restart().Get(j.Id));
    }

    [Fact]
    public async Task Newer_save_clears_older_pending_copy()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp("2.0.0");
        Sql(k, FailOnState("Scheduled"));
        var j = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate);   // 생성 저장 실패
        Assert.Equal(1, k.Svc.UnsavedCount);
        Sql(k, "DROP TRIGGER fail_Scheduled");
        await k.TickAndWaitAsync();                     // 실행 중·완료 저장이 더 새 복사본
        k.Svc.Tick();
        Assert.Equal(0, k.Svc.UnsavedCount);
        Assert.Equal(JobState.Done, k.Restart().Get(j.Id)!.State);   // 옛 Scheduled 복사본이 덮어쓰지 않음
    }

    // ── R5: 펌웨어 저장소는 lock 을 쥔 채 DB 를 기다리지 않음 → 작업 lock 도 막히지 않음 ──

    [Fact]
    public async Task Firmware_delete_waiting_on_db_does_not_block_job_lock()
    {
        using var k = new JobTestKit();
        var a = k.AddDsp("1.0.0");
        var b = k.AddDsp("1.0.1");
        Task<bool> del;
        Task<Job> create;
        Task<FirmwareEntry> add;
        using (var locker = k.Db.Open())
        {
            using var tx = locker.BeginTransaction();
            GatewayDb.Exec(locker, "DELETE FROM jobs WHERE id='x'");   // 쓰기 잠금
            del = Task.Run(() => k.Svc.DeleteFirmwareIfUnused(a.Id, out _));
            add = Task.Run(() => k.Store.AddAsync(FirmwareKind.Dsp, "", "", "", [("dspfw", "c.dspfw", new MemoryStream(JobTestKit.DspImage("1.0.2")))]));
            await Task.Delay(300);
            create = Task.Run(() => k.Svc.Create("", b.Id, [7], DateTime.UtcNow.AddHours(1), true));   // 작업 lock 안에서 펌웨어 조회
            await Task.Delay(300);
            var sw = Stopwatch.StartNew();
            var list = k.Svc.List();
            Assert.True(sw.ElapsedMilliseconds < 500, $"List {sw.ElapsedMilliseconds}ms");
            Assert.Single(list);
            sw.Restart();
            Assert.NotNull(k.Store.Get(b.Id));
            Assert.True(sw.ElapsedMilliseconds < 500, $"Get {sw.ElapsedMilliseconds}ms");
            Assert.False(del.IsCompleted);                // DB 를 기다리는 중
            tx.Rollback();
        }
        Assert.True(await del.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(k.Store.Get(a.Id));
        Assert.Equal(JobState.Scheduled, (await create.WaitAsync(TimeSpan.FromSeconds(10))).State);
        Assert.NotNull(k.Store.Get((await add.WaitAsync(TimeSpan.FromSeconds(10))).Id));
    }

    // ── R6: 스테이징이 끝나 적용 대기로 넘어가는 순간의 취소 ──

    [Fact]
    public async Task Cancel_at_stage_end_is_not_lost()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], null, true);
        bool cancelled = false;
        k.Svc.BeforePark = job => cancelled = k.Svc.Cancel(job.Id);   // 취소 확인을 지난 직후에 들어온 취소
        await k.TickAndWaitAsync();
        Assert.True(cancelled);
        Assert.Equal(JobState.Cancelled, j.State);
        Assert.Equal(TargetState.Skipped, j.Results[0].State);
        Assert.Contains("스테이징만 됨", j.Results[0].Message);
        Assert.Null(k.Svc.AwaitingJob());
        Assert.Equal(JobState.Cancelled, k.Restart().Get(j.Id)!.State);
    }

    // ── R7: 취소 뒤 결과는 버전만 보지 않고 상태 변화로 판정 ──

    static async Task<(JobTestKit k, Job j)> CancelAfterApplyAsync(string dspBefore, Action<FakeBridges> setup)
    {
        var k = new JobTestKit();
        k.Link.Add(7, dsp: dspBefore);
        setup(k.Link);
        k.Svc.FollowUpNoApplyWait = TimeSpan.FromSeconds(1);
        k.Svc.FollowUpTimeout = TimeSpan.FromSeconds(4);
        var fw = k.AddDsp("2.0.0");
        var j = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate);
        k.Svc.Tick();
        await Until(() => j.Results[0].Committed, "APPLY 보냄");
        await Task.Delay(100);
        k.Svc.Cancel(j.Id);
        await k.Svc.LastRun.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(JobState.Cancelled, j.State);
        Assert.Contains("브리지는 계속 진행", j.Results[0].Message);
        return (k, j);
    }

    [Fact]
    public async Task Reinstall_cancel_with_lost_apply_is_not_recorded_as_upgraded()
    {
        var (k, j) = await CancelAfterApplyAsync("2.0.0", l => l.DeafApply = true);   // 같은 버전, APPLY 가 닿지 않음
        using (k)
        {
            await Until(() => j.Results[0].Message.Contains("적용 안 됨"), "적용 안 됨 기록");
            Assert.Contains("스테이징 유지", j.Results[0].Message);
            Assert.DoesNotContain(j.Results[0].Log, l => l.Contains("올라감"));
            Assert.Equal("", j.Results[0].VersionAfter);
        }
    }

    [Fact]
    public async Task Reinstall_cancel_then_burn_failure_is_recorded_as_failure()
    {
        var (k, j) = await CancelAfterApplyAsync("2.0.0", l => l.HangApply = true);
        using (k)
        {
            await Task.Delay(300);
            Assert.DoesNotContain(j.Results[0].Log, l => l.Contains("올라감"));       // programming 중에는 판정하지 않음
            k.Link.SetDspState(7, "?", "staged");                                     // 지운 뒤 실패 — 스테이징 유지
            await Until(() => j.Results[0].Message.Contains("굽다가 실패"), "굽기 실패 기록");
            Assert.Equal("?", j.Results[0].VersionAfter);
        }
    }

    [Fact]
    public async Task Cancel_follow_up_uses_bridge_fail_report()
    {
        var (k, j) = await CancelAfterApplyAsync("1.0.0", l => l.HangApply = true);
        using (k)
        {
            k.Link.SendReport(7, j.Results[0].StageJob, "fail", "erase");
            await Until(() => j.Results[0].Message.Contains("굽다가 실패: erase"), "실패 보고 기록");
        }
    }

    [Fact]
    public async Task Cancel_follow_up_records_success_after_programming()
    {
        var (k, j) = await CancelAfterApplyAsync("2.0.0", l => l.HangApply = true);
        using (k)
        {
            k.Link.SetDsp(7, "2.0.0");
            await Until(() => j.Results[0].Message.Contains("올라감"), "올라감 기록");
            Assert.Equal("2.0.0", j.Results[0].VersionAfter);
        }
    }

    static async Task<(JobTestKit k, Job j)> CancelBridgeReinstallAsync()
    {
        var k = new JobTestKit();
        k.Link.Add(7);                                 // 브리지 fw 0.5.2, part ota_0
        k.Svc.FollowUpTimeout = TimeSpan.FromSeconds(2);
        var fw = await k.Store.AddAsync(FirmwareKind.Esp, "", "", "", [("esp", "agv_wifi_bridge.bin",
            new MemoryStream(EspAppImage.Build("0.5.2", EspAppImage.BridgeProject)))]);
        var j = k.Svc.Create("", fw.Id, [7], null, true);
        k.Svc.Tick();
        await Until(() => j.Results[0].Committed && k.Link.Sent.Any(s => s.kind == "OTA"), "OTA_START 보냄");
        k.Svc.Cancel(j.Id);
        await k.Svc.LastRun.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("브리지는 계속 진행", j.Results[0].Message);
        return (k, j);
    }

    [Fact]
    public async Task Bridge_reinstall_cancel_without_reboot_is_not_recorded_as_upgraded()
    {
        var (k, j) = await CancelBridgeReinstallAsync();
        using (k)
        {
            await Until(() => j.Results[0].Message.Contains("버전 변화 없음"), "버전 변화 없음 기록");
            Assert.Contains("재설치 여부 미확인", j.Results[0].Message);
            Assert.DoesNotContain(j.Results[0].Log, l => l.Contains("올라감") || l.Contains("끝까지"));
        }
    }

    [Fact]
    public async Task Bridge_reinstall_cancel_with_new_slot_is_recorded()
    {
        var (k, j) = await CancelBridgeReinstallAsync();
        using (k)
        {
            k.Link.SetEsp(7, "0.5.2", "ota_1", 3);
            await Until(() => j.Results[0].Message.Contains("재설치"), "재설치 기록");
            Assert.Contains("ota_1", j.Results[0].Message);
        }
    }
}
