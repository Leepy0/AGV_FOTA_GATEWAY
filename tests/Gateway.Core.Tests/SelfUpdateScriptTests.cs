using System.Text.RegularExpressions;
using Gateway.Core.Update;

namespace Gateway.Core.Tests;

/// <summary>apply-update.bat 은 Windows 에서만 돌므로 문자열 규칙으로 검사한다</summary>
public class SelfUpdateScriptTests
{
    static readonly string[] Lines = SelfUpdateScript.Text.ReplaceLineEndings("\n").Split('\n');

    [Fact]
    public void Script_is_ascii_only() => Assert.All(SelfUpdateScript.Text, c => Assert.True(c < 128, $"non-ASCII '{c}'"));

    [Fact]
    public void Every_set_uses_quoted_form()
    {
        // set X=1 & ... 는 & 앞 공백까지 값에 들어간다 (I-01: WASSVC 가 "1 " 이 되어 서비스가 다시 시작되지 않던 원인)
        foreach (var l in Lines)
            foreach (Match m in Regex.Matches(l, @"(?i)(?:^|[\s(&|])set\s+(?!/a\s)(\S)"))
                Assert.True(m.Groups[1].Value == "\"", $"unquoted set: {l}");
        Assert.DoesNotMatch(@"(?i)set\s+""?\w+=[^""\r\n&]*\s&", SelfUpdateScript.Text);
    }

    [Fact]
    public void Every_goto_and_call_target_exists_once()
    {
        var labels = Lines.Select(l => l.Trim()).Where(l => l.StartsWith(':')).Select(l => l[1..].ToLowerInvariant()).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
        foreach (Match m in Regex.Matches(SelfUpdateScript.Text, @"(?i)\b(?:goto|call)\s+:?([a-z_]+)"))
            Assert.Contains(m.Groups[1].Value.ToLowerInvariant(), labels);
    }

    [Fact]
    public void Parentheses_balance_outside_quotes()
    {
        int depth = 0;
        foreach (var l in Lines.Select(x => x.Trim()).Where(x => !x.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)))
        {
            bool q = false;
            for (int i = 0; i < l.Length; i++)
            {
                char c = l[i];
                if (c == '^') { i++; continue; }
                if (c == '"') q = !q;
                else if (!q && c == '(') depth++;
                else if (!q && c == ')') { depth--; Assert.True(depth >= 0, l); }
            }
            Assert.False(q, $"unbalanced quote: {l}");
        }
        Assert.Equal(0, depth);
    }

    [Fact]
    public void Script_restarts_by_runas_argument_and_checks_health()
    {
        var t = SelfUpdateScript.Text;
        Assert.DoesNotContain("WASSVC", t);
        Assert.Contains("if /i \"%RUNAS%\"==\"svc\" (", t);
        Assert.Contains("sc start \"%SVC%\"", t);
        Assert.Contains("curl.exe", t);
        Assert.Contains("Invoke-WebRequest", t);                 // curl 없는 PC 대안
        Assert.Contains($"if %T% GEQ {SelfUpdateScript.HealthTimeoutSec} exit /b 1", t);
        Assert.Contains("echo %MODE%-reverted %WANT%", t);       // 자동 되돌림 결과를 다음 기동 때 화면에
        Assert.Contains("AgvFotaGateway.exe.bad", t);
    }

    [Fact]
    public void Arguments_order_and_sanitizing()
    {
        var a = SelfUpdateScript.Arguments("apply", 1234, @"C:\AgvFotaGateway\", @"C:\AgvFotaGateway\data\update\v0.3.1\AgvFotaGateway", "AgvFotaGateway", true,
                                           "http://127.0.0.1:8080/api/health", "0.3.1");
        Assert.Equal(@"apply 1234 ""C:\AgvFotaGateway"" ""C:\AgvFotaGateway\data\update\v0.3.1\AgvFotaGateway"" ""AgvFotaGateway"" svc ""http://127.0.0.1:8080/api/health"" 0.3.1", a);
        var r = SelfUpdateScript.Arguments("rollback", 1, @"C:\A", "", "S", false, null, "이전 버전");
        Assert.Equal(@"rollback 1 ""C:\A"" """" ""S"" console ""-"" -", r);
        Assert.Throws<ArgumentException>(() => SelfUpdateScript.Arguments("x", 1, "a", "", "s", true, null, null));
    }

    [Theory]
    [InlineData("http://0.0.0.0:8080", "http://127.0.0.1:8080/api/health")]
    [InlineData("http://[::]:8080", "http://127.0.0.1:8080/api/health")]
    [InlineData("http://*:9000", "http://127.0.0.1:9000/api/health")]
    [InlineData("http://+:9000", "http://127.0.0.1:9000/api/health")]
    [InlineData("http://localhost:5000", "http://127.0.0.1:5000/api/health")]
    [InlineData("http://192.168.0.10:8080", "http://192.168.0.10:8080/api/health")]
    public void HealthUrl_maps_bind_address_to_local(string addr, string expected) => Assert.Equal(expected, SelfUpdateScript.HealthUrl([addr]));

    [Fact]
    public void HealthUrl_prefers_http_and_null_without()
    {
        Assert.Equal("http://127.0.0.1:8080/api/health", SelfUpdateScript.HealthUrl(["https://0.0.0.0:8443", "http://0.0.0.0:8080"]));
        Assert.Null(SelfUpdateScript.HealthUrl(["https://0.0.0.0:8443"]));
        Assert.Null(SelfUpdateScript.HealthUrl([]));
    }
}
