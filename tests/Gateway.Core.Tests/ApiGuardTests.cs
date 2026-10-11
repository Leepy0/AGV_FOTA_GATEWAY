using System.Net;
using Gateway.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gateway.Core.Tests;

/// <summary>/api 공통 검사: 다른 출처 변경 요청 거부(X-03), 수동 전송 거부 조건(결정 2, S-10)</summary>
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
}
