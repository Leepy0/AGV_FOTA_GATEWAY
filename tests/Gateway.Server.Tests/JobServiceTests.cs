using System.Net;
using Gateway.Server.Services;

namespace Gateway.Server.Tests;

public class JobServiceTests
{
    // ── 결정 1B: 메인보드 승인 모드 = 전 호기 스테이징 → 적용 대기 → 승인 → 한 대씩 적용 ──

    [Fact]
    public async Task Approve_mode_stages_all_then_waits_then_applies_with_same_job_number()
    {
        using var k = new JobTestKit();
        k.Link.Add(7); k.Link.Add(8);
        var fw = k.AddDsp("2.0.0");
        var j = k.Svc.Create("", fw.Id, [7, 8], null, true);
        Assert.Equal(ApplyMode.Approve, j.ApplyMode);

        await k.TickAndWaitAsync();
        Assert.Equal(JobState.AwaitingApproval, j.State);
        Assert.All(j.Results, r => { Assert.Equal(TargetState.Staged, r.State); Assert.NotEqual(0, r.StageJob); });
        Assert.DoesNotContain(k.Link.Sent, s => s.kind == "APPLY");
        Assert.False(k.Svc.IsBusy);

        // 승인 전에는 다른 작업도, 이 작업의 적용도 시작하지 않는다
        var other = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate);
        k.Svc.Tick();
        Assert.Equal(JobState.AwaitingApproval, j.State);
        Assert.Equal(JobState.Scheduled, other.State);

