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
        var js = """{"version":"1.0.0","file":"a.zip","url":"http://srv/fota/a.zip","sha256":"short"}""";
        var i = UpdateFeed.ParseManifest(js, "http://srv/fota")!;
        Assert.Equal("http://srv/fota/a.zip", i.AssetUrl);
        Assert.Null(i.Sha256);
        Assert.Null(UpdateFeed.ParseManifest("{}", "x"));
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
