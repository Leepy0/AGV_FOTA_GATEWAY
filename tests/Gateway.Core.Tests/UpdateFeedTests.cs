using Gateway.Core.Update;

namespace Gateway.Core.Tests;

public class UpdateFeedTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-abc1234", "1.2.3")]
    [InlineData("0.1.0+build.5", "0.1.0")]
    [InlineData("AgvFotaGateway_2.0_win-x64.zip", "2.0.0")]
    public void ParseVersion_strips_prefix_and_suffix(string s, string expected) => Assert.Equal(Version.Parse(expected), UpdateFeed.ParseVersion(s));

    [Fact]
    public void IsNewer_compares_three_parts()
    {
        Assert.True(UpdateFeed.IsNewer(new(0, 2, 0), new(0, 1, 9)));
        Assert.False(UpdateFeed.IsNewer(new(0, 1, 0), new(0, 1, 0)));
        Assert.False(UpdateFeed.IsNewer(new(0, 1, 0), new Version(0, 1, 0, 0)));
    }

    [Fact]
    public void ParseGitHubRelease_picks_win_x64_asset()
    {
        var json = """
        {"tag_name":"v0.2.0","draft":false,"html_url":"https://github.com/x/y/releases/tag/v0.2.0","body":"notes",
         "assets":[{"name":"latest.json","url":"u1","browser_download_url":"b1","size":10},
                   {"name":"AgvFotaGateway_0.2.0_win-x64.zip","url":"https://api.github.com/repos/x/y/releases/assets/1","browser_download_url":"b2","size":1000}]}
        """;
        var i = UpdateFeed.ParseGitHubRelease(json, "_win-x64.zip")!;
        Assert.Equal((new Version(0, 2, 0), "v0.2.0", "AgvFotaGateway_0.2.0_win-x64.zip", 1000L), (i.Version, i.Tag, i.AssetName, i.Size));
        Assert.Contains("api.github.com", i.AssetUrl);
        Assert.Null(UpdateFeed.ParseGitHubRelease(json.Replace("\"draft\":false", "\"draft\":true"), "_win-x64.zip"));
    }

    [Fact]
    public void LatestJson_roundtrip()
    {
        var js = UpdateFeed.BuildLatestJson("0.3.1", "http://srv/fota/AgvFotaGateway_0.3.1_win-x64.zip", "n", 5);
        var i = UpdateFeed.ParseLatestJson(js)!;
        Assert.Equal((new Version(0, 3, 1), "AgvFotaGateway_0.3.1_win-x64.zip", 5L), (i.Version, i.AssetName, i.Size));
    }
}
