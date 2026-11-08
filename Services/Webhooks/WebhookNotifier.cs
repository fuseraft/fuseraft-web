using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using fuseraft.Web.Models;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services.Webhooks;

/// <summary>
/// Sends each notification to the webhooks of the people it's for, straight away, retrying a few times when the
/// receiver is down. Keeps each webhook's last few deliveries for the Webhooks page.
/// </summary>
public sealed class WebhookNotifier : IHostedService, IDisposable
{
    private const int LogSize = 20;

    private readonly NotificationService _notifications;
    private readonly UserStore _users;
    private readonly WebhookStore _store;
    private readonly WebhookClient _client;
    private readonly WebhookOptions _options;
    private readonly string? _publicUrl;
    private readonly ILogger<WebhookNotifier> _logger;
    private readonly ConcurrentDictionary<string, List<WebhookDelivery>> _log = new();
    private readonly CancellationTokenSource _stopping = new();

    internal TimeSpan[] RetryDelays { get; init; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    /// <summary>A delivery to the webhook with this id was attempted.</summary>
    public event Action<string>? Delivered;

    public WebhookNotifier(NotificationService notifications, UserStore users, WebhookStore store, WebhookClient client,
        IOptions<WebhookOptions> options, IOptions<WebOptions> web, ILogger<WebhookNotifier> logger)
    {
        _notifications = notifications;
        _users         = users;
        _store         = store;
        _client        = client;
        _options       = options.Value;
        _publicUrl     = string.IsNullOrWhiteSpace(web.Value.PublicUrl) ? null : web.Value.PublicUrl.TrimEnd('/');
        _logger        = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled) _notifications.Published += Handle;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _notifications.Published -= Handle;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _notifications.Published -= Handle;
        _stopping.Cancel();
        _stopping.Dispose();
    }

    internal void Handle(AppNotification n)
    {
        foreach (var hook in _store.All())
            if (_users.Find(hook.OwnerId) is { } owner && Wants(hook, owner, n))
                _ = DeliverAsync(hook, n);
    }

    internal bool Wants(Webhook hook, WebUser owner, AppNotification n) =>
        hook.Enabled && !owner.Disabled && _options.MayAdd(owner) && n.IsFor(owner) && hook.Wants(n.Category);

    /// <summary>The webhook's recent deliveries, newest first.</summary>
    public IReadOnlyList<WebhookDelivery> Deliveries(string id)
    {
        if (!_log.TryGetValue(id, out var log)) return [];
        lock (log) return [.. Enumerable.Reverse(log)];
    }

    /// <summary>Sends a test notification once, without retrying.</summary>
    public async Task<WebhookDelivery> SendTestAsync(Webhook hook, WebUser owner)
    {
        var n = new AppNotification(NotificationKind.Test, owner.Id, "Test from fuseraft",
            $"{owner.Label}'s webhook {hook.Name} works.", "/webhooks");
        var delivery = await SendOnceAsync(hook, n);
        Record(hook.Id, delivery);
        return delivery;
    }

    private async Task DeliverAsync(Webhook hook, AppNotification n)
    {
        for (var attempt = 0; ; attempt++)
        {
            // Changed or removed since: send what the owner wants now.
            if (_store.Find(hook.Id) is not { Enabled: true } current) return;
            hook = current;

            WebhookDelivery delivery;
            bool retry;
            try { (delivery, retry) = await SendCoreAsync(hook, n); }
            catch (OperationCanceledException) { return; }
            Record(hook.Id, delivery);
            if (!retry || attempt >= RetryDelays.Length)
            {
                if (!delivery.Succeeded)
                    _logger.LogWarning("Webhook {Name} of {Owner} didn't take {Event}: {Error}", hook.Name, hook.OwnerId, n.Title, delivery.Error);
                return;
            }
            try { await Task.Delay(RetryDelays[attempt], _stopping.Token); }
            catch (OperationCanceledException) { return; }
            // An approval answered meanwhile is no longer news.
            if (n.StillWaiting?.Invoke() == false) return;
        }
    }

    private async Task<WebhookDelivery> SendOnceAsync(Webhook hook, AppNotification n)
    {
        try { return (await SendCoreAsync(hook, n)).Delivery; }
        catch (OperationCanceledException) { return new(DateTimeOffset.UtcNow, n.Title, null, "The server is stopping"); }
    }

    private async Task<(WebhookDelivery Delivery, bool Retry)> SendCoreAsync(Webhook hook, AppNotification n)
    {
        if (WebhookAddress.Problem(hook.Url, _options) is { } problem) return (new(DateTimeOffset.UtcNow, n.Title, null, problem), false);
        if (_store.Secret(hook.Id) is not { } secret)
            return (new(DateTimeOffset.UtcNow, n.Title, null, "It can't be read since the server's keys changed. Delete it and add it again."), false);
        return await _client.SendAsync(hook, secret, n, _publicUrl, _stopping.Token);
    }

    private void Record(string id, WebhookDelivery delivery)
    {
        var log = _log.GetOrAdd(id, _ => []);
        lock (log)
        {
            log.Add(delivery);
            if (log.Count > LogSize) log.RemoveAt(0);
        }
        Delivered?.Invoke(id);
    }
}
