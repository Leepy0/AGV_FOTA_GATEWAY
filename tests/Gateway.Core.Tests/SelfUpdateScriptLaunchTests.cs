using Gateway.Core.Update;

namespace Gateway.Core.Tests;

/// <summary>R11 · R12 · R13: 배치 실행 줄, 시작 확인 표, 서비스 등록 판정, 자동 적용 제외</summary>
public class SelfUpdateScriptLaunchTests
{
    /// <summary>
    /// cmd /C·/K 의 따옴표 규칙(예전 동작): 뒤 문자열이 따옴표로 시작하고 따옴표가 2개가 아니면 처음·마지막 따옴표를 지운다.
    /// 그 결과가 원래와 같아야 경로가 깨지지 않는다
    /// </summary>
    static string CmdStrip(string rest)
    {
        if (!rest.StartsWith('"') || rest.Count(c => c == '"') == 2) return rest;
        int last = rest.LastIndexOf('"');
        return rest[1..last] + rest[(last + 1)..];
    }

    [Theory]
    [InlineData(@"C:\AgvFotaGateway")]
    [InlineData(@"C:\Program Files (x86)\AGV Gateway")]
    [InlineData(@"D:\Tools (old)\AGV FOTA (test)")]
    public void Launch_goes_through_cmd_call_and_survives_quote_stripping(string app)
    {
        string bat = app + @"\data\update\" + SelfUpdateScript.FileName;
        string args = SelfUpdateScript.Arguments("apply", 1234, app + @"\", app + @"\data\update\v0.3.1\AgvFotaGateway", "AgvFotaGateway", true,
                                                 "http://127.0.0.1:8080/api/health", "0.3.1");
        string line = SelfUpdateScript.LaunchArguments(bat, args);
        Assert.Equal($"/c start \"\" /min cmd.exe /d /c call \"{bat}\" {args}", line);

        // 바깥 cmd: /c 뒤 문자열
        string outer = line["/c ".Length..];
        Assert.Equal(outer, CmdStrip(outer));
        // START 는 배치가 아니라 cmd.exe(실행 파일)를 띄운다 → /K 를 쓰지 않음
        Assert.StartsWith("start \"\" /min cmd.exe /d /c ", outer);
        // 안쪽 cmd: /c 뒤 문자열도 따옴표로 시작하지 않는다
        string inner = outer["start \"\" /min cmd.exe /d /c ".Length..];
        Assert.Equal(inner, CmdStrip(inner));
        Assert.StartsWith($"call \"{bat}\" apply 1234 \"{app}\" \"{app}\\data\\update\\v0.3.1\\AgvFotaGateway\" \"AgvFotaGateway\" svc ", inner);

        // 공백·괄호가 든 경로는 모두 따옴표 안에 있다 (따옴표 밖 토큰에 괄호·공백 경로가 없음)
        var outside = string.Concat(inner.Split('"').Where((_, i) => i % 2 == 0));
        Assert.DoesNotContain("(", outside);
        Assert.DoesNotContain(")", outside);
        Assert.Equal(0, inner.Count(c => c == '"') % 2);
    }