        Assert.True(k.Svc.Approve(j.Id));
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.Done, j.State);
        var applies = k.Link.Sent.Where(s => s.kind == "APPLY").ToList();
        Assert.Equal(2, applies.Count);
        foreach (var r in j.Results)
        {
            Assert.Equal(TargetState.Done, r.State);
            Assert.Equal("2.0.0", r.VersionAfter);
            Assert.Contains(applies, a => a.agv == r.Agv && a.json.Contains($"\"job\":{r.StageJob}") && !a.json.Contains("recover"));
        }
        // 적용 대기가 끝나면 기다리던 작업이 시작된다
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.Done, other.State);
    }

    [Fact]
    public async Task Immediate_mode_stages_and_applies_per_target()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp("2.0.0");
        var j = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate);
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.Done, j.State);
        Assert.Equal(["START", "APPLY"], k.Link.Sent.Select(s => s.kind).ToArray());
    }

    [Fact]
    public async Task Restart_keeps_awaiting_job_but_clears_approval()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], null, true);
        await k.TickAndWaitAsync();
        k.Svc.HoldNewJobs = true;          // 서버 업데이트 직전: 승인돼도 적용을 시작하지 않음
        k.Svc.Approve(j.Id);
        k.Svc.Tick();
        Assert.Equal(JobState.AwaitingApproval, k.Svc.Get(j.Id)!.State);

        var svc2 = k.Restart();
        var j2 = svc2.Get(j.Id)!;
        Assert.Equal(JobState.AwaitingApproval, j2.State);
        Assert.Null(j2.ApprovedAt);                       // 다시 정지 확인을 받아야 함
        Assert.Equal(j.Results[0].StageJob, j2.Results[0].StageJob);   // DB 에 스테이징 번호 보관
        Assert.Equal(TargetState.Staged, j2.Results[0].State);
        svc2.Tick();
        Assert.Equal(JobState.AwaitingApproval, j2.State);

        svc2.Approve(j.Id);
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.Done, j2.State);
    }

    [Fact]
    public async Task Apply_fails_only_target_whose_bridge_lost_staging()
    {
        using var k = new JobTestKit();
        k.Link.Add(7); k.Link.Add(8);
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7, 8], null, false);
        await k.TickAndWaitAsync();
        k.Link.DropStaged(7);
        k.Svc.Approve(j.Id);
        await k.TickAndWaitAsync();
        Assert.Equal(TargetState.Failed, j.Results[0].State);
        Assert.Contains("not_staged", j.Results[0].Message);
        Assert.Equal(TargetState.Done, j.Results[1].State);
        Assert.Equal(JobState.Failed, j.State);
    }

    [Fact]
    public async Task Cancel_awaiting_job_skips_staged_targets()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], null, true);
        await k.TickAndWaitAsync();
        Assert.True(k.Svc.Cancel(j.Id));
        Assert.Equal(JobState.Cancelled, j.State);
        Assert.Equal(TargetState.Skipped, j.Results[0].State);
        Assert.Throws<InvalidOperationException>(() => k.Svc.Approve(j.Id));
    }

    // ── 결정 4·G-09: 마감 시각 ──

    [Fact]
    public async Task Awaiting_job_past_deadline_is_closed_and_approve_is_refused()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Approve, DateTime.UtcNow.AddMinutes(10));
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.AwaitingApproval, j.State);
        j.NotAfter = DateTime.UtcNow.AddSeconds(-1);
        Assert.Throws<InvalidOperationException>(() => k.Svc.Approve(j.Id));
        k.Svc.Tick();
        Assert.Equal(JobState.Failed, j.State);
        Assert.Equal(TargetState.Skipped, j.Results[0].State);
        Assert.Contains("마감 시각 지남", j.Results[0].Message);
        Assert.DoesNotContain(k.Link.Sent, s => s.kind == "APPLY");
    }

    [Fact]
    public async Task Targets_after_deadline_are_skipped()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7, 8], null, true, ApplyMode.Immediate, DateTime.UtcNow.AddMinutes(10));
        j.NotAfter = DateTime.UtcNow.AddSeconds(-1);
        await k.TickAndWaitAsync();
        Assert.All(j.Results, r => { Assert.Equal(TargetState.Skipped, r.State); Assert.Equal("마감 시각 지남", r.Message); });
        Assert.Equal(JobState.Failed, j.State);
        Assert.Empty(k.Link.Sent);
    }

    // ── G-05·S-01: 늦은 예약 자동 취소, 기동 직후 유예 ──

    [Fact]
    public void Scheduler_cancels_jobs_delayed_more_than_30_minutes_even_without_restart()
    {
        using var k = new JobTestKit();
        var fw = k.AddDsp();
        var sched = k.Svc.Create("", fw.Id, [7], DateTime.UtcNow.AddMinutes(5), true);
        var now = k.Svc.Create("", fw.Id, [8], null, true);
        sched.ScheduledAt = DateTime.UtcNow.AddMinutes(-40);
        now.CreatedAt = DateTime.UtcNow.AddMinutes(-31);
        k.Svc.HoldNewJobs = true;
        k.Svc.Tick();
        Assert.Equal(JobState.Cancelled, sched.State);
        Assert.Contains("앞 작업이 길어져 예약 시각 40분 경과", sched.Results[0].Message);
        Assert.Equal(JobState.Cancelled, now.State);
        Assert.Contains("생성 시각", now.Results[0].Message);
    }

    [Fact]
    public void Restart_cancels_stale_immediate_job_too()
    {
        using var k = new JobTestKit();
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], null, true);
        j.CreatedAt = DateTime.UtcNow.AddHours(-3);
        // 저장된 생성 시각을 옛날로 (재시작 시 DB 에서 읽음)
        using (var c = k.Db.Open()) Gateway.Server.Data.GatewayDb.Exec(c, "UPDATE jobs SET created_at=$t WHERE id=$id", ("$t", Gateway.Server.Data.GatewayDb.Ts(j.CreatedAt)), ("$id", j.Id));
        var svc2 = k.Restart();
        Assert.Equal(JobState.Cancelled, svc2.Get(j.Id)!.State);
        Assert.Contains("서버 중단 중", svc2.Get(j.Id)!.Results[0].Message);
    }

    [Fact]
    public void No_job_starts_during_startup_grace()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], null, true);
        k.Svc.NotBefore = DateTime.UtcNow.AddSeconds(10);
        k.Svc.Tick();
        Assert.Equal(JobState.Scheduled, j.State);
        k.Svc.NotBefore = DateTime.MinValue;
        k.Svc.Tick();
        Assert.NotEqual(JobState.Scheduled, j.State);
    }

    [Fact]
    public async Task Offline_target_waits_for_heartbeat_before_failing()
    {
        using var k = new JobTestKit();
        k.Svc.OnlineWait = TimeSpan.FromSeconds(5);
        var a = k.Link.Add(7);
        a.LastSeen = DateTime.UtcNow.AddMinutes(-10);   // 재시작 전 값만 있음
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7, 9], null, false, ApplyMode.Immediate);
        _ = Task.Delay(700).ContinueWith(_ => k.Link.Beat(7));   // 곧 heartbeat 도착
        await k.TickAndWaitAsync();
        Assert.Equal(TargetState.Done, j.Results[0].State);
        Assert.Equal(TargetState.Failed, j.Results[1].State);   // 9호기는 끝내 없음
        Assert.Contains("오프라인", j.Results[1].Message);
    }

    // ── 결정 2: 번호 충돌 호기 거부 ──

    [Fact]
    public async Task Conflicting_agv_is_refused()
    {
        using var k = new JobTestKit();
        var a = k.Link.Add(7);
        a.ConflictEndPoint = new IPEndPoint(IPAddress.Parse("10.0.0.99"), 50000);
        a.ConflictAt = DateTime.UtcNow;
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate);
        await k.TickAndWaitAsync();
        Assert.Equal(TargetState.Failed, j.Results[0].State);
        Assert.Equal("호기 번호 충돌 — 같은 번호의 장치가 둘", j.Results[0].Message);
        Assert.Empty(k.Link.Sent);
    }

    // ── R15·R1: 복구 적용은 운용자가 고른 작업만 (dsp="?" 만으로 붙이지 않음) ──

    [Fact]
    public async Task Agv_without_app_version_gets_plain_apply_unless_recover_is_chosen()
    {
        using var k = new JobTestKit();
        k.Link.Add(7, dsp: "?");
        k.Link.Add(8);
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7, 8], null, true, ApplyMode.Immediate);
        Assert.False(j.Recover);
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.Done, j.State);
        Assert.All(k.Link.Sent.Where(s => s.kind == "APPLY"), a => Assert.DoesNotContain("recover", a.json));
        Assert.Contains(j.Results[0].Log, l => l.Contains("앱 버전 모름") && l.Contains("복구 적용"));
    }

    [Fact]
    public async Task Recover_job_sends_recover_flag_to_every_target_and_survives_restart()
    {
        using var k = new JobTestKit();
        k.Link.Add(7, dsp: "?");
        k.Link.Add(8);
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7, 8], null, true, ApplyMode.Approve, recover: true);
        Assert.True(j.Recover);
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.AwaitingApproval, j.State);
        var j2 = k.Restart().Get(j.Id)!;          // DB(v3 recover 열)에 보관
        Assert.True(j2.Recover);
        k.Svc.Approve(j.Id);
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.Done, j2.State);
        var applies = k.Link.Sent.Where(s => s.kind == "APPLY").ToList();
        Assert.Equal(2, applies.Count);
        Assert.All(applies, a => Assert.Contains("\"recover\":1", a.json));
        Assert.Contains(j2.Results[1].Log, l => l.Contains("복구 적용 —"));
    }

    [Fact]
    public async Task Recover_is_refused_for_bridge_firmware()
    {
        using var k = new JobTestKit();
        var fw = await k.Store.AddAsync(FirmwareKind.Esp, "", "", "", [("esp", "agv_wifi_bridge.bin",
            new MemoryStream(Gateway.Core.Fota.EspAppImage.Build("0.5.3", Gateway.Core.Fota.EspAppImage.BridgeProject)))]);
        Assert.Throws<InvalidOperationException>(() => k.Svc.Create("", fw.Id, [7], null, true, recover: true));
    }

    [Theory]
    [InlineData("app_no_reply", false, true)]
    [InlineData("app_no_fwup", false, false)]   // 살아 있는 구 앱 — 복구 적용으로도 안 됨, JTAG 안내만
    [InlineData("no_bootloader", false, true)]
    [InlineData("no_bootloader", true, false)]   // 이미 복구 적용이면 덧붙이지 않음
    [InlineData("erase", false, false)]
    public async Task App_failures_suggest_recover_apply(string err, bool recover, bool hint)
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        k.Link.ApplyFailErr = err;
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate, recover: recover);
        await k.TickAndWaitAsync();
        Assert.Equal(TargetState.Failed, j.Results[0].State);
        Assert.Contains(err, j.Results[0].Message);
        Assert.Equal(hint, j.Results[0].Message.Contains(JobService.RecoverHint));
        Assert.True(j.Results[0].Message.Split("복구 적용으로").Length <= 2, j.Results[0].Message);   // 안내가 두 번 붙지 않음
        if (err == "app_no_fwup") Assert.Contains("JTAG", j.Results[0].Message);
    }

    // ── C-02·T-03: 9자 이상 버전도 앞 8자로 맞춰 봄 ──

    [Fact]
    public async Task Long_version_matches_on_first_8_chars()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp("1.2.3-rc1");
        var j = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate);
        await k.TickAndWaitAsync();
        Assert.Equal(TargetState.Done, j.Results[0].State);
        Assert.Equal("1.2.3-rc", j.Results[0].VersionAfter);
    }

    // ── C-09: 스테이징 URL 199자 한도 ──

    [Fact]
    public async Task Too_long_staging_url_fails_before_start()
    {
        using var k = new JobTestKit("http://10.0.0.1:8080/" + new string('p', 160));
        k.Link.Add(7);
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate);
        await k.TickAndWaitAsync();
        Assert.Equal(TargetState.Failed, j.Results[0].State);
        Assert.Contains("199자", j.Results[0].Message);
        Assert.Empty(k.Link.Sent);
    }

    // ── G-08: 생성 검사 ──

    [Fact]
    public void Create_validates_schedule_deadline_and_targets()
    {
        using var k = new JobTestKit();
        var fw = k.AddDsp();
        Assert.Throws<InvalidOperationException>(() => k.Svc.Create("", fw.Id, [7], DateTime.UtcNow.AddHours(-6), true));
        Assert.Throws<InvalidOperationException>(() => k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Approve, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Throws<InvalidOperationException>(() => k.Svc.Create("", fw.Id, [7], DateTime.UtcNow.AddHours(2), true, ApplyMode.Approve, DateTime.UtcNow.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => k.Svc.Create("", fw.Id, [0, 7], null, true));
        Assert.Throws<InvalidOperationException>(() => k.Svc.Create("", fw.Id, null, null, true));
        Assert.Throws<InvalidOperationException>(() => k.Svc.Create("", "nope", [7], null, true));
        var ok = k.Svc.Create("", fw.Id, [7], DateTime.UtcNow.AddSeconds(-20), true);   // 1분 안쪽은 허용 (화면 입력 지연)
        Assert.Equal(JobState.Scheduled, ok.State);
    }

    // ── G-06·U-04: 실행 중 취소 ──

    [Fact]
    public async Task Cancel_running_job_can_also_cancel_queued_jobs()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        k.Link.SilentStage = true;   // 스테이징 보고가 오지 않아 계속 기다리는 중
        var fw = k.AddDsp();
        var a = k.Svc.Create("A", fw.Id, [7], null, true);
        var b = k.Svc.Create("B", fw.Id, [7], null, true);
        var c = k.Svc.Create("C", fw.Id, [7], DateTime.UtcNow.AddHours(1), true);
        k.Svc.Tick();
        Assert.Equal(JobState.Running, a.State);
        Assert.Equal([b.Id], k.Svc.QueuedDue(a.Id).Select(x => x.Id).ToArray());
        Assert.True(k.Svc.Cancel(a.Id, cancelQueued: true));
        await k.Svc.LastRun.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(JobState.Cancelled, a.State);
        Assert.Equal("취소", a.Results[0].Message);       // 스테이징 단계 — 브리지에 피해 없음
        Assert.Equal(JobState.Cancelled, b.State);
        Assert.Contains("함께 취소", b.Results[0].Message);
        Assert.Equal(JobState.Scheduled, c.State);        // 아직 시각이 안 된 예약은 그대로
    }

    [Fact]
    public async Task Cancel_after_apply_sent_records_that_bridge_continues_and_follows_version()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        k.Link.HangApply = true;
        var fw = k.AddDsp("2.0.0");
        var j = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate);
        k.Svc.Tick();
        for (int i = 0; i < 100 && !j.Results[0].Committed; i++) await Task.Delay(50);
        Assert.True(j.Results[0].Committed);
        k.Svc.Cancel(j.Id);
        await k.Svc.LastRun.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(JobState.Cancelled, j.State);
        Assert.Contains("브리지는 계속 진행", j.Results[0].Message);
        k.Link.SetDsp(7, "2.0.0");   // 브리지가 끝까지 구움
        for (int i = 0; i < 60 && j.Results[0].VersionAfter == ""; i++) await Task.Delay(100);
        Assert.Equal("2.0.0", j.Results[0].VersionAfter);
        Assert.Contains("끝까지 진행", j.Results[0].Message);
    }

    // ── S-09: 바뀐 것이 없으면 알리지 않음 ──

    [Fact]
    public void Tick_does_not_notify_while_queued_job_just_waits()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        k.Link.SilentStage = true;
        var fw = k.AddDsp();
        var a = k.Svc.Create("A", fw.Id, [7], null, true);
        k.Svc.Create("B", fw.Id, [7], null, true);
        k.Svc.Tick();
        Assert.Equal(JobState.Running, a.State);
        int n = 0;
        k.Svc.Changed += () => n++;
        k.Svc.Tick(); k.Svc.Tick();
        Assert.Equal(0, n);
        k.Svc.Cancel(a.Id);
    }

    // ── 펌웨어 삭제는 끝나지 않은 작업(적용 대기 포함)이 쓰면 거부 ──

    [Fact]
    public async Task Firmware_used_by_awaiting_job_is_not_deleted()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7], null, true);
        await k.TickAndWaitAsync();
        Assert.False(k.Svc.DeleteFirmwareIfUnused(fw.Id, out var inUse));
        Assert.Equal(j.Id, inUse.Single().Id);
        k.Svc.Cancel(j.Id);
        Assert.True(k.Svc.DeleteFirmwareIfUnused(fw.Id, out _));
        Assert.Null(k.Store.Get(fw.Id));
    }

    // ── INT-A1: 펌웨어 삭제는 하나의 원자적 경로, 작업 생성과 경쟁해도 교착·고아 작업 없음 ──

    [Fact]
    public void Delete_unknown_firmware_reports_not_found_and_in_use_is_empty()
    {
        using var k = new JobTestKit();
        Assert.False(k.Svc.DeleteFirmwareIfUnused("nope", out var inUse));
        Assert.Empty(inUse);
    }

    [Fact]
    public async Task Concurrent_delete_and_create_never_leave_a_job_without_firmware()
    {
        using var k = new JobTestKit();
        for (int round = 0; round < 15; round++)
        {
            var fw = k.AddDsp($"3.0.{round}");
            using var go = new ManualResetEventSlim();
            var creates = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                go.Wait();
                try { return k.Svc.Create("", fw.Id, [7], DateTime.UtcNow.AddHours(1), true); }
                catch (InvalidOperationException) { return null; }   // 펌웨어 없음·지우는 중
            })).ToList();
            var delete = Task.Run(() => { go.Wait(); return k.Svc.DeleteFirmwareIfUnused(fw.Id, out _); });
            go.Set();
            // 교착이면 여기서 시간 초과
            var made = (await Task.WhenAll(creates).WaitAsync(TimeSpan.FromSeconds(10))).Where(j => j is not null).ToList();
            var deleted = await delete.WaitAsync(TimeSpan.FromSeconds(10));
            if (deleted) Assert.Empty(made);                       // 지웠으면 그 펌웨어로 만든 작업이 없어야 함
            else { Assert.NotEmpty(made); Assert.NotNull(k.Store.Get(fw.Id)); }
            foreach (var j in made) k.Svc.Cancel(j!.Id);
        }
    }

    // ── INT-A10: 저장은 작업 lock 밖에서, 실패해도 계속 돈다 ──

    [Fact]
    public async Task Save_failure_does_not_stop_the_service()
    {
        using var k = new JobTestKit();
        k.Link.Add(7);
        var fw = k.AddDsp("2.0.0");
        using (var c = k.Db.Open())
            Gateway.Server.Data.GatewayDb.Exec(c, "CREATE TRIGGER no_jobs BEFORE INSERT ON jobs BEGIN SELECT RAISE(FAIL, 'disk full'); END");
        var j = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate);   // 저장 실패해도 만들어짐 (메모리)
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.Done, j.State);
        Assert.Equal(TargetState.Done, j.Results[0].State);
        Assert.Single(k.Svc.List());
        // DB 가 돌아오면 다음 저장이 최신 상태를 쓴다
        using (var c = k.Db.Open()) Gateway.Server.Data.GatewayDb.Exec(c, "DROP TRIGGER no_jobs");
        Assert.True(k.Svc.Delete(j.Id));
        var j2 = k.Svc.Create("", fw.Id, [7], null, true, ApplyMode.Immediate);
        await k.TickAndWaitAsync();
        Assert.Equal(JobState.Done, k.Restart().Get(j2.Id)!.State);
    }

    [Fact]
    public async Task Locked_database_does_not_block_readers_of_the_job_list()
    {
        using var k = new JobTestKit();
        var fw = k.AddDsp();
        using var locker = k.Db.Open();
        using var tx = locker.BeginTransaction();
        Gateway.Server.Data.GatewayDb.Exec(locker, "DELETE FROM jobs WHERE id='x'");   // 쓰기 잠금을 잡는다
        var create = Task.Run(() => k.Svc.Create("", fw.Id, [7], DateTime.UtcNow.AddHours(1), true));
        await Task.Delay(300);
        Assert.False(create.IsCompleted);                    // DB 잠금을 기다리는 중
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var list = k.Svc.List();                             // 작업 lock 은 비어 있어야 함
        Assert.True(sw.ElapsedMilliseconds < 500, sw.ElapsedMilliseconds.ToString());
        Assert.Single(list);
        var j = await create.WaitAsync(TimeSpan.FromSeconds(JobService.SaveTimeoutSec + 5));   // 짧게 기다리고 포기
        Assert.Equal(JobState.Scheduled, j.State);
        tx.Rollback();
    }

    [Fact]
    public async Task Final_state_survives_restart_after_lock_free_saves()
    {
        using var k = new JobTestKit();
        k.Link.Add(7); k.Link.Add(8);
        var fw = k.AddDsp("2.0.0");
        var j = k.Svc.Create("", fw.Id, [7, 8], null, true, ApplyMode.Immediate);
        await k.TickAndWaitAsync();
        var again = k.Restart().Get(j.Id)!;
        Assert.Equal(JobState.Done, again.State);
        Assert.All(again.Results, r => { Assert.Equal(TargetState.Done, r.State); Assert.Equal(100, r.Percent); });
        Assert.Equal(j.Results[0].Log.Count, again.Results[0].Log.Count);
    }

    // ── INT-A3: 진행률은 낮추지 않음, 단계 문구 ──

    [Fact]
    public void Retry_and_wait_power_do_not_lower_progress()
    {
        static Gateway.Core.Fota.DspOtaResult R(string st, int pct = -1) => new(1, st, pct, "", "");
        int p = JobService.DspPercent(0, R("progress", 80));
        Assert.Equal(81, p);
        p = JobService.DspPercent(p, R("retry", 1));
        Assert.Equal(81, p);
        p = JobService.DspPercent(p, R("erase"));
        Assert.Equal(81, p);
        p = JobService.DspPercent(p, R("progress", 10));
        Assert.Equal(81, p);
        Assert.Equal(81, JobService.DspPercent(p, R("wait_power")));
        Assert.Equal(81, JobService.DspPercent(p, R("resend")));
        Assert.Equal(100, JobService.DspPercent(p, R("done", 100)));
        Assert.Contains("전원", JobService.DspStageText(R("wait_power")));
        Assert.Contains("재시도 2회째", JobService.DspStageText(R("retry", 2)));
        Assert.Contains("다시 보냄", JobService.DspStageText(R("resend")));
    }

    // ── INT-A5: PublicBaseUrl 이 호스트명이면 실패 메시지에 url_host 안내 ──

    [Fact]
    public async Task Hostname_public_base_url_adds_url_host_note_to_failures()
    {
        using var k = new JobTestKit("http://gateway.local:8080");
        k.Link.Add(7);
        k.Link.StageFailErr = "url_host";                                         // 브리지가 호스트명 URL 을 거부
        var fw = k.AddDsp();
        var j = k.Svc.Create("", fw.Id, [7, 9], null, false, ApplyMode.Immediate); // 9호기는 없음 → URL 과 무관한 실패
        await k.TickAndWaitAsync();
        Assert.Equal(TargetState.Failed, j.Results[0].State);
        Assert.Contains("url_host", j.Results[0].Message);
        Assert.Contains("IPv4", j.Results[0].Message);
        Assert.Equal(TargetState.Failed, j.Results[1].State);
        Assert.DoesNotContain("IPv4", j.Results[1].Message);                       // 오프라인 실패에는 붙이지 않음
    }
}
