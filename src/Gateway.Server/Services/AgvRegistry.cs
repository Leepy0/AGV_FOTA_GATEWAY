using System.Collections.Concurrent;
using System.Net;
using Gateway.Core.Bridge;
using Gateway.Server.Data;

namespace Gateway.Server.Services;

/// <summary>호기 하나의 최신 상태 (heartbeat/INFO). 메인보드 버전·스테이징 상태도 heartbeat 의 dsp 필드로 온다</summary>
public sealed class AgvState
{
    public int Id { get; init; }
    public IPEndPoint? EndPoint { get; set; }
    public DateTime LastSeen { get; set; }
    public BridgeStatus? Status { get; set; }
    /// <summary>heartbeat 원문 (JSON 파싱에 실패해도 보관)</summary>
    public string StatusRaw { get; set; } = "";
    public long MainUp, MainDown, SeqLoss;
    public string LastFrame { get; set; } = "";
    public Dictionary<byte, ushort> LastSeq { get; } = new();
    /// <summary>DB 에 마지막으로 기록한 시각·버전 묶음 (30초마다 한 번, 버전이 바뀌면 바로)</summary>
    public DateTime LastPersisted { get; set; }
    public string LastPersistedKey { get; set; } = "";
    public bool Online(int offlineAfterSec) => (DateTime.UtcNow - LastSeen).TotalSeconds < offlineAfterSec;
}

/// <summary>호기 목록. 변경 알림은 최대 초당 몇 번으로 묶어서 보낸다. 마지막 접속은 SQLite 에 남겨 재시작 뒤에도 "n시간 전" 으로 보인다</summary>
public sealed class AgvRegistry
{
    readonly ConcurrentDictionary<int, AgvState> _agvs = new();
    readonly GatewayDb? _db;
    public event Action? Changed;
    int _pending;

    public AgvRegistry() { }

    public AgvRegistry(GatewayDb db)
    {
        _db = db;
        using var c = db.Open();
        using var cmd = GatewayDb.Cmd(c, "SELECT agv, last_seen, endpoint, status_json FROM agv_last_seen");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var a = new AgvState { Id = r.GetInt32(0), LastSeen = GatewayDb.Dt(r.GetString(1)), LastPersisted = DateTime.UtcNow };
            if (!r.IsDBNull(2) && IPEndPoint.TryParse(r.GetString(2), out var ep)) a.EndPoint = ep;
            if (!r.IsDBNull(3)) { a.StatusRaw = r.GetString(3); a.Status = BridgeStatus.Parse(System.Text.Encoding.UTF8.GetBytes(a.StatusRaw)); }
            a.LastPersistedKey = $"{a.Status?.Fw}|{a.Status?.Part}|{a.Status?.Dsp}|{a.Status?.DspBoot}|{a.EndPoint}";
            _agvs[a.Id] = a;
        }
    }

    /// <summary>heartbeat 를 받을 때 호출: 30초에 한 번 DB 에 마지막 접속·상태를 남긴다 (브리지 50대 × 1Hz 에도 쓰기는 분당 100번 이하)</summary>
    public void Touch(AgvState a)
    {
        var key = $"{a.Status?.Fw}|{a.Status?.Part}|{a.Status?.Dsp}|{a.Status?.DspBoot}|{a.EndPoint}";
        if (_db is null || ((DateTime.UtcNow - a.LastPersisted).TotalSeconds < 30 && key == a.LastPersistedKey)) return;
        a.LastPersisted = DateTime.UtcNow; a.LastPersistedKey = key;
        try
        {
            using var c = _db.Open();
            GatewayDb.Exec(c, """
                INSERT OR REPLACE INTO agv_last_seen(agv, last_seen, endpoint, fw, part, dsp, dsp_boot, status_json)
                VALUES($agv, $seen, $ep, $fw, $part, $dsp, $dspb, $js)
                """, ("$agv", a.Id), ("$seen", GatewayDb.Ts(a.LastSeen)), ("$ep", a.EndPoint?.ToString()), ("$fw", a.Status?.Fw), ("$part", a.Status?.Part),
                ("$dsp", a.Status?.Dsp), ("$dspb", a.Status?.DspBoot), ("$js", a.StatusRaw.Length > 0 ? a.StatusRaw : null));
        }
        catch (Exception) { }
    }

    public AgvState GetOrAdd(int id) => _agvs.GetOrAdd(id, i => new AgvState { Id = i });
    public AgvState? Get(int id) => _agvs.TryGetValue(id, out var a) ? a : null;
    public IReadOnlyList<AgvState> Snapshot() => _agvs.Values.OrderBy(a => a.Id).ToList();
    public BridgeStatus? StatusOf(int id) => Get(id)?.Status;

    /// <summary>타입별 seq 연속성 검사 → 누락 수</summary>
    public static int CheckSeq(AgvState a, byte type, ushort seq)
    {
        lock (a.LastSeq)
        {
            int loss = 0;
            if (a.LastSeq.TryGetValue(type, out var last))
            {
                var expect = (ushort)(last + 1);
                var diff = (ushort)(seq - expect);
                // 뒤로 간 번호(diff 가 32768 이상)는 브리지 재부팅으로 0 부터 다시 시작한 것 — 손실이 아니다
                if (seq != expect && diff < 32768) loss = diff;
            }
            a.LastSeq[type] = seq;
            return loss;
        }
    }

    public void NotifyChanged()
    {
        // 여러 호기의 heartbeat 가 몰려도 UI 갱신은 200ms 에 한 번
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        _ = Task.Delay(200).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _pending, 0);
            try { Changed?.Invoke(); } catch { }
        });
    }
}
