using System.Text.Json;
using Gateway.Core.Bridge;
using Gateway.Core.Fota;

namespace Gateway.Core.Tests;

/// <summary>
/// 가짜 브리지(B안): START 에 download→staged, APPLY 에 start→enter→erase→progress→verify→done 을 보고하고 heartbeat(dsp·dsp_st·staged)를 갱신.
/// 실제 브리지처럼 스테이징이 없을 때 APPLY 는 job 0 으로 start·fail(not_staged) 를 보고하고, 브리지 0.5.2 처럼 이미 적용한 job 의
/// APPLY 에는 최근 결과(done)를 다시 보고한다(Replay=false 면 구 브리지). 유실·반복 보고를 흉내 낼 수 있다.
/// </summary>
sealed class FakeEspLink : IAgvLink
{
    readonly List<AgvSubscription> _subs = new();
    public string DspVersion = "1.0.0";
    public long StagedJob; public string StagedVer = "";
    public string? FailAt;             // 이 단계에서 fail 보고
    public bool Silent;                // 아무 보고도 안 함 (시간 초과 검사)
    public int DropCommands;           // 앞의 명령 N개를 받지 못함 (UDP 유실)
    public Func<string, bool>? DropReport;  // true 면 그 보고(단계 이름)를 유실
    public int Repeat = 1;             // 최종 보고(staged/done/fail) 반복 횟수 (브리지 0.5.2 는 3)
    public BridgeStatus? Frozen;       // null 이 아니면 heartbeat 가 이 값에 멈춤 (heartbeat 지연)
    public bool Replay = true;         // 이미 적용한 job 의 중복 APPLY 에 최근 결과 재보고 (dsp_ota.c replay_recent)
    public Action<int>? OnApply;       // APPLY 를 받을 때마다 (n번째, 유실 포함) — 시험이 상태를 바꾸는 데 씀
    long _recentJob; string _recentVer = "";
    int _applies;
    public List<string> Sent = new();

    public void Inject(string js) { foreach (var s in _subs.ToArray()) s.DspOtaResults.Writer.TryWrite(js); }

    void Report(long job, string stage, int pct = -1, string? ver = null, string? err = null)
    {
        if (DropReport?.Invoke(stage) == true) return;
        var js = $"{{\"job\":{job},\"stage\":\"{stage}\"" + (pct >= 0 ? $",\"pct\":{pct}" : "") + (ver is null ? "" : $",\"ver\":\"{ver}\"") + (err is null ? "" : $",\"err\":\"{err}\"") + "}";
        int n = stage is "staged" or "done" or "fail" ? Repeat : 1;
        for (int i = 0; i < n; i++) Inject(js);
    }

    bool Drop() { if (DropCommands <= 0) return false; DropCommands--; return true; }

    public bool SendDspOtaStart(int agv, string json)
    {
        Sent.Add(json);
        if (Silent || Drop()) return true;
        using var d = JsonDocument.Parse(json);
        long job = d.RootElement.GetProperty("job").GetInt64();
        var ver = d.RootElement.GetProperty("ver").GetString()!;
        Report(job, "download", 20); Report(job, "download", 100);
        if (FailAt == "download") { Report(job, "fail", err: "crc_mismatch"); return true; }
        StagedJob = job; StagedVer = ver;
        Report(job, "staged", 100, ver);
        return true;
    }

    public bool SendDspOtaApply(int agv, string json)
    {
        Sent.Add(json);
        OnApply?.Invoke(++_applies);
        if (Silent || Drop()) return true;
        using var d = JsonDocument.Parse(json);
        long job = d.RootElement.GetProperty("job").GetInt64();
        if (Replay && job != 0 && job == _recentJob) { Report(job, "done", 100, _recentVer); return true; }
        if (StagedJob != 0 && job != 0 && job != StagedJob) { Report(job, "fail", ver: StagedVer, err: "job_mismatch"); return true; }
        Report(StagedJob, "start", 0, StagedVer);
        if (StagedJob == 0) { Report(0, "fail", err: "not_staged"); return true; }
        foreach (var st in new[] { "enter", "erase" })
        {
            if (FailAt == st) { Report(StagedJob, "fail", ver: StagedVer, err: st + "_timeout"); return true; }
            Report(StagedJob, st);
        }
        for (int p = 0; p <= 100; p += 25) Report(StagedJob, "progress", p);
        Report(StagedJob, "verify");
        DspVersion = StagedVer.Length > 8 ? StagedVer[..8] : StagedVer;     // 앱 R 응답의 버전 칸은 8자
        long j = StagedJob;
        StagedJob = 0; StagedVer = "";                                        // stage_clear
        _recentJob = j; _recentVer = DspVersion;
        Report(j, "done", 100, DspVersion);
        return true;
    }

