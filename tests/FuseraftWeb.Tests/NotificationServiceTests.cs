using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Web.Models;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Daemons;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class NotificationServiceTests : IDisposable
{
    private readonly FakeCli    _cli      = new();
    private readonly HitlBroker _broker   = new();
    private readonly Accounts   _accounts = new();
    private readonly ModelProfileRegistry _profiles = new() { UseKeychain = false };
    private readonly SessionHostService _sessions;
    private readonly ReplService _repl;
    private readonly NotificationService _notifications;
    private readonly List<AppNotification> _published = [];

    public NotificationServiceTests()
    {
        _cli.Responses["sessions --all --json"] = """{"protocolVersion":1,"items":[]}""";
        _sessions = new SessionHostService(_cli, _broker, _accounts.Store, _profiles, NullLogger<SessionHostService>.Instance);
        _repl     = new ReplService(_profiles, _cli, _broker);
        var daemons = new DaemonService([], _broker, () => throw new InvalidOperationException("no transport in these tests"));
        _notifications = new NotificationService(_broker, _sessions, _repl, daemons);
        _notifications.Published += n => { lock (_published) _published.Add(n); };
    }

    public void Dispose()
    {
        _notifications.Dispose();
        _accounts.Dispose();
    }

    private AppNotification Published(NotificationKind kind)
    {
        lock (_published) return Assert.Single(_published, n => n.Kind == kind);
    }

    private static object Ready() => new { type = "ready", protocolVersion = 1, sessionId = "r1", model = "grok-4.7" };

    [Fact]
    public void Approval_ForASession_LinksToItAndKeepsItsOwner()
    {
        _broker.Enqueue(new PendingApproval
        {
            SessionId = "s 1", Source = ApprovalSource.Session, OwnerId = "bob", Kind = "shell_command", Command = "rm -rf\nbuild",
        });

        var n = Published(NotificationKind.Approval);
        Assert.Equal(("bob", "/sessions/s%201", "attention"), (n.OwnerId, n.Link, n.Category));
        Assert.Equal("rm -rf build", n.Body);
    }

    [Fact]
    public void Approval_FromADaemon_IsForAdminsOnly()
    {
        _broker.Enqueue(new PendingApproval { SessionId = "d1", Source = ApprovalSource.Daemon, Origin = "box", Kind = "shell_command" });

        var n = Published(NotificationKind.Approval);
        Assert.Null(n.OwnerId);
        Assert.Contains("box", n.Title);
        Assert.Equal("/hitl", n.Link);
        Assert.False(n.IsFor(_accounts.Add("bob")));
        Assert.True(n.IsFor(_accounts.Alice));
    }

    [Fact]
    public void Admins_HearOfOthersSessionApprovals_ButNotTheirChatsOrResults()
    {
        var bob = _accounts.Add("bob");
        _broker.Enqueue(new PendingApproval { SessionId = "s", Source = ApprovalSource.Session, OwnerId = bob.Id, Kind = "shell_command" });
        _broker.Enqueue(new PendingApproval { SessionId = "c", Source = ApprovalSource.Repl, OwnerId = bob.Id, Kind = "shell_command" });
        var done = NotificationService.For(new ManagedSession
        {
            SessionId = "s", OwnerId = bob.Id, Task = "t", ConfigPath = "c", Status = SessionStatus.Succeeded,
        })!;

        List<AppNotification> approvals;
        lock (_published) approvals = [.. _published];
        Assert.Equal([true, false], approvals.Select(n => n.IsFor(_accounts.Alice)));
        Assert.All(approvals, n => Assert.True(n.IsFor(bob)));
        Assert.False(done.IsFor(_accounts.Alice));
        Assert.True(done.IsFor(bob));
    }

    [Fact]
    public async Task Session_ThatFails_SaysWhy()
    {
        var bridge = new FakeBridge().Send(new { type = "ready", protocolVersion = 1, sessionId = "abcd1234", task = "Build it" });
        _cli.Bridges.Enqueue(bridge);
        var id = await _sessions.StartSessionAsync(_accounts.Alice, "Build it", "/w/o.yaml", "/w");

        bridge.Send(new { type = "result", sessionId = id, succeeded = false, errorMessage = "Validator stuck", exitCode = 1 });
        bridge.Exit(1);
        await Wait.UntilAsync(() => { lock (_published) return _published.Count > 0; }, "the session to end");

        var n = Published(NotificationKind.SessionFailed);
        Assert.Equal((_accounts.Alice.Id, "Validator stuck", "/sessions/abcd1234", "err"), (n.OwnerId, n.Body, n.Link, n.Level));
    }

    [Fact]
    public void Session_CancelledByItsUser_IsNotNews()
    {
        var s = new ManagedSession { SessionId = "s", OwnerId = "bob", Task = "t", ConfigPath = "c", Status = SessionStatus.Cancelled };
        Assert.Null(NotificationService.For(s));
    }

    [Fact]
    public async Task Chat_RepliesAndQuestions_AreTheOwners()
    {
        var bob = _accounts.Add("bob", roots: ["/w"]);
        await _profiles.For(bob).SaveAsync(new ModelProfile { Id = "p1", Name = "Grok", ModelId = "grok-4.7" }, "sk-bob");
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        await _repl.CreateSessionAsync(bob, "p1", "", "/w");

        bridge.Send(new { type = "token", text = "All **done**: see `out.txt`." })
              .Send(new { type = "message_end" })
              .Send(new { type = "question", question = "Ship it?", options = new[] { "yes", "no" } });
        await Wait.UntilAsync(() => { lock (_published) return _published.Count == 2; }, "a reply and a question");

        var reply = Published(NotificationKind.ChatReplied);
        Assert.Equal((bob.Id, "All done: see out.txt.", "/chat", "replies"), (reply.OwnerId, reply.Body, reply.Link, reply.Category));
        Assert.Equal("Ship it?", Published(NotificationKind.Question).Body);
    }

    [Fact]
    public async Task Chat_WhoseProcessDies_IsAFailure()
    {
        var bob = _accounts.Add("bob", roots: ["/w"]);
        await _profiles.For(bob).SaveAsync(new ModelProfile { Id = "p1", Name = "Grok", ModelId = "grok-4.7" }, "sk-bob");
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        await _repl.CreateSessionAsync(bob, "p1", "", "/w");

        bridge.Exit(137);
        await Wait.UntilAsync(() => { lock (_published) return _published.Count > 0; }, "the chat to fail");

        Assert.Contains("exit 137", Published(NotificationKind.ChatFailed).Body);
    }

    [Fact]
    public void DaemonTask_ReportsItsDaemonAndOutcome()
    {
        var ok     = NotificationService.For("box", new DaemonTask { SessionId = "s", Task = "write a haiku", OwnerId = "bob", Status = DaemonTaskStatus.Succeeded });
        var failed = NotificationService.For("box", new DaemonTask { SessionId = "s", Task = "t", Status = DaemonTaskStatus.Failed, ErrorMessage = "boom" });

        Assert.Equal((NotificationKind.TaskFinished, "bob", "write a haiku", "/daemons"), (ok.Kind, ok.OwnerId, ok.Body, ok.Link));
        Assert.Equal((NotificationKind.TaskFailed, "boom"), (failed.Kind, failed.Body));
        Assert.Contains("box", failed.Title);
    }

    [Fact]
    public void LongText_IsCutToOneShortLine()
    {
        var s = new ManagedSession { SessionId = "s", OwnerId = "bob", Task = new string('x', 500) + "\n\nmore", ConfigPath = "c", Status = SessionStatus.Succeeded };

        var body = NotificationService.For(s)!.Body;

        Assert.Equal(161, body.Length);
        Assert.EndsWith("…", body);
    }
}
