using System.Collections.Concurrent;
using Gateway.Core.Bridge;
using Gateway.Core.Update;
using Gateway.Server.Data;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

public sealed record CfgEntry(int Agv, DateTime TakenAt, CfgSnapshot Snapshot);

/// <summary>설정 수집 한 번의 결과</summary>
public sealed record CfgCollectResult(bool Ok, string Message, CfgSnapshot? Snapshot = null);

/// <summary>
/// 호기 설정 사본 보관·기준값 비교. 브리지 <c>cfg json</c>(0.6.0+)을 CFG_CMD 로 읽어 SQLite 에 남긴다 — 비밀번호는 브리지가 길이만 알려 주므로 사본에도 길이만 있다.
/// 자동 수집: 처음 보인 호기·재부팅한 호기·원격 저장이 확정된(trial→saved) 호기는 몇 초 뒤, 그리고 CfgCollectIntervalMin 마다 온라인 호기 전체.
/// 기준값(드리프트 비교 대상)은 운용자가 정하거나 수집된 사본의 최빈값으로 만든다.
/// </summary>
public sealed class CfgStore : BackgroundService
{
    /// <summary>cfg json 을 지원하는 첫 브리지 버전</summary>
    public static readonly Version MinFw = new(0, 6, 0);

    readonly GatewayDb _db;
    readonly AgvRegistry _reg;
    readonly ICfgChannel _ch;
    readonly GatewayOptions _opt;
    readonly ILogger<CfgStore> _log;
    readonly object _lock = new();
    readonly Dictionary<int, CfgEntry> _snaps = new();
    readonly Dictionary<string, string> _baseline = new();
    /// <summary>자동 수집 예약 — 호기 → 수집할 시각(UTC)</summary>
    readonly ConcurrentDictionary<int, DateTime> _due = new();
    readonly ConcurrentDictionary<int, DateTime> _lastTry = new();
    public event Action? Changed;
    TimeSpan _replyTimeout = TimeSpan.FromSeconds(20);   // 화면의 "설정 읽기"와 같은 20초 (5초는 모자랐음)

    public CfgStore(GatewayDb db, AgvRegistry reg, BridgeUdpService link, IOptions<GatewayOptions> opt, ILogger<CfgStore> log)
        : this(db, reg, link, link, opt, log) { }

    /// <summary>시험용: 응답 길을 따로 줄 수 있다. link 가 null 이면 상태 이벤트를 구독하지 않는다</summary>
    public CfgStore(GatewayDb db, AgvRegistry reg, BridgeUdpService? link, ICfgChannel ch, IOptions<GatewayOptions> opt, ILogger<CfgStore> log)
    {
        _db = db; _reg = reg; _ch = ch; _opt = opt.Value; _log = log;
        Load();
        if (link is not null) link.StatusUpdated += OnStatus;
    }

    internal CfgStore WithReplyTimeout(TimeSpan t) { _replyTimeout = t; return this; }

