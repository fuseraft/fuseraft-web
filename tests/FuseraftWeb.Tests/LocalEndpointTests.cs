using fuseraft.Web.Services;

namespace FuseraftWeb.Tests;

public sealed class LocalEndpointTests
{
    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:8000/v1")]
    [InlineData("http://192.168.1.5:11434")]
    [InlineData("http://10.0.0.2")]
    [InlineData("http://172.20.1.1")]
    [InlineData("http://100.101.1.2")]
    [InlineData("http://[fd00::1]:11434")]
    [InlineData("http://gpu-box:8000")]
    [InlineData("http://gpu-box.lan:8000")]
    [InlineData("192.168.1.5:11434")]
    public void IsLocal_ThisMachineAndTheLocalNetwork(string endpoint) =>
        Assert.True(LocalEndpoint.IsLocal(endpoint));

    [Theory]
    [InlineData("https://api.anthropic.com")]
    [InlineData("http://8.8.8.8")]
    [InlineData("http://172.32.0.1")]
    [InlineData("http://100.128.0.1")]
    [InlineData("")]
    [InlineData(null)]
    public void IsLocal_NotForTheInternet(string? endpoint) =>
        Assert.False(LocalEndpoint.IsLocal(endpoint));

    [Theory]
    [InlineData("ollama", "", true)]
    [InlineData("anthropic", "", false)]
    [InlineData("other", "http://192.168.1.5:8000/v1", true)]
    [InlineData("ollama", "https://ollama.example.com", false)]
    public void KeyOptional(string provider, string endpoint, bool optional) =>
        Assert.Equal(optional, LocalEndpoint.KeyOptional(provider, endpoint));
}
