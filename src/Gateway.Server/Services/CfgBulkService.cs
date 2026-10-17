using Gateway.Core.Bridge;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

public sealed record BulkChange(string Key, string Value)
{
    /// <summary>화면·로그용 값 — 비밀번호는 가린다</summary>
    public string Shown => Key == "pass" ? "••••••" : Value;
}

public enum BulkTargetState { Waiting, Applying, Rebooting, Verifying, Done, Failed, Skipped }

public sealed class BulkTarget
{
    public int Agv { get; init; }
    public BulkTargetState State { get; set; } = BulkTargetState.Waiting;
    public string Message { get; set; } = "";
}

public enum BulkRunState { Running, Done, Failed, Cancelled }

/// <summary>일괄 설정 변경 한 번. 화면이 진행을 보여 주려고 상태를 직접 읽는다 (메모리에만 — 서버를 다시 켜면 사라짐)</summary>
public sealed class BulkRun
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public IReadOnlyList<BulkChange> Changes { get; init; } = [];
    public int BatchSize { get; init; }
    public BulkRunState State { get; set; } = BulkRunState.Running;
    public string Message { get; set; } = "";
    public List<BulkTarget> Targets { get; init; } = [];
    public int CurrentBatch { get; set; }
    public int BatchCount { get; init; }
}

/// <summary>
/// 호기 설정 일괄 변경 (fleet 전체 서버 IP·DNS·hb_ms·Wi-Fi 교체 등). 안전장치:
///  - 서버 원격 저장(trial)으로 적용 — 새 설정으로 서버에 닿지 못하면 브리지가 180초 뒤 스스로 이전 설정으로 돌아간다.
///  - 호기를 batchSize 대씩 나눠 한 묶음이 모두 성공(재부팅 후 확정 + 설정 읽어 확인)해야 다음 묶음으로. 하나라도 실패하면 남은 묶음은 건너뛰고 멈춘다.
///  - 오프라인·번호 충돌·메인보드 작업 중 호기는 처음부터 건너뛴다. 한 번에 한 작업만.
/// 비밀번호는 브리지가 되읽어 주지 않아 길이로만 검증한다 (같은 길이로 바꾸면 반영 여부는 구분 못 함).
/// </summary>
public sealed class CfgBulkService
{
    readonly AgvRegistry _reg;
    readonly ICfgChannel _ch;
    readonly CfgStore _store;
    readonly GatewayOptions _opt;
    readonly ILogger<CfgBulkService> _log;
    readonly object _lock = new();
    BulkRun? _run;
    CancellationTokenSource? _cts;
    public event Action? Changed;

    /// <summary>명령 한 번의 응답 대기, 재부팅 뒤 돌아오기까지 최대 대기, 상태 확인 주기 — 시험에서 줄인다</summary>
    internal TimeSpan ReplyTimeout = TimeSpan.FromSeconds(6), RebootWait = TimeSpan.FromMinutes(5), Poll = TimeSpan.FromSeconds(1);

    public CfgBulkService(AgvRegistry reg, ICfgChannel ch, CfgStore store, IOptions<GatewayOptions> opt, ILogger<CfgBulkService> log)
    {
        _reg = reg; _ch = ch; _store = store; _opt = opt.Value; _log = log;
    }

    public BulkRun? Current { get { lock (_lock) return _run; } }
    public bool Running { get { lock (_lock) return _run?.State == BulkRunState.Running; } }

    /// <summary>입력 검사. 문제 없으면 null, 있으면 사유</summary>
    public static string? Check(IReadOnlyList<BulkChange> changes, IReadOnlyCollection<int> agvs, int batchSize)
    {
        if (changes.Count == 0) return "바꿀 항목이 없습니다";
        if (agvs.Count == 0) return "대상 호기가 없습니다";
        if (agvs.Any(a => a is < 1 or > 99)) return "호기 번호는 1~99";
        if (batchSize is < 1 or > 50) return "한 번에 바꿀 호기 수는 1~50";
        foreach (var c in changes)
        {
            if (!CfgSnapshot.BulkKeys.Contains(c.Key)) return $"일괄로 바꿀 수 없는 항목: {c.Key} (호기마다 다른 agv·ip 는 제외)";
            if (c.Value.Length == 0 || c.Value.Length > 150 || c.Value.Any(ch => ch is '\r' or '\n' or '\0')) return $"{c.Key} 값이 비었거나 깁니다(150자 이하·한 줄)";
        }
        if (changes.Select(c => c.Key).Distinct().Count() != changes.Count) return "같은 항목을 두 번 바꿀 수 없습니다";
        return null;
    }

