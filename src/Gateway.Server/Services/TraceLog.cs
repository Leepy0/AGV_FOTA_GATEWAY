namespace Gateway.Server.Services;

/// <summary>화면에 보여줄 최근 통신 로그 (메모리 링버퍼). Changed 는 250ms 단위로 묶어서 발생 (프레임 폭주 시 UI 재렌더 억제)</summary>
public sealed class TraceLog
{
    readonly LinkedList<string> _lines = new();
    const int Max = 300;
    int _pending;
    public event Action? Changed;

    public TraceLog(BridgeUdpService bridge) => bridge.Trace += Add;

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.AddLast($"{DateTime.Now:HH:mm:ss.fff} {line}");
            if (_lines.Count > Max) _lines.RemoveFirst();
        }
        if (Interlocked.Exchange(ref _pending, 1) == 0)
            _ = Task.Delay(250).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _pending, 0);
                try { Changed?.Invoke(); } catch { }
            });
    }

    public void Clear() { lock (_lines) _lines.Clear(); }

    public string[] Snapshot() { lock (_lines) return _lines.Reverse().ToArray(); }
}
