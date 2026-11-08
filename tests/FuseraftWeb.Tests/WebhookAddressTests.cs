using System.Net;
using fuseraft.Web.Services.Webhooks;

namespace FuseraftWeb.Tests;

public sealed class WebhookAddressTests
{
    private static readonly WebhookOptions Defaults = new();

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    public void PrivateAndLocalAddresses_AreBlocked(string ip) => Assert.True(WebhookAddress.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddresses_AreNot(string ip) => Assert.False(WebhookAddress.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("https://hooks.example.com/x", null)]
    [InlineData("http://hooks.example.com/x", "Use https")]
    [InlineData("https://localhost/x", "private network")]
    [InlineData("https://app.localhost/x", "private network")]
    [InlineData("https://[::1]/x", "private network")]
    [InlineData("https://169.254.169.254/latest", "private network")]
    [InlineData("https://user:pw@hooks.example.com/", "credentials")]
    [InlineData("ftp://hooks.example.com/", "full http")]
    [InlineData("hooks.example.com/x", "full http")]
    public void Problem_ExplainsWhatsWrong(string url, string? expected)
    {
        var problem = WebhookAddress.Problem(url, Defaults);
        if (expected is null) Assert.Null(problem);
        else Assert.Contains(expected, problem);
    }

    [Fact]
    public void AnAllowedHost_MayBePrivateAndPlainHttp()
    {
        var options = new WebhookOptions { AllowedHosts = ["LocalHost", "10.0.0.5"] };
        Assert.Null(WebhookAddress.Problem("http://localhost:8080/hook", options));
        Assert.Null(WebhookAddress.Problem("http://10.0.0.5/hook", options));
        Assert.NotNull(WebhookAddress.Problem("http://10.0.0.6/hook", options));
    }

    [Fact]
    public async Task Connect_RefusesANameThatResolvesPrivately()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            WebhookAddress.ConnectAsync(new DnsEndPoint("localhost", 9), Defaults, default).AsTask());
        Assert.Contains("private network", ex.Message);
    }
}