    void Load()
    {
        using var c = _db.Open();
        using (var cmd = GatewayDb.Cmd(c, "SELECT agv, taken_at, raw FROM agv_cfg"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (CfgSnapshot.Parse(r.GetString(2)) is { } s) _snaps[r.GetInt32(0)] = new CfgEntry(r.GetInt32(0), GatewayDb.Dt(r.GetString(1)), s);
        using (var cmd = GatewayDb.Cmd(c, "SELECT key, value FROM cfg_baseline"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) _baseline[r.GetString(0)] = r.GetString(1);
    }

    public IReadOnlyList<CfgEntry> Snapshots() { lock (_lock) return _snaps.Values.OrderBy(e => e.Agv).ToList(); }
    public CfgEntry? Get(int agv) { lock (_lock) return _snaps.TryGetValue(agv, out var e) ? e : null; }
    public IReadOnlyDictionary<string, string> Baseline() { lock (_lock) return new Dictionary<string, string>(_baseline); }

    /// <summary>기준값과 다른 항목 (사본이 없으면 빈 목록)</summary>
    public List<CfgDiff> DriftOf(int agv) => Get(agv) is { } e ? CfgDrift.Compare(Baseline(), e.Snapshot) : [];

    // ───── 기준값 ─────
    /// <summary>기준값을 통째로 바꾼다 (FleetKeys 의 키만, 빈 값은 건너뜀)</summary>
    public void SetBaseline(IReadOnlyDictionary<string, string> values)
    {
        var clean = values.Where(kv => CfgSnapshot.FleetKeys.Contains(kv.Key) && kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        lock (_lock) { _baseline.Clear(); foreach (var (k, v) in clean) _baseline[k] = v; }
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            GatewayDb.Exec(c, "DELETE FROM cfg_baseline");
            foreach (var (k, v) in clean) GatewayDb.Exec(c, "INSERT INTO cfg_baseline(key, value) VALUES($k, $v)", ("$k", k), ("$v", v));
            tx.Commit();
        }
        Notify();
    }

    /// <summary>수집된 사본의 최빈값으로 기준값을 만든다 (onlyAgvs 가 있으면 그 호기들만) → 만든 항목 수</summary>
    public int SetBaselineFromFleet(IEnumerable<int>? onlyAgvs = null)
    {
        var set = onlyAgvs?.ToHashSet();
        var common = CfgDrift.MostCommon(Snapshots().Where(e => set is null || set.Contains(e.Agv)).Select(e => e.Snapshot));
        SetBaseline(common);
        return common.Count;
    }

    // ───── 수집 ─────
    /// <summary>호기 한 대의 설정을 읽어 보관한다. 구 펌웨어(cfg json 미지원)·오프라인·응답 없음은 사유와 함께 실패</summary>
    public async Task<CfgCollectResult> CollectAsync(int agv, CancellationToken ct = default)
    {
        _lastTry[agv] = DateTime.UtcNow;
        var reply = await _ch.RequestCfgAsync(agv, "cfg json", _replyTimeout, ct);
        if (!reply.Ok) return new(false, reply.Error ?? "응답 없음");
        var snap = CfgSnapshot.Parse(reply.Text!);
        if (snap is null)
            return new(false, reply.Text!.StartsWith("{\"err\"", StringComparison.Ordinal)
                ? $"브리지가 오류를 돌려줌: {reply.Text}"
                : reply.Text!.TrimStart().StartsWith('{')
                    ? $"응답 형식을 읽지 못함 (cfg 항목 없음): {Short(reply.Text!)}"
                    : $"cfg json 을 지원하지 않는 펌웨어 (브리지 0.6.0 이상 필요): {Short(reply.Text!)}");
        var e = new CfgEntry(agv, DateTime.UtcNow, snap);
        lock (_lock) _snaps[agv] = e;
        try
        {
            using var c = _db.Open();
            GatewayDb.Exec(c, "INSERT OR REPLACE INTO agv_cfg(agv, taken_at, raw) VALUES($a, $t, $r)", ("$a", agv), ("$t", GatewayDb.Ts(e.TakenAt)), ("$r", snap.Raw));
        }
        catch (Exception ex) { _log.LogWarning("설정 사본 저장 실패 ({Agv:D2}): {Msg}", agv, ex.Message); }
        Notify();
        return new(true, "수집함", snap);
    }

    /// <summary>호기들을 한 번에 몇 대씩 읽는다 → (성공, 실패) 수</summary>
    public async Task<(int ok, int fail)> CollectManyAsync(IEnumerable<int> agvs, int parallel = 4, CancellationToken ct = default)
    {
        int ok = 0, fail = 0;
        await Parallel.ForEachAsync(agvs.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = ct }, async (a, token) =>
        {
            if ((await CollectAsync(a, token)).Ok) Interlocked.Increment(ref ok); else Interlocked.Increment(ref fail);
        });
        return (ok, fail);
    }

    static string Short(string s) => s.Length > 80 ? s[..80] + "…" : s.Trim();

    public static bool SupportsCfgJson(string? fw) => UpdateFeed.ParseVersion(fw) is { } v && v >= MinFw;

    void Notify() { try { Changed?.Invoke(); } catch { } }

    // ───── 자동 수집 ─────
    /// <summary>heartbeat 가 왔을 때 — 처음 보인 호기·재부팅·원격 저장 확정이면 3초 뒤 수집하도록 예약 (수신 루프: 메모리 연산만)</summary>
    void OnStatus(AgvState a, BridgeStatus? prev, BridgeStatus cur)
    {
        if (!SupportsCfgJson(cur.Fw)) return;
        bool reboot = prev is not null && cur.UpSeconds < prev.UpSeconds;
        bool confirmed = prev is not null && prev.Cfg != cur.Cfg && cur.Cfg == "saved";
        if (prev is null || reboot || confirmed) _due.TryAdd(a.Id, DateTime.UtcNow.AddSeconds(3));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var nextAll = DateTime.UtcNow.AddSeconds(20);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                var now = DateTime.UtcNow;
                foreach (var (agv, due) in _due.ToArray())
                {
                    if (due > now) continue;
                    _due.TryRemove(agv, out _);
                    if (_lastTry.TryGetValue(agv, out var t) && (now - t).TotalSeconds < 30) continue;     // 방금 읽었다
                    var a = _reg.Get(agv);
                    if (a is null || !a.Online(_opt.OfflineAfterSec) || !SupportsCfgJson(a.Status?.Fw)) continue;
                    var r = await CollectAsync(agv, ct);
                    if (!r.Ok) _log.LogDebug("설정 자동 수집 실패 ({Agv:D2}): {Msg}", agv, r.Message);
                }
                if (_opt.CfgCollectIntervalMin > 0 && now >= nextAll)
                {
                    nextAll = now.AddMinutes(_opt.CfgCollectIntervalMin);
                    var targets = _reg.Snapshot().Where(x => x.Online(_opt.OfflineAfterSec) && SupportsCfgJson(x.Status?.Fw)).Select(x => x.Id).ToList();
                    if (targets.Count > 0) await CollectManyAsync(targets, 4, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
