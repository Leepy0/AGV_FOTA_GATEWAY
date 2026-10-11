using System.Text.Json;
using System.Text.Json.Serialization;
using Gateway.Core.Fota;
using Gateway.Server.Data;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Gateway.Server.Tests")]

namespace Gateway.Server.Services;

/// <summary>AwaitingApproval = 메인보드 스테이징을 마치고 운용자의 '적용 시작' 승인을 기다림</summary>
public enum JobState { Scheduled, Running, AwaitingApproval, Done, Failed, Cancelled }
/// <summary>Staged = 브리지에 이미지를 받아 둠 (적용 대기)</summary>
public enum TargetState { Waiting, Running, Staged, Done, Failed, Skipped }
/// <summary>메인보드 작업의 적용 방식. Approve = 전 호기 스테이징 후 승인하면 한 대씩 적용(기본), Immediate = 호기마다 스테이징 직후 바로 적용</summary>
public enum ApplyMode { Approve, Immediate }

public sealed class TargetResult
{
    public int Agv { get; set; }
    public TargetState State { get; set; }
    public int Percent { get; set; }
    public string Message { get; set; } = "";
    public string VersionBefore { get; set; } = "";
    public string VersionAfter { get; set; } = "";
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>메인보드 스테이징 때 쓴 작업 번호 (적용도 같은 번호로 보내야 브리지가 받는다). 0 = 없음</summary>
    public long StageJob { get; set; }
    /// <summary>되돌릴 수 없는 명령(메인보드 APPLY·브리지 OTA_START)을 보냄 — 취소해도 브리지는 끝까지 진행</summary>
    public bool Committed { get; set; }
    public List<string> Log { get; set; } = new();
}

public sealed class Job
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FirmwareId { get; set; } = "";
    public FirmwareKind Kind { get; set; }
    public string FirmwareVersion { get; set; } = "";
    public List<int> Targets { get; set; } = new();
    /// <summary>null = 즉시</summary>
    public DateTime? ScheduledAt { get; set; }
    public bool StopOnFailure { get; set; } = true;
    /// <summary>메인보드만 의미 있음 (브리지는 늘 즉시)</summary>
    public ApplyMode ApplyMode { get; set; } = ApplyMode.Approve;
    /// <summary>마감 시각(UTC). 지나면 남은 호기는 시작하지 않고 건너뜀. null = 제한 없음</summary>
    public DateTime? NotAfter { get; set; }
    /// <summary>적용 승인 시각 (AwaitingApproval 에서 승인되면 실행기가 적용 단계를 시작)</summary>
    public DateTime? ApprovedAt { get; set; }
    public JobState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public List<TargetResult> Results { get; set; } = new();
    [JsonIgnore] public int DoneCount => Results.Count(r => r.State == TargetState.Done);
    [JsonIgnore] public int FailCount => Results.Count(r => r.State == TargetState.Failed);
    [JsonIgnore] public int StagedCount => Results.Count(r => r.State == TargetState.Staged);
    /// <summary>스테이징과 적용을 나눠 승인을 받는 작업인지 (메인보드 + Approve)</summary>
    [JsonIgnore] public bool NeedsApproval => Kind == FirmwareKind.Dsp && ApplyMode == ApplyMode.Approve;
    /// <summary>끝나지 않은 작업 (대기·실행 중·적용 대기) — 펌웨어 삭제 등을 막는다</summary>
    [JsonIgnore] public bool IsActive => State is JobState.Scheduled or JobState.Running or JobState.AwaitingApproval;
}

/// <summary>
/// 작업 목록 (SQLite) + 실행기. 예약 시각이 되면 대상 호기를 한 대씩 순차로 업데이트한다.
/// 메인보드 + 승인 모드: 1단계로 전 호기 스테이징 → 적용 대기(AwaitingApproval) → 승인(Approve) → 2단계로 한 대씩 적용.
/// 한 번에 한 작업만 돌고, 적용 대기 작업이 있는 동안에는 다른 작업을 시작하지 않는다(같은 호기 스테이징을 덮어쓰지 않도록).
/// </summary>
public sealed class JobService : BackgroundService
{
    readonly GatewayOptions _opt;
    readonly FirmwareStore _fw;
    readonly AgvRegistry _reg;
    readonly IAgvLink _link;
    readonly ILogger<JobService> _log;
    readonly GatewayDb _db;
    readonly IHostApplicationLifetime? _life;
    readonly object _lock = new();
    readonly List<Job> _jobs = new();
    readonly Dictionary<string, CancellationTokenSource> _running = new();
    CancellationToken _stopping = CancellationToken.None;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>브리지가 받는 스테이징 URL 최대 길이 (dsp_ota.c cmd_t.url[200])</summary>
    public const int DspUrlMax = 199;
    /// <summary>브리지 OTA URL 최대 길이 (ota.c URL_MAX 256)</summary>
    public const int EspUrlMax = 255;
    /// <summary>예약(또는 '지금 바로' 생성) 시각이 이보다 오래 지나도록 시작하지 못한 작업은 자동 취소 (서버 중단·앞 작업 지연 모두)</summary>
    public const int StaleScheduleMinutes = 30;

