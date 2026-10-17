using System.Text.Json;
using Gateway.Core.Update;

namespace Gateway.Core.Bridge;

/// <summary>
/// 브리지 저장소 릴리스의 bridge.json 매니페스트(브리지 0.7.0+ 가 릴리스에 첨부). 서버는 api.github.com 없이
/// {저장소}/releases/latest/download/bridge.json 으로 최신 버전·체크섬을 안다.
/// </summary>
public sealed record BridgeRelease(Version Version, string Tag, DateTimeOffset Published, string PageUrl,
                                   string AppFile, string AppSha256, long AppSize, string MinPartitionVersion, string Notes)
{
    public const string ManifestName = "bridge.json";
    public const string AppAsset = "agv_wifi_bridge.bin";

    /// <summary>OTA 에 쓰는 앱 이미지 받기 주소 (출처는 <see cref="UpdateFeed.IsAllowedSource"/> 로 따로 확인)</summary>
    public string AppUrl(string repoRoot) => $"{repoRoot.TrimEnd('/')}/releases/download/{Tag}/{AppFile}";

    /// <summary>매니페스트 → 릴리스. 버전·앱 sha256(64자)이 없으면 쓸 수 없으므로 null</summary>
    public static BridgeRelease? Parse(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var m = d.RootElement;
            if (m.ValueKind != JsonValueKind.Object) return null;
            var ver = UpdateFeed.ParseVersion(Str(m, "version"));
            if (ver is null || !m.TryGetProperty("app", out var app) || app.ValueKind != JsonValueKind.Object) return null;
            var sha = Str(app, "sha256");
            if (sha.Length != 64) return null;
            var file = Str(app, "file") is { Length: > 0 } f ? f : AppAsset;
            if (file.Contains('/') || file.Contains('\\')) return null;     // 경로가 낀 파일 이름은 받지 않는다
            return new BridgeRelease(ver, "v" + ver.ToString(3), DateTimeOffset.TryParse(Str(m, "published"), out var p) ? p : DateTimeOffset.MinValue,
                Str(m, "page"), file, sha.ToLowerInvariant(),
                app.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
                Str(m, "minPartitionVersion"), Str(m, "notes"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>호기 fw("0.7.0", "0.6.0-abc1234")가 최신보다 낮은지. 버전을 못 읽으면 false (모르는 것을 구버전이라 하지 않음)</summary>
    public bool IsOutdated(string? fw)
        => UpdateFeed.ParseVersion(fw) is { } v && UpdateFeed.IsNewer(Version, v);

    static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
