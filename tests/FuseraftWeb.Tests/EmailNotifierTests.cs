using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using fuseraft.Web.Models;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Daemons;
using fuseraft.Web.Services.Email;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class EmailNotifierTests : IAsyncDisposable
{
    private readonly HitlBroker _broker   = new();
    private readonly Accounts   _accounts = new();
    private readonly FakeSender _sender   = new();
    private readonly NotificationService _notifications;
    private readonly EmailNotifier _notifier;
    private readonly WebUser _bob;

    public EmailNotifierTests()
    {
        var cli      = new FakeCli();
        var profiles = new ModelProfileRegistry { UseKeychain = false };
        _notifications = new NotificationService(_broker,
            new SessionHostService(cli, _broker, _accounts.Store, profiles, NullLogger<SessionHostService>.Instance),
            new ReplService(profiles, cli, _broker),
            new DaemonService([], _broker, () => throw new InvalidOperationException("no transport in these tests")));
        _notifier = new EmailNotifier(_notifications, _accounts.Store, _sender,
            Options.Create(new EmailOptions { Host = "smtp.example.com", From = "fuseraft@example.com" }),
            Options.Create(new WebOptions { PublicUrl = "https://fuseraft.example.com/" }),
            NullLogger<EmailNotifier>.Instance)
        {
            Wait        = TimeSpan.FromMilliseconds(150),
            Batch       = TimeSpan.FromMilliseconds(100),
            RetryDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)],
        };
        _notifier.StartAsync(default);
        _bob = WithEmail("bob", "bob@example.com");
    }

    public async ValueTask DisposeAsync()
    {
        await _notifier.StopAsync(default);
        _notifications.Dispose();
        _accounts.Dispose();
    }

    private WebUser WithEmail(string name, string email)
    {
        var user = _accounts.Add(name);
        _accounts.Store.Update(user.Id, u => u.Email = email);
        return _accounts.Store.Find(user.Id)!;
    }

    private static AppNotification Finished(string ownerId, string task) =>
        NotificationService.For(new ManagedSession
        {
            SessionId = task, OwnerId = ownerId, Task = task, ConfigPath = "c", Status = SessionStatus.Succeeded,
        })!;

    [Fact]
    public async Task Results_CloseTogether_ArriveAsOneEmail_WithLinks()
    {
        _notifier.Handle(Finished(_bob.Id, "first"));
        _notifier.Handle(Finished(_bob.Id, "second"));

        var mail = await _sender.SingleAsync();

        Assert.Equal(("bob@example.com", "2 updates from fuseraft"), (mail.To, mail.Subject));
        Assert.Contains("first", mail.Body);
        Assert.Contains("https://fuseraft.example.com/sessions/second", mail.Body);
        Assert.Contains("https://fuseraft.example.com/account", mail.Body);
    }

    [Fact]
    public async Task Approval_AnsweredWithinTheWait_IsNotEmailed()
    {
        var answered = _broker.Enqueue(new PendingApproval { SessionId = "s", Source = ApprovalSource.Session, OwnerId = _bob.Id, Kind = "shell_command", Command = "ls" });
        _broker.Respond(answered.Id, true);
        _broker.Enqueue(new PendingApproval { SessionId = "t", Source = ApprovalSource.Session, OwnerId = _bob.Id, Kind = "shell_command", Command = "make" });

        var mail = await _sender.SingleAsync();

        Assert.Equal("A session needs your approval", mail.Subject);
        Assert.Contains("make", mail.Body);
    }

    [Fact]
    public async Task OnlyThoseWhoWantIt_AreEmailed()
    {
        var carol = WithEmail("carol", "carol@example.com");
        _accounts.Store.Update(carol.Id, u => u.EmailChoices.Results = false);
        _accounts.Add("dave"); // no address
        var chat = new ActiveReplSession { OwnerId = _bob.Id };
        chat.Messages.Add(new ReplMessage("assistant", "hi", [], DateTimeOffset.UtcNow));

        _notifier.Handle(NotificationService.For(chat, ReplAlert.Replied));
        _notifier.Handle(Finished(carol.Id, "carol's"));
        _notifier.Handle(Finished(_bob.Id, "bob's"));

        var mail = await _sender.SingleAsync();
        // Alice is an admin with no address; bob's results are bob's alone either way.
        Assert.Equal(("bob@example.com", "Session finished"), (mail.To, mail.Subject));
    }

    [Fact]
    public async Task AFailedSend_IsRetried()
    {
        _sender.FailuresLeft = 2;

        _notifier.Handle(Finished(_bob.Id, "flaky"));

        await _sender.SingleAsync();
        Assert.Equal(3, _sender.Attempts);
    }

    [Fact]
    public void Compose_WithoutAPublicUrl_HasNoLinks()
    {
        var (subject, body) = EmailNotifier.Compose([Finished(_bob.Id, "t")], _bob, publicUrl: null);

        Assert.Equal("Session finished", subject);
        Assert.DoesNotContain("/sessions/", body);
        Assert.Contains("Account page", body);
    }

    private sealed class FakeSender : IEmailSender
    {
        private readonly List<(string To, string Subject, string Body)> _sent = [];
        public int FailuresLeft { get; set; }
        public int Attempts { get; private set; }

        public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
        {
            lock (_sent)
            {
                Attempts++;
                if (FailuresLeft-- > 0) throw new IOException("SMTP 451");
                _sent.Add((to, subject, body));
            }
            return Task.CompletedTask;
        }

        /// <summary>The one email sent, after waiting long enough that a second would have arrived too.</summary>
        public async Task<(string To, string Subject, string Body)> SingleAsync()
        {
            await Wait.UntilAsync(() => { lock (_sent) return _sent.Count > 0; }, "an email");
            await Task.Delay(500);
            lock (_sent) return Assert.Single(_sent);
        }
    }
}
