using Gateway.Core.Positions;

namespace Gateway.Core.Acs;

/// <summary>
/// 호기별 최신 위치 + 변경 버전. ACS 발행이 "이 버전 이후 바뀐 호기" 만 보낼 수 있게 한다.
/// 버전 증가·쓰기와 읽기(변경분+현재 버전)를 한 lock 으로 묶어, 돌려준 버전 이하의 변경은 반드시 목록에 들어 있게 한다
/// (버전만 앞서 올라가 변경이 영영 빠지는 일이 없도록).
/// </summary>
public sealed class VersionedPositions
{
    readonly object _lock = new();
    readonly Dictionary<int, (AgvPosition pos, long ver)> _pos = new();
    long _ver;

    /// <summary>새 위치를 쓰고 그 버전을 돌려준다</summary>
    public long Set(AgvPosition p)
    {
        lock (_lock) { var v = ++_ver; _pos[p.Agv] = (p, v); return v; }
    }

    /// <summary>
    /// 현재 값을 보고 바꿀 때 (읽기~쓰기 사이에 다른 Set 이 끼어들어 덮이지 않게). change 가 null 이면 그대로.
    /// 바꾼 값을 돌려주고, 바꾸지 않았으면 null.
    /// </summary>
    public AgvPosition? Update(int agv, Func<AgvPosition, AgvPosition?> change)
    {
        lock (_lock)
        {
            if (!_pos.TryGetValue(agv, out var e) || change(e.pos) is not { } n) return null;
            if (n.Agv != agv) n = n with { Agv = agv };
            _pos[agv] = (n, ++_ver);
            return n;
        }
    }

    /// <summary>재시작 복원용 — 버전 0 (발행 대상 아님)</summary>
    public void Restore(AgvPosition p)
    {
        lock (_lock) _pos[p.Agv] = (p, 0);
    }

    public AgvPosition? Get(int agv)
    {
        lock (_lock) return _pos.TryGetValue(agv, out var e) ? e.pos : null;
    }

    public long Version { get { lock (_lock) return _ver; } }

    public IReadOnlyList<AgvPosition> Snapshot() => All().all;

    /// <summary>전체와, 그 전체가 반영하는 버전</summary>
    public (IReadOnlyList<AgvPosition> all, long version) All()
    {
        lock (_lock) return (_pos.Values.Select(e => e.pos).OrderBy(p => p.Agv).ToList(), _ver);
    }

    /// <summary>sinceVersion 이후에 바뀐 호기들과, 그 목록이 반영하는 버전(다음 워터마크)</summary>
    public (IReadOnlyList<AgvPosition> changed, long version) ChangedSince(long sinceVersion)
    {
        lock (_lock) return (_pos.Values.Where(e => e.ver > sinceVersion).Select(e => e.pos).OrderBy(p => p.Agv).ToList(), _ver);
    }
}
