using Gateway.Core.Acs;
using Gateway.Core.Groups;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

/// <summary>
/// 기존 ACS 서버에서 호기 소속 목록을 주기적으로 GET 해 AgvGroupStore 에 반영한다.
/// 스키마 확정 전에는 Enabled=false 로 두고 화면의 "미리보기" 로 응답·해석 결과만 확인할 수 있다.
/// </summary>
public sealed class AcsInventoryPullService : BackgroundService
{
    readonly AcsInventoryOptions _opt;
    readonly AgvGroupStore _store;
    readonly AcsRestPublisher _http;
    readonly TraceLog _trace;
    readonly ILogger<AcsInventoryPullService> _log;

    public bool Enabled => _opt.Enabled;
    public string Target => $"GET {_opt.BaseUrl.TrimEnd('/')}{_opt.Path}";
    public DateTime? LastTry { get; private set; }
    public DateTime? LastOk { get; private set; }
    public string LastResult { get; private set; } = "";
    public int LastCount { get; private set; }
    public IReadOnlyList<string> LastWarnings { get; private set; } = [];
    public int FailStreak { get; private set; }
    public event Action? Changed;

    public AcsInventoryPullService(IOptions<AcsInventoryOptions> opt, AgvGroupStore store, AcsRestPublisher http, TraceLog trace, ILogger<AcsInventoryPullService> log)
    {
        _opt = opt.Value; _store = store; _http = http; _trace = trace; _log = log;
    }

    AcsRestSettings Settings => new(_opt.BaseUrl, _opt.Path, "GET", _opt.AuthType, _opt.AuthValue, _opt.ApiKeyHeader, _opt.TimeoutMs);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled) { _log.LogInformation("ACS 소속 받기 꺼짐 (AcsInventory.Enabled=false) — 화면·CSV 로 관리"); return; }
        _log.LogInformation("ACS 소속 받기: {Target} {Interval}초", Target, _opt.IntervalSec);
        try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await PullAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogWarning(e, "acs inventory"); }
            if (_opt.IntervalSec <= 0) return;
            var delay = Math.Max(10, _opt.IntervalSec) * 1000;
            if (FailStreak > 0) delay = Math.Min(delay, 30_000 << Math.Min(FailStreak, 4));   // 실패 시 30초~8분 후 재시도
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>받아서 해석만 (저장 안 함) — 스키마 맞추기용. 원문 앞부분도 돌려준다</summary>
    public async Task<(PublishResult http, AcsInventoryParseResult? parsed, string sample)> PreviewAsync(CancellationToken ct = default)
    {
        var r = await _http.GetAsync(Settings, ct);
        if (!r.Ok) return (r, null, "");
        try
        {
            var parsed = AcsInventoryMapper.Parse(r.Body, _opt.Fields, DateTimeOffset.UtcNow);
            return (r, parsed, r.Body.Length > 2000 ? r.Body[..2000] + "…" : r.Body);
        }
        catch (System.Text.Json.JsonException e)
        {
            return (r with { Ok = false, Error = "JSON 아님: " + e.Message }, null, r.Body.Length > 500 ? r.Body[..500] + "…" : r.Body);
        }
    }

    /// <summary>지금 받아서 반영 (화면·API 의 "지금 받기")</summary>
    public async Task<string> PullAsync(CancellationToken ct = default)
    {
        LastTry = DateTime.UtcNow;
        var (r, parsed, _) = await PreviewAsync(ct);
        if (!r.Ok || parsed is null)
        {
            FailStreak++;
            LastResult = r.ToString();
            _trace.Add($"ACS-INV> 실패 {LastResult}");
        }
        else
        {
            var (changed, removed) = _store.MergeFromAcs(parsed.Items, _opt.OverrideManual, _opt.RemoveMissing);
            FailStreak = 0; LastOk = DateTime.UtcNow; LastCount = parsed.Items.Count; LastWarnings = parsed.Warnings;
            LastResult = $"HTTP {r.StatusCode} · {parsed.TotalRecords}건 중 {parsed.Items.Count}대 해석 · 변경 {changed} · 삭제 {removed}{(parsed.Warnings.Count > 0 ? $" · 경고 {parsed.Warnings.Count}" : "")}";
            _trace.Add($"ACS-INV> {LastResult}");
            if (parsed.Warnings.Count > 0) _log.LogWarning("ACS 소속 경고: {W}", string.Join(" | ", parsed.Warnings.Take(5)));
        }
        try { Changed?.Invoke(); } catch { }
        return LastResult;
    }
}
