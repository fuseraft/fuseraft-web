using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using fuseraft.Web.Models;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Daemons;
using fuseraft.Web.Services.Users;
using fuseraft.Web.Services.Webhooks;

namespace FuseraftWeb.Tests;

public sealed class WebhookNotifierTests : IDisposable
{
    private readonly HitlBroker _broker   = new();
    private readonly Accounts   _accounts = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-web-webhooks").FullName;
    private readonly WebhookStore _store;
    private readonly FakeHandler _handler;
    private readonly NotificationService _notifications;
    private readonly WebUser _bob;

    public WebhookNotifierTests()
    {
        var cli      = new FakeCli();
        var profiles = new ModelProfileRegistry { UseKeychain = false };
        _notifications = new NotificationService(_broker,
            new SessionHostService(cli, _broker, _accounts.Store, profiles, NullLogger<SessionHostService>.Instance),
            new ReplService(profiles, cli, _broker),
            new DaemonService([], _broker, () => throw new InvalidOperationException("no transport in these tests")),
            new ScheduleTicker(cli, _accounts.Store, profiles, Options.Create(new WebOptions()), NullLogger<ScheduleTicker>.Instance));
        _store   = new WebhookStore(Path.Combine(_dir, "webhooks.json"), new EphemeralDataProtectionProvider());
        _handler = new FakeHandler(HttpStatusCode.OK);
        _bob     = _accounts.Add("bob");
    }

    public void Dispose()
    {
        _notifications.Dispose();
        _accounts.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private WebhookNotifier Start(WebhookOptions? options = null, FakeHandler? handler = null)
    {
        options ??= new WebhookOptions();
        var notifier = new WebhookNotifier(_notifications, _accounts.Store, _store, new WebhookClient(handler ?? _handler, options),
            Options.Create(options), Options.Create(new WebOptions { PublicUrl = "https://fuseraft.example.com/" }),
            NullLogger<WebhookNotifier>.Instance)
        {
            RetryDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)],
        };
        notifier.StartAsync(default);
        return notifier;
    }

    private Webhook Hook(WebUser owner, Action<Webhook>? change = null)
    {
        var hook = new Webhook { OwnerId = owner.Id, Name = owner.UserName, Url = $"https://hooks.example.com/{owner.UserName}" };
        change?.Invoke(hook);
        return _store.Add(hook);
    }

    private void Approval(string? ownerId, ApprovalSource source = ApprovalSource.Repl) =>
        _broker.Enqueue(new PendingApproval { SessionId = "s", Source = source, OwnerId = ownerId, Kind = "shell_command", Command = "make" });

    private List<string> PostedTo()
    {
        lock (_handler.Requests) return [.. _handler.Requests.Select(r => r.Request.RequestUri!.AbsolutePath)];
    }

    [Fact]
    public async Task Approval_GoesStraightToItsOwnersWebhook_Only()
    {
        using var _ = Start();
        Hook(_bob);
        Hook(_accounts.Alice);

        Approval(_bob.Id);
        await Wait.UntilAsync(() => PostedTo().Count > 0, "a delivery");
        await Task.Delay(50);

        Assert.Equal(["/bob"], PostedTo());
    }

    [Fact]
    public async Task EachWebhook_GetsOnlyTheGroupsItChose()
    {
        using var _ = Start();
        var quiet = Hook(_bob, h => { h.Name = "quiet"; h.Url = "https://hooks.example.com/quiet"; h.Attention = false; });
        Hook(_bob, h => { h.Name = "off"; h.Url = "https://hooks.example.com/off"; h.Enabled = false; });
        Hook(_bob);

        Approval(_bob.Id);
        await Wait.UntilAsync(() => PostedTo().Count > 0, "a delivery");
        await Task.Delay(50);

        Assert.Equal(["/bob"], PostedTo());
        Assert.False(quiet.Replies);
    }

    [Fact]
    public async Task AdminsOnly_StopsOthersWebhooks()
    {
        using var _ = Start(new WebhookOptions { AdminsOnly = true });
        Hook(_bob);
        Hook(_accounts.Alice);

        Approval(_bob.Id, ApprovalSource.Session);
        await Wait.UntilAsync(() => PostedTo().Count > 0, "a delivery");
        await Task.Delay(50);

        Assert.Equal(["/alice"], PostedTo());
    }

    [Fact]
    public async Task AReceiverThatsDown_IsTriedAgain_AndTheAttemptsAreLogged()
    {
        var flaky = new FakeHandler(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using var notifier = Start(handler: flaky);
        var hook = Hook(_bob);

        Approval(_bob.Id);
        await Wait.UntilAsync(() => notifier.Deliveries(hook.Id).Count == 2, "a retry");

        Assert.Equal([200, 502], notifier.Deliveries(hook.Id).Select(d => d.Status));
        Assert.True(notifier.Deliveries(hook.Id)[0].Succeeded);
    }

    [Fact]
    public async Task AnApprovalAnsweredMeanwhile_IsntRetried()
    {
        var down = new FakeHandler(HttpStatusCode.ServiceUnavailable);
        using var notifier = new WebhookNotifier(_notifications, _accounts.Store, _store, new WebhookClient(down, new WebhookOptions()),
            Options.Create(new WebhookOptions()), Options.Create(new WebOptions()), NullLogger<WebhookNotifier>.Instance)
        {
            RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)],
        };
        await notifier.StartAsync(default);
        var hook = Hook(_bob);

        var approval = new PendingApproval { SessionId = "s", Source = ApprovalSource.Repl, OwnerId = _bob.Id, Kind = "shell_command" };
        _broker.Enqueue(approval);
        await Wait.UntilAsync(() => notifier.Deliveries(hook.Id).Count == 1, "the first attempt");
        _broker.Respond(approval.Id, true);
        await Task.Delay(600);

        Assert.Single(notifier.Deliveries(hook.Id));
    }

    [Fact]
    public async Task SendTest_ReportsWhatTheReceiverSaid()
    {
        using var notifier = Start(handler: new FakeHandler(HttpStatusCode.Unauthorized));
        var hook = Hook(_bob);

        var delivery = await notifier.SendTestAsync(hook, _bob);

        Assert.Equal((401, false), (delivery.Status, delivery.Succeeded));
        Assert.Same(delivery, Assert.Single(notifier.Deliveries(hook.Id)));
    }
}
