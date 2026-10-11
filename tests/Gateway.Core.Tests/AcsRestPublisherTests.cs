using System.Net;
using Gateway.Core.Acs;

namespace Gateway.Core.Tests;

/// <summary>ACS REST 주소 검사 (INT-B4): http/https 절대 주소가 아니면 예외 대신 실패 결과</summary>
public class AcsRestPublisherTests
{
    sealed class Stub : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Seen.Add(req);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("acs.local:8000")]
    [InlineData("acs.local")]
    [InlineData("ftp://acs.local")]
    [InlineData("file:///etc")]
    [InlineData("http://")]
    [InlineData("http://exa mple")]
    public async Task Bad_base_url_returns_failure_without_sending(string baseUrl)
    {
        var stub = new Stub();
        var p = new AcsRestPublisher(new HttpClient(stub));
        var s = new AcsRestSettings(baseUrl, "/api/agv/{agv}");
        var g = await p.GetAsync(s);
        var w = await p.PublishAsync(s, "{}", 7);
        foreach (var r in new[] { g, w })
        {
            Assert.False(r.Ok);
            Assert.Equal(0, r.StatusCode);
            Assert.StartsWith("잘못된 주소", r.Error);
            Assert.Contains("잘못된 주소", r.ToString());
        }
        Assert.Empty(stub.Seen);
    }

    [Fact]
    public async Task Good_url_is_joined_and_sent()
    {
        var stub = new Stub();
        var p = new AcsRestPublisher(new HttpClient(stub));
        var g = await p.GetAsync(new AcsRestSettings(" http://acs.local:8000/ ", "api/inv"));
        var w = await p.PublishAsync(new AcsRestSettings("https://acs.local", "/api/agv/{agv}/pos", "PUT"), "{}", 7);
        Assert.True(g.Ok); Assert.True(w.Ok);
        Assert.Equal("http://acs.local:8000/api/inv", stub.Seen[0].RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.Seen[0].Method);
        Assert.Equal("https://acs.local/api/agv/7/pos", stub.Seen[1].RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, stub.Seen[1].Method);
    }
}
