using System.Net;
using System.Text.Json;
using Gateway.Core.Acs;
using Gateway.Core.Positions;

namespace Gateway.Core.Tests;

sealed class CaptureHandler(HttpStatusCode code, string body = "ok") : HttpMessageHandler
{
    public HttpRequestMessage? Last; public string? LastBody;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Last = request; LastBody = await request.Content!.ReadAsStringAsync(ct);
        return new HttpResponseMessage(code) { Content = new StringContent(body) };
    }
}

public class AcsPositionTests
{
    static AgvPosition P(int agv) => new(agv, new DateTimeOffset(2026, 10, 9, 3, 0, 0, TimeSpan.Zero), 12.5, 3.0, 90, PositionState.Tracked, 0.8, false, "0T21", null, "1라인", true);

    [Fact]
    public void Mapper_builds_camelCase_json_without_nulls()
    {
        var js = AcsPositionMapper.Serialize(AcsPositionMapper.Build([P(7), P(3)], new DateTimeOffset(2026, 10, 9, 3, 0, 1, TimeSpan.Zero)));
        using var d = JsonDocument.Parse(js);
        var r = d.RootElement;
        Assert.Equal("AgvFotaGateway", r.GetProperty("source").GetString());
        var items = r.GetProperty("positions").EnumerateArray().ToList();
        Assert.Equal(new[] { 3, 7 }, items.Select(i => i.GetProperty("agvId").GetInt32()));   // 호기 순 정렬
        Assert.Equal(12.5, items[0].GetProperty("x").GetDouble());
        Assert.Equal("TRACKED", items[0].GetProperty("state").GetString());
        Assert.False(items[0].TryGetProperty("lastTagAt", out _));                             // null 은 생략
        Assert.Contains("\"sentAt\":\"2026-10-09T03:00:01+00:00\"", js);
        Assert.Contains("\"map\":\"1라인\"", js);                                                   // 한글 그대로
    }

    [Theory]
    [InlineData("Bearer", "tok", "Bearer tok", null)]
    [InlineData("Basic", "u:p", "Basic dTpw", null)]
    [InlineData("ApiKey", "k1", null, "k1")]
    public async Task Publisher_sets_auth_method_and_path(string type, string value, string? authHeader, string? apiKey)
    {
        var h = new CaptureHandler(HttpStatusCode.OK);
        var pub = new AcsRestPublisher(new HttpClient(h));
        var s = new AcsRestSettings("http://acs/", "/api/agv/{agv}/position", "PUT", type, value, "X-Key", 2000);
        var r = await pub.PublishAsync(s, "{\"a\":1}", 7);
        Assert.True(r.Ok); Assert.Equal(200, r.StatusCode);
        Assert.Equal(HttpMethod.Put, h.Last!.Method);
        Assert.Equal("http://acs/api/agv/7/position", h.Last.RequestUri!.ToString());
        Assert.Equal(authHeader, h.Last.Headers.Authorization?.ToString());
        if (apiKey is not null) Assert.Equal(apiKey, h.Last.Headers.GetValues("X-Key").Single());
        Assert.Equal("{\"a\":1}", h.LastBody);
        Assert.Equal("application/json", h.Last.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Publisher_reports_http_error()
    {
        var pub = new AcsRestPublisher(new HttpClient(new CaptureHandler(HttpStatusCode.BadRequest, "schema mismatch")));
        var r = await pub.PublishAsync(new AcsRestSettings("http://acs", "/p"), "{}");
        Assert.False(r.Ok); Assert.Equal(400, r.StatusCode); Assert.Contains("schema mismatch", r.ToString());
    }
}
