using System.Collections.Concurrent;
using System.Net;
using Gateway.Core.Bridge;
using Gateway.Server.Data;
using Microsoft.Data.Sqlite;

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

    /// <summary>같은 호기 번호로 다른 주소(장치)에서 패킷이 온 마지막 시각과 그 주소. 기존 endpoint 는 바꾸지 않는다</summary>
    public DateTime ConflictAt { get; set; }
    public IPEndPoint? ConflictEndPoint { get; set; }
    public long ConflictCount;
    /// <summary>번호 충돌 중인지 (최근 offlineAfterSec×2 안에 다른 주소에서 패킷이 왔음). 충돌 중에는 배포·수동 전송을 거부한다</summary>
    public bool InConflict(int offlineAfterSec) => ConflictEndPoint is not null && (DateTime.UtcNow - ConflictAt).TotalSeconds < offlineAfterSec * 2;
    /// <summary>충돌 경고(Trace·로그)를 마지막으로 남긴 시각 — 빈도 제한용</summary>
    public DateTime ConflictWarnedAt { get; set; }
    /// <summary>cfg=trial / ota=pending 확정용 자동 PING 을 마지막으로 보낸 시각 (호기당 5초에 1회)</summary>
    public DateTime LastAutoPing { get; set; }

    /// <summary>시각 동기 (브리지 0.8.0+): heartbeat ts − 서버가 받은 시각 (ms, 양수 = 브리지가 앞섬). 미동기·구 펌웨어면 null</summary>
    public long? ClockSkewMs { get; set; }
    public DateTime ClockSkewAt { get; set; }
    /// <summary>서버가 시각을 먼저 보낸 마지막 시각 (자동 보정 빈도 제한·화면 "시각 동기")</summary>
    public DateTime LastTimePush { get; set; }
    /// <summary>브리지의 TIME_REQ 에 답한 횟수와 마지막 시각</summary>
    public long TimeReplies;
    public DateTime LastTimeReply { get; set; }

    /// <summary>마지막으로 읽은 바닥 RFID 태그(위치 대용)와 읽은 시각 — RFID 포트로 받는다</summary>
    public string LastTag { get; set; } = "";
    public DateTime LastTagAt { get; set; }
    /// <summary>Wi-Fi 분석에 쓸 위치 키: 최근 30분 안에 읽은 태그, 없으면 ""</summary>
    public string LocationTag => LastTag.Length > 0 && (DateTime.UtcNow - LastTagAt).TotalMinutes < 30 ? LastTag : "";
}

/// <summary>
/// 호기 목록. 변경 알림은 최대 초당 몇 번으로 묶어서 보낸다. 마지막 접속은 SQLite 에 남겨 재시작 뒤에도 "n시간 전" 으로 보인다.
/// DB 쓰기는 수신 루프에서 하지 않는다 — Touch 는 표시만 하고 Flush(BridgeUdpService 의 타이머·종료 시)가 모아서 쓴다.
/// </summary>
public sealed class AgvRegistry
{
    /// <summary>레지스트리 쓰기 연결의 잠금 대기 (초). 다른 프로그램이 DB 를 잠가도 오래 막히지 않게</summary>
    const int DbTimeoutSec = 2;
    readonly ConcurrentDictionary<int, AgvState> _agvs = new();
    readonly ConcurrentDictionary<int, byte> _dirty = new();
    readonly object _flushLock = new();
    readonly string? _cs;
    public event Action? Changed;
    int _pending;

    public AgvRegistry() { }