    [Fact]
    public void Batch_claims_ticket_before_anything_else()
    {
        var cmds = SelfUpdateScript.Text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) && l != "@echo off").ToList();
        Assert.Equal($"ren \"%~dp0{SelfUpdateScript.TicketFile}\" {SelfUpdateScript.StartedFile} >nul 2>&1 || exit /b 1", cmds[0]);
    }

    static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "su-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>배치 첫 줄(ren 표 → 시작 표시)과 같은 동작. 이미 거둬들였으면 false (배치는 exit /b 1)</summary>
    static bool BatchClaims(string dir)
    {
        try { File.Move(Path.Combine(dir, SelfUpdateScript.TicketFile), Path.Combine(dir, SelfUpdateScript.StartedFile)); return true; }
        catch (IOException) { return false; }
    }

    [Fact]
    public async Task Started_when_batch_claims_ticket_in_time()
    {
        var d = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(d, SelfUpdateScript.StartedFile), "지난 실행");
            SelfUpdateScript.PrepareStart(d);
            Assert.False(File.Exists(Path.Combine(d, SelfUpdateScript.StartedFile)));   // 지난 표시는 지움
            var batch = Task.Run(async () => { await Task.Delay(300); return BatchClaims(d); });
            Assert.True(await SelfUpdateScript.WaitStartedAsync(d, TimeSpan.FromSeconds(5)));
            Assert.True(await batch);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public async Task Not_started_withdraws_ticket_so_late_batch_quits()
    {
        var d = NewDir();
        try
        {
            SelfUpdateScript.PrepareStart(d);
            Assert.False(await SelfUpdateScript.WaitStartedAsync(d, TimeSpan.FromMilliseconds(300)));
            Assert.False(File.Exists(Path.Combine(d, SelfUpdateScript.TicketFile)));
            Assert.False(BatchClaims(d));   // 늦게 뜬 배치는 표가 없어 바로 끝남 → 서버가 안 멈췄는데 교체가 시작되는 일 없음
            Assert.False(File.Exists(Path.Combine(d, SelfUpdateScript.StartedFile)));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public async Task Claimed_at_deadline_counts_as_started()
    {
        var d = NewDir();
        try
        {
            SelfUpdateScript.PrepareStart(d);
            // 확인이 끝나기 직전에 집은 경우: 거둬들이기가 실패하고 시작 표시가 있으므로 true
            File.Move(Path.Combine(d, SelfUpdateScript.TicketFile), Path.Combine(d, SelfUpdateScript.StartedFile));
            Assert.True(await SelfUpdateScript.WaitStartedAsync(d, TimeSpan.Zero));
        }
        finally { Directory.Delete(d, true); }
    }

    [Theory]
    [InlineData("\"C:\\AgvFotaGateway\\AgvFotaGateway.exe\"", @"C:\AgvFotaGateway\AgvFotaGateway.exe", true)]
    [InlineData("\"c:\\agvfotagateway\\AGVFOTAGATEWAY.EXE\"", @"C:\AgvFotaGateway\AgvFotaGateway.exe", true)]
    [InlineData(@"C:\AgvFotaGateway\AgvFotaGateway.exe", @"C:\AgvFotaGateway\AgvFotaGateway.exe", true)]
    [InlineData("\"C:\\Program Files (x86)\\AGV\\AgvFotaGateway.exe\" --contentRoot x", @"C:\Program Files (x86)\AGV\AgvFotaGateway.exe", true)]
    [InlineData(@"C:\AgvFotaGateway\AgvFotaGateway.exe -x", @"C:\AgvFotaGateway\AgvFotaGateway.exe", true)]
    [InlineData(@"\??\C:\AgvFotaGateway\AgvFotaGateway.exe", @"C:\AgvFotaGateway\AgvFotaGateway.exe", true)]
    [InlineData("\"D:\\Other\\AgvFotaGateway.exe\"", @"C:\AgvFotaGateway\AgvFotaGateway.exe", false)]
    [InlineData(null, @"C:\AgvFotaGateway\AgvFotaGateway.exe", false)]
    [InlineData("", @"C:\AgvFotaGateway\AgvFotaGateway.exe", false)]
    public void SameExe_compares_service_image_path(string? image, string exe, bool expected) => Assert.Equal(expected, SelfUpdateScript.SameExe(image, exe));

    [Theory]
    [InlineData("apply-reverted", true)]
    [InlineData("apply-failed", true)]
    [InlineData("apply-unhealthy", true)]
    [InlineData("apply", false)]
    [InlineData("rollback", false)]
    [InlineData("rollback-failed", false)]
    [InlineData("rollback-reverted", false)]
    public void Failed_apply_results_are_excluded_from_auto_apply(string result, bool skip) => Assert.Equal(skip, SelfUpdateScript.SkipsAutoApply(result));
}
