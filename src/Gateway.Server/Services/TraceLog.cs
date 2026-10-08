namespace Gateway.Server.Services;

/// <summary>화면에 보여줄 최근 통신 로그 (메모리 링버퍼)</summary>
public sealed class TraceLog
{
    readonly LinkedList<string> _lines = new();
    const int Max = 300;
    public event Action? Changed;

    public TraceLog(BridgeUdpService bridge) => bridge.Trace += Add;

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.AddLast($"{DateTime.Now:HH:mm:ss.fff} {line}");
            if (_lines.Count > Max) _lines.RemoveFirst();
        }
        try { Changed?.Invoke(); } catch { }
    }

    public string[] Snapshot() { lock (_lines) return _lines.Reverse().ToArray(); }
}
