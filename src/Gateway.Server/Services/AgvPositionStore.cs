using System.Collections.Concurrent;
using Gateway.Core.Positions;
using Gateway.Server.Data;
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
    readonly GatewayDb _db;
    readonly int _offlineSec;
    long _ver;
    readonly Timer _timer;
    public event Action? Changed;

    public AgvPositionStore(AgvRegistry reg, GatewayDb db, IOptions<GatewayOptions> opt)
    {
        _reg = reg; _db = db; _offlineSec = opt.Value.OfflineAfterSec;
        // 재시작 후에도 마지막 위치를 보여 준다 (heartbeat 가 없으면 1초 뒤 LOST 로 바뀜)
        using (var c = _db.Open())
        {
            using var cmd = GatewayDb.Cmd(c, "SELECT agv, at, x, y, heading, state, speed, rotating, last_tag, last_tag_at, map, online, note FROM agv_positions");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                _pos[r.GetInt32(0)] = (new AgvPosition(r.GetInt32(0), GatewayDb.Dto(r.GetString(1)), GatewayDb.DN(r.GetValue(2)), GatewayDb.DN(r.GetValue(3)), GatewayDb.DN(r.GetValue(4)),
                    r.GetString(5), GatewayDb.DN(r.GetValue(6)), r.GetInt64(7) != 0, GatewayDb.SN(r.GetValue(8)), GatewayDb.DtoN(GatewayDb.SN(r.GetValue(9))),
                    GatewayDb.SN(r.GetValue(10)), false, GatewayDb.SN(r.GetValue(12))), 0);
        }
        reg.Changed += OnRegistryChanged;
        // Registry.Changed 는 패킷이 올 때만 발생하므로, 호기가 (전부) 조용해져도 LOST 로 넘어가도록 1초마다 재평가
        _timer = new Timer(_ => { try { OnRegistryChanged(); } catch { } }, null, 1000, 1000);
    }

    public void Set(AgvPosition p)
    {
        var v = Interlocked.Increment(ref _ver);
        _pos[p.Agv] = (p, v);
        try
        {
            using var c = _db.Open();
            GatewayDb.Exec(c, """
                INSERT OR REPLACE INTO agv_positions(agv, at, x, y, heading, state, speed, rotating, last_tag, last_tag_at, map, online, note)
                VALUES($agv, $at, $x, $y, $h, $st, $sp, $rot, $tag, $tagat, $map, $on, $note)
                """, ("$agv", p.Agv), ("$at", GatewayDb.Ts(p.At)), ("$x", p.X), ("$y", p.Y), ("$h", p.Heading), ("$st", p.State), ("$sp", p.Speed),
                ("$rot", p.Rotating ? 1 : 0), ("$tag", p.LastTag), ("$tagat", GatewayDb.Ts(p.LastTagAt)), ("$map", p.Map), ("$on", p.Online ? 1 : 0), ("$note", p.Note));
        }
        catch (Exception) { }
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
