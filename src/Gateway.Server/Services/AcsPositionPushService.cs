using Gateway.Core.Acs;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

/// <summary>
/// 호기 위치를 기존 ACS 서버로 REST 발행. IntervalMs 마다 바뀐 호기(또는 전체)를 모아 한 번(또는 호기별) 보낸다.
/// 위치는 "최신값이 전부" 라 실패해도 큐에 쌓지 않고 다음 주기에 최신값을 다시 보낸다(연속 실패는 백오프).
/// LogOnly 면 보내지 않고 통신 로그에 JSON 만 남긴다 — 월요일 스키마 확정 전 형식 확인용.
/// </summary>
public sealed class AcsPositionPushService : BackgroundService
{
    readonly AcsApiOptions _opt;
    readonly AgvPositionStore _store;
    readonly AcsRestPublisher _pub;
    readonly TraceLog _trace;
    readonly ILogger<AcsPositionPushService> _log;
    long _sentVersion;
    int _failStreak;

    public bool Enabled => _opt.Enabled;
    public bool LogOnly => _opt.LogOnly;
    public DateTime? LastOk { get; private set; }
    public DateTime? LastTry { get; private set; }
    public string LastResult { get; private set; } = "";
    public long SentCount { get; private set; }
    public int FailStreak => _failStreak;
    public string Target => $"{_opt.Method} {_opt.BaseUrl.TrimEnd('/')}{_opt.Path}";

    public AcsPositionPushService(IOptions<AcsApiOptions> opt, AgvPositionStore store, AcsRestPublisher pub, TraceLog trace, ILogger<AcsPositionPushService> log)
    {
        _opt = opt.Value; _store = store; _pub = pub; _trace = trace; _log = log;
    }

    AcsRestSettings Settings => new(_opt.BaseUrl, _opt.Path, _opt.Method, _opt.AuthType, _opt.AuthValue, _opt.ApiKeyHeader, _opt.TimeoutMs);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled) { _log.LogInformation("ACS REST 발행 꺼짐 (AcsApi.Enabled=false)"); return; }
        _log.LogInformation("ACS REST 발행: {Target} {Mode} {Interval}ms{LogOnly}", Target, _opt.PerAgv ? "호기별" : "묶음", _opt.IntervalMs, _opt.LogOnly ? " (LogOnly)" : "");
        while (!ct.IsCancellationRequested)
        {
            try { await PushAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogWarning(e, "acs push"); }
            var delay = _failStreak > 0 ? Math.Min(_opt.MaxBackoffSec * 1000, _opt.IntervalMs << Math.Min(_failStreak, 10)) : _opt.IntervalMs;
            try { await Task.Delay(Math.Max(100, delay), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>지금 한 번 발행 (화면·API 의 "지금 보내기"). force = 변경 없어도 전체 전송</summary>
    public async Task<string> PushAsync(CancellationToken ct = default, bool force = false)
    {
        var (changed, version) = _opt.OnlyChanged && !force ? _store.ChangedSince(_sentVersion) : _store.All();
        if (changed.Count == 0) return "보낼 변경 없음";
        var now = DateTimeOffset.UtcNow;
        LastTry = DateTime.UtcNow;
        bool allOk = true;
        if (_opt.PerAgv)
        {
            foreach (var p in changed)
            {
                var json = AcsPositionMapper.Serialize(AcsPositionMapper.Map(p));
                allOk &= await SendAsync(json, p.Agv, ct);
            }
        }
        else
        {
            allOk = await SendAsync(AcsPositionMapper.Serialize(AcsPositionMapper.Build(changed, now)), null, ct);
        }
        if (allOk) { _sentVersion = version; _failStreak = 0; LastOk = DateTime.UtcNow; SentCount += changed.Count; }
        else _failStreak++;
        return LastResult;
    }

    async Task<bool> SendAsync(string json, int? agv, CancellationToken ct)
    {
        if (_opt.LogOnly)
        {
            _trace.Add($"ACS< (LogOnly{(agv is null ? "" : $" agv {agv:D2}")}) {json}");
            LastResult = $"LogOnly {json.Length}B";
            return true;
        }
        var r = await _pub.PublishAsync(Settings, json, agv, ct);
        LastResult = r.ToString();
        _trace.Add($"ACS< {(agv is null ? "" : $"agv {agv:D2} ")}{r} {json.Length}B");
        if (!r.Ok) _log.LogWarning("ACS REST {Result}", r);
        return r.Ok;
    }
}