    /// <summary>대상 시작 때 오프라인이면 heartbeat 를 이만큼 기다린 뒤 판정 (Wi-Fi 순간 끊김·재시작 직후)</summary>
    internal TimeSpan OnlineWait { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>이 시각 전에는 새 작업을 시작하지 않는다 (기동 직후 heartbeat 수집·HTTP 포트 확정 대기)</summary>
    internal DateTime NotBefore { get; set; } = DateTime.MaxValue;
    /// <summary>마지막으로 시작한 작업의 실행 태스크 (시험용)</summary>
    internal Task LastRun { get; private set; } = Task.CompletedTask;

    public event Action? Changed;

    public JobService(IOptions<GatewayOptions> opt, FirmwareStore fw, AgvRegistry reg, BridgeUdpService link, GatewayDb db,
                      IHostApplicationLifetime life, ILogger<JobService> log)
        : this(opt, fw, reg, (IAgvLink)link, db, log) => _life = life;

    internal JobService(IOptions<GatewayOptions> opt, FirmwareStore fw, AgvRegistry reg, IAgvLink link, GatewayDb db, ILogger<JobService> log)
    {
        _opt = opt.Value; _fw = fw; _reg = reg; _link = link; _db = db; _log = log;
        Load();
        MigrateJson(Path.Combine(_opt.DataDir, "jobs.json"));
        var touched = new List<Job>();
        // 서버가 재시작됐을 때 실행 중이던 작업은 실패로 정리
        foreach (var j in _jobs.Where(j => j.State == JobState.Running))
        {
            j.State = JobState.Failed; j.FinishedAt = DateTime.UtcNow;
            foreach (var r in j.Results.Where(r => r.State == TargetState.Running)) { r.State = TargetState.Failed; r.Message = "서버 재시작"; }
            foreach (var r in j.Results.Where(r => r.State is TargetState.Waiting or TargetState.Staged))
            { r.Message = r.State == TargetState.Staged ? "서버 재시작 — 적용 안 함 (스테이징만 됨)" : "서버 재시작"; r.State = TargetState.Skipped; }
            touched.Add(j);
        }
        // 적용 대기는 유지(브리지에 스테이징이 남아 있음). 승인만 받고 시작 전이었다면 승인을 풀어 다시 정지 확인을 받는다
        foreach (var j in _jobs.Where(j => j.State == JobState.AwaitingApproval && j.ApprovedAt is not null))
        {
            j.ApprovedAt = null;
            _log.LogWarning("job {Id}: 서버 재시작으로 적용 승인 해제 — 화면에서 다시 승인", j.Id);
            touched.Add(j);
        }
        // 서버가 꺼져 있는 동안 예약 시각이 한참 지난 작업은 몰래 실행하지 않고 취소 (야간 예약이 아침에 갑자기 도는 사고 방지)
        touched.AddRange(CancelStale(DateTime.UtcNow, (what, late) => $"서버 중단 중 {what} {late}분 경과 ({StaleScheduleMinutes}분 초과) — 다시 예약하세요"));
        foreach (var j in touched.Distinct()) Save(j);
    }

    // ───── SQLite (jobs + job_targets) ─────

    void Load()
    {
        using var c = _db.Open();
        using (var cmd = GatewayDb.Cmd(c, "SELECT id, name, firmware_id, kind, firmware_version, scheduled_at, stop_on_failure, state, created_at, started_at, finished_at, apply_mode, not_after, approved_at FROM jobs"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                _jobs.Add(new Job
                {
                    Id = r.GetString(0), Name = r.GetString(1), FirmwareId = r.GetString(2), Kind = Enum.Parse<FirmwareKind>(r.GetString(3)), FirmwareVersion = r.GetString(4),
                    ScheduledAt = GatewayDb.DtN(r.IsDBNull(5) ? null : r.GetString(5)), StopOnFailure = r.GetInt64(6) != 0, State = Enum.Parse<JobState>(r.GetString(7)),
                    CreatedAt = GatewayDb.Dt(r.GetString(8)), StartedAt = GatewayDb.DtN(r.IsDBNull(9) ? null : r.GetString(9)), FinishedAt = GatewayDb.DtN(r.IsDBNull(10) ? null : r.GetString(10)),
                    ApplyMode = Enum.TryParse<ApplyMode>(r.GetString(11), out var m) ? m : ApplyMode.Immediate,
                    NotAfter = GatewayDb.DtN(r.IsDBNull(12) ? null : r.GetString(12)), ApprovedAt = GatewayDb.DtN(r.IsDBNull(13) ? null : r.GetString(13)),
                });
        var byId = _jobs.ToDictionary(j => j.Id);
        using (var cmd = GatewayDb.Cmd(c, "SELECT job_id, agv, state, percent, message, version_before, version_after, started_at, finished_at, log, stage_job FROM job_targets ORDER BY job_id, seq"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (!byId.TryGetValue(r.GetString(0), out var j)) continue;
                var t = new TargetResult
                {
                    Agv = r.GetInt32(1), State = Enum.Parse<TargetState>(r.GetString(2)), Percent = r.GetInt32(3), Message = r.GetString(4),
                    VersionBefore = r.GetString(5), VersionAfter = r.GetString(6),
                    StartedAt = GatewayDb.DtN(r.IsDBNull(7) ? null : r.GetString(7)), FinishedAt = GatewayDb.DtN(r.IsDBNull(8) ? null : r.GetString(8)),
                    StageJob = r.IsDBNull(10) ? 0 : r.GetInt64(10),
                };
                var log = r.GetString(9);
                if (log.Length > 0) t.Log = log.Split('\n').ToList();
                j.Results.Add(t);
                j.Targets.Add(t.Agv);
            }
    }

    /// <summary>SQLite 도입 전 jobs.json 이 있으면 한 번 가져오고 .migrated 로 이름을 바꾼다. 실패하면 원본을 그대로 두고 로그만 (서비스는 뜬다)</summary>
    void MigrateJson(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var old = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(path), Json) ?? new();
            int n = 0;
            foreach (var j in old.Where(j => !_jobs.Any(x => x.Id == j.Id)))
            {
                j.ApplyMode = ApplyMode.Immediate;   // jobs.json 시절 작업은 스테이징 직후 바로 적용이었다
                _jobs.Add(j); Save(j); n++;
            }
            File.Move(path, path + ".migrated", true);
            if (n > 0) _log.LogInformation("jobs.json → SQLite: {N}건 이전", n);
        }
        catch (Exception e) { _log.LogError("jobs.json 이전 실패 (원본 보존, 다음 기동 때 다시 시도): {Msg}", e.Message); }
    }

    /// <summary>작업 한 건(대상 포함) 저장. 저장 실패로 서비스가 죽지 않도록 로그만 남긴다 — 메모리 상태는 유지</summary>
    void Save(Job j)
    {
        try
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            GatewayDb.Exec(c, """
                INSERT OR REPLACE INTO jobs(id, name, firmware_id, kind, firmware_version, scheduled_at, stop_on_failure, state, created_at, started_at, finished_at, apply_mode, not_after, approved_at)
                VALUES($id, $name, $fw, $kind, $ver, $sched, $stop, $state, $created, $started, $finished, $mode, $notafter, $approved)
                """, ("$id", j.Id), ("$name", j.Name), ("$fw", j.FirmwareId), ("$kind", j.Kind.ToString()), ("$ver", j.FirmwareVersion),
                ("$sched", GatewayDb.Ts(j.ScheduledAt)), ("$stop", j.StopOnFailure ? 1 : 0), ("$state", j.State.ToString()),
                ("$created", GatewayDb.Ts(j.CreatedAt)), ("$started", GatewayDb.Ts(j.StartedAt)), ("$finished", GatewayDb.Ts(j.FinishedAt)),
                ("$mode", j.ApplyMode.ToString()), ("$notafter", GatewayDb.Ts(j.NotAfter)), ("$approved", GatewayDb.Ts(j.ApprovedAt)));
            int seq = 0;
            foreach (var r in j.Results)
                GatewayDb.Exec(c, """
                    INSERT OR REPLACE INTO job_targets(job_id, agv, seq, state, percent, message, version_before, version_after, started_at, finished_at, log, stage_job)
                    VALUES($job, $agv, $seq, $state, $pct, $msg, $vb, $va, $started, $finished, $log, $stage)
                    """, ("$job", j.Id), ("$agv", r.Agv), ("$seq", seq++), ("$state", r.State.ToString()), ("$pct", r.Percent), ("$msg", r.Message),
                    ("$vb", r.VersionBefore), ("$va", r.VersionAfter), ("$started", GatewayDb.Ts(r.StartedAt)), ("$finished", GatewayDb.Ts(r.FinishedAt)),
                    ("$log", string.Join('\n', r.Log)), ("$stage", r.StageJob == 0 ? null : r.StageJob));
            tx.Commit();
        }
        catch (Exception e) { _log.LogError("작업 저장 실패 ({Id}): {Msg}", j.Id, e.Message); }
    }

    void DeleteRow(string id)
    {
        try { using var c = _db.Open(); GatewayDb.Exec(c, "DELETE FROM jobs WHERE id=$id", ("$id", id)); }   // job_targets 는 CASCADE
        catch (Exception e) { _log.LogError("작업 삭제 실패 ({Id}): {Msg}", id, e.Message); }
    }

    /// <summary>true 면 새 작업(승인된 적용 단계 포함)을 시작하지 않는다 (서버 업데이트 적용 직전 등). 실행 중인 작업에는 영향 없음</summary>
    public bool HoldNewJobs { get; set; }
    /// <summary>
    /// 실행 중인 작업이 있는지. 적용 대기(AwaitingApproval)는 포함하지 않는다 — 서버가 재시작해도 적용 대기는 그대로 남고
    /// (승인만 풀림) 브리지의 스테이징도 유지되므로, 오래 묵은 적용 대기 작업이 서버 업데이트를 막지 않게 한다.
    /// </summary>
    public bool IsBusy { get { lock (_lock) return _running.Count > 0; } }

    public IReadOnlyList<Job> List() { lock (_lock) return _jobs.OrderByDescending(j => j.CreatedAt).ToList(); }
    public Job? Get(string id) { lock (_lock) return _jobs.FirstOrDefault(j => j.Id == id); }

    /// <summary>승인을 기다리는 작업 (있으면 다른 작업은 시작하지 않는다)</summary>
    public Job? AwaitingJob() { lock (_lock) return _jobs.FirstOrDefault(j => j.State == JobState.AwaitingApproval); }

    /// <summary>지금 실행 중·적용 대기인 작업이 끝나면 곧바로 시작될 대기 작업 (예약 시각이 지났거나 '지금 바로')</summary>
    public IReadOnlyList<Job> QueuedDue(string? exceptId = null)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
            return _jobs.Where(j => j.Id != exceptId && j.State == JobState.Scheduled && (j.ScheduledAt is null || j.ScheduledAt <= now))
                        .OrderBy(j => j.ScheduledAt ?? j.CreatedAt).ToList();
    }

