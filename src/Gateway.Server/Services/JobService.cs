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
        }
    }

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
            Name = string.IsNullOrWhiteSpace(name) ? $"{fw.Kind} {fw.Version}" : name.Trim(),
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
            Job? due;
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
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    async Task RunJobAsync(Job job, CancellationToken ct)
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
        r.Log.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (r.Log.Count > 200) r.Log.RemoveAt(0);
        r.Message = line;
        Notify();
    }

    async Task RunDspAsync(FirmwareEntry fw, TargetResult r, CancellationToken ct)
    {
        var agv = _reg.Get(r.Agv);
        if (agv is null || !agv.Online(_opt.OfflineAfterSec)) throw new InvalidOperationException("호기 오프라인 (heartbeat 없음)");
        using var c = new DspOtaClient(_link, r.Agv);
        var st = await c.StatusAsync(ct);
        agv.Dsp = st; agv.DspSeen = DateTime.UtcNow;
        r.VersionBefore = st.Version;
        Add(r, $"현재 {st}");
        if (st.State == 'T') throw new InvalidOperationException("DSP 가 미확정(trial) 상태 — 먼저 확정 필요");
        var file = _fw.PickDspFile(fw, st.Slot) ?? throw new InvalidOperationException($"실행 슬롯 {st.Slot} 의 반대쪽 이미지가 없음");
        var image = await File.ReadAllBytesAsync(_fw.PathOf(fw, file), ct);
        var info = FirmwareImage.Parse(image) ?? throw new InvalidOperationException("이미지 헤더 오류");
        Add(r, $"전송 {file.FileName} ({image.Length:N0}B, slot {info.SlotName}, ver {info.Version})");
        int lastPct = -1;
        var target = await c.UpdateAsync(image, _opt.DspBlockSize, (done, total) =>
        {
            int pct = done * 90 / total;
            if (pct / 10 != lastPct / 10) { lastPct = pct; r.Percent = pct; Add(r, $"{done}/{total} 블록 (재전송 {c.Retries})"); }
        }, ct);
        Add(r, $"기록 완료 → slot {target} pending, 재부팅");
        await c.RebootAsync(ct);
        r.Percent = 92;
        st = await c.WaitBackAsync(TimeSpan.FromSeconds(60), ct);
        Add(r, $"재부팅 후 {st}");
        if (st.State == 'T')
        {
            await c.ConfirmAsync(ct);
            st = await c.StatusAsync(ct);
        }
        agv.Dsp = st; agv.DspSeen = DateTime.UtcNow;
        r.VersionAfter = st.Version;
        if (st.State != 'V') throw new InvalidOperationException($"확정 실패: {st}");
        if (st.Version != info.Version) throw new InvalidOperationException($"버전 불일치: 기대 {info.Version}, 실제 {st.Version}");
        Add(r, $"완료 ver {st.Version} slot {st.Slot}");
    }

    async Task RunEspAsync(FirmwareEntry fw, TargetResult r, CancellationToken ct)
    {
        var agv = _reg.Get(r.Agv);
        if (agv is null || !agv.Online(_opt.OfflineAfterSec)) throw new InvalidOperationException("호기 오프라인 (heartbeat 없음)");
        r.VersionBefore = agv.Status?.Fw ?? "";
        var file = fw.Files.First();
        var baseUrl = _opt.PublicBaseUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            var ip = _link.LocalAddressToward(r.Agv) ?? throw new InvalidOperationException("서버 IP 를 알 수 없음 — PublicBaseUrl 설정");
            baseUrl = $"http://{ip}:{HttpPort}";
        }
        var url = $"{baseUrl}/fw/{fw.Id}/{file.FileName}";
        var client = new EspOtaClient(_link, _reg.StatusOf);
        r.Percent = 10;
        var ver = await client.RunAsync(r.Agv, url, fw.Version, line => { Add(r, line); if (line.StartsWith("재부팅")) r.Percent = 70; }, ct);
        r.VersionAfter = ver;
    }

    /// <summary>Kestrel 이 실제로 연 HTTP 포트 (Program.cs 가 기동 후 설정)</summary>
    public int HttpPort { get; set; } = 8080;

    void Persist() { lock (_lock) Save(); Notify(); }
    void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_jobs, Json));
    void Notify() { try { Changed?.Invoke(); } catch { } }
}
