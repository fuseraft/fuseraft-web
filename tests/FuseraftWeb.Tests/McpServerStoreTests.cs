using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Mcp;

namespace FuseraftWeb.Tests;

public sealed class McpServerStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-web-mcp").FullName;
    private readonly EphemeralDataProtectionProvider _protection = new();
    private string FilePath => Path.Combine(_dir, "mcp-servers.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private McpServerStore NewStore() => new(FilePath, _protection);

    [Fact]
    public void Token_IsEncryptedOnDisk_AndSurvivesAReload()
    {
        var added = NewStore().Add("Sql", "http://localhost:8080/mcp", " s3cret-token-value ", "admin");

        var reloaded = NewStore();

        Assert.Equal("s3cret-token-value", reloaded.Token(added.Id));
        Assert.True(Assert.Single(reloaded.All()).HasToken);
        Assert.DoesNotContain("s3cret-token-value", File.ReadAllText(FilePath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Theory]
    [InlineData("1sql", "http://h/mcp", "starts with a letter")]
    [InlineData("my sql", "http://h/mcp", "starts with a letter")]
    [InlineData("Sql", "ftp://h/mcp", "http:// or https://")]
    [InlineData("Sql", "localhost:8080/mcp", "http:// or https://")]
    [InlineData("Sql", "http://user:pw@h/mcp", "not the URL")]
    public void Add_RefusesBadInput(string name, string url, string problem)
    {
        var ex = Assert.Throws<ArgumentException>(() => NewStore().Add(name, url, null, null));
        Assert.Contains(problem, ex.Message);
    }

    [Fact]
    public void Add_RefusesADuplicateName_WhateverItsCase()
    {
        var store = NewStore();
        store.Add("Sql", "http://a/mcp", null, null);

        Assert.Contains("already", Assert.Throws<ArgumentException>(() => store.Add("SQL", "http://b/mcp", null, null)).Message);
    }

    [Fact]
    public void Update_KeepsTheToken_UnlessGivenOneOrEmpty()
    {
        var store = NewStore();
        var s = store.Add("Sql", "http://a/mcp", "first-token", null);

        store.Update(s.Id, "http://b/mcp", null);
        Assert.Equal(("http://b/mcp", "first-token"), (store.Find(s.Id)!.Url, store.Token(s.Id)));

        store.Update(s.Id, "http://b/mcp", "second-token");
        Assert.Equal("second-token", store.Token(s.Id));

        store.Update(s.Id, "http://b/mcp", "");
        Assert.False(store.Find(s.Id)!.HasToken);
        Assert.Null(store.Token(s.Id));
    }

    [Fact]
    public void Environment_IsEmpty_WithNoEnabledServers()
    {
        var store = NewStore();
        Assert.Empty(store.Environment());

        var s = store.Add("Sql", "http://a/mcp", null, null);
        store.SetEnabled(s.Id, false);

        Assert.Empty(store.Environment());
    }

    [Fact]
    public void Environment_ListsEnabledServers_WithTokensInTheirOwnVariables()
    {
        var store = NewStore();
        store.Add("Sql", "http://localhost:8080/mcp", "sql-token-1234", null);
        store.Add("Open", "https://open.example.com/mcp", null, null);
        var off = store.Add("Off", "http://off/mcp", "off-token-1234", null);
        store.SetEnabled(off.Id, false);

        var env = store.Environment();

        Assert.Equal("sql-token-1234", env["FUSERAFT_WEB_MCP_1_TOKEN"]);
        Assert.Equal(2, env.Count);
        Assert.DoesNotContain("sql-token-1234", env["FUSERAFT_MCP_SERVERS"]);

        var servers = JsonDocument.Parse(env["FUSERAFT_MCP_SERVERS"]).RootElement;
        Assert.Equal(2, servers.GetArrayLength());
        var sql = servers[0];
        Assert.Equal("Sql", sql.GetProperty("Name").GetString());
        Assert.Equal("http", sql.GetProperty("Transport").GetString());
        Assert.Equal("streamable-http", sql.GetProperty("TransportMode").GetString());
        Assert.Equal("Bearer ${FUSERAFT_WEB_MCP_1_TOKEN}", sql.GetProperty("Headers").GetProperty("Authorization").GetString());
        Assert.Empty(servers[1].GetProperty("Headers").EnumerateObject());
    }

    [Fact]
    public void Environment_LeavesOutAServerWhoseTokenCantBeDecrypted()
    {
        NewStore().Add("Sql", "http://a/mcp", "sql-token-1234", null);

        var otherKeys = new McpServerStore(FilePath, new EphemeralDataProtectionProvider());

        Assert.Empty(otherKeys.Environment());
        Assert.Null(otherKeys.Token(otherKeys.All()[0].Id));
    }

    [Fact]
    public void Changes_RaiseChanged()
    {
        var store = NewStore();
        var raised = 0;
        store.Changed += () => raised++;

        var s = store.Add("Sql", "http://a/mcp", null, null);
        store.SetEnabled(s.Id, false);
        store.SetEnabled(s.Id, false);
        store.Delete(s.Id);

        Assert.Equal(3, raised);
    }

    [Fact]
    public async Task FuseraftCli_HandsTheServersToWhatItStarts()
    {
        if (OperatingSystem.IsWindows()) return; // runs env(1) as the fuseraft binary

        var store = NewStore();
        store.Add("Sql", "http://localhost:8080/mcp", "sql-token-1234", null);
        var cli = new FuseraftCli(Options.Create(new FuseraftCliOptions { Binary = "env" }), mcp: store)
            .ForHome(Path.Combine(_dir, "home"));

        using var proc = cli.StartBridge([]);
        var output = await proc.Output.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.Contains("FUSERAFT_WEB_MCP_1_TOKEN=sql-token-1234", output);
        Assert.Contains("FUSERAFT_MCP_SERVERS=[{\"Name\":\"Sql\"", output);
    }
}
