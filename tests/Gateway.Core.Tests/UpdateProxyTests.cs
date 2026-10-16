using System.Net;
using Gateway.Core.Update;

namespace Gateway.Core.Tests;

public class UpdateProxyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_uses_system_proxy_with_current_credentials(string? address)
    {
        var h = new HttpClientHandler();
        var s = UpdateProxy.Apply(h, address, "", "");
        Assert.True(h.UseProxy);
        Assert.Null(h.Proxy);
        Assert.Same(CredentialCache.DefaultCredentials, h.DefaultProxyCredentials);
        Assert.Contains("시스템 설정", s.Text);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    public void None_disables_proxy(string address)
    {
        var h = new HttpClientHandler();
        UpdateProxy.Apply(h, address, "ignored", "ignored");
        Assert.False(h.UseProxy);
        Assert.Null(h.Proxy);
    }

    [Fact]
    public void Address_with_domain_account_builds_web_proxy()
    {
        var h = new HttpClientHandler();
        var s = UpdateProxy.Apply(h, " http://proxy.corp:8080 ", @"CORP\svc", "secret-pw");
        Assert.True(h.UseProxy);
        var p = Assert.IsType<WebProxy>(h.Proxy);
        Assert.Equal(new Uri("http://proxy.corp:8080"), p.Address);
        Assert.True(p.BypassProxyOnLocal);
        var c = Assert.IsType<NetworkCredential>(p.Credentials);
        Assert.Equal("svc", c.UserName);
        Assert.Equal("CORP", c.Domain);
        Assert.Equal("secret-pw", c.Password);
        Assert.Null(h.DefaultProxyCredentials);
        Assert.DoesNotContain("secret-pw", s.Text);
        Assert.Contains("proxy.corp:8080", s.Text);
    }

    [Fact]
    public void Address_with_plain_account_has_no_domain()
    {
        var p = Assert.IsType<WebProxy>(UpdateProxy.Resolve("http://proxy:3128", "svc", "pw").Proxy);
        var c = Assert.IsType<NetworkCredential>(p.Credentials);
        Assert.Equal("svc", c.UserName);
        Assert.Equal("", c.Domain);
    }

    [Fact]
    public void Address_without_account_uses_current_credentials()
    {
        var p = Assert.IsType<WebProxy>(UpdateProxy.Resolve("https://proxy.corp", "", null).Proxy);
        Assert.Same(CredentialCache.DefaultCredentials, p.Credentials);
    }

    [Theory]
    [InlineData("proxy.corp:8080")]                // 스킴 없음
    [InlineData("ftp://proxy.corp")]               // http(s) 아님
    [InlineData("http://user:pw@proxy.corp:8080")] // 주소에 계정 금지 (로그에 남음)
    [InlineData("http://")]
    [InlineData("프록시")]
    public void Invalid_address_throws(string address)
        => Assert.Throws<ArgumentException>(() => UpdateProxy.Resolve(address, "", ""));
}
