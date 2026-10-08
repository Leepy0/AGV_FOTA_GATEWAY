using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gateway.Core.Update;

/// <summary>새 버전 정보 (GitHub Release 또는 사내 피드 latest.json)</summary>
public sealed record UpdateInfo(Version Version, string Tag, string AssetName, string AssetUrl, string Notes, string PageUrl, long Size);

public static partial class UpdateFeed
{
    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?")]
    private static partial Regex VersionRx();

    /// <summary>"v1.2.3", "1.2.3-abc1234", "AgvFotaGateway_1.2.3_win-x64.zip" 에서 숫자 버전만</summary>
    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = VersionRx().Match(s);
        if (!m.Success) return null;
        int major = int.Parse(m.Groups[1].Value), minor = int.Parse(m.Groups[2].Value);
        int build = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        return new Version(major, minor, build);
    }

    public static bool IsNewer(Version latest, Version current)
        => new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build)) > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>GitHub API /repos/{repo}/releases/latest 응답. assetSuffix(예: "_win-x64.zip") 로 끝나는 자산을 고른다</summary>
    public static UpdateInfo? ParseGitHubRelease(string json, string assetSuffix)
    {
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("tag_name", out var tagEl)) return null;
        if (r.TryGetProperty("draft", out var dr) && dr.GetBoolean()) return null;
        var tag = tagEl.GetString() ?? "";
        var ver = ParseVersion(tag);
        if (ver is null || !r.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            // api url (Accept: application/octet-stream) — 비공개 저장소도 토큰으로 받을 수 있다
            var url = a.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(url)) url = a.GetProperty("browser_download_url").GetString() ?? "";
            long size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            return new UpdateInfo(ver, tag, name, url,
                r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "", size);
        }
        return null;
    }

    /// <summary>사내 피드: { "version": "1.2.3", "url": "http://.../AgvFotaGateway_1.2.3_win-x64.zip", "notes": "...", "size": 123 }</summary>
    public static UpdateInfo? ParseLatestJson(string json)
    {
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        if (!r.TryGetProperty("version", out var v) || !r.TryGetProperty("url", out var u)) return null;
        var ver = ParseVersion(v.GetString());
        var url = u.GetString() ?? "";
        if (ver is null || string.IsNullOrEmpty(url)) return null;
        return new UpdateInfo(ver, "v" + ver, Path.GetFileName(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(url).AbsolutePath : url), url,
            r.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "", url,
            r.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0);
    }

    /// <summary>CI 가 릴리스에 함께 올리는 latest.json 생성</summary>
    public static string BuildLatestJson(string version, string url, string notes, long size)
        => JsonSerializer.Serialize(new { version, url, notes, size }, new JsonSerializerOptions { WriteIndented = true });
}