    public BridgeStatus Status() => Frozen ?? BridgeStatus.Parse(System.Text.Encoding.UTF8.GetBytes(
        $"{{\"fw\":\"0.5.0\",\"part\":\"ota_0\",\"ota\":\"valid\",\"up\":10,\"dsp\":\"{DspVersion}\",\"dspb\":\"1.0.0\"," +
        (StagedJob != 0 ? $"\"dsp_st\":\"staged\",\"staged\":{{\"job\":{StagedJob},\"ver\":\"{StagedVer}\"}}}}" : "\"dsp_st\":\"idle\",\"staged\":null}")))!;

    public bool SendMainDown(int agv, ReadOnlySpan<byte> frame) => true;
    public bool SendOtaStart(int agv, string url) => true;
    public bool SendInfoRequest(int agv) => true;
    public bool SendPing(int agv) => true;
    public AgvSubscription Subscribe(int agv) { AgvSubscription? s = null; s = new(() => _subs.Remove(s!)); _subs.Add(s); return s; }
    public string? LocalAddressToward(int agv) => "127.0.0.1";
}

public class DspOtaClientTests
{
    static DspfwInfo Img(string ver) => DspfwImage.Parse(DspfwImage.BuildApp(Enumerable.Range(0, 300).Select(i => (ushort)i).ToArray(), ver))!;

    // 시험용 짧은 시간: 실제 시간 초과·재전송 경로를 지나게 한다
    static readonly OtaTimeouts Fast = new()
    {
        DspStageIdle = TimeSpan.FromMilliseconds(1500), DspApplyIdle = TimeSpan.FromMilliseconds(1500),   // CI 러너가 느려도 여유
        Resend = TimeSpan.FromMilliseconds(100), Poll = TimeSpan.FromMilliseconds(10),
    };

