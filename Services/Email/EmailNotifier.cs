using System.Text;
using Microsoft.Extensions.Options;
using fuseraft.Web.Models;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services.Email;

/// <summary>
/// Emails notifications to the people who'd otherwise miss them with every tab closed. An approval or question is
/// sent only if it's still unanswered after <see cref="EmailOptions.WaitMinutes"/>; chat replies never are. What
/// arrives close together goes out as one email per person.
/// </summary>
public sealed class EmailNotifier : IHostedService
{
    private readonly NotificationService _notifications;
    private readonly UserStore _users;
    private readonly IEmailSender _sender;
    private readonly EmailOptions _email;
    private readonly string? _publicUrl;
    private readonly ILogger<EmailNotifier> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<AppNotification>> _batches = [];
    private readonly CancellationTokenSource _stopping = new();

    internal TimeSpan Wait  { get; init; }
    internal TimeSpan Batch { get; init; }
    internal TimeSpan[] RetryDelays { get; init; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    public EmailNotifier(NotificationService notifications, UserStore users, IEmailSender sender,
        IOptions<EmailOptions> email, IOptions<WebOptions> web, ILogger<EmailNotifier> logger)
    {
        _notifications = notifications;
        _users         = users;
        _sender        = sender;
        _email         = email.Value;
        _publicUrl     = string.IsNullOrWhiteSpace(web.Value.PublicUrl) ? null : web.Value.PublicUrl.TrimEnd('/');
        _logger        = logger;
        Wait  = TimeSpan.FromMinutes(Math.Max(0, _email.WaitMinutes));
        Batch = TimeSpan.FromSeconds(Math.Max(0, _email.BatchSeconds));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_email.Enabled) return Task.CompletedTask;
        if (_publicUrl is null)
            _logger.LogWarning("Fuseraft:PublicUrl isn't set, so notification emails can't link back to this server.");
        _notifications.Published += Handle;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _notifications.Published -= Handle;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    internal void Handle(AppNotification n)
    {
        switch (n.Category)
        {
            case "replies":   return;
            case "attention": _ = QueueIfStillWaitingAsync(n); return;
            default:          Queue(n); return;
        }
    }

    private async Task QueueIfStillWaitingAsync(AppNotification n)
    {
        try { await Task.Delay(Wait, _stopping.Token); }
        catch (OperationCanceledException) { return; }
        if (n.StillWaiting?.Invoke() != false) Queue(n);
    }

    private void Queue(AppNotification n)
    {
        foreach (var user in _users.All.Where(u => Wants(u, n)))
        {
            bool first;
            lock (_lock)
            {
                first = !_batches.TryGetValue(user.Id, out var batch);
                if (first) _batches[user.Id] = batch = [];
                batch!.Add(n);
            }
            if (first) _ = SendBatchAsync(user.Id);
        }
    }

    internal static bool Wants(WebUser u, AppNotification n) =>
        !u.Disabled && !string.IsNullOrEmpty(u.Email) && n.IsFor(u) &&
        (n.Category == "attention" ? u.EmailChoices.Attention : u.EmailChoices.Results);

    private async Task SendBatchAsync(string userId)
    {
        try { await Task.Delay(Batch, _stopping.Token); }
        catch (OperationCanceledException) { return; }

        List<AppNotification> batch;
        lock (_lock)
        {
            batch = _batches[userId];
            _batches.Remove(userId);
        }
        // Answered while it sat in the batch: that one's no longer news.
        batch.RemoveAll(n => n.StillWaiting?.Invoke() == false);
        if (batch.Count == 0 || _users.Find(userId) is not { Email: { Length: > 0 } to } user || user.Disabled) return;

        var (subject, body) = Compose(batch, user, _publicUrl);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _sender.SendAsync(to, subject, body, _stopping.Token);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (attempt < RetryDelays.Length)
            {
                _logger.LogWarning(ex, "Couldn't email {User}; trying again in {Delay}", user.UserName, RetryDelays[attempt]);
                try { await Task.Delay(RetryDelays[attempt], _stopping.Token); }
                catch (OperationCanceledException) { return; }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gave up emailing {User} {Count} notification(s)", user.UserName, batch.Count);
                return;
            }
        }
    }

    internal static (string Subject, string Body) Compose(IReadOnlyList<AppNotification> batch, WebUser user, string? publicUrl)
    {
        var subject = batch.Count == 1 ? batch[0].Title : $"{batch.Count} updates from fuseraft";
        var body = new StringBuilder();
        foreach (var n in batch)
        {
            body.AppendLine(n.Title);
            if (n.Body.Length > 0) body.AppendLine(n.Body);
            if (publicUrl is not null) body.AppendLine(publicUrl + n.Link);
            body.AppendLine();
        }
        body.AppendLine("--");
        body.Append($"Sent to {user.UserName} because email notifications are on. ");
        body.AppendLine(publicUrl is null ? "Change them on your Account page." : $"Change them at {publicUrl}/account");
        return (subject, body.ToString());
    }
}
