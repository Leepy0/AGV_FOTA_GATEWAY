using System.Text;

namespace Gateway.Server.Services;

/// <summary>
/// 일자별 파일 로그 ({DataDir}/logs/gateway-yyyyMMdd.log, 14일 보관). Windows 서비스로 돌 때 콘솔 로그가 보이지 않아 필요하다.
/// 수준은 설정을 따른다: Logging:LogLevel (전체), Logging:File:LogLevel (파일만). 이 공급자는 따로 거르지 않는다.
/// 로거는 어떤 경우에도 예외를 던지지 않는다 — 파일을 못 쓰면 그 줄은 버린다.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int KeepDays = 14;
    readonly string _dir;
    readonly object _lock = new();
    StreamWriter? _w;
    string _day = "";
    DateTime _openFailedAt = DateTime.MinValue;
    public string Directory => _dir;
    /// <summary>파일 삭제 (시험에서 실패를 흉내 내려고 바꿀 수 있음)</summary>
    internal Action<string> DeleteFile { get; set; } = File.Delete;
    internal Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        try { System.IO.Directory.CreateDirectory(dir); } catch (Exception) { }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                var now = Now();
                var day = now.ToString("yyyyMMdd");
                bool rolled = false;
                if (day != _day)
                {
                    // 열기에 실패하면 10초 동안은 다시 시도하지 않는다 (호출마다 파일 열기 반복 방지)
                    if ((now - _openFailedAt).TotalSeconds < 10) return;
                    _w?.Dispose(); _w = null;
                    try
                    {
                        System.IO.Directory.CreateDirectory(_dir);
                        _w = new StreamWriter(Path.Combine(_dir, $"gateway-{day}.log"), true, new UTF8Encoding(false)) { AutoFlush = true };
                    }
                    catch (Exception) { _openFailedAt = now; return; }
                    _day = day;
                    rolled = true;
                }
                _w?.WriteLine(line);
                if (rolled) Cleanup(now);   // 기록 먼저, 정리는 나중
            }
            catch (Exception) { }
        }
    }

    /// <summary>보관 기간이 지난 로그 삭제. 파일별로 따로 시도하고, 실패는 이 파일에 직접 한 줄 남긴다 (ILogger 를 다시 부르지 않음)</summary>
    void Cleanup(DateTime now)
    {
        string[] files;
        try { files = System.IO.Directory.GetFiles(_dir, "gateway-*.log"); }
        catch (Exception) { return; }
        var failed = new List<string>();
        foreach (var f in files)
        {
            try
            {
                if (File.GetLastWriteTime(f) >= now.AddDays(-KeepDays)) continue;
                try { DeleteFile(f); }
                catch (UnauthorizedAccessException)
                {
                    // 읽기 전용 속성이면 풀고 한 번 더
                    File.SetAttributes(f, FileAttributes.Normal);
                    DeleteFile(f);
                }
            }
            catch (Exception e) { failed.Add($"{Path.GetFileName(f)} — {e.Message}"); }
        }
        if (failed.Count > 0)
            try { _w?.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} [WRN] FileLogger: 옛 로그 삭제 실패 {failed.Count}개: {string.Join("; ", failed)}"); }
            catch (Exception) { }
    }

    public void Dispose() { lock (_lock) { try { _w?.Dispose(); } catch (Exception) { } _w = null; } }

    sealed class FileLogger(FileLoggerProvider p, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        // 수준 판단은 프레임워크 필터(Logging:LogLevel, Logging:File:LogLevel)에 맡긴다
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            try
            {
                var cat = category.StartsWith("Gateway.Server.Services.") ? category[24..] : category;
                p.Write($"{p.Now():yyyy-MM-dd HH:mm:ss.fff} [{Abbr(level)}] {cat}: {fmt(state, ex)}{(ex is null ? "" : Environment.NewLine + ex)}");
            }
            catch (Exception) { }
        }
        static string Abbr(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF", LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???",
        };
    }
}