    [Fact]
    public async Task Stage_then_apply_reports_stages_and_updates_version()
    {
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7);
        var stages = new List<string>();
        await c.StageAsync("http://10.0.0.1:8080/fw/x/app.dspfw", Img("2.0.0"), 42, r => stages.Add(r.Stage));
        Assert.Contains("\"job\":42", link.Sent[0]); Assert.Contains("\"apply\":0", link.Sent[0]); Assert.Contains("\"crc32\":", link.Sent[0]);
        var ver = await c.ApplyAsync(42, r => stages.Add(r.Stage));
        Assert.Equal("2.0.0", ver);
        Assert.Equal(new[] { "download", "download", "staged", "start", "enter", "erase", "progress", "progress", "progress", "progress", "progress", "verify", "done" }, stages);
        var st = await c.WaitVersionAsync("2.0.0", TimeSpan.FromSeconds(2));
        Assert.Equal("2.0.0", st.Dsp);
    }

    [Fact]
    public async Task Start_json_keeps_plus_and_ampersand_literal_for_bridge_parser()
    {
        // 브리지의 최소 JSON 파서는 \u002B 같은 이스케이프를 풀지 못한다 — 버전·URL 의 + & 는 그대로 가야 한다
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7);
        await c.StageAsync("http://10.0.0.1:8080/fw/x/a%20b.dspfw?x=1&y=2", Img("1.2.3+rc1"), 5);
        Assert.Contains("\"ver\":\"1.2.3+rc1\"", link.Sent[0]);
        Assert.Contains("?x=1&y=2", link.Sent[0]);
        Assert.DoesNotContain("\\u", link.Sent[0]);
    }

    [Fact]
    public async Task Fail_report_throws_with_err()
    {
        var link = new FakeEspLink { FailAt = "erase" };
        using var c = new DspOtaClient(link, _ => link.Status(), 7);
        await c.StageAsync("http://x/f", Img("2.0.0"), 1);
        var ex = await Assert.ThrowsAsync<DspOtaException>(() => c.ApplyAsync(1));
        Assert.Contains("erase_timeout", ex.Message);
    }

    [Fact]
    public async Task Job_mismatch_and_other_jobs_results_are_ignored_or_fail()
    {
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7);
        await c.StageAsync("http://x/f", Img("2.0.0"), 5);
        var ex = await Assert.ThrowsAsync<DspOtaException>(() => c.ApplyAsync(6));
        Assert.Contains("job_mismatch", ex.Message);
    }

    [Fact]
    public async Task Silent_bridge_times_out_with_dsp_exception_after_resends()
    {
        var link = new FakeEspLink { Silent = true };
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        var ex = await Assert.ThrowsAsync<DspOtaException>(() => c.StageAsync("http://x/f", Img("2.0.0"), 1));
        Assert.Contains("브리지 보고 없음", ex.Message);
        Assert.Contains("'staged' 대기 중", ex.Message);
        Assert.Equal(1 + Fast.MaxResends, link.Sent.Count);          // 처음 1회 + 재전송 3회
        Assert.All(link.Sent, j => Assert.Contains("\"job\":1", j));
    }

    [Fact]
    public async Task Silent_apply_times_out_with_dsp_exception()
    {
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        await c.StageAsync("http://x/f", Img("2.0.0"), 3);
        link.Silent = true;
        var ex = await Assert.ThrowsAsync<DspOtaException>(() => c.ApplyAsync(3));
        Assert.Contains("'done' 대기 중", ex.Message);
    }

    [Fact]
    public async Task External_cancel_is_still_cancellation()
    {
        var link = new FakeEspLink { Silent = true };
        using var c = new DspOtaClient(link, _ => link.Status(), 7);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.StageAsync("http://x/f", Img("2.0.0"), 1, ct: cts.Token));
    }

    [Fact]
    public async Task Other_job_reports_do_not_restart_the_idle_clock()
    {
        var link = new FakeEspLink { Silent = true };
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast with { DspStageIdle = TimeSpan.FromMilliseconds(400) });
        using var stop = new CancellationTokenSource();
        var noise = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested) { link.Inject("""{"job":999,"stage":"download","pct":40}"""); await Task.Delay(30); }
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DspOtaException>(() => c.StageAsync("http://x/f", Img("2.0.0"), 1));
        stop.Cancel(); await noise;
        Assert.Contains("브리지 보고 없음", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"다른 job 보고가 시계를 다시 시작함 ({sw.Elapsed})");
    }

    [Fact]
    public async Task Lost_start_is_resent()
    {
        var link = new FakeEspLink { DropCommands = 1 };
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        var stages = new List<string>();
        await c.StageAsync("http://x/f", Img("2.0.0"), 8, r => stages.Add(r.Stage));
        Assert.Equal(2, link.Sent.Count);
        Assert.Equal(link.Sent[0], link.Sent[1]);
        Assert.Equal(new[] { "resend", "download", "download", "staged" }, stages);
    }

    [Fact]
    public async Task Lost_apply_is_resent()
    {
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        await c.StageAsync("http://x/f", Img("2.0.0"), 8);
        link.DropCommands = 1;
        Assert.Equal("2.0.0", await c.ApplyAsync(8));
        Assert.Equal(3, link.Sent.Count);
    }

    [Fact]
    public async Task Lost_staged_report_is_taken_from_heartbeat()
    {
        var link = new FakeEspLink { DropReport = s => s == "staged" };
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        var got = new List<DspOtaResult>();
        await c.StageAsync("http://x/f", Img("2.0.0"), 11, got.Add);
        Assert.Equal(("staged", 11L, "2.0.0"), (got[^1].Stage, got[^1].Job, got[^1].Ver));
        Assert.Single(link.Sent);
    }

    [Fact]
    public async Task Lost_done_report_is_taken_from_heartbeat()
    {
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        await c.StageAsync("http://x/f", Img("2.0.0"), 12);
        link.DropReport = s => s == "done";
        Assert.Equal("2.0.0", await c.ApplyAsync(12));
    }

    [Fact]
    public async Task Lost_done_with_long_version_matches_first_8_chars()
    {
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        await c.StageAsync("http://x/f", Img("2026.10.11"), 13);
        link.DropReport = s => s == "done";
        Assert.Equal("2026.10.", await c.ApplyAsync(13, expectedVersion: "2026.10.11"));
        var st = await c.WaitVersionAsync("2026.10.11", TimeSpan.FromSeconds(1));
        Assert.Equal("2026.10.", st.Dsp);
    }

    [Fact]
    public async Task Heartbeat_done_needs_this_job_to_have_been_seen()
    {
        // 스테이징이 없는데 메인보드가 이미 같은 버전이면: 브리지는 job 0 not_staged 로 실패 — heartbeat 만 보고 완료로 오판하면 안 됨
        var link = new FakeEspLink { DspVersion = "2.0.0" };
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        var ex = await Assert.ThrowsAsync<DspOtaException>(() => c.ApplyAsync(14, expectedVersion: "2.0.0"));
        Assert.Contains("not_staged", ex.Message);
    }

    [Fact]
    public async Task Resent_apply_after_lost_reports_gets_replayed_done()
    {
        // 첫 APPLY 는 처리됐지만 보고가 모두 유실되고 heartbeat 도 늦음 → 재전송한 APPLY 에 브리지 0.5.2 가 최근 결과(done)를 다시 보고
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        await c.StageAsync("http://x/f", Img("2.0.0"), 15);
        link.Frozen = link.Status();
        var orig = link.Sent.Count;
        var got = new List<DspOtaResult>();
        link.DropReport = _ => link.Sent.Count - orig == 1;          // 첫 APPLY 의 보고만 유실
        Assert.Equal("2.0.0", await c.ApplyAsync(15, got.Add));
        Assert.Equal(2, link.Sent.Count - orig);
        Assert.DoesNotContain(got, r => r.Stage == "fail");
    }

    // ── R9: 재전송 뒤 job 0 not_staged 는 실제 스테이징 소실 ──

    [Fact]
    public async Task Resent_apply_to_lost_staging_fails_fast_with_not_staged()
    {
        // 승인 대기 중 스테이징이 지워짐 → 첫 APPLY 유실 → 재전송에 브리지가 job 0 으로 start·fail(not_staged) ×3
        var link = new FakeEspLink { Repeat = 3 };
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast with { DspApplyIdle = TimeSpan.FromSeconds(30) });
        await c.StageAsync("http://x/f", Img("2.0.0"), 24);
        link.StagedJob = 0; link.StagedVer = "";
        link.DropCommands = 1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DspOtaException>(() => c.ApplyAsync(24));
        Assert.Contains("not_staged", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"적용 대기 시한까지 기다림 ({sw.Elapsed})");
        Assert.Equal(3, link.Sent.Count);
    }

    [Fact]
    public async Task Resent_apply_not_staged_after_heartbeat_shows_done_is_done()
    {
        // 구 브리지(최근 결과 재보고 없음): 첫 APPLY 는 처리됐으나 보고 유실 → 재전송에 job 0 not_staged. 그 사이 heartbeat 가 완료를 보이면 완료
        var link = new FakeEspLink { Replay = false };
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        await c.StageAsync("http://x/f", Img("2.0.0"), 25);
        link.Frozen = link.Status();
        var orig = link.Sent.Count;
        link.DropReport = _ => link.Sent.Count - orig == 1;
        link.OnApply = n => { if (n == 2) link.Frozen = null; };      // 재전송 시점에 heartbeat 가 따라잡음
        Assert.Equal("2.0.0", await c.ApplyAsync(25));
    }

    // ── R0: 바로 적용 — 늦게 온 staged 반복 보고가 APPLY 재전송을 막지 않음 ──

    [Fact]
    public async Task Late_staged_repeats_do_not_block_resending_a_lost_apply()
    {
        var link = new FakeEspLink();
        using (var stager = new DspOtaClient(link, _ => link.Status(), 7, Fast))
            await stager.StageAsync("http://x/f", Img("2.0.0"), 26);
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast with { DspApplyIdle = TimeSpan.FromSeconds(30) });
        link.DropCommands = 1;                                        // 첫 APPLY 유실
        var stages = new List<string>();
        var apply = c.ApplyAsync(26, r => { lock (stages) stages.Add(r.Stage); });
        link.Inject("""{"job":26,"stage":"staged","pct":100,"ver":"2.0.0"}""");   // 브리지 최종 보고 반복의 2·3회째
        link.Inject("""{"job":26,"stage":"staged","pct":100,"ver":"2.0.0"}""");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("2.0.0", await apply.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"재전송 없이 오래 기다림 ({sw.Elapsed})");
        Assert.Equal(3, link.Sent.Count);                             // START + APPLY + 재전송
        lock (stages) { Assert.DoesNotContain("staged", stages); Assert.Equal("resend", stages[0]); }
    }

    [Fact]
    public async Task Repeated_final_reports_are_handled_once()
    {
        var link = new FakeEspLink { Repeat = 3 };
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        var stages = new List<string>();
        await c.StageAsync("http://x/f", Img("2.0.0"), 16, r => stages.Add(r.Stage));
        Assert.Equal("2.0.0", await c.ApplyAsync(16, r => stages.Add(r.Stage)));
        Assert.Equal(new[] { "download", "download", "staged", "start", "enter", "erase", "progress", "progress", "progress", "progress", "progress", "verify", "done" }, stages);
    }

    [Fact]
    public async Task Apply_json_carries_recover_flag_only_when_asked()
    {
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        await c.StageAsync("http://x/f", Img("2.0.0"), 17);
        await c.ApplyAsync(17, recover: true);
        Assert.Equal("""{"job":17,"recover":1}""", link.Sent[^1]);
        await c.StageAsync("http://x/f", Img("2.0.1"), 18);
        await c.ApplyAsync(18);
        Assert.Equal("""{"job":18}""", link.Sent[^1]);
    }

    // ── INT-A3: 복구 적용 — 재전송에도 recover, wait_power 를 받는 동안 대기 시한 연장 ──

    [Fact]
    public async Task Resent_apply_keeps_recover_flag()
    {
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        await c.StageAsync("http://x/f", Img("2.0.0"), 21);
        link.DropCommands = 1;
        Assert.Equal("2.0.0", await c.ApplyAsync(21, recover: true, expectedVersion: "2.0.0"));
        Assert.Equal(new[] { """{"job":21,"recover":1}""", """{"job":21,"recover":1}""" }, link.Sent.Skip(1).ToArray());
    }

    [Fact]
    public async Task Wait_power_reports_extend_the_idle_deadline_and_each_is_shown()
    {
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);   // 적용 idle 600ms
        await c.StageAsync("http://x/f", Img("2.0.0"), 22);
        link.Silent = true;                                                   // 브리지가 전원 재투입을 기다리며 wait_power 만 보냄
        var stages = new List<string>();
        var apply = c.ApplyAsync(22, r => { lock (stages) stages.Add(r.Stage); }, recover: true);
        for (int i = 0; i < 8; i++) { link.Inject("""{"job":22,"stage":"wait_power"}"""); await Task.Delay(200); }   // 1.6초 > idle
        Assert.False(apply.IsCompleted);
        link.Inject("""{"job":22,"stage":"done","ver":"2.0.0"}""");
        Assert.Equal("2.0.0", await apply.WaitAsync(TimeSpan.FromSeconds(5)));
        lock (stages) Assert.Equal(8, stages.Count(x => x == "wait_power"));    // 같은 내용의 주기 보고도 매번 넘김
    }

    // ── INT-A3·A4: 오류 코드 한국어 안내 ──

    [Theory]
    [InlineData("app_ng_03")] [InlineData("app_no_reply")] [InlineData("uart_busy")] [InlineData("url_host")] [InlineData("url_too_long")]
    [InlineData("ver_invalid")] [InlineData("image_bad")] [InlineData("no_bootloader")] [InlineData("esp_ota_busy")]
    [InlineData("mainboard_programming")] [InlineData("mainboard_staging")] [InlineData("bad_url")] [InlineData("no_mem")]
    [InlineData("http_status")] [InlineData("perform ESP_FAIL")] [InlineData("busy")] [InlineData("app_no_fwup")]
    public void Every_bridge_error_code_has_a_korean_hint(string err) => Assert.NotNull(BridgeErrors.Hint(err));

    [Fact]
    public void Stuck_app_hints_and_old_app_to_jtag()
    {
        // '복구 적용으로 다시 배포' 는 작업 실행기가 복구 적용이 아닌 작업에만 덧붙인다 (JobService.RecoverHint) — 여기서는 겹치지 않게 뺀다
        Assert.DoesNotContain("복구 적용으로", BridgeErrors.Hint("app_no_reply"));
        Assert.Contains("전원", BridgeErrors.Hint("no_bootloader"));
        Assert.Contains("JTAG", BridgeErrors.Hint("app_no_fwup"));
    }

    [Fact]
    public async Task Fail_report_message_carries_the_hint()
    {
        var link = new FakeEspLink();
        using var c = new DspOtaClient(link, _ => link.Status(), 7, Fast);
        await c.StageAsync("http://x/f", Img("2.0.0"), 23);
        link.Silent = true;
        var apply = c.ApplyAsync(23);
        link.Inject("""{"job":23,"stage":"fail","err":"esp_ota_busy"}""");
        var ex = await Assert.ThrowsAsync<DspOtaException>(() => apply);
        Assert.Contains("esp_ota_busy", ex.Message);
        Assert.Contains("브리지 자체 업데이트", ex.Message);
    }

    [Fact]
    public void Status_parses_dsp_fields()
    {
        var s = BridgeStatus.Parse("""{"fw":"0.5.0","dsp":"1.2.3","dspb":"1.0.0","dsp_st":"staged","staged":{"job":7,"ver":"1.2.4"}}"""u8)!;
        Assert.Equal(("1.2.3", "1.0.0", "staged", 7L, "1.2.4"), (s.Dsp, s.DspBoot, s.DspState, s.StagedJob, s.StagedVer));
        Assert.True(s.HasDsp);
        var n = BridgeStatus.Parse("""{"fw":"0.3.0","staged":null}"""u8)!;
        Assert.False(n.HasDsp); Assert.Null(n.StagedJob);
    }
}