    /// <summary>이 펌웨어를 쓰는 끝나지 않은 작업</summary>
    public IReadOnlyList<Job> ActiveJobsUsing(string firmwareId) { lock (_lock) return _jobs.Where(j => j.FirmwareId == firmwareId && j.IsActive).ToList(); }

    /// <summary>끝나지 않은 작업이 쓰지 않으면 펌웨어를 지운다 (작업 lock 안에서 검사·삭제 — 그 사이에 작업이 생기지 않음)</summary>
    public bool DeleteFirmwareIfUnused(string firmwareId, out IReadOnlyList<Job> inUse)
    {
        lock (_lock)
        {
            inUse = _jobs.Where(j => j.FirmwareId == firmwareId && j.IsActive).ToList();
            return inUse.Count == 0 && _fw.Delete(firmwareId);
        }
    }

    public Job Create(string name, string firmwareId, IEnumerable<int>? targets, DateTime? scheduledAtUtc, bool stopOnFailure,
                      ApplyMode applyMode = ApplyMode.Approve, DateTime? notAfterUtc = null)
    {
        var now = DateTime.UtcNow;
        var t = (targets ?? []).Distinct().OrderBy(x => x).ToList();
        if (t.Count == 0) throw new InvalidOperationException("대상 호기가 없습니다");
        if (t.Any(x => x is < 1 or > 99)) throw new InvalidOperationException("호기 번호는 1~99");
        if (scheduledAtUtc is { } s && s < now.AddMinutes(-1)) throw new InvalidOperationException("예약 시각이 이미 지났습니다");
        if (notAfterUtc is { } na)
        {
            if (na <= now) throw new InvalidOperationException("마감 시각이 이미 지났습니다");
            if (scheduledAtUtc is { } s2 && na <= s2) throw new InvalidOperationException("마감 시각이 예약 시각보다 앞섭니다");
        }
        Job j;
        lock (_lock)
        {
            // 펌웨어 확인도 lock 안에서 (DeleteFirmwareIfUnused 와 엇갈리지 않게)
            var fw = _fw.Get(firmwareId) ?? throw new InvalidOperationException("펌웨어 없음");
            j = new Job
            {
                Id = now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4],
                Name = string.IsNullOrWhiteSpace(name) ? $"{(fw.Kind == FirmwareKind.Dsp ? "메인보드" : "브리지")} {fw.Version}" : name.Trim(),
                FirmwareId = fw.Id, Kind = fw.Kind, FirmwareVersion = fw.Version, Targets = t,
                ScheduledAt = scheduledAtUtc, StopOnFailure = stopOnFailure, State = JobState.Scheduled, CreatedAt = now,
                // 브리지 작업은 스테이징 분리가 없어 늘 즉시
                ApplyMode = fw.Kind == FirmwareKind.Dsp ? applyMode : ApplyMode.Immediate, NotAfter = notAfterUtc,
                Results = t.Select(a => new TargetResult { Agv = a }).ToList(),
            };
            _jobs.Add(j); Save(j);
        }
        Notify();
        return j;
    }

    /// <summary>
    /// 작업 취소. 대기 → 취소, 적용 대기 → 취소(브리지에는 스테이징만 남음), 실행 중 → 토큰 취소(이미 APPLY·OTA_START 를 보낸 호기는 브리지가 끝까지 진행).
    /// cancelQueued 면 이 작업이 끝나자마자 이어서 시작될 대기 작업(QueuedDue)도 함께 취소한다.
    /// </summary>
    public bool Cancel(string id, bool cancelQueued = false)
    {
        lock (_lock)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j is null) return false;
            var queued = cancelQueued && j.State is JobState.Running or JobState.AwaitingApproval ? QueuedDue(id) : [];
            switch (j.State)
            {
                case JobState.Scheduled:
                    Finish(j, JobState.Cancelled, "취소");
                    break;
                case JobState.AwaitingApproval:
                    Finish(j, JobState.Cancelled, "취소 — 적용 안 함 (브리지에는 스테이징만 남음)");
                    _log.LogInformation("job {Id}: 적용 대기 중 취소", j.Id);
                    break;
                case JobState.Running when _running.TryGetValue(id, out var cts):
                    cts.Cancel();
                    break;
                default:
                    return false;
            }
            foreach (var q in queued) Finish(q, JobState.Cancelled, $"앞 작업 '{j.Name}' 과 함께 취소");
        }
        Notify();
        return true;
    }

    /// <summary>
    /// 적용 승인 (화면에서 'AGV 정지 확인' 뒤). 실행기가 다음 루프(1초 안)에 스테이징된 호기만 한 대씩 적용한다.
    /// 없는 작업이면 false, 적용 대기가 아니거나 마감이 지났으면 예외.
    /// </summary>
    public bool Approve(string id)
    {
        lock (_lock)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j is null) return false;
            if (j.State != JobState.AwaitingApproval) throw new InvalidOperationException("적용 대기 중인 작업이 아닙니다");
            if (j.NotAfter is { } na && na <= DateTime.UtcNow) throw new InvalidOperationException("마감 시각이 지났습니다 — 작업을 취소하고 다시 만드세요");
            if (j.ApprovedAt is not null) return true;
            j.ApprovedAt = DateTime.UtcNow;
            Save(j);
            _log.LogInformation("job {Id}: 적용 승인 ({N}대 스테이징됨)", j.Id, j.StagedCount);
        }
        Notify();
        return true;
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j is null || j.IsActive) return false;
            _jobs.Remove(j); DeleteRow(id);
        }
        Notify();
        return true;
    }

    /// <summary>작업을 끝냄 (lock 안에서). 끝나지 않은 대상은 건너뜀으로</summary>
    void Finish(Job j, JobState state, string targetMessage)
    {
        j.State = state; j.FinishedAt = DateTime.UtcNow;
        foreach (var r in j.Results.Where(r => r.State is TargetState.Waiting or TargetState.Staged)) { r.State = TargetState.Skipped; r.Message = targetMessage; }
        Save(j);
    }

    /// <summary>예약(또는 생성) 시각이 StaleScheduleMinutes 넘게 지나도록 시작하지 못한 대기 작업을 취소 (lock 안에서 또는 생성자에서)</summary>
    List<Job> CancelStale(DateTime now, Func<string, int, string> message)
    {
        var stale = now.AddMinutes(-StaleScheduleMinutes);
        var list = _jobs.Where(j => j.State == JobState.Scheduled && (j.ScheduledAt ?? j.CreatedAt) < stale).ToList();
        foreach (var j in list)
        {
            var late = (int)(now - (j.ScheduledAt ?? j.CreatedAt)).TotalMinutes;
            j.State = JobState.Cancelled; j.FinishedAt = now;
            var msg = message(j.ScheduledAt is null ? "생성 시각" : "예약 시각", late);
            foreach (var r in j.Results) { r.State = TargetState.Skipped; r.Message = msg; }
            _log.LogWarning("job {Id}: 예약 시각 {Late}분 경과로 자동 취소", j.Id, late);
        }
        return list;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _stopping = ct;
        // 기동 직후에는 heartbeat 를 아직 못 받았고(재시작 전 LastSeen 만 있음) HTTP 포트도 정해지기 전이다
        // → ApplicationStarted 뒤 OfflineAfterSec×2 초 동안은 새 작업을 시작하지 않는다
        if (_life is not null)
        {
            var started = new TaskCompletionSource();
            using (_life.ApplicationStarted.Register(() => started.TrySetResult()))
                try { await started.Task.WaitAsync(ct); } catch (OperationCanceledException) { return; }
        }
        NotBefore = DateTime.UtcNow.AddSeconds(Math.Max(1, _opt.OfflineAfterSec) * 2);
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception e) { _log.LogError(e, "작업 스케줄러 오류"); }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>스케줄러 한 번: 늦은 예약 취소 → 마감 지난 적용 대기 정리 → (비어 있으면) 승인된 적용 또는 다음 작업 시작. 상태가 바뀐 때만 알린다</summary>
    internal void Tick()
    {
        bool changed = false;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            changed |= CancelStale(now, (what, late) => $"앞 작업이 길어져 {what} {late}분 경과 — 다시 예약하세요").Count > 0;
            foreach (var j in _jobs.Where(j => j.State == JobState.AwaitingApproval && j.NotAfter is { } na && na <= now).ToList())
            {
                Finish(j, JobState.Failed, "마감 시각 지남 — 적용 안 함 (스테이징만 됨)");
                _log.LogWarning("job {Id}: 적용 대기 중 마감 시각 지남", j.Id);
                changed = true;
            }
            // 한 번에 한 작업만 (호기 순차 실행과 같은 이유: 무선·서버 부하, AGV 정지 확인)
            if (_running.Count == 0 && !HoldNewJobs && now >= NotBefore)
            {
                Job? next = _jobs.Where(j => j.State == JobState.AwaitingApproval && j.ApprovedAt is not null).OrderBy(j => j.ApprovedAt).FirstOrDefault();
                bool applyPhase = next is not null;
                // 승인 안 된 적용 대기 작업이 있으면 다른 작업은 기다린다 (같은 호기의 스테이징을 덮어쓰지 않게)
                if (next is null && !_jobs.Any(j => j.State == JobState.AwaitingApproval))
                    next = _jobs.Where(j => j.State == JobState.Scheduled && (j.ScheduledAt is null || j.ScheduledAt <= now))
                                .OrderBy(j => j.ScheduledAt ?? j.CreatedAt).FirstOrDefault();
                if (next is not null) { Start(next, applyPhase); changed = true; }
            }
        }
        if (changed) Notify();
    }

    void Start(Job job, bool applyPhase)
    {
        job.State = JobState.Running;
        if (!applyPhase) job.StartedAt = DateTime.UtcNow;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        _running[job.Id] = cts;
        Save(job);
        LastRun = RunJobAsync(job, applyPhase, cts.Token).ContinueWith(_ =>
        {
            lock (_lock) { _running.Remove(job.Id); cts.Dispose(); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    async Task RunJobAsync(Job job, bool applyPhase, CancellationToken ct)
    {
        // 어떤 예외가 나도(저장 실패 등) 작업이 Running 으로 고착되지 않도록 전체를 감싼다
        try { await RunJobCoreAsync(job, applyPhase, ct); }
        catch (Exception e)
        {
            _log.LogError(e, "job {Id} 실행기 오류", job.Id);
            foreach (var r in job.Results.Where(r => r.State is TargetState.Running or TargetState.Waiting or TargetState.Staged)) { r.State = TargetState.Failed; r.Message = "실행기 오류: " + e.Message; }
            job.State = JobState.Failed; job.FinishedAt = DateTime.UtcNow;
            Persist(job);
        }
    }

    async Task RunJobCoreAsync(Job job, bool applyPhase, CancellationToken ct)
    {
        // 단계: 승인 모드 메인보드는 stage(1단계) → apply(2단계), 그 외는 호기마다 끝까지(full)
        bool stagePhase = job.NeedsApproval && !applyPhase;
        _log.LogInformation("job {Id} {Phase}: {Kind} {Ver} → {N} agv", job.Id, stagePhase ? "staging" : applyPhase ? "apply" : "start", job.Kind, job.FirmwareVersion, job.Targets.Count);
        var fw = _fw.Get(job.FirmwareId);
        bool stop = false;
        foreach (var r in job.Results)
        {
            if (applyPhase && r.State != TargetState.Staged) continue;   // 스테이징에 실패한 호기는 그대로
            var skip = fw is null ? "펌웨어 삭제됨" : ct.IsCancellationRequested ? "취소" : stop ? "앞 호기 실패로 중단"
                     : job.NotAfter is { } na && na <= DateTime.UtcNow ? "마감 시각 지남" : null;
            if (skip is not null)
            {
                r.State = TargetState.Skipped; r.Message = applyPhase ? skip + " — 적용 안 함 (스테이징만 됨)" : skip;
                continue;
            }
            r.State = TargetState.Running; r.StartedAt = DateTime.UtcNow; r.FinishedAt = null; r.Committed = false;
            if (!applyPhase) r.Percent = 0;
            Persist(job);
            try
            {
                if (job.Kind == FirmwareKind.Esp) await RunEspAsync(fw!, r, ct);
                else if (stagePhase) await StageDspAsync(fw!, r, ct);
                else if (applyPhase) await ApplyDspAsync(fw!, r, ct);
                else { await StageDspAsync(fw!, r, ct); await ApplyDspAsync(fw!, r, ct); }
                if (stagePhase) { r.State = TargetState.Staged; r.Percent = 30; Add(r, "스테이징 완료 — 적용 대기 (전 호기 스테이징 뒤 화면에서 '적용 시작')"); }
                else { r.State = TargetState.Done; r.Percent = 100; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                r.State = TargetState.Failed;
                if (r.Committed)
                {
                    // 브리지에는 중단 명령이 없다 — 이미 굽기·OTA 를 시작한 호기는 끝까지 진행되므로 결과를 heartbeat 로 따라가 본다
                    r.Message = "취소 — 브리지는 계속 진행 (heartbeat 버전 확인)";
                    FollowUpAfterCancel(job, r);
                }
                else r.Message = "취소";
            }
            catch (Exception e)
            {
                r.State = TargetState.Failed; r.Message = WithHint(e.Message); r.Log.Add("ERROR " + r.Message);
                if (job.StopOnFailure) stop = true;
            }
            r.FinishedAt = DateTime.UtcNow;
            Persist(job);
        }
        if (stagePhase && !ct.IsCancellationRequested && !stop && job.StagedCount > 0 && fw is not null)
        {
            // 1단계 끝 — 실행기를 비우고 승인을 기다린다
            lock (_lock) { job.State = JobState.AwaitingApproval; job.ApprovedAt = null; Save(job); }
            Notify();
            _log.LogInformation("job {Id} awaiting approval: staged {Staged} fail {Fail}", job.Id, job.StagedCount, job.FailCount);
            return;
        }
        foreach (var r in job.Results.Where(r => r.State == TargetState.Staged))
        { r.State = TargetState.Skipped; r.Message = (ct.IsCancellationRequested ? "취소" : stop ? "앞 호기 실패로 중단" : "적용 안 함") + " (스테이징만 됨)"; }
        job.State = ct.IsCancellationRequested ? JobState.Cancelled : job.Results.All(r => r.State == TargetState.Done) ? JobState.Done : JobState.Failed;
        job.FinishedAt = DateTime.UtcNow;
        Persist(job);
        _log.LogInformation("job {Id} {State}: done {Done} fail {Fail}", job.Id, job.State, job.DoneCount, job.FailCount);
    }

    void Add(TargetResult r, string line)
    {
        // 화면·JSON 직렬화가 다른 스레드에서 열거하므로 리스트를 고치지 않고 새 리스트로 바꿔 끼운다 (copy-on-write)
        var log = new List<string>(r.Log.Count + 1);
        log.AddRange(r.Log.Count >= 200 ? r.Log.Skip(r.Log.Count - 199) : r.Log);
        log.Add($"{DateTime.Now:HH:mm:ss} {line}");
        r.Log = log;
        r.Message = line;
        Notify();
    }

    /// <summary>대상 시작 전 확인: 오프라인이면 OnlineWait 동안 heartbeat 를 기다리고, 번호 충돌이면 거부</summary>
    async Task<AgvState> WaitOnlineAsync(TargetResult r, CancellationToken ct)
    {
        var agv = _reg.Get(r.Agv);
        if (agv is null || !agv.Online(_opt.OfflineAfterSec))
        {
            Add(r, $"heartbeat 대기 (최대 {OnlineWait.TotalSeconds:F0}초)");
            var until = DateTime.UtcNow + OnlineWait;
            while (DateTime.UtcNow < until && (agv is null || !agv.Online(_opt.OfflineAfterSec)))
            {
                await Task.Delay(500, ct);
                agv = _reg.Get(r.Agv);
            }
            if (agv is null || !agv.Online(_opt.OfflineAfterSec)) throw new InvalidOperationException($"호기 오프라인 (heartbeat 없음 {OnlineWait.TotalSeconds:F0}초)");
        }
        if (agv.InConflict(_opt.OfflineAfterSec)) throw new InvalidOperationException("호기 번호 충돌 — 같은 번호의 장치가 둘");
        return agv;
    }

    /// <summary>메인보드 OTA 를 받을 수 있는 브리지인지</summary>
    static Gateway.Core.Bridge.BridgeStatus CheckDspBridge(AgvState agv)
    {
        var st = agv.Status;
        if (st is null || string.IsNullOrEmpty(st.DspState)) throw new InvalidOperationException($"브리지 펌웨어가 메인보드 OTA 를 지원하지 않음 (fw {st?.Fw ?? "?"}, 0.5.0 이상 필요)");
        if (st.DspState == "nopart") throw new InvalidOperationException($"브리지에 dspfw 파티션이 없음 (fw {st.Fw}) — 구 파티션 테이블인 채 OTA 로 올라온 브리지. USB 로 한 번 전체 굽기 필요");
        if (st.DspState == "programming") throw new InvalidOperationException("브리지가 이미 메인보드를 프로그래밍 중");
        return st;
    }

    async Task<(FirmwareFile file, DspfwInfo img)> LoadDspImageAsync(FirmwareEntry fw, CancellationToken ct)
    {
        var file = fw.Files.First();
        var img = DspfwImage.Parse(await File.ReadAllBytesAsync(_fw.PathOf(fw, file), ct)) ?? throw new InvalidOperationException("이미지 파일 손상 (.dspfw CRC)");
        return (file, img);
    }

    void OnDspStage(TargetResult r, DspOtaResult x)
    {
        r.Percent = x.Stage switch
        {
            "download" => 5 + x.Pct * 25 / 100, "staged" => 30, "start" => 32, "enter" => 35, "erase" => 40,
            "progress" => 45 + x.Pct * 45 / 100, "verify" => 92, "done" => 100, _ => r.Percent,
        };
        Add(r, x.Stage switch
        {
            // 복구 적용: 부트로더가 HELLO 를 잡지 못함 — 운용자가 메인보드 전원을 껐다 켜야 함 (브리지가 10초마다 보고, 최대 60초)
            "wait_power" => "메인보드 전원을 껐다 켜세요 — 브리지가 부트로더를 기다리는 중 (최대 60초)",
            "retry" => $"굽기 재시도{(x.Pct >= 0 ? $" {x.Pct}" : "")}",
            _ => x.Stage + (x.Pct >= 0 ? $" {x.Pct}%" : "") + (x.Ver.Length > 0 ? $" ver {x.Ver}" : "") + (x.Err.Length > 0 ? $" err {x.Err}" : ""),
        });
    }

    /// <summary>브리지 오류 코드에 운용자용 설명을 덧붙인다</summary>
    static string WithHint(string msg)
    {
        string? hint = msg switch
        {
            _ when msg.Contains("app_ng") => "메인보드 앱이 거부 — AGV 가 정지 상태인지 확인",
            _ when msg.Contains("app_no_reply") => "메인보드 앱이 응답하지 않음 — 정지 확인 후 다시 (앱이 멈춘 호기는 복구 적용 필요)",
            _ when msg.Contains("uart_busy") => "브리지 → 메인보드 송신 대기열이 가득 참 — 잠시 뒤 다시",
            _ when msg.Contains("url_host") => "URL 주소가 브리지에 설정된 서버 IP 와 다름 — PublicBaseUrl·브리지 서버 IP 확인",
            _ when msg.Contains("url_too_long") => "URL 이 브리지 한도를 넘음 — 펌웨어 파일명을 줄여 다시 올리기",
            _ when msg.Contains("ver_invalid") => "메인보드 버전 형식 오류 (1~8자 영문·숫자·._-)",
            _ when msg.Contains("image_bad") => "이미지 검증 실패 — dsp_pack.py 로 다시 만들어 올리기",
            _ when msg.Contains("no_bootloader") => "부트로더 응답 없음 — 메인보드 전원·배선 확인",
            _ => null,
        };
        return hint is null ? msg : $"{msg} — {hint}";
    }

    // 메인보드 OTA B안 1단계: 브리지에 .dspfw 스테이징(0x32). 주행 중에도 가능 (브리지가 받아 두기만 함)
    async Task StageDspAsync(FirmwareEntry fw, TargetResult r, CancellationToken ct)
    {
        var agv = await WaitOnlineAsync(r, ct);
        var st = CheckDspBridge(agv);
        r.VersionBefore = st.Dsp;
        var (file, img) = await LoadDspImageAsync(fw, ct);
        var url = $"{BaseUrlFor(r.Agv)}/fw/{Uri.EscapeDataString(fw.Id)}/{Uri.EscapeDataString(file.FileName)}";
        if (url.Length > DspUrlMax) throw new InvalidOperationException($"스테이징 URL 이 {url.Length}자 — 브리지 한도 {DspUrlMax}자 초과 (펌웨어 파일명을 줄여 다시 올리세요)");
        // 브리지 보고와 대조할 작업 번호 — 브리지가 u32 로 읽으므로 4,294,967,295 이하 (초 단위 시각 × 100 + 호기, 약 115일 주기)
        r.StageJob = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 10_000_000) * 100 + r.Agv;
        Add(r, $"현재 {(st.HasDsp ? st.Dsp : "?")} (부트로더 {st.DspBoot}) → {img.Version} 스테이징 {file.FileName} ({img.Size:N0}B)");
        using var c = new DspOtaClient(_link, _reg.StatusOf, r.Agv);
        await c.StageAsync(url, img, r.StageJob, x => OnDspStage(r, x), ct);
    }

    // 메인보드 OTA B안 2단계: 적용(0x34, 스테이징 때와 같은 job) → 보고(0x33) → heartbeat 의 dsp 버전 확인
    async Task ApplyDspAsync(FirmwareEntry fw, TargetResult r, CancellationToken ct)
    {
        var agv = await WaitOnlineAsync(r, ct);
        var st = CheckDspBridge(agv);
        var (_, img) = await LoadDspImageAsync(fw, ct);
        if (r.StageJob == 0) throw new InvalidOperationException("스테이징 작업 번호 없음 — 다시 배포하세요");
        // 앱이 버전에 응답하지 않는 호기(dsp=?: 구 앱·앱 멈춤)는 복구 적용 — 브리지 0.5.2 는 recover 가 있을 때만 앱 무응답 시 부트로더로 바로 들어간다
        bool recover = !st.HasDsp;
        if (recover) Add(r, "메인보드 앱이 버전에 응답하지 않음 (dsp=?) — 복구 적용: 앱이 응답하지 않으면 브리지가 부트로더로 바로 들어감. 안 되면 메인보드 전원을 껐다 켜세요");
        Add(r, $"적용{(recover ? " (복구)" : "")} — 메인보드 앱 정지, 약 20~30초");
        using var c = new DspOtaClient(recover ? new RecoverLink(_link) : _link, _reg.StatusOf, r.Agv);
        r.Committed = true;
        string ver;
        try { ver = await c.ApplyAsync(r.StageJob, x => OnDspStage(r, x), ct); }
        catch (DspOtaException e) when (e.Message.Contains("not_staged") || e.Message.Contains("job_mismatch"))
        {
            r.Committed = false;
            throw new InvalidOperationException(e.Message + " — 브리지의 스테이징이 없어졌거나 바뀜. 다시 배포하세요");
        }
        r.VersionAfter = ver;
        if (!DspVersion.Matches(ver, img.Version)) throw new InvalidOperationException($"적용 후 앱 버전 {ver} 이 이미지 {img.Version} 과 다름");
        var after = await WaitDspVersionAsync(r.Agv, img.Version, TimeSpan.FromSeconds(30), ct);
        Add(r, $"완료 ver {after} (heartbeat 확인)");
    }

    /// <summary>heartbeat 의 dsp 버전이 기대값(앞 8자)과 같아질 때까지</summary>
    async Task<string> WaitDspVersionAsync(int agv, string expected, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            var cur = _reg.StatusOf(agv)?.Dsp;
            if (DspVersion.Matches(cur, expected)) return cur!;
            if (DateTime.UtcNow >= until) throw new InvalidOperationException($"heartbeat 의 메인보드 버전이 {expected} 가 아님 (현재 {cur ?? "?"})");
            await Task.Delay(500, ct);
        }
    }

    /// <summary>취소했지만 브리지가 계속 진행하는 호기: 3분 동안 heartbeat 버전을 보고 결과를 기록에 남긴다</summary>
    void FollowUpAfterCancel(Job job, TargetResult r)
    {
        var expected = job.FirmwareVersion;
        bool dsp = job.Kind == FirmwareKind.Dsp;
        _ = Task.Run(async () =>
        {
            try
            {
                var until = DateTime.UtcNow.AddMinutes(3);
                while (DateTime.UtcNow < until)
                {
                    var st = _reg.StatusOf(r.Agv);
                    var cur = dsp ? st?.Dsp : st?.Fw;
                    if (dsp ? DspVersion.Matches(cur, expected) && st?.DspState != "programming" : cur == expected)
                    {
                        r.VersionAfter = cur!;
                        Add(r, $"취소 — 브리지가 끝까지 진행해 {cur} 로 올라감 (heartbeat 확인)");
                        Persist(job);
                        return;
                    }
                    await Task.Delay(1000, _stopping);
                }
                Add(r, $"취소 — 3분 안에 새 버전 확인 안 됨 (현재 {(dsp ? _reg.StatusOf(r.Agv)?.Dsp : _reg.StatusOf(r.Agv)?.Fw) ?? "?"}) — 호기 현황에서 확인하세요");
                Persist(job);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { _log.LogWarning("job {Id} 취소 뒤 확인 오류: {Msg}", job.Id, e.Message); }
        });
    }

    string BaseUrlFor(int agv)
    {
        var baseUrl = _opt.PublicBaseUrl?.TrimEnd('/');
        if (!string.IsNullOrEmpty(baseUrl)) return baseUrl;
        var ip = _link.LocalAddressToward(agv) ?? throw new InvalidOperationException("서버 IP 를 알 수 없음 — PublicBaseUrl 설정");
        return $"http://{ip}:{HttpPort}";
    }

    async Task RunEspAsync(FirmwareEntry fw, TargetResult r, CancellationToken ct)
    {
        var agv = await WaitOnlineAsync(r, ct);
        r.VersionBefore = agv.Status?.Fw ?? "";
        if (agv.Status?.Fw == fw.Version)
            Add(r, $"이미 {fw.Version} 실행 중 — 그래도 진행 (재설치)");
        if (agv.Status?.Ota == "pending")
        {
            // 이전 OTA 가 확정되지 않은 상태(pending)에서는 새 이미지를 받으면 롤백 정책과 충돌할 수 있다 — 먼저 PING 으로 확정
            Add(r, "이전 업데이트가 미확정(pending) — PING 으로 확정 후 진행");
            using var sub = _link.Subscribe(r.Agv);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && _reg.StatusOf(r.Agv)?.Ota == "pending")
            {
                _link.SendPing(r.Agv);
                await Task.Delay(1000, ct);
            }
            if (_reg.StatusOf(r.Agv)?.Ota == "pending")
                throw new InvalidOperationException("브리지가 이전 업데이트를 확정하지 못함 (ota=pending 유지) — 브리지 재부팅 후 다시 시도");
        }
        var file = fw.Files.First();
        var url = $"{BaseUrlFor(r.Agv)}/fw/{Uri.EscapeDataString(fw.Id)}/{Uri.EscapeDataString(file.FileName)}";
        if (url.Length > EspUrlMax) throw new InvalidOperationException($"OTA URL 이 {url.Length}자 — 브리지 한도 {EspUrlMax}자 초과 (펌웨어 파일명을 줄여 다시 올리세요)");
        var client = new EspOtaClient(_link, _reg.StatusOf);
        r.Percent = 10;
        r.Committed = true;   // OTA_START 를 보내면 브리지는 받아서 재부팅까지 간다
        var ver = await client.RunAsync(r.Agv, url, fw.Version, line => { Add(r, line); if (line.StartsWith("재부팅")) r.Percent = 70; }, ct);
        r.VersionAfter = ver;
    }

    /// <summary>
    /// 복구 적용용 링크: APPLY JSON 에 "recover":1 을 덧붙인다 (브리지 0.5.2 가 이 플래그가 있을 때만 앱 무응답 시 부트로더 직행·HELLO 반복).
    /// DspOtaClient.ApplyAsync 에 recover 인자가 생기면 그쪽으로 바꾼다.
    /// </summary>
    sealed class RecoverLink(IAgvLink inner) : IAgvLink
    {
        public bool SendDspOtaApply(int agv, string json) =>
            inner.SendDspOtaApply(agv, json.TrimEnd().EndsWith('}') ? json.TrimEnd()[..^1] + ",\"recover\":1}" : json);
        public bool SendMainDown(int agv, ReadOnlySpan<byte> frame) => inner.SendMainDown(agv, frame);
        public bool SendOtaStart(int agv, string url) => inner.SendOtaStart(agv, url);
        public bool SendDspOtaStart(int agv, string json) => inner.SendDspOtaStart(agv, json);
        public bool SendInfoRequest(int agv) => inner.SendInfoRequest(agv);
        public bool SendPing(int agv) => inner.SendPing(agv);
        public AgvSubscription Subscribe(int agv) => inner.Subscribe(agv);
        public string? LocalAddressToward(int agv) => inner.LocalAddressToward(agv);
    }

    /// <summary>Kestrel 이 실제로 연 HTTP 포트 (Program.cs 가 기동 후 설정)</summary>
    public int HttpPort { get; set; } = 8080;

    void Persist(Job j) { lock (_lock) Save(j); Notify(); }
    void Notify() { try { Changed?.Invoke(); } catch { } }
}
