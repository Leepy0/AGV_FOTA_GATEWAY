using System.Net;
using System.Text.Json;
using Gateway.Core.Acs;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

public sealed record AcsMapAgv(
    string Ip, string Name, string AgvCode, string AgvId, string AcsKey,
    string Area, string Product, string AgvType, string StatusCode,
    string StatusLabel, decimal? BatteryPercent, DateTimeOffset UpdatedAt);

/// <summary>Combines ACS map agv-info with the tape AGV status API.</summary>
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
            var root = UnwrapArray(doc.RootElement);
            if (root.ValueKind != JsonValueKind.Array) { SetResult("subareas 응답이 배열이 아님"); return; }
            foreach (var row in root.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Object)
                {
                    var code = First(row, "code", "subareaCode", "areaCode");
                    if (!string.IsNullOrWhiteSpace(code)) codes.Add(code);
                }
        }
        if (codes.Count == 0) { SetResult("subareas 목록이 비어 있음 — 기존 목록 유지"); return; }

        var query = string.Join("&", codes.OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => Uri.EscapeDataString("subareaCode[]") + "=" + Uri.EscapeDataString(x)));
        var path = _opt.AgvInfoPath + (_opt.AgvInfoPath.Contains('?') ? "&" : "?") + query;
        var response = await _http.GetAsync(Settings(path), ct);
        if (!response.Ok) { SetResult("agv-info 조회 실패: " + response); return; }

        var mapRows = new List<JsonElement>();
        using (var doc = JsonDocument.Parse(response.Body))
        {
            var root = UnwrapArray(doc.RootElement);
            if (root.ValueKind != JsonValueKind.Array) { SetResult("agv-info 응답이 배열이 아님 — 기존 목록 유지"); return; }
            mapRows.AddRange(root.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => x.Clone()));
        }

        // The second API provides live tape/status fields. Its path may include query parameters.
        var tapeResponse = await _http.GetAsync(Settings(_opt.TapePath), ct);
        if (!tapeResponse.Ok) { SetResult("TAPE 상태 API 조회 실패: " + tapeResponse); return; }
        var tapeRows = new List<JsonElement>();
        using (var doc = JsonDocument.Parse(tapeResponse.Body))
        {
            var root = UnwrapArray(doc.RootElement);
            if (root.ValueKind != JsonValueKind.Array) { SetResult("TAPE 상태 API 응답이 배열이 아님 — 기존 목록 유지"); return; }
            tapeRows.AddRange(root.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => x.Clone()));
        }

        var tapeById = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in tapeRows)
        {
            var id = First(row, _opt.TapeAgvIdField, "agvid", "agvId", "agvCode", "agvdode", "agvcode");
            if (!string.IsNullOrWhiteSpace(id)) tapeById[id] = row;
        }

        var parsed = new List<AcsMapAgv>();
        foreach (var row in mapRows)
        {
            var type = First(row, "agvType");
            if (!type.Equals("TAPE", StringComparison.OrdinalIgnoreCase)) continue;
            var code = First(row, "agvCode", "agvdode", "agvcode");
            var id = First(row, "agvId", "agvid", "agvCode", "agvdode", "agvcode");
            tapeById.TryGetValue(id, out var live);
            var hasLive = live.ValueKind == JsonValueKind.Object;
            var ip = First(row, "agvOpcUrl").Trim();
            if (!IPAddress.TryParse(ip, out _)) ip = "";
            var status = hasLive ? First(live, _opt.TapeStatusField, "status") : "";
            var batteryText = hasLive ? First(live, _opt.TapeBatteryField, "battery", "battery_level", "batteryLevel") : "";
            decimal? battery = decimal.TryParse(batteryText, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var b) ? Math.Clamp(b, 0m, 100m) : null;
            parsed.Add(new AcsMapAgv(
                ip, First(row, "agvName", "agvCode"), code, id,
                hasLive ? First(live, "ACS_KEY", "acsKey", "acs_key") : "",
                hasLive ? First(live, "area", "areaCode") : First(row, "areaCode"),
                hasLive ? First(live, "product", "productFamily", "product_name") : "",
                type, status, ResolveStatus(status), battery, DateTimeOffset.UtcNow));
        }

        var duplicates = parsed.Where(x => x.AgvId.Length > 0).GroupBy(x => x.AgvId, StringComparer.OrdinalIgnoreCase).Count(g => g.Count() > 1);
        lock (_lock)
        {
            _items = parsed.GroupBy(x => x.AgvId.Length > 0 ? x.AgvId : x.AgvCode, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).OrderBy(x => x.Product).ThenBy(x => x.Name).ThenBy(x => x.AgvId).ToArray();
            _lastOk = DateTimeOffset.UtcNow;
            _lastResult = $"정상 · TAPE {_items.Count}대 · 상태 데이터 {tapeById.Count}건" + (duplicates > 0 ? $" · 중복 ID {duplicates}개" : "");
        }
        try { Changed?.Invoke(); } catch { }
    }

    string ResolveStatus(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "상태 미수신";
        foreach (var pair in _opt.StatusLabels)
            if (string.Equals(pair.Key, code, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return $"미등록 상태 ({code})";
    }

    static JsonElement UnwrapArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "data", "items", "result", "list" })
                if (root.TryGetProperty(name, out var value))
                {
                    if (value.ValueKind == JsonValueKind.Array) return value;
                    if (value.ValueKind == JsonValueKind.Object)
                        foreach (var nested in new[] { "data", "items", "list" })
                            if (value.TryGetProperty(nested, out var arr) && arr.ValueKind == JsonValueKind.Array) return arr;
                }
        return root;
    }

    static string First(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            if (row.ValueKind != JsonValueKind.Object) return "";
            foreach (var prop in row.EnumerateObject())
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    return prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? "",
                        JsonValueKind.Number => prop.Value.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => ""
                    };
        }
        return "";
    }

    void SetResult(string value)
    {
        lock (_lock) _lastResult = value;
        try { Changed?.Invoke(); } catch { }
    }

    public bool IsConnected(AcsMapAgv agv, int offlineAfterSec)
    {
        if (string.IsNullOrWhiteSpace(agv.Ip)) return false;
        var state = _registry.Snapshot();
        return state.Any(s => s.EndPoint is not null
            && IPAddress.TryParse(agv.Ip, out var expected)
            && s.EndPoint.Address.Equals(expected)
            && s.Online(offlineAfterSec));
    }
}
