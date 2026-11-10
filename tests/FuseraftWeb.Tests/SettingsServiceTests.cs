using fuseraft.Web.Services;
using fuseraft.Web.Services.Cli;

namespace FuseraftWeb.Tests;

public sealed class SettingsServiceTests
{
    private const string Listing = """
        {"protocolVersion":1,"items":[
          {"key":"provider.modelId","kind":"text","description":"d","value":"m","default":null,"min":null,"max":null,"choices":null,"clearable":false},
          {"key":"repl.theme","kind":"text","description":"d","value":null,"default":"default","min":null,"max":null,"choices":null,"clearable":true},
          {"key":"sampling.temperature","kind":"number","description":"d","value":"0.7","default":null,"min":0,"max":2,"choices":null,"clearable":true}
        ]}
        """;

    [Fact]
    public async Task List_LeavesOutWhatProfilesAndTheTerminalOwn()
    {
        var cli = new FakeCli();
        cli.Responses["settings show --json"] = Listing;

        var setting = Assert.Single(await new SettingsService(cli).ListAsync());

        Assert.Equal("sampling.temperature", setting.Key);
        Assert.Equal("0.7", setting.Value);
        Assert.Equal(2, setting.Max);
    }

    [Fact]
    public async Task Set_ReturnsTheSettingAsSaved()
    {
        var cli = new FakeCli();
        cli.Responses["settings set sampling.temperature 0.4 --json"] =
            """{"protocolVersion":1,"item":{"key":"sampling.temperature","kind":"number","description":"d","value":"0.4","default":null,"min":0,"max":2,"choices":null,"clearable":true}}""";

        var saved = await new SettingsService(cli).SetAsync("sampling.temperature", "0.4");

        Assert.Equal("0.4", saved.Value);
    }

    [Fact]
    public async Task Set_RejectedValue_ThrowsTheCliReason()
    {
        var cli = new FakeCli();
        cli.Responses["settings set sampling.temperature 3 --json"] =
            """{"protocolVersion":1,"error":"Expected a number from 0 to 2, got '3'."}""";

        var ex = await Assert.ThrowsAsync<FuseraftCliException>(() => new SettingsService(cli).SetAsync("sampling.temperature", "3"));

        Assert.Contains("from 0 to 2", ex.Message);
    }

    [Fact]
    public async Task Set_HiddenKey_IsRefusedWithoutRunningTheCli()
    {
        var cli = new FakeCli();

        await Assert.ThrowsAsync<FuseraftCliException>(() => new SettingsService(cli).SetAsync("provider.modelId", "x"));

        Assert.Empty(cli.Queries);
    }

    [Fact]
    public async Task List_FailingOnAnOlderFuseraft_SaysToUpdate()
    {
        var cli = new FakeCli { Version = new Version(1, 22, 0) };

        var ex = await Assert.ThrowsAsync<FuseraftCliException>(() => new SettingsService(cli).ListAsync());

        Assert.Contains("needs fuseraft 1.23.0", ex.Message);
    }

    [Fact]
    public async Task List_FailingOnACurrentFuseraft_KeepsItsReason()
    {
        var cli = new FakeCli { Version = new Version(1, 23, 0) };

        var ex = await Assert.ThrowsAsync<FuseraftCliException>(() => new SettingsService(cli).ListAsync());

        Assert.Contains("no canned response", ex.Message);
    }
}
