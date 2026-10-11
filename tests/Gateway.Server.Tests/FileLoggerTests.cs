using Gateway.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gateway.Server.Tests;

/// <summary>파일 로그: 어떤 경우에도 예외를 던지지 않음(G-03), 수준은 설정을 따름(G-04)</summary>
public class FileLoggerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gw-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string Today => Path.Combine(_dir, $"gateway-{DateTime.Now:yyyyMMdd}.log");

    string OldLog(int daysAgo)
    {
        Directory.CreateDirectory(_dir);
        var f = Path.Combine(_dir, $"gateway-{DateTime.Now.AddDays(-daysAgo):yyyyMMdd}.log");
        File.WriteAllText(f, "old");
        File.SetLastWriteTime(f, DateTime.Now.AddDays(-daysAgo));
        return f;
    }

    static string ReadShared(string f)
    {
        using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public void Undeletable_old_log_does_not_throw_and_line_is_written_and_others_cleaned()
    {
        var locked = OldLog(30);
        var other = OldLog(20);
        var keep = OldLog(3);
        using var p = new FileLoggerProvider(_dir);
        p.DeleteFile = f => { if (f == locked) throw new UnauthorizedAccessException("denied"); File.Delete(f); };
        var log = p.CreateLogger("Gateway.Server.Services.JobService");

        var ex = Record.Exception(() => log.LogInformation("작업 시작"));
        Assert.Null(ex);
        var text = ReadShared(Today);
        Assert.Contains("[INF] JobService: 작업 시작", text);
        Assert.Contains("옛 로그 삭제 실패", text);          // 실패는 파일에 직접 한 줄
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(other));                   // 한 파일 실패가 나머지 정리를 막지 않음
        Assert.True(File.Exists(keep));
        // 기록이 먼저: 작업 줄이 경고 줄보다 앞
        Assert.True(text.IndexOf("작업 시작", StringComparison.Ordinal) < text.IndexOf("옛 로그 삭제 실패", StringComparison.Ordinal));
    }

    [Fact]
    public void Any_exception_from_cleanup_is_swallowed()
    {
        OldLog(30);
        using var p = new FileLoggerProvider(_dir);
        p.DeleteFile = _ => throw new InvalidOperationException("boom");
        var log = p.CreateLogger("x");
        Assert.Null(Record.Exception(() => log.LogWarning("w")));
        Assert.Contains("[WRN] x: w", ReadShared(Today));
    }

    [Fact]
    public void Unwritable_directory_does_not_throw()
    {
        Directory.CreateDirectory(_dir);
        var notADir = Path.Combine(_dir, "file");
        File.WriteAllText(notADir, "x");
        using var p = new FileLoggerProvider(notADir);   // 폴더 자리에 파일 → 열기 실패
        var log = p.CreateLogger("x");
        Assert.Null(Record.Exception(() => { log.LogInformation("a"); log.LogError(new Exception("e"), "b"); }));
    }

    [Fact]
    public void Day_change_rolls_to_new_file()
    {
        using var p = new FileLoggerProvider(_dir);
        var t = new DateTime(2026, 9, 16, 23, 59, 59);
        p.Now = () => t;
        var log = p.CreateLogger("x");
        log.LogInformation("before");
        t = t.AddSeconds(2);
        log.LogInformation("after");
        Assert.Contains("before", ReadShared(Path.Combine(_dir, "gateway-20260916.log")));
        Assert.Contains("after", ReadShared(Path.Combine(_dir, "gateway-20260917.log")));
    }

    [Fact]
    public void Level_follows_configuration_including_debug_and_file_alias()
    {
        // Logging:LogLevel:Default=Debug → 파일에도 Debug 가 남는다 (예전에는 Information 미만을 고정으로 버림)
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Debug",
            ["Logging:File:LogLevel:Noisy"] = "Warning",   // 파일만 따로: Noisy 카테고리는 Warning 이상
        }).Build();
        using (var p = new FileLoggerProvider(_dir))
        using (var f = LoggerFactory.Create(b => { b.AddConfiguration(cfg.GetSection("Logging")); b.AddProvider(p); }))
        {
            f.CreateLogger("Diag").LogDebug("udp rx: x");
            f.CreateLogger("Diag").LogTrace("trace-hidden");
            f.CreateLogger("Noisy").LogInformation("noisy-hidden");
            f.CreateLogger("Noisy").LogWarning("noisy-shown");
        }
        var text = ReadShared(Today);
        Assert.Contains("[DBG] Diag: udp rx: x", text);
        Assert.DoesNotContain("trace-hidden", text);
        Assert.DoesNotContain("noisy-hidden", text);
        Assert.Contains("noisy-shown", text);
    }
}