    /// <summary>시작. 입력 오류·이미 실행 중이면 InvalidOperationException. 실제 변경은 백그라운드에서</summary>
    public BulkRun Start(IEnumerable<int> agvs, IReadOnlyList<BulkChange> changes, int batchSize)
    {
        var ids = agvs.Distinct().OrderBy(a => a).ToList();
        if (Check(changes, ids, batchSize) is { } bad) throw new InvalidOperationException(bad);
        BulkRun run;
        lock (_lock)
        {
            if (_run?.State == BulkRunState.Running) throw new InvalidOperationException("이미 실행 중인 일괄 변경이 있습니다");
            run = new BulkRun
            {
                Changes = changes, BatchSize = batchSize, Targets = ids.Select(a => new BulkTarget { Agv = a }).ToList(),
                BatchCount = CfgDrift.Batches(ids, batchSize).Count,
            };
            _run = run;
            _cts = new CancellationTokenSource();
        }
        _log.LogInformation("설정 일괄 변경 시작 {Id}: {Changes} → {N}대 ({Batch}대씩)", run.Id, string.Join(", ", changes.Select(c => $"{c.Key}={c.Shown}")), ids.Count, batchSize);
        var ct = _cts!.Token;
        _ = Task.Run(() => RunAsync(run, ct), CancellationToken.None);
        return run;
    }

    public void Cancel() { lock (_lock) _cts?.Cancel(); }

    async Task RunAsync(BulkRun run, CancellationToken ct)
    {
        try
        {
            // 처음부터 할 수 없는 호기는 건너뜀으로 — 묶음에는 할 수 있는 호기만 넣는다
            foreach (var t in run.Targets)
                if (Ineligible(t.Agv) is { } why) { t.State = BulkTargetState.Skipped; t.Message = why; }
            Notify();
            var runnable = run.Targets.Where(t => t.State == BulkTargetState.Waiting).Select(t => t.Agv).ToList();
            if (runnable.Count == 0) { Finish(run, BulkRunState.Failed, "바꿀 수 있는 호기가 없습니다 (모두 건너뜀)"); return; }

            var batches = CfgDrift.Batches(runnable, run.BatchSize);
            for (int i = 0; i < batches.Count; i++)
            {
                run.CurrentBatch = i + 1;
                Notify();
                var results = await Task.WhenAll(batches[i].Select(a => ApplyOneAsync(run, run.Targets.First(t => t.Agv == a), ct)));
                if (ct.IsCancellationRequested) { SkipRest(run, "취소됨"); Finish(run, BulkRunState.Cancelled, "취소했습니다"); return; }
                if (results.Any(ok => !ok))
                {
                    SkipRest(run, "앞 묶음 실패로 건너뜀");
                    Finish(run, BulkRunState.Failed, $"묶음 {i + 1}/{batches.Count} 에서 실패 — 나머지는 적용하지 않았습니다");
                    return;
                }
            }
            Finish(run, BulkRunState.Done, $"{run.Targets.Count(t => t.State == BulkTargetState.Done)}대 적용 완료" +
                (run.Targets.Any(t => t.State == BulkTargetState.Skipped) ? $", {run.Targets.Count(t => t.State == BulkTargetState.Skipped)}대 건너뜀" : ""));
        }
        catch (Exception e)
        {
            _log.LogError(e, "설정 일괄 변경 오류 {Id}", run.Id);
            SkipRest(run, "오류로 중단");
            Finish(run, BulkRunState.Failed, "내부 오류: " + e.Message);
        }
    }

    string? Ineligible(int agv)
    {
        var a = _reg.Get(agv);
        if (a?.Status is null) return "호기를 모름 (heartbeat 없음)";
        if (!a.Online(_opt.OfflineAfterSec)) return "오프라인";
        if (a.InConflict(_opt.OfflineAfterSec)) return "호기 번호 충돌 중";
        if (a.Status.DspState is "downloading" or "programming") return "메인보드 작업 중";
        return null;
    }

    void SkipRest(BulkRun run, string why)
    {
        foreach (var t in run.Targets.Where(t => t.State == BulkTargetState.Waiting)) { t.State = BulkTargetState.Skipped; t.Message = why; }
        Notify();
    }

    void Finish(BulkRun run, BulkRunState state, string msg)
    {
        run.State = state; run.Message = msg; run.FinishedAt = DateTime.UtcNow;
        _log.LogInformation("설정 일괄 변경 끝 {Id}: {State} — {Msg}", run.Id, state, msg);
        Notify();
    }

