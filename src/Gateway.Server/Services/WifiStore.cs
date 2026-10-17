using Gateway.Core.Bridge;
using Gateway.Server.Data;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

/// <summary>AP(BSSID) 별 집계: 표본 수·평균·최저 RSSI·약한 신호 비율·끊김·로밍</summary>
public sealed record WifiApRow(string Bssid, long Samples, double AvgRssi, int MinRssi, double WeakPercent, long Drops, long Roams);
/// <summary>위치(마지막으로 읽은 RFID 태그) 별 집계. Aps = 그 위치에서 붙은 AP 수</summary>
public sealed record WifiSpotRow(string Tag, long Samples, double AvgRssi, int MinRssi, double WeakPercent, long Drops, long Roams, int Aps);

/// <summary>
/// Wi-Fi 음영·로밍 분석. 브리지 heartbeat(RSSI·BSSID·끊김 횟수)를 서버에서 이어 보며 사건을 가려내고(WifiMonitor),
/// 신호 표본은 시간·위치·AP 별로 메모리에 모았다가 30초마다 SQLite 에 합산한다 (호기 37대 × 초당 1회를 그대로 쓰지 않음).
/// 위치는 호기가 마지막으로 읽은 바닥 RFID 태그다 — 좌표 추정(RTLS)이 들어오면 그 값으로 바꿀 수 있다.
/// 수신 루프에서는 메모리 연산만 하고 DB 는 이 서비스의 타이머가 쓴다.
/// </summary>
public sealed class WifiStore : BackgroundService
{
    readonly GatewayDb _db;
    readonly GatewayOptions _opt;
    readonly ILogger<WifiStore> _log;
    readonly WifiMonitor _mon = new();
    readonly WifiAggregator _agg = new();
    readonly List<WifiEvent> _pendingEvents = new();
    public event Action? Changed;

    public WifiStore(GatewayDb db, BridgeUdpService link, IOptions<GatewayOptions> opt, ILogger<WifiStore> log)
    {
        _db = db; _opt = opt.Value; _log = log;
        link.StatusUpdated += OnStatus;
    }

    /// <summary>heartbeat 한 건 — 수신 루프에서 불리므로 메모리 연산만</summary>
    public void OnStatus(AgvState a, BridgeStatus? prev, BridgeStatus cur) => Observe(a.Id, DateTime.UtcNow, a.LocationTag, cur);

