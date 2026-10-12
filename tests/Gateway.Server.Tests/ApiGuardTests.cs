using System.Net;
using Gateway.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Server.Tests;

/// <summary>/api 공통 검사: 다른 출처 변경 요청 거부(X-03), Host 검사(DNS 리바인딩, R16), 수동 전송 거부 조건(결정 2, S-10)</summary>
public class ApiGuardTests
{
    static HttpRequest Req(string method, string host = "192.168.10.5:8080", string? origin = null, string? fetchSite = null, string scheme = "http")
    {
        var c = new DefaultHttpContext();
        c.Request.Method = method;
        c.Request.Scheme = scheme;
        c.Request.Host = new HostString(host);
        if (origin is not null) c.Request.Headers.Origin = origin;
        if (fetchSite is not null) c.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        return c.Request;
    }

    [Theory]
    [InlineData("POST", null, null)]                                 // curl·스크립트
    [InlineData("POST", "http://192.168.10.5:8080", "same-origin")]  // 이 서버 화면
    [InlineData("DELETE", "http://192.168.10.5:8080", null)]
    [InlineData("GET", "http://evil.example", "cross-site")]         // 읽기는 검사 안 함
    [InlineData("POST", null, "same-site")]
    [InlineData("POST", null, "none")]
    public void Allowed(string method, string? origin, string? site) => Assert.False(ApiGuard.IsCrossSite(Req(method, origin: origin, fetchSite: site), out _));

    [Theory]
    [InlineData("POST", "http://wiki.factory.local", null)]
    [InlineData("POST", null, "cross-site")]
    [InlineData("PUT", "http://192.168.10.5:9090", null)]            // 같은 IP 다른 포트
    [InlineData("DELETE", "null", null)]                             // sandbox iframe·file://
    [InlineData("POST", "https://192.168.10.5:8080", null)]          // scheme 다름
    [InlineData("POST", "https://192.168.10.5", null)]               // 기본 포트 443 ≠ 8080
    public void Rejected(string method, string? origin, string? site)
    {
        Assert.True(ApiGuard.IsCrossSite(Req(method, origin: origin, fetchSite: site), out var why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void Default_port_and_case_are_normalized()
    {
        Assert.False(ApiGuard.IsCrossSite(Req("POST", host: "Gateway-PC", origin: "http://gateway-pc"), out _));
        Assert.False(ApiGuard.IsCrossSite(Req("POST", host: "gw:443", origin: "https://gw", scheme: "https"), out _));
    }

    [Fact]
    public void Manual_send_refusal()
    {
        Assert.IsType<BadRequest<string>>(ApiGuard.ManualSendRefusal(0, null, 5));
        Assert.IsType<BadRequest<string>>(ApiGuard.ManualSendRefusal(100, null, 5));
        Assert.Null(ApiGuard.ManualSendRefusal(7, null, 5));
        var a = new AgvState { Id = 7, EndPoint = new IPEndPoint(IPAddress.Parse("192.168.10.107"), 50000) };
        Assert.Null(ApiGuard.ManualSendRefusal(7, a, 5));
        a.ConflictEndPoint = new IPEndPoint(IPAddress.Parse("192.168.10.99"), 50000);
        a.ConflictAt = DateTime.UtcNow;
        var r = Assert.IsType<Conflict<string>>(ApiGuard.ManualSendRefusal(7, a, 5));
        Assert.Contains("192.168.10.99", r.Value);
        a.ConflictAt = DateTime.UtcNow.AddMinutes(-1);   // 충돌이 지난 일이면 다시 허용
        Assert.Null(ApiGuard.ManualSendRefusal(7, a, 5));
    }

    static HostGuard Guard(params string[] extra) => new(extra, machineNames: ["GW-PC", "gw-pc", "gw-pc.corp.local"]);

    [Theory]
    [InlineData("192.168.10.5")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("[fe80::1]")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("gw-pc")]
    [InlineData("GW-PC")]
    [InlineData("gw-pc.corp.local")]
    [InlineData("gw-pc.")]                 // 끝 점
    [InlineData("fota.factory")]           // Gateway.AllowedHostNames
    [InlineData("")]                       // Host 없음 (HTTP/1.0 스크립트) — 브라우저 아님
    public void Host_allowed(string host) => Assert.True(Guard("fota.factory").IsAllowed(host));

    [Theory]
    [InlineData("evil.example")]
    [InlineData("gw-pc.evil.example")]
    [InlineData("localhost.evil.example")]
    [InlineData("192.168.10.5.nip.io")]
    [InlineData("corp.local")]
    public void Host_rejected(string host) => Assert.False(Guard("fota.factory").IsAllowed(host));

    [Fact]
    public void Host_star_allows_all() => Assert.True(Guard("*").IsAllowed("evil.example"));

    static async Task<(bool passed, int status, string body)> Run(HostGuard g, string host, string path, string method = "GET")
    {
        var c = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        c.Request.Method = method;
        c.Request.Host = new HostString(host);
        c.Request.Path = path;
        c.Response.Body = new MemoryStream();
        bool passed = false;
        await g.Middleware(c, _ => { passed = true; return Task.CompletedTask; });
        c.Response.Body.Position = 0;
        return (passed, c.Response.StatusCode, new StreamReader(c.Response.Body).ReadToEnd());
    }

    [Theory]
    [InlineData("/api/groups/import", "POST")]
    [InlineData("/api/db/backup", "GET")]     // 읽기도 막는다 (DB 전체가 같은 출처로 읽히지 않게)
    [InlineData("/_blazor", "POST")]          // 화면 SignalR
    [InlineData("/", "GET")]
    [InlineData("/app.css", "GET")]
    public async Task Rebound_host_is_403_on_every_path(string path, string method)
    {
        var (passed, status, body) = await Run(Guard(), "evil.example:8080", path, method);
        Assert.False(passed);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains("evil.example", body);
        Assert.Contains("AllowedHostNames", body);
    }

    [Theory]
    [InlineData("192.168.10.5:8080")]
    [InlineData("gw-pc:8080")]
    [InlineData("[::1]:8080")]
    public async Task Own_host_passes(string host)
    {
        var (passed, status, _) = await Run(Guard(), host, "/api/db/backup");
        Assert.True(passed);
        Assert.Equal(StatusCodes.Status200OK, status);
    }
}
