using System.Net;
using System.Text.Json;
using Gateway.Core.Acs;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

public sealed record AcsMapAgv(string Ip, string Name, string AgvCode, DateTimeOffset UpdatedAt);

/// <summary>Reads the ACS map inventory and matches agvOpCtrl IP addresses to the gateway UDP source addresses.</summary>
public sealed class AcsMapInventoryService : BackgroundService
{
    readonly AcsMapInventoryOptions _opt;
    readonly AcsRestPublisher _http;
    readonly AgvRegistry _registry;
    readonly ILogger<AcsMapInventoryService> _log;
    readonly object _lock = new();
    IReadOnlyList<AcsMapAgv> _items = [];
    string _lastResult = "아직 조회하지 않음";
    DateTimeOffset? _lastOk;
    public event Action? Changed;
    public IReadOnlyList<AcsMapAgv> Snapshot { get { lock (_lock) return _items.ToArray(); } }
    public string LastResult { get { lock (_lock) return _lastResult; } }
    public DateTimeOffset? LastOk { get { lock (_lock) return _lastOk; } }
    public bool Enabled => _opt.Enabled;

    public AcsMapInventoryService(IOptions<AcsMapInventoryOptions> options, AcsRestPublisher http, AgvRegistry registry, ILogger<AcsMapInventoryService> log)
    { _opt = options.Value; _http = http; _registry = registry; _log = log; }

    AcsRestSettings Settings(string path) => new(_opt.BaseUrl, path, "GET", _opt.AuthType, _opt.AuthValue, _opt.ApiKeyHeader, _opt.TimeoutMs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled) { SetResult("비활성화됨 (AcsMapInventory.Enabled=false)"); return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { _log.LogWarning(e, "ACS map inventory refresh failed"); SetResult("실패: " + e.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_opt.IntervalSec, 10, 3600)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var subareas = await _http.GetAsync(Settings(_opt.SubareasPath), ct);
        if (!subareas.Ok) { SetResult("subareas 조회 실패: " + subareas); return; }
        var codes = new HashSet<string>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(subareas.Body))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { SetResult("subareas 응답이 배열이 아님"); return; }
            foreach (var row in doc.RootElement.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(code.GetString()))
                    codes.Add(code.GetString()!);
        }
        if (codes.Count == 0) { SetResult("subareas 목록이 비어 있음 — 기존 목록 유지"); return; }

        var query = string.Join("&", codes.OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => Uri.EscapeDataString("subareaCode[]") + "=" + Uri.EscapeDataString(x)));
        var path = _opt.AgvInfoPath + (_opt.AgvInfoPath.Contains('?') ? "&" : "?") + query;
        var response = await _http.GetAsync(Settings(path), ct);
        if (!response.Ok) { SetResult("agv-info 조회 실패: " + response); return; }

        var parsed = new List<AcsMapAgv>();
        using (var doc = JsonDocument.Parse(response.Body))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { SetResult("agv-info 응답이 배열이 아님 — 기존 목록 유지"); return; }
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var ip = GetString(row, "agvOpCtrl").Trim();
                var name = GetString(row, "agvName").Trim();
                var code = GetString(row, "agvCode").Trim();
                if (!IPAddress.TryParse(ip, out _)) continue;
                parsed.Add(new AcsMapAgv(ip, name, code, DateTimeOffset.UtcNow));
            }
        }
        if (parsed.Count == 0) { SetResult("agv-info에서 유효한 agvOpCtrl IP를 찾지 못함 — 기존 목록 유지"); return; }
        var duplicates = parsed.GroupBy(x => x.Ip, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        lock (_lock)
        {
            _items = parsed.GroupBy(x => x.Ip, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(x => x.Name).ThenBy(x => x.Ip).ToArray();
            _lastOk = DateTimeOffset.UtcNow;
            _lastResult = $"정상 · {_items.Count}대" + (duplicates.Length > 0 ? $" · 중복 IP {duplicates.Length}개" : "");
        }
        try { Changed?.Invoke(); } catch { }
    }

    static string GetString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => ""
        } : "";

    void SetResult(string value)
    {
        lock (_lock) _lastResult = value;
        try { Changed?.Invoke(); } catch { }
    }

    public bool IsConnected(AcsMapAgv agv, int offlineAfterSec)
    {
        var state = _registry.Snapshot();
        return state.Any(s => s.EndPoint is not null
            && IPAddress.TryParse(agv.Ip, out var expected)
            && s.EndPoint.Address.Equals(expected)
            && s.Online(offlineAfterSec));
    }
}