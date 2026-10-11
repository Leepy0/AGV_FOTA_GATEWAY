using Gateway.Core.Acs;
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
    readonly VersionedPositions _pos = new();
    readonly object _dbLock = new();
    readonly AgvRegistry _reg;
    readonly GatewayDb _db;
    readonly int _offlineSec;
    readonly Timer _timer;
    public event Action? Changed;

    public AgvPositionStore(AgvRegistry reg, GatewayDb db, IOptions<GatewayOptions> opt)
    {
        _reg = reg; _db = db; _offlineSec = opt.Value.OfflineAfterSec;
        // 재시작 후에도 마지막 좌표는 보여 주되, 그동안 움직였을 수 있으므로 LOST·오프라인으로 복원한다.
        // heartbeat 가 돌아와도 상태는 LOST 그대로(Online 만 복구) — 새 태그·추정이 들어와야 TRACKED 가 된다.
        using (var c = _db.Open())
        {
            using var cmd = GatewayDb.Cmd(c, "SELECT agv, at, x, y, heading, state, speed, rotating, last_tag, last_tag_at, map, online, note FROM agv_positions");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                _pos.Restore(new AgvPosition(r.GetInt32(0), GatewayDb.Dto(r.GetString(1)), GatewayDb.DN(r.GetValue(2)), GatewayDb.DN(r.GetValue(3)), GatewayDb.DN(r.GetValue(4)),
                    PositionState.Lost, GatewayDb.DN(r.GetValue(6)), r.GetInt64(7) != 0, GatewayDb.SN(r.GetValue(8)), GatewayDb.DtoN(GatewayDb.SN(r.GetValue(9))),
                    GatewayDb.SN(r.GetValue(10)), false, GatewayDb.SN(r.GetValue(12))));
        }
        reg.Changed += OnRegistryChanged;
        // Registry.Changed 는 패킷이 올 때만 발생하므로, 호기가 (전부) 조용해져도 LOST 로 넘어가도록 1초마다 재평가
        _timer = new Timer(_ => { try { OnRegistryChanged(); } catch { } }, null, 1000, 1000);
    }

    public void Set(AgvPosition p)
    {
        _pos.Set(p);
        Persist(p.Agv);
    }

    // DB 에는 그 순간의 최신값을 쓴다 (동시 Set 의 쓰기 순서가 뒤바뀌어 옛 값이 남지 않게)
    void Persist(int agv)
    {
        try
        {
            lock (_dbLock)
            {
                if (_pos.Get(agv) is not { } p) return;
                using var c = _db.Open();
                GatewayDb.Exec(c, """
                    INSERT OR REPLACE INTO agv_positions(agv, at, x, y, heading, state, speed, rotating, last_tag, last_tag_at, map, online, note)
                    VALUES($agv, $at, $x, $y, $h, $st, $sp, $rot, $tag, $tagat, $map, $on, $note)
                    """, ("$agv", p.Agv), ("$at", GatewayDb.Ts(p.At)), ("$x", p.X), ("$y", p.Y), ("$h", p.Heading), ("$st", p.State), ("$sp", p.Speed),
                    ("$rot", p.Rotating ? 1 : 0), ("$tag", p.LastTag), ("$tagat", GatewayDb.Ts(p.LastTagAt)), ("$map", p.Map), ("$on", p.Online ? 1 : 0), ("$note", p.Note));
            }
        }
        catch (Exception) { }
        try { Changed?.Invoke(); } catch { }
    }

    public AgvPosition? Get(int agv) => _pos.Get(agv);
    public IReadOnlyList<AgvPosition> Snapshot() => _pos.Snapshot();
    public long Version => _pos.Version;

    /// <summary>전체와 그 전체가 반영하는 버전 (발행 force 용 — 둘을 따로 읽으면 사이에 든 변경이 빠짐)</summary>
    public (IReadOnlyList<AgvPosition> all, long version) All() => _pos.All();

    /// <summary>sinceVersion 이후에 바뀐 호기들과, 그 목록이 반영하는 버전(다음 워터마크)</summary>
    public (IReadOnlyList<AgvPosition> changed, long version) ChangedSince(long sinceVersion) => _pos.ChangedSince(sinceVersion);

    // 브리지 heartbeat 가 끊기면 LOST 로, 돌아오면 Online 만 복구 (위치는 다음 태그에서)
    void OnRegistryChanged()
    {
        foreach (var a in _reg.Snapshot())
        {
            var online = a.Online(_offlineSec);
            // 읽기~쓰기를 한 번에 — 그 사이 들어온 주입·추정값을 옛 값으로 덮지 않게
            if (_pos.Update(a.Id, p => p.Online == online ? null
                    : p with { Online = online, State = online ? p.State : PositionState.Lost, At = DateTimeOffset.UtcNow }) is not null)
                Persist(a.Id);
        }
    }
}
