using System.Collections.Concurrent;
using Gateway.Core.Positions;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

/// <summary>
/// 호기별 최신 위치. RTLS 추정기가 Set() 으로 채운다(구현 전). 지금은 /api/positions/{agv} 주입과
/// heartbeat 오프라인 → LOST 전환만 있다. 바뀐 호기는 버전 번호로 추적해 ACS 발행이 변경분만 보낼 수 있게 한다.
/// </summary>
public sealed class AgvPositionStore
{
    readonly ConcurrentDictionary<int, (AgvPosition pos, long ver)> _pos = new();
    readonly AgvRegistry _reg;
    readonly int _offlineSec;
    long _ver;
    readonly Timer _timer;
    public event Action? Changed;

    public AgvPositionStore(AgvRegistry reg, IOptions<GatewayOptions> opt)
    {
        _reg = reg; _offlineSec = opt.Value.OfflineAfterSec;
        reg.Changed += OnRegistryChanged;
        // Registry.Changed 는 패킷이 올 때만 발생하므로, 호기가 (전부) 조용해져도 LOST 로 넘어가도록 1초마다 재평가
        _timer = new Timer(_ => { try { OnRegistryChanged(); } catch { } }, null, 1000, 1000);
    }

    public void Set(AgvPosition p)
    {
        var v = Interlocked.Increment(ref _ver);
        _pos[p.Agv] = (p, v);
        try { Changed?.Invoke(); } catch { }
    }

    public AgvPosition? Get(int agv) => _pos.TryGetValue(agv, out var e) ? e.pos : null;
    public IReadOnlyList<AgvPosition> Snapshot() => _pos.Values.Select(e => e.pos).OrderBy(p => p.Agv).ToList();
    public long Version => Interlocked.Read(ref _ver);

    /// <summary>sinceVersion 이후에 바뀐 호기들과 현재 버전</summary>
    public (IReadOnlyList<AgvPosition> changed, long version) ChangedSince(long sinceVersion)
    {
        var v = Version;
        return (_pos.Values.Where(e => e.ver > sinceVersion).Select(e => e.pos).OrderBy(p => p.Agv).ToList(), v);
    }

    // 브리지 heartbeat 가 끊기면 LOST 로, 돌아오면 Online 만 복구 (위치는 다음 태그에서)
    void OnRegistryChanged()
    {
        foreach (var a in _reg.Snapshot())
        {
            var online = a.Online(_offlineSec);
            if (_pos.TryGetValue(a.Id, out var e) && e.pos.Online != online)
                Set(e.pos with { Online = online, State = online ? e.pos.State : PositionState.Lost, At = DateTimeOffset.UtcNow });
        }
    }
}
