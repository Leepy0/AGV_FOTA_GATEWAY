using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gateway.Core.Update;

/// <summary>정식 릴리스 정보 (매니페스트 AgvFotaGateway.json, releases/latest 리다이렉트, 또는 GitHub API)</summary>
public sealed record UpdateInfo(Version Version, string Tag, string AssetName, string AssetUrl, string Notes, string PageUrl,
                                long Size, string? Sha256, string? Sha256Url, DateTimeOffset Published);

public static partial class UpdateFeed
{
    public const string AssetName = "AgvFotaGateway_win-x64.zip";    // 버전과 무관한 고정 이름 → releases/latest/download/… 고정 주소
    public const string ManifestName = "AgvFotaGateway.json";

    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?")]
    private static partial Regex VersionRx();

    /// <summary>"v1.2.3", "1.2.3-abc1234", "1.2.3+sha" 에서 숫자 버전(3자리)만</summary>
    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = VersionRx().Match(s);
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    public static bool IsNewer(Version latest, Version current)
        => new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build)) > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>업데이트를 받아도 되는 출처: https 의 github.com · api.github.com 만 (서명 검증이 없으므로 출처를 TLS 로 보증)</summary>
    public static readonly string[] AllowedHosts = ["github.com", "api.github.com"];

    /// <summary>https 이고 호스트가 <see cref="AllowedHosts"/> 중 하나인 절대 주소인지 (file://, http, 다른 호스트·포트는 거부)</summary>
    public static bool IsAllowedSource(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo)
           && AllowedHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>릴리스 정보의 받기·해시 주소가 모두 허용 출처인지. 아니면 오류 문구</summary>
    public static string? SourceError(UpdateInfo info)
    {
        if (!IsAllowedSource(info.AssetUrl)) return $"허용되지 않은 받기 주소: {info.AssetUrl} (https://github.com · api.github.com 만)";
        if (info.Sha256Url is { Length: > 0 } s && !IsAllowedSource(s)) return $"허용되지 않은 해시 주소: {s} (https://github.com · api.github.com 만)";
        return null;
    }

    /// <summary>GitHub 토큰을 붙여도 되는 주소 (api.github.com 만 — 글자 포함이 아니라 호스트로 판단)</summary>
    public static bool IsGitHubApi(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 받아 둔 폴더 이름(vX.Y.Z) 중 현재보다 새 가장 높은 것 (재시작 뒤 '적용 대기' 복원용). 없으면 null
    /// </summary>
    public static string? NewestStagedTag(IEnumerable<string> tags, Version current)
        => tags.Select(t => (t, v: t.StartsWith('v') ? ParseVersion(t) : null))
               .Where(x => x.v is not null && x.t == "v" + x.v.ToString(3) && IsNewer(x.v, current))
               .OrderByDescending(x => x.v).Select(x => x.t).FirstOrDefault();

    /// <summary>
    /// 릴리스에 함께 올린 매니페스트. baseUrl = 릴리스 페이지 루트(https://github.com/owner/repo).
    /// { "version", "file", "sha256", "size", "published", "page", "notes", ["url": 절대 주소면 그대로 — 출처는 <see cref="SourceError"/> 로 확인] }
    /// </summary>
    public static UpdateInfo? ParseManifest(string json, string baseUrl)
    {
        using var d = JsonDocument.Parse(json);
        var m = d.RootElement;
        if (m.ValueKind != JsonValueKind.Object) return null;
        var ver = ParseVersion(Str(m, "version"));
        if (ver is null) return null;
        string tag = "v" + ver.ToString(3);
        string file = Str(m, "file") is { Length: > 0 } f ? f : AssetName;
        string root = baseUrl.TrimEnd('/');
        string url = Str(m, "url") is { Length: > 0 } u && Uri.IsWellFormedUriString(u, UriKind.Absolute) ? u : $"{root}/releases/download/{tag}/{file}";
        string sha = Str(m, "sha256");
        return new UpdateInfo(ver, tag, file, url, Str(m, "notes"),
            Str(m, "page") is { Length: > 0 } p ? p : $"{root}/releases/tag/{tag}",
            m.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
            sha.Length == 64 ? sha.ToLowerInvariant() : null, url + ".sha256",
            DateTimeOffset.TryParse(Str(m, "published"), out var pub) ? pub : DateTimeOffset.MinValue);
    }

    /// <summary>GitHub API /repos/{repo}/releases/latest (사외망·토큰 폴백). asset 이름이 AssetName 인 것</summary>
    public static UpdateInfo? ParseGitHubRelease(string json, string assetName = AssetName)
    {
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("tag_name", out var tagEl)) return null;
        if (r.TryGetProperty("draft", out var dr) && dr.GetBoolean()) return null;
        var tag = tagEl.GetString() ?? "";
        var ver = ParseVersion(tag);
        if (ver is null || !r.TryGetProperty("assets", out var assets)) return null;
        JsonElement? zip = null;
        string? shaUrl = null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = Str(a, "name");
            if (name.Equals(assetName, StringComparison.OrdinalIgnoreCase)) zip = a;
            else if (name.Equals(assetName + ".sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = Str(a, "url");
        }
        if (zip is not { } z) return null;
        var digest = Str(z, "digest");
        return new UpdateInfo(ver, tag, assetName, Str(z, "url"), Str(r, "body"), Str(r, "html_url"),
            z.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
            digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null, shaUrl,
            DateTimeOffset.TryParse(Str(r, "published_at"), out var pub) ? pub : DateTimeOffset.MinValue);
    }

    /// <summary>".sha256" 파일 ("해시  파일명" 또는 "해시 *파일명") → 소문자 64자, 아니면 null</summary>
    public static string? ParseShaFile(string text)
    {
        var first = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length == 64 ? first.ToLowerInvariant() : null;
    }

    /// <summary>CI 가 만드는 매니페스트 (테스트·도구용)</summary>
    public static string BuildManifest(string version, string file, string sha256, long size, string page, string notes)
        => JsonSerializer.Serialize(new { version, file, sha256 = sha256.ToLowerInvariant(), size, published = DateTimeOffset.UtcNow.ToString("o"), page, notes },
                                    new JsonSerializerOptions { WriteIndented = true });

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
