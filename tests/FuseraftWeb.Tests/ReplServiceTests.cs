using fuseraft.Web.Models;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class ReplServiceTests : IDisposable
{
    private readonly FakeCli    _cli      = new();
    private readonly HitlBroker _broker   = new();
    private readonly Accounts   _accounts = new();
    private readonly ModelProfileRegistry _profiles = new() { UseKeychain = false };

    public void Dispose() => _accounts.Dispose();

    private static object Ready() => new { type = "ready", protocolVersion = 1, sessionId = "r1", model = "grok-4.7" };

    [Fact]
    public async Task Chat_UsesTheProfilesConnection_AndItsKeyGoesToTheProcessOnly()
    {
        var bob = _accounts.Add("bob", roots: ["/w"]);
        await _profiles.For(bob).SaveAsync(new ModelProfile
        {
            Id = "p1", Name = "Local", ModelId = "llama3.1:8b", Provider = "ollama", Endpoint = "http://gpu-box:11434",
        }, "sk-bob");
        _cli.Bridges.Enqueue(new FakeBridge().Send(Ready()));
        var repl = new ReplService(_profiles, _cli, _broker);

        var key = await repl.CreateSessionAsync(bob, "p1", "", "/w");

        Assert.Equal(["repl", "--vscode", "--no-banner", "--model", "llama3.1:8b",
                      "--provider", "ollama", "--endpoint", "http://gpu-box:11434"], _cli.Started[0].Args);
        Assert.Empty(_cli.Runs); // nothing written to the account's config
        Assert.Equal("sk-bob", _cli.Environments[0]!["FUSERAFT_API_KEY"]);
        Assert.Equal([bob.Home], _cli.Homes.Distinct());
        Assert.NotNull(repl.GetSession(bob, key));
    }

    [Theory]
    [InlineData("", "", new string[0])]
    [InlineData("other", "https://gw.example/v1", new[] { "--endpoint", "https://gw.example/v1" })]
    [InlineData("anthropic", "", new[] { "--provider", "anthropic" })]
    public void ConnectionArgs_PassOnlyWhatTheProfileSets(string provider, string endpoint, string[] expected)
    {
        var profile = new ModelProfile { Id = "p", ModelId = "m", Provider = provider, Endpoint = endpoint };
        Assert.Equal(expected, ReplService.ConnectionArgs(profile));
    }

    [Fact]
    public async Task Chats_AreTheirOwnersOnly_EvenForAdmins()
    {
        var bob = _accounts.Add("bob", roots: ["/w"]);
        await _profiles.For(bob).SaveAsync(new ModelProfile { Id = "p1", Name = "Grok", ModelId = "grok-4.7" }, "sk-bob");
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        var repl = new ReplService(_profiles, _cli, _broker);
        var key  = await repl.CreateSessionAsync(bob, "p1", "", "/w");
        await Wait.UntilAsync(() => repl.GetSession(bob, key)!.SessionId == "r1", "the chat to be ready");

        Assert.Null(repl.GetSession(_accounts.Alice, key));
        Assert.Empty(repl.GetSessions(_accounts.Alice));
        repl.SendMessage(_accounts.Alice, key, "hijack");
        repl.CloseSession(_accounts.Alice, key);

        Assert.DoesNotContain(bridge.InputLines, l => l.Contains("hijack"));
        Assert.NotNull(repl.GetSession(bob, key));
    }

    [Fact]
    public async Task SwitchingSessions_FollowsTheNewSessionId()
    {
        var bob = _accounts.Add("bob", roots: ["/w"]);
        await _profiles.For(bob).SaveAsync(new ModelProfile { Id = "p1", Name = "Grok", ModelId = "grok-4.7" }, "sk-bob");
        var bridge = new FakeBridge()
            .Send(Ready())
            .Send(new { type = "title", sessionId = "r2", title = "other chat", autoTitle = false })
            .Send(new { type = "approval_request", kind = "shell_command", command = "ls" });
        _cli.Bridges.Enqueue(bridge);
        var repl = new ReplService(_profiles, _cli, _broker);

        var key = await repl.CreateSessionAsync(bob, "p1", "", "/w");

        await Wait.UntilAsync(() => _broker.GetPending(bob).Count == 1, "the approval");
        Assert.Equal("r2", repl.GetSession(bob, key)!.SessionId);
        Assert.Equal("r2", _broker.GetPending(bob).Single().SessionId);
    }

    private async Task<(ReplService Repl, WebUser Bob, string Key, FakeBridge Bridge)> ReplyInProgressAsync()
    {
        var bob = _accounts.Add("bob", roots: ["/w"]);
        await _profiles.For(bob).SaveAsync(new ModelProfile { Id = "p1", Name = "Grok", ModelId = "grok-4.7" }, "sk-bob");
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        var repl = new ReplService(_profiles, _cli, _broker);
        var key  = await repl.CreateSessionAsync(bob, "p1", "", "/w");
        await Wait.UntilAsync(() => repl.GetSession(bob, key)!.SessionId == "r1", "the chat to be ready");
        repl.SendMessage(bob, key, "refactor the parser");
        bridge.Send(new { type = "token", text = "Reading the parser" });
        await Wait.UntilAsync(() => repl.GetSession(bob, key)!.StreamingText == "Reading the parser", "the reply to stream");
        return (repl, bob, key, bridge);
    }

    [Fact]
    public async Task MidReply_MessageGoesToTheCli_BelowWhatStreamedSoFar()
    {
        var (repl, bob, key, bridge) = await ReplyInProgressAsync();

        repl.SendMessage(bob, key, "keep the public API");

        Assert.Contains(bridge.InputLines, l => l.Contains("keep the public API"));
        var chat = repl.GetSession(bob, key)!;
        Assert.Equal(["user", "assistant", "user"], chat.Messages.Select(m => m.Role));
        Assert.Equal("Reading the parser", chat.Messages[1].Text);
        Assert.Equal(ReplStatus.Thinking, chat.Status);
        Assert.Equal("1", _cli.Tags.Single()!.Extra["inputWhileBusy"]);
    }

    [Fact]
    public async Task MidReply_OlderCli_KeepsTheInputForAfterTheReply()
    {
        _cli.Version = new Version(1, 19, 0);
        var (repl, bob, key, bridge) = await ReplyInProgressAsync();

        repl.SendMessage(bob, key, "keep the public API");

        Assert.DoesNotContain(bridge.InputLines, l => l.Contains("keep the public API"));
        Assert.False(repl.GetSession(bob, key)!.InputWhileBusy);
        Assert.False(_cli.Tags.Single()!.Extra.ContainsKey("inputWhileBusy"));
    }

    [Theory]
    [InlineData("applied", null, "HITL off")]
    [InlineData("interjection", "carried", "starts the next turn")]
    [InlineData("interjection", "dropped", "was dropped")]
    public async Task MidReply_WhatTheCliDidWithIt_IsShown(string type, string? status, string expected)
    {
        var (repl, bob, key, bridge) = await ReplyInProgressAsync();

        bridge.Send(new { type, status, count = 1, command = "/hitl off", message = "HITL off" });

        await Wait.UntilAsync(() => repl.GetSession(bob, key)!.Messages.Any(m => m.Role == "system"), "the note");
        Assert.Contains(expected, repl.GetSession(bob, key)!.Messages.Single(m => m.Role == "system").Text);
    }

    [Fact]
    public void OpenChat_IsAdopted_WithWhetherItTakesInputWhileBusy()
    {
        var bob = _accounts.Add("bob", roots: ["/w"]);
        var repl = new ReplService(_profiles, _cli, _broker);
        fuseraft.Web.Services.Relay.BridgeTag Tag(string key, bool busyInput)
        {
            var tag = new fuseraft.Web.Services.Relay.BridgeTag { Kind = ReplService.Kind, OwnerId = bob.Id, Extra = { ["key"] = key } };
            if (busyInput) tag.Extra["inputWhileBusy"] = "1";
            return tag;
        }

        repl.AdoptRunning(Tag("new", true), new FakeBridge(), bob);
        repl.AdoptRunning(Tag("old", false), new FakeBridge(), bob);

        Assert.True(repl.GetSession(bob, "new")!.InputWhileBusy);
        Assert.False(repl.GetSession(bob, "old")!.InputWhileBusy);
    }

    [Theory]
    [InlineData("fuseraft 1.20.0+253d8ef1cfbc", "1.20.0")]
    [InlineData("fuseraft 1.20.1-alpha.0.2+abc", "1.20.1")]
    [InlineData("command not found", null)]
    public void ParseVersion_ReadsTheRelease(string text, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), fuseraft.Web.Services.Cli.FuseraftCli.ParseVersion(text));

    [Fact]
    public async Task NonAdmin_CantChatOutsideTheirWorkspaces()
    {
        var bob = _accounts.Add("bob", roots: ["/w"]);
        var repl = new ReplService(_profiles, _cli, _broker);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repl.CreateSessionAsync(bob, "p1", "", "/etc"));
        Assert.Empty(_cli.Started);
    }

    [Fact]
    public async Task OpenChat_IsAdopted_WithWhatWasTyped()
    {
        var bob = _accounts.Add("bob", roots: ["/w"]);
        var repl = new ReplService(_profiles, _cli, _broker);
        var bridge = new FakeBridge()
            .Send(Ready())
            .Send(new { type = "replayed_input", text = "what is 2+2?", raw = false })
            .Send(new { type = "token", text = "4" })
            .Send(new { type = "message_end" })
            .Send(new { type = "replayed_input", text = "now write it to a file", raw = false })
            .Send(new { type = "approval_request", kind = "file_write", path = "/w/a.txt" });
        var tag = new fuseraft.Web.Services.Relay.BridgeTag
        {
            Kind = ReplService.Kind, OwnerId = bob.Id,
            Extra = { ["key"] = "k1", ["profileId"] = "p1", ["modelId"] = "grok-4.7", ["cwd"] = "/w" },
        };

        repl.AdoptRunning(tag, bridge, bob);

        await Wait.UntilAsync(() => _broker.GetPending(bob).Count == 1, "the open approval");
        var chat = repl.GetSession(bob, "k1")!;
        Assert.Equal(["user", "assistant", "user"], chat.Messages.Select(m => m.Role));
        Assert.Equal("now write it to a file", chat.Messages[^1].Text);
        Assert.Equal(ReplStatus.Thinking, chat.Status);
        Assert.Equal("/w", chat.Cwd);
    }

    [Fact]
    public async Task StoppingTheServer_LeavesChatsOpen()
    {
        var bob = _accounts.Add("bob", roots: ["/w"]);
        await _profiles.For(bob).SaveAsync(new ModelProfile { Id = "p1", Name = "Grok", ModelId = "grok-4.7" }, "sk-bob");
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        var repl = new ReplService(_profiles, _cli, _broker);
        var key = await repl.CreateSessionAsync(bob, "p1", "", "/w");
        await Wait.UntilAsync(() => repl.GetSession(bob, key)!.SessionId == "r1", "the chat to be ready");

        repl.Dispose();

        Assert.False(bridge.Killed);
        var tag = _cli.Tags.Single()!;
        Assert.Equal(ReplService.Kind, tag.Kind);
        Assert.Equal(key, tag.Extra["key"]);
        Assert.Equal("p1", tag.Extra["profileId"]);
    }
}