    public AgvRegistry(GatewayDb db)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = db.Path, Pooling = true, DefaultTimeout = DbTimeoutSec }.ToString();
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

    /// <summary>heartbeat 를 받을 때 호출: 30초에 한 번(버전·endpoint 가 바뀌면 바로) DB 기록 대상으로 표시만 한다 — 수신 루프에서 DB I/O 없음</summary>
    public void Touch(AgvState a)
    {
        var key = $"{a.Status?.Fw}|{a.Status?.Part}|{a.Status?.Dsp}|{a.Status?.DspBoot}|{a.EndPoint}";
        if (_cs is null || ((DateTime.UtcNow - a.LastPersisted).TotalSeconds < 30 && key == a.LastPersistedKey)) return;
        a.LastPersisted = DateTime.UtcNow; a.LastPersistedKey = key;
        _dirty[a.Id] = 0;
    }

    /// <summary>
    /// 표시된 호기의 최신 상태를 한 트랜잭션으로 DB 에 쓴다. 잠금 대기는 DbTimeoutSec 까지.
    /// 실패하면 다시 표시해 두고(다음 Flush 에서 재시도) 예외를 그대로 던진다 — 호출 쪽이 빈도를 제한해 로그를 남긴다.
    /// </summary>
    /// <returns>기록한 호기 수</returns>
    public int Flush()
    {
        if (_cs is null) return 0;
        lock (_flushLock)
        {
            if (_dirty.IsEmpty) return 0;
            var list = new List<AgvState>();
            foreach (var id in _dirty.Keys)
                if (_dirty.TryRemove(id, out _) && Get(id) is { } a) list.Add(a);
            try
            {
                using var c = new SqliteConnection(_cs);
                c.Open();
                using var tx = c.BeginTransaction();
                foreach (var a in list)
                {
                    var st = a.Status;
                    using var cmd = GatewayDb.Cmd(c, """
                        INSERT OR REPLACE INTO agv_last_seen(agv, last_seen, endpoint, fw, part, dsp, dsp_boot, status_json)
                        VALUES($agv, $seen, $ep, $fw, $part, $dsp, $dspb, $js)
                        """, ("$agv", a.Id), ("$seen", GatewayDb.Ts(a.LastSeen)), ("$ep", a.EndPoint?.ToString()), ("$fw", st?.Fw), ("$part", st?.Part),
                        ("$dsp", st?.Dsp), ("$dspb", st?.DspBoot), ("$js", a.StatusRaw.Length > 0 ? a.StatusRaw : null));
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
                return list.Count;
            }
            catch
            {
                foreach (var a in list) _dirty.TryAdd(a.Id, 0);
                throw;
            }
        }
    }

    /// <summary>마지막 기록 뒤 패킷이 온 호기를 모두 기록 대상으로 — 종료 직전 마지막 접속 시각을 정확히 남기려고</summary>
    public void MarkAllSeen()
    {
        if (_cs is null) return;
        foreach (var a in _agvs.Values)
            if (a.LastSeen > a.LastPersisted) _dirty[a.Id] = 0;
    }

    /// <summary>DB 에 아직 쓰지 않은 호기 수</summary>
    public int PendingWrites => _dirty.Count;

    public AgvState GetOrAdd(int id) => _agvs.GetOrAdd(id, i => new AgvState { Id = i });
    public AgvState? Get(int id) => _agvs.TryGetValue(id, out var a) ? a : null;
    public IReadOnlyList<AgvState> Snapshot() => _agvs.Values.OrderBy(a => a.Id).ToList();
    public BridgeStatus? StatusOf(int id) => Get(id)?.Status;

    /// <summary>타입별 seq 연속성 검사 → 누락 수 (판정 규칙은 BridgeSeq.Loss)</summary>
    public static int CheckSeq(AgvState a, byte type, ushort seq)
    {
        lock (a.LastSeq)
        {
            int loss = a.LastSeq.TryGetValue(type, out var last) ? BridgeSeq.Loss(last, seq) : 0;
            a.LastSeq[type] = seq;
            return loss;
        }
    }

    /// <summary>seq 기준을 비운다 — 브리지 재부팅(up 감소) 또는 다른 장치로 endpoint 가 바뀐 경우</summary>
    public static void ResetSeq(AgvState a)
    {
        lock (a.LastSeq) a.LastSeq.Clear();
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
