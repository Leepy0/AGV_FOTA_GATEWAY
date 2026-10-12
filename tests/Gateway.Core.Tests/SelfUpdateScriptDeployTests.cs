using System.Text;
using Gateway.Core.Update;

namespace Gateway.Core.Tests;

/// <summary>
/// R14: cmd 는 괄호 블록 ( ) 을 통째로 해석하므로 블록 안에서 따옴표 없이 펼친 경로의 ')' (예: Program Files (x86)) 가 블록을 닫아
/// 구문 오류로 배치가 끝난다. 설치 스크립트와 교체 배치 모두 블록 안 경로는 따옴표 안에만 있어야 한다
/// </summary>
public class SelfUpdateScriptDeployTests
{
    static readonly string[] PathVars = ["%~dp0", "%~p0", "%cd%", "%exe%", "%app%", "%stage%", "%log%", "%pending%", "%body%", "%prev%"];

    /// <summary>괄호 블록 안·따옴표 밖에서 경로 변수를 펼친 줄</summary>
    static List<string> UnquotedPathsInBlocks(IEnumerable<string> lines)
    {
        var bad = new List<string>();
        int depth = 0;
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.StartsWith("::") || l.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) continue;
            bool q = false;
            for (int i = 0; i < l.Length; i++)
            {
                char c = l[i];
                if (c == '^') { i++; continue; }
                if (c == '"') { q = !q; continue; }
                if (q) continue;
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == '%' && depth > 0 && PathVars.Any(v => string.Compare(l, i, v, 0, v.Length, StringComparison.OrdinalIgnoreCase) == 0))
                    bad.Add(raw);
            }
        }
        return bad;
    }

    static string RepoFile(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, rel);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(rel);
    }

    /// <summary>설치 스크립트는 CP949 — 따옴표·괄호·% 는 ASCII 바이트라 Latin1 로 읽어도 위치가 같다 (한글 둘째 바이트는 0x41 이상)</summary>
    static string[] InstallScript() => File.ReadAllText(RepoFile(Path.Combine("deploy", "install-service.bat")), Encoding.Latin1).ReplaceLineEndings("\n").Split('\n');

    [Fact]
    public void Lint_catches_the_old_line() =>
        Assert.Single(UnquotedPathsInBlocks(["if not exist \"%EXE%\" (echo not here: %~dp0 & pause & exit /b 1)"]));

    [Fact]
    public void Install_script_has_no_unquoted_path_inside_blocks() => Assert.Empty(UnquotedPathsInBlocks(InstallScript()));

    [Fact]
    public void Update_script_has_no_unquoted_path_inside_blocks() => Assert.Empty(UnquotedPathsInBlocks(SelfUpdateScript.Text.ReplaceLineEndings("\n").Split('\n')));

    [Fact]
    public void Install_script_stops_stray_process_before_sc_start_and_sets_acl()
    {
        var t = string.Join("\n", InstallScript());
        int kill = t.IndexOf("taskkill /F /IM AgvFotaGateway.exe", StringComparison.Ordinal);
        int start = t.IndexOf("\nsc start %NAME%", StringComparison.Ordinal);
        Assert.True(kill > 0 && start > kill, "taskkill must come right before sc start");
        Assert.Contains("*S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX", t);
        Assert.Contains("if exist \"%EXE%\" goto exe_ok", t);
    }
}
