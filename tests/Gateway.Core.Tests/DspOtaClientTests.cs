using System.Text.Json;
using Gateway.Core.Bridge;
using Gateway.Core.Fota;

namespace Gateway.Core.Tests;

/// <summary>가짜 브리지(B안): START 에 download→staged, APPLY 에 start→enter→erase→progress→verify→done 을 보고하고 heartbeat dsp 를 갱신</summary>
sealed class FakeEspLink : IAgvLink
{
    readonly List<AgvSubscription> _subs = new();
    public string DspVersion = "1.0.0";
    public long StagedJob; public string StagedVer = "";
    public string? FailAt;             // 이 단계에서 fail 보고
    public bool Silent;                // 아무 보고도 안 함 (시간 초과 검사)
    public List<string> Sent = new();

    void Report(long job, string stage, int pct = -1, string? ver = null, string? err = null)
    {
        var js = $"{{\"job\":{job},\"stage\":\"{stage}\"" + (pct >= 0 ? $",\"pct\":{pct}" : "") + (ver is null ? "" : $",\"ver\":\"{ver}\"") + (err is null ? "" : $",\"err\":\"{err}\"") + "}";
        foreach (var s in _subs.ToArray()) s.DspOtaResults.Writer.TryWrite(js);
    }

    public bool SendDspOtaStart(int agv, string json)
    {
        Sent.Add(json);
        if (Silent) return true;
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
        if (Silent) return true;
        using var d = JsonDocument.Parse(json);
        long job = d.RootElement.GetProperty("job").GetInt64();
        if (job != 0 && job != StagedJob) { Report(job, "fail", ver: StagedVer, err: "job_mismatch"); return true; }
        Report(StagedJob, "start", 0, StagedVer);
        foreach (var st in new[] { "enter", "erase" })
        {
            if (FailAt == st) { Report(StagedJob, "fail", ver: StagedVer, err: st + "_timeout"); return true; }
            Report(StagedJob, st);
        }
        for (int p = 0; p <= 100; p += 25) Report(StagedJob, "progress", p);
        Report(StagedJob, "verify");
        DspVersion = StagedVer;
        Report(StagedJob, "done", 100, DspVersion);
        return true;
    }

    public BridgeStatus Status() => BridgeStatus.Parse(System.Text.Encoding.UTF8.GetBytes(
        $"{{\"fw\":\"0.5.0\",\"part\":\"ota_0\",\"ota\":\"valid\",\"up\":10,\"dsp\":\"{DspVersion}\",\"dspb\":\"1.0.0\",\"dsp_st\":\"idle\",\"staged\":null}}"))!;

    public bool SendMainDown(int agv, ReadOnlySpan<byte> frame) => true;
    public bool SendOtaStart(int agv, string url) => true;
    public bool SendInfoRequest(int agv) => true;
    public bool SendPing(int agv) => true;
    public AgvSubscription Subscribe(int agv) { AgvSubscription? s = null; s = new(() => _subs.Remove(s!)); _subs.Add(s); return s; }
    public string? LocalAddressToward(int agv) => "127.0.0.1";
}

public class DspOtaClientTests
{
    static DspfwInfo Img(string ver) => DspfwImage.Parse(DspfwImage.Build(new byte[500], ver))!;

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
    public async Task Silent_bridge_times_out()
    {
        var link = new FakeEspLink { Silent = true };
        using var c = new DspOtaClient(link, _ => link.Status(), 7);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // 시간 초과 검사는 짧은 idle 로: 실제 값(3분)은 그대로 두고 취소 토큰으로 끊어 예외 종류만 확인
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.StageAsync("http://x/f", Img("2.0.0"), 1, ct: cts.Token));
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