    void Notify() { try { Changed?.Invoke(); } catch { } }

    void Set(BulkTarget t, BulkTargetState s, string msg) { t.State = s; t.Message = msg; Notify(); }

    /// <summary>호기 한 대: set… → save(trial) → reboot → 돌아와 확정될 때까지 기다림 → 설정을 읽어 확인</summary>
    async Task<bool> ApplyOneAsync(BulkRun run, BulkTarget t, CancellationToken ct)
    {
        try
        {
            Set(t, BulkTargetState.Applying, "set 전송");
            foreach (var c in run.Changes)
            {
                var r = await _ch.RequestCfgAsync(t.Agv, $"set {c.Key} {c.Value}", ReplyTimeout, ct);
                if (!r.Ok) return Fail(t, $"set {c.Key}: {r.Error}");
                if (!r.Text!.StartsWith("ok ", StringComparison.Ordinal)) return Fail(t, $"set {c.Key} 거부: {r.Text.Trim()}");
            }
            var save = await _ch.RequestCfgAsync(t.Agv, "save", ReplyTimeout, ct);
            if (!save.Ok) return Fail(t, $"save: {save.Error}");
            if (!save.Text!.StartsWith("saved", StringComparison.Ordinal)) return Fail(t, $"save 거부: {save.Text.Trim()}");

            var upBefore = _reg.Get(t.Agv)?.Status?.UpSeconds ?? 0;
            var rb = await _ch.RequestCfgAsync(t.Agv, "reboot", ReplyTimeout, ct);
            if (!rb.Ok) return Fail(t, $"reboot: {rb.Error} — 저장은 됐으므로 설정을 직접 확인하세요");
            if (!rb.Text!.StartsWith("rebooting", StringComparison.Ordinal)) return Fail(t, $"reboot 거부: {rb.Text.Trim()} — 저장만 된 상태, 재부팅 뒤 적용");

            Set(t, BulkTargetState.Rebooting, "재부팅 후 돌아오기를 기다림");
            if (!await WaitBackAsync(t.Agv, upBefore, ct))
                return Fail(t, $"{(int)RebootWait.TotalSeconds}초 안에 확정된 상태로 돌아오지 않음 — 새 설정으로 서버에 닿지 못하면 브리지가 이전 설정으로 되돌아갑니다");

            Set(t, BulkTargetState.Verifying, "설정 읽어 확인");
            var got = await _store.CollectAsync(t.Agv, ct);
            if (!got.Ok) { Set(t, BulkTargetState.Done, $"적용됨 (검증 생략: {got.Message})"); return true; }
            var wrong = run.Changes.Where(c => !Matches(got.Snapshot!, c)).Select(c => c.Key).ToList();
            if (wrong.Count > 0) return Fail(t, $"반영 안 됨: {string.Join(", ", wrong)} — 서버에 닿지 못해 이전 설정으로 되돌아갔을 수 있습니다");
            Set(t, BulkTargetState.Done, "적용·확인 완료");
            return true;
        }
        catch (OperationCanceledException) { Set(t, BulkTargetState.Skipped, "취소됨"); return true; }
    }

    bool Fail(BulkTarget t, string msg) { Set(t, BulkTargetState.Failed, msg); return false; }

    /// <summary>비밀번호는 길이로만, 나머지는 같은 글자인지</summary>
    internal static bool Matches(CfgSnapshot s, BulkChange c)
        => c.Key == "pass" ? s.Get("pass_len") == (c.Value == "-" ? 0 : c.Value.Length).ToString() : string.Equals(s.Get(c.Key), c.Value, StringComparison.Ordinal);

    /// <summary>재부팅했다가(가동 시간이 줄어듦) 온라인이고 cfg 가 saved(원격 변경 확정)가 될 때까지. 확정은 서버의 자동 PING 이 시킨다</summary>
    async Task<bool> WaitBackAsync(int agv, long upBefore, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + RebootWait;
        long prevUp = upBefore;
        bool rebooted = false;      // 오프라인이 됐거나 가동 시간이 줄었다 = 실제로 재부팅했다 (재부팅 전의 saved 와 구분)
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(Poll, ct);
            var a = _reg.Get(agv);
            if (a is null) continue;
            if (!a.Online(_opt.OfflineAfterSec)) { rebooted = true; continue; }
            if (a.Status is not { } st) continue;
            if (st.UpSeconds < prevUp) rebooted = true;
            prevUp = st.UpSeconds;
            if (rebooted && st.Cfg == "saved") return true;
        }
        return false;
    }
}
