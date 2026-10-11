using Gateway.Core.Update;

namespace Gateway.Core.Tests;

public class UpdateFeedTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-abc1234", "1.2.3")]
    [InlineData("0.1.0+build.5", "0.1.0")]
    [InlineData("2.0", "2.0.0")]
    public void ParseVersion_strips_prefix_and_suffix(string s, string expected) => Assert.Equal(Version.Parse(expected), UpdateFeed.ParseVersion(s));

    [Fact]
    public void IsNewer_compares_three_parts()
    {
        Assert.True(UpdateFeed.IsNewer(new(0, 2, 0), new(0, 1, 9)));
        Assert.False(UpdateFeed.IsNewer(new(0, 1, 0), new(0, 1, 0)));
        Assert.False(UpdateFeed.IsNewer(new(0, 1, 0), new Version(0, 1, 0, 0)));
    }

    [Fact]
    public void Manifest_roundtrip_and_url_composition()
    {
        var sha = new string('a', 64);
        var js = UpdateFeed.BuildManifest("0.3.1", UpdateFeed.AssetName, sha.ToUpperInvariant(), 5, "https://github.com/x/y/releases/tag/v0.3.1", "n");
        var i = UpdateFeed.ParseManifest(js, "https://github.com/x/y/")!;
        Assert.Equal((new Version(0, 3, 1), "v0.3.1", UpdateFeed.AssetName, 5L, sha), (i.Version, i.Tag, i.AssetName, i.Size, i.Sha256));
        Assert.Equal("https://github.com/x/y/releases/download/v0.3.1/" + UpdateFeed.AssetName, i.AssetUrl);
        Assert.Equal(i.AssetUrl + ".sha256", i.Sha256Url);
        Assert.True(i.Published > DateTimeOffset.MinValue);
    }

    [Fact]
    public void Manifest_absolute_url_wins_and_bad_sha_is_null()
    {
        var js = """{"version":"1.0.0","file":"a.zip","url":"https://github.com/x/y/releases/download/v1.0.0/a.zip","sha256":"short"}""";
        var i = UpdateFeed.ParseManifest(js, "https://github.com/x/y")!;
        Assert.Equal("https://github.com/x/y/releases/download/v1.0.0/a.zip", i.AssetUrl);
        Assert.Null(i.Sha256);
        Assert.Null(UpdateFeed.SourceError(i));
        Assert.Null(UpdateFeed.ParseManifest("{}", "x"));
    }

    [Fact]
    public void Manifest_pointing_to_other_origin_is_rejected()
    {
        var http = UpdateFeed.ParseManifest("""{"version":"1.0.0","url":"http://srv/fota/a.zip"}""", "https://github.com/x/y")!;
        Assert.NotNull(UpdateFeed.SourceError(http));
        var other = UpdateFeed.ParseManifest("""{"version":"1.0.0","url":"https://evil.example/github.com/a.zip"}""", "https://github.com/x/y")!;
        Assert.NotNull(UpdateFeed.SourceError(other));
        var file = UpdateFeed.ParseManifest("""{"version":"1.0.0"}""", "file://server/share/fota")!;
        Assert.NotNull(UpdateFeed.SourceError(file));
    }

    [Theory]
    [InlineData("https://github.com/Leepy0/AGV_FOTA_GATEWAY", true)]
    [InlineData("https://GitHub.com/x/y/releases/latest/download/AgvFotaGateway.json", true)]
    [InlineData("https://api.github.com/repos/x/y/releases/assets/1", true)]
    [InlineData("http://github.com/x/y", false)]
    [InlineData("file://server/share/fota", false)]
    [InlineData("file:///C:/fota/AgvFotaGateway.json", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://evil.example/api.github.com/x", false)]
    [InlineData("https://github.com:8443/x/y", false)]
    [InlineData("https://user@github.com/x/y", false)]
    [InlineData("https://objects.githubusercontent.com/x", false)]   // 리다이렉트로 가는 것은 HttpClient 가 따라감 — 설정·매니페스트에 직접 쓰는 것은 거부
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_https_github_origins_are_allowed(string? url, bool ok) => Assert.Equal(ok, UpdateFeed.IsAllowedSource(url));

    [Fact]
    public void Token_only_for_api_github_host()
    {
        Assert.True(UpdateFeed.IsGitHubApi("https://api.github.com/repos/x/y/releases/assets/1"));
        Assert.False(UpdateFeed.IsGitHubApi("https://x.example/api.github.com/repos/x/y"));
        Assert.False(UpdateFeed.IsGitHubApi("https://github.com/x/y/releases/download/v1.0.0/a.zip"));
        Assert.False(UpdateFeed.IsGitHubApi("http://api.github.com/repos/x/y"));
    }

    [Fact]
    public void NewestStagedTag_picks_highest_newer_folder()
    {
        var cur = new Version(0, 3, 0);
        Assert.Equal("v0.3.2", UpdateFeed.NewestStagedTag(["v0.2.9", "v0.3.0", "v0.3.2", "v0.3.1", "vX", "v0.3.10-rc"], cur));
        Assert.Null(UpdateFeed.NewestStagedTag(["v0.2.9", "v0.3.0"], cur));
        Assert.Null(UpdateFeed.NewestStagedTag([], cur));
        Assert.Equal("v0.10.0", UpdateFeed.NewestStagedTag(["v0.9.0", "v0.10.0"], cur));
    }

    [Fact]
    public void GitHubRelease_picks_fixed_asset_and_digest()
    {
        var json = """
        {"tag_name":"v0.2.0","draft":false,"html_url":"h","body":"notes","published_at":"2026-10-08T09:00:00Z",
         "assets":[{"name":"AgvFotaGateway.json","url":"u1","size":10},
                   {"name":"AgvFotaGateway_win-x64.zip.sha256","url":"u3","size":100},
                   {"name":"AgvFotaGateway_win-x64.zip","url":"https://api.github.com/repos/x/y/releases/assets/1","size":1000,"digest":"sha256:ABCDEF"}]}
        """;
        var i = UpdateFeed.ParseGitHubRelease(json)!;
        Assert.Equal((new Version(0, 2, 0), "v0.2.0", 1000L, "abcdef", "u3"), (i.Version, i.Tag, i.Size, i.Sha256, i.Sha256Url));
        Assert.Contains("api.github.com", i.AssetUrl);
        Assert.Null(UpdateFeed.ParseGitHubRelease(json.Replace("\"draft\":false", "\"draft\":true")));
    }

    [Fact]
    public void ShaFile_parses_sha256sum_format()
    {
        var sha = new string('B', 64);
        Assert.Equal(sha.ToLowerInvariant(), UpdateFeed.ParseShaFile($"{sha}  AgvFotaGateway_win-x64.zip\n"));
        Assert.Equal(sha.ToLowerInvariant(), UpdateFeed.ParseShaFile($"{sha} *file"));
        Assert.Null(UpdateFeed.ParseShaFile("garbage"));
    }
}
