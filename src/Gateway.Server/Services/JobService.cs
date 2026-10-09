using System.Text.Json;
using System.Text.Json.Serialization;
using Gateway.Core.Fota;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

public enum JobState { Scheduled, Running, Done, Failed, Cancelled }
public enum TargetState { Waiting, Running, Done, Failed, Skipped }

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
    public JobState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public List<TargetResult> Results { get; set; } = new();
    [JsonIgnore] public int DoneCount => Results.Count(r => r.State == TargetState.Done);
    [JsonIgnore] public int FailCount => Results.Count(r => r.State == TargetState.Failed);
}

/// <summary>작업 목록 (JSON 파일) + 실행기. 예약 시각이 되면 대상 호기를 한 대씩 순차로 업데이트한다.</summary>
public sealed class JobService : BackgroundService
{
    readonly GatewayOptions _opt;
    readonly FirmwareStore _fw;
    readonly AgvRegistry _reg;
    readonly BridgeUdpService _link;
    readonly ILogger<JobService> _log;
    readonly string _path;
    readonly object _lock = new();
    readonly List<Job> _jobs = new();
    readonly Dictionary<string, CancellationTokenSource> _running = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public event Action? Changed;

    public JobService(IOptions<GatewayOptions> opt, FirmwareStore fw, AgvRegistry reg, BridgeUdpService link, ILogger<JobService> log)
    {
        _opt = opt.Value; _fw = fw; _reg = reg; _link = link; _log = log;
        _path = Path.Combine(_opt.DataDir, "jobs.json");
        if (File.Exists(_path))
            _jobs = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(_path), Json) ?? new();
        // 서버가 재시작됐을 때 실행 중이던 작업은 실패로 정리
        foreach (var j in _jobs.Where(j => j.State == JobState.Running))
        {
            j.State = JobState.Failed; j.FinishedAt = DateTime.UtcNow;
            foreach (var r in j.Results.Where(r => r.State == TargetState.Running)) { r.State = TargetState.Failed; r.Message = "서버 재시작"; }
            foreach (var r in j.Results.Where(r => r.State == TargetState.Waiting)) { r.State = TargetState.Skipped; r.Message = "서버 재시작"; }
        }
        // 서버가 꺼져 있는 동안 예약 시각이 한참 지난 작업은 몰래 실행하지 않고 취소 (야간 예약이 아침에 갑자기 도는 사고 방지)
        var stale = DateTime.UtcNow.AddMinutes(-StaleScheduleMinutes);
        foreach (var j in _jobs.Where(j => j.State == JobState.Scheduled && j.ScheduledAt is not null && j.ScheduledAt < stale))
        {
            j.State = JobState.Cancelled; j.FinishedAt = DateTime.UtcNow;
            foreach (var r in j.Results) { r.State = TargetState.Skipped; r.Message = $"서버 중단 중 예약 시각 경과 ({StaleScheduleMinutes}분 초과) — 다시 예약하세요"; }
        }
        if (_jobs.Count > 0) Save();
    }

    /// <summary>예약 시각이 이보다 오래 지난 채 서버가 켜지면 자동 취소</summary>
    public const int StaleScheduleMinutes = 30;

    public IReadOnlyList<Job> List() { lock (_lock) return _jobs.OrderByDescending(j => j.CreatedAt).ToList(); }
    public Job? Get(string id) { lock (_lock) return _jobs.FirstOrDefault(j => j.Id == id); }

    public Job Create(string name, string firmwareId, IEnumerable<int> targets, DateTime? scheduledAtUtc, bool stopOnFailure)
    {
        var fw = _fw.Get(firmwareId) ?? throw new InvalidOperationException("펌웨어 없음");
        var t = targets.Distinct().OrderBy(x => x).ToList();
        if (t.Count == 0) throw new InvalidOperationException("대상 호기가 없습니다");
        var j = new Job
        {
            Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4],
            Name = string.IsNullOrWhiteSpace(name) ? $"{(fw.Kind == FirmwareKind.Dsp ? "메인보드" : "브리지")} {fw.Version}" : name.Trim(),
            FirmwareId = fw.Id, Kind = fw.Kind, FirmwareVersion = fw.Version, Targets = t,
            ScheduledAt = scheduledAtUtc, StopOnFailure = stopOnFailure, State = JobState.Scheduled, CreatedAt = DateTime.UtcNow,
            Results = t.Select(a => new TargetResult { Agv = a }).ToList(),
        };
        lock (_lock) { _jobs.Add(j); Save(); }
        Notify();
        return j;
    }

    public bool Cancel(string id)
    {
        lock (_lock)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j is null) return false;
            if (j.State == JobState.Scheduled) { j.State = JobState.Cancelled; j.FinishedAt = DateTime.UtcNow; Save(); }
            else if (j.State == JobState.Running && _running.TryGetValue(id, out var cts)) cts.Cancel();
            else return false;
        }
        Notify();
        return true;
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j is null || j.State == JobState.Running) return false;
            _jobs.Remove(j); Save();
        }
        Notify();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Job? due = null;
            try
            {
            lock (_lock)
            {
                due = _jobs.Where(j => j.State == JobState.Scheduled && (j.ScheduledAt is null || j.ScheduledAt <= DateTime.UtcNow))
                           .OrderBy(j => j.ScheduledAt ?? j.CreatedAt).FirstOrDefault();
                // 한 번에 한 작업만 (호기 순차 실행과 같은 이유: 무선·서버 부하, AGV 정지 확인)
                if (due is not null && _running.Count == 0)
                {
                    due.State = JobState.Running; due.StartedAt = DateTime.UtcNow;
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _running[due.Id] = cts;
                    Save();
                    _ = RunJobAsync(due, cts.Token).ContinueWith(_ => { lock (_lock) { _running.Remove(due.Id); } }, CancellationToken.None);
                }
            }
            if (due is not null) Notify();
            }
            catch (Exception e) { _log.LogError(e, "작업 스케줄러 오류"); }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    async Task RunJobAsync(Job job, CancellationToken ct)
    {
        // 어떤 예외가 나도(저장 실패 등) 작업이 Running 으로 고착되지 않도록 전체를 감싼다
        try { await RunJobCoreAsync(job, ct); }
        catch (Exception e)
        {
            _log.LogError(e, "job {Id} 실행기 오류", job.Id);
            foreach (var r in job.Results.Where(r => r.State is TargetState.Running or TargetState.Waiting)) { r.State = TargetState.Failed; r.Message = "실행기 오류: " + e.Message; }
            job.State = JobState.Failed; job.FinishedAt = DateTime.UtcNow;
            Persist();
        }
    }

    async Task RunJobCoreAsync(Job job, CancellationToken ct)
    {
        _log.LogInformation("job {Id} start: {Kind} {Ver} → {N} agv", job.Id, job.Kind, job.FirmwareVersion, job.Targets.Count);
        var fw = _fw.Get(job.FirmwareId);
        bool stop = false;
        foreach (var r in job.Results)
        {
            if (ct.IsCancellationRequested || stop || fw is null)
            {
                r.State = TargetState.Skipped; r.Message = fw is null ? "펌웨어 삭제됨" : ct.IsCancellationRequested ? "취소" : "앞 호기 실패로 중단";
                continue;
            }
            r.State = TargetState.Running; r.StartedAt = DateTime.UtcNow; r.Percent = 0;
            Persist();
            try
            {
                if (job.Kind == FirmwareKind.Dsp) await RunDspAsync(fw, r, ct);
                else await RunEspAsync(fw, r, ct);
                r.State = TargetState.Done; r.Percent = 100;
            }
            catch (OperationCanceledException) { r.State = TargetState.Failed; r.Message = "취소"; }
            catch (Exception e)
            {
                r.State = TargetState.Failed; r.Message = e.Message; r.Log.Add("ERROR " + e.Message);
                if (job.StopOnFailure) stop = true;
            }
            r.FinishedAt = DateTime.UtcNow;
            Persist();
        }
        job.State = ct.IsCancellationRequested ? JobState.Cancelled : job.FailCount > 0 ? JobState.Failed : JobState.Done;
        job.FinishedAt = DateTime.UtcNow;
        Persist();
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

    // 메인보드 OTA B안: 브리지에 .dspfw 스테이징(0x32) → 적용(0x34) → 보고(0x33) → heartbeat 의 dsp 버전 확인
    async Task RunDspAsync(FirmwareEntry fw, TargetResult r, CancellationToken ct)
    {
        var agv = _reg.Get(r.Agv);
        if (agv is null || !agv.Online(_opt.OfflineAfterSec)) throw new InvalidOperationException("호기 오프라인 (heartbeat 없음)");
        var st = agv.Status;
        if (st is null || string.IsNullOrEmpty(st.DspState)) throw new InvalidOperationException($"브리지 펌웨어가 메인보드 OTA 를 지원하지 않음 (fw {st?.Fw ?? "?"}, 0.5.0 이상 필요)");
        if (st.DspState == "programming") throw new InvalidOperationException("브리지가 이미 메인보드를 프로그래밍 중");
        r.VersionBefore = st.Dsp;
        var file = fw.Files.First();
        var img = DspfwImage.Parse(await File.ReadAllBytesAsync(_fw.PathOf(fw, file), ct)) ?? throw new InvalidOperationException("이미지 파일 손상 (.dspfw CRC)");
        var url = $"{BaseUrlFor(r.Agv)}/fw/{Uri.EscapeDataString(fw.Id)}/{Uri.EscapeDataString(file.FileName)}";
        // 브리지 보고와 대조할 작업 번호 — 브리지가 u32 로 읽으므로 4,294,967,295 이하 (초 단위 시각 × 100 + 호기, 약 115일 주기)
        long job = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 10_000_000) * 100 + r.Agv;
        Add(r, $"현재 {(st.HasDsp ? st.Dsp : "?")} (부트로더 {st.DspBoot}) → {img.Version} 스테이징 {file.FileName} ({img.Size:N0}B)");
        using var c = new DspOtaClient(_link, _reg.StatusOf, r.Agv);
        void OnStage(DspOtaResult x)
        {
            r.Percent = x.Stage switch
            {
                "download" => 5 + x.Pct * 25 / 100, "staged" => 30, "start" => 32, "enter" => 35, "erase" => 40,
                "progress" => 45 + x.Pct * 45 / 100, "verify" => 92, "done" => 100, _ => r.Percent,
            };
            Add(r, x.Stage + (x.Pct >= 0 ? $" {x.Pct}%" : "") + (x.Ver.Length > 0 ? $" ver {x.Ver}" : "") + (x.Err.Length > 0 ? $" err {x.Err}" : ""));
        }
        await c.StageAsync(url, img, job, OnStage, ct);
        Add(r, "스테이징 완료 — 적용 (메인보드 앱 정지, 약 20~30초)");
        var ver = await c.ApplyAsync(job, OnStage, ct);
        r.VersionAfter = ver;
        if (ver != img.Version) throw new InvalidOperationException($"적용 후 앱 버전 {ver} 이 이미지 {img.Version} 과 다름");
        var after = await c.WaitVersionAsync(img.Version, TimeSpan.FromSeconds(30), ct);
        Add(r, $"완료 ver {after.Dsp} (heartbeat 확인)");
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
        var agv = _reg.Get(r.Agv);
        if (agv is null || !agv.Online(_opt.OfflineAfterSec)) throw new InvalidOperationException("호기 오프라인 (heartbeat 없음)");
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
        var client = new EspOtaClient(_link, _reg.StatusOf);
        r.Percent = 10;
        var ver = await client.RunAsync(r.Agv, url, fw.Version, line => { Add(r, line); if (line.StartsWith("재부팅")) r.Percent = 70; }, ct);
        r.VersionAfter = ver;
    }

    /// <summary>Kestrel 이 실제로 연 HTTP 포트 (Program.cs 가 기동 후 설정)</summary>
    public int HttpPort { get; set; } = 8080;

    void Persist() { lock (_lock) Save(); Notify(); }
    void Save()
    {
        // 저장 실패(백신이 잠깐 잠금, 디스크 풀)로 서비스 전체가 죽지 않도록 — 메모리 상태는 유지되고 다음 저장 때 다시 쓴다
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_jobs, Json)); }
        catch (Exception e) { _log.LogError("jobs.json 저장 실패: {Msg}", e.Message); }
    }
    void Notify() { try { Changed?.Invoke(); } catch { } }
}
