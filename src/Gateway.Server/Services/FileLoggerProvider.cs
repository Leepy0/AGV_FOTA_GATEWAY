using System.Text;

namespace Gateway.Server.Services;

/// <summary>
/// 일자별 파일 로그 ({DataDir}/logs/gateway-yyyyMMdd.log, 14일 보관). Windows 서비스로 돌 때 콘솔 로그가 보이지 않아 필요하다.
/// 카테고리 필터(appsettings Logging:LogLevel)는 프레임워크가 먼저 적용한다.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    readonly string _dir;
    readonly object _lock = new();
    StreamWriter? _w;
    string _day = "";
    public string Directory => _dir;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        System.IO.Directory.CreateDirectory(dir);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                var day = DateTime.Now.ToString("yyyyMMdd");
                if (day != _day)
                {
                    _w?.Dispose();
                    _w = new StreamWriter(Path.Combine(_dir, $"gateway-{day}.log"), true, new UTF8Encoding(false)) { AutoFlush = true };
                    _day = day;
                    Cleanup();
                }
                _w!.WriteLine(line);
            }
            catch (IOException) { }
        }
    }

    void Cleanup()
    {
        foreach (var f in System.IO.Directory.GetFiles(_dir, "gateway-*.log"))
            if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) { try { File.Delete(f); } catch (IOException) { } }
    }

    public void Dispose() { lock (_lock) { _w?.Dispose(); _w = null; } }

    sealed class FileLogger(FileLoggerProvider p, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            var cat = category.StartsWith("Gateway.Server.Services.") ? category[24..] : category;
            p.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Abbr(level)}] {cat}: {fmt(state, ex)}{(ex is null ? "" : Environment.NewLine + ex)}");
        }
        static string Abbr(LogLevel l) => l switch { LogLevel.Information => "INF", LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => l.ToString()[..3].ToUpper() };
    }
}