    internal void Observe(int agv, DateTime at, string tag, BridgeStatus cur)
    {
        if (cur.Rssi == 0 || string.IsNullOrEmpty(cur.Bssid)) { _mon.Observe(agv, at, cur.UpSeconds, 0, 0, "", cur.Disconnects, tag); return; }
        _agg.AddSample(at, tag, cur.Bssid, cur.Rssi);
        if (_mon.Observe(agv, at, cur.UpSeconds, cur.Rssi, cur.Channel, cur.Bssid, cur.Disconnects, tag) is { } ev)
        {
            _agg.AddEvent(ev);
            lock (_pendingEvents) _pendingEvents.Add(ev);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var t = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _opt.WifiFlushSec)));
        var lastPrune = DateTime.MinValue;
        try
        {
            while (await t.WaitForNextTickAsync(ct))
            {
                Flush();
                if ((DateTime.UtcNow - lastPrune).TotalHours >= 1) { Prune(); lastPrune = DateTime.UtcNow; }
            }
        }
        catch (OperationCanceledException) { }
        Flush();     // 종료 직전 마지막 기록
    }

    /// <summary>모은 표본·사건을 DB 에 쓴다. 실패하면 다음 주기에 다시 (로그만)</summary>
    public void Flush()
    {
        var spots = _agg.Drain();
        List<WifiEvent> evs;
        lock (_pendingEvents) { evs = _pendingEvents.ToList(); _pendingEvents.Clear(); }
        if (spots.Count == 0 && evs.Count == 0) return;
        try
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            foreach (var (k, s) in spots)
            {
                using var cmd = GatewayDb.Cmd(c, """
                    INSERT INTO wifi_spot(hour, tag, bssid, n, rssi_sum, rssi_min, weak, drops, roams)
                    VALUES($h, $t, $b, $n, $sum, $min, $w, $d, $r)
                    ON CONFLICT(hour, tag, bssid) DO UPDATE SET
                        n = n + excluded.n, rssi_sum = rssi_sum + excluded.rssi_sum,
                        rssi_min = CASE WHEN excluded.n = 0 THEN rssi_min WHEN n = 0 THEN excluded.rssi_min ELSE MIN(rssi_min, excluded.rssi_min) END,
                        weak = weak + excluded.weak, drops = drops + excluded.drops, roams = roams + excluded.roams
                    """, ("$h", GatewayDb.Ts(k.Hour)), ("$t", k.Tag), ("$b", k.Bssid), ("$n", s.Samples), ("$sum", s.RssiSum), ("$min", s.RssiMin),
                    ("$w", s.Weak), ("$d", s.Drops), ("$r", s.Roams));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            foreach (var e in evs)
            {
                using var cmd = GatewayDb.Cmd(c, "INSERT INTO wifi_event(at, agv, kind, from_bssid, to_bssid, ch, rssi, tag) VALUES($at,$a,$k,$f,$t,$c,$r,$g)",
                    ("$at", GatewayDb.Ts(e.At)), ("$a", e.Agv), ("$k", e.Kind.ToString()), ("$f", e.FromBssid), ("$t", e.ToBssid), ("$c", e.Channel), ("$r", e.Rssi), ("$g", e.Tag));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            if (evs.Count > 0) try { Changed?.Invoke(); } catch { }
        }
        catch (Exception e)
        {
            // 이번 표본은 잃는다 (분석용 통계라 재시도 큐까지는 두지 않음)
            _log.LogWarning("Wi-Fi 분석 기록 실패 ({Spots}칸·{Events}건 버림): {Msg}", spots.Count, evs.Count, e.Message);
        }
    }

    void Prune()
    {
        try
        {
            var cut = DateTime.UtcNow.AddDays(-Math.Max(1, _opt.WifiKeepDays));
            using var c = _db.Open();
            GatewayDb.Exec(c, "DELETE FROM wifi_spot WHERE hour < $c", ("$c", GatewayDb.Ts(cut)));
            GatewayDb.Exec(c, "DELETE FROM wifi_event WHERE at < $c", ("$c", GatewayDb.Ts(cut)));
            // 사건이 아주 많아도 표가 끝없이 커지지 않게 (최근 5만 건)
            GatewayDb.Exec(c, "DELETE FROM wifi_event WHERE id <= (SELECT MAX(id) FROM wifi_event) - 50000");
        }
        catch (Exception e) { _log.LogWarning("Wi-Fi 분석 기록 정리 실패: {Msg}", e.Message); }
    }

    // ───── 조회 (화면·REST). 아직 DB 에 쓰지 않은 표본은 포함하지 않는다 (최대 30초 늦음) ─────

    public List<WifiApRow> Aps(DateTime sinceUtc)
    {
        var r = new List<WifiApRow>();
        using var c = _db.Open();
        using var cmd = GatewayDb.Cmd(c, """
            SELECT bssid, SUM(n), SUM(rssi_sum), MIN(CASE WHEN n > 0 THEN rssi_min END), SUM(weak), SUM(drops), SUM(roams)
            FROM wifi_spot WHERE hour >= $s GROUP BY bssid ORDER BY SUM(n) DESC
            """, ("$s", GatewayDb.Ts(WifiAggregator.HourOf(sinceUtc))));
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) r.Add(Row(rd.GetString(0), rd));
        return r;
    }

    static WifiApRow Row(string key, Microsoft.Data.Sqlite.SqliteDataReader rd)
    {
        long n = rd.GetInt64(1);
        return new WifiApRow(key, n, n == 0 ? 0 : (double)rd.GetInt64(2) / n, rd.IsDBNull(3) ? 0 : rd.GetInt32(3), n == 0 ? 0 : 100.0 * rd.GetInt64(4) / n, rd.GetInt64(5), rd.GetInt64(6));
    }

    public List<WifiSpotRow> Spots(DateTime sinceUtc)
    {
        var r = new List<WifiSpotRow>();
        using var c = _db.Open();
        using var cmd = GatewayDb.Cmd(c, """
            SELECT tag, SUM(n), SUM(rssi_sum), MIN(CASE WHEN n > 0 THEN rssi_min END), SUM(weak), SUM(drops), SUM(roams), COUNT(DISTINCT bssid)
            FROM wifi_spot WHERE hour >= $s GROUP BY tag
            """, ("$s", GatewayDb.Ts(WifiAggregator.HourOf(sinceUtc))));
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var a = Row(rd.GetString(0), rd);
            r.Add(new WifiSpotRow(a.Bssid, a.Samples, a.AvgRssi, a.MinRssi, a.WeakPercent, a.Drops, a.Roams, rd.GetInt32(7)));
        }
        // 가장 문제가 큰 위치부터: 끊김 → 약한 신호 비율
        return r.OrderByDescending(x => x.Drops).ThenByDescending(x => x.WeakPercent).ToList();
    }

    public List<WifiEvent> Events(DateTime sinceUtc, int? agv, int limit)
    {
        var r = new List<WifiEvent>();
        using var c = _db.Open();
        using var cmd = GatewayDb.Cmd(c, $"""
            SELECT at, agv, kind, from_bssid, to_bssid, ch, rssi, tag FROM wifi_event
            WHERE at >= $s {(agv is null ? "" : "AND agv = $a")} ORDER BY id DESC LIMIT $l
            """, ("$s", GatewayDb.Ts(sinceUtc)), ("$a", agv), ("$l", Math.Clamp(limit, 1, 1000)));
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            r.Add(new WifiEvent(GatewayDb.Dt(rd.GetString(0)), rd.GetInt32(1), Enum.Parse<WifiEventKind>(rd.GetString(2)),
                                rd.GetString(3), rd.GetString(4), rd.GetInt32(5), rd.GetInt32(6), rd.GetString(7)));
        return r;
    }
}
