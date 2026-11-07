using fuseraft.Web.Models;
using fuseraft.Web.Services.Daemons;

namespace fuseraft.Web.Services;

/// <summary>
/// Turns what happens in sessions, chats and daemon tasks into <see cref="AppNotification"/>s. Every signed-in
/// browser tab listens, and shows those meant for its user (<see cref="AppNotification.IsFor"/>).
/// </summary>
public sealed class NotificationService : IDisposable
{
    private const int MaxBody = 160;

    private readonly HitlBroker         _hitl;
    private readonly SessionHostService _sessions;
    private readonly ReplService        _repl;
    private readonly DaemonService      _daemons;

    public event Action<AppNotification>? Published;

    public NotificationService(HitlBroker hitl, SessionHostService sessions, ReplService repl, DaemonService daemons)
    {
        _hitl     = hitl;
        _sessions = sessions;
        _repl     = repl;
        _daemons  = daemons;
        _hitl.Added           += OnApproval;
        _sessions.SessionEnded += OnSessionEnded;
        _repl.Alert           += OnReplAlert;
        _daemons.TaskFinished += OnTaskFinished;
    }

    private void OnApproval(PendingApproval r) => Publish(For(r) with { StillWaiting = () => _hitl.IsPending(r.Id) });
    private void OnSessionEnded(ManagedSession s) => Publish(For(s));
    private void OnReplAlert(ActiveReplSession s, ReplAlert a)
    {
        var question = s.Question;
        Publish(a == ReplAlert.Question ? For(s, a) with { StillWaiting = () => ReferenceEquals(s.Question, question) } : For(s, a));
    }
    private void OnTaskFinished(DaemonConnection c, DaemonTask t) => Publish(For(c.Endpoint.Name, t));

    private void Publish(AppNotification? n)
    {
        if (n is not null) Published?.Invoke(n);
    }

    internal static AppNotification For(PendingApproval r) => new(
        NotificationKind.Approval, r.OwnerId,
        r.Source switch
        {
            ApprovalSource.Repl   => "A chat needs your approval",
            ApprovalSource.Daemon => $"Daemon {r.Origin} needs your approval",
            _                     => "A session needs your approval",
        },
        Plain(r.Summary),
        r.Source switch
        {
            ApprovalSource.Repl    => "/chat",
            ApprovalSource.Session => $"/sessions/{Uri.EscapeDataString(r.SessionId)}",
            _                      => "/hitl",
        }) { OwnerOnly = r.Source == ApprovalSource.Repl };

    /// <summary>Null for a session that ended because its user cancelled it.</summary>
    internal static AppNotification? For(ManagedSession s) => s.Status switch
    {
        SessionStatus.Succeeded => new(NotificationKind.SessionFinished, s.OwnerId, "Session finished", Trim(s.Task),
            $"/sessions/{Uri.EscapeDataString(s.SessionId)}"),
        SessionStatus.Failed => new(NotificationKind.SessionFailed, s.OwnerId, "Session failed", Trim(s.ErrorMessage ?? s.Task),
            $"/sessions/{Uri.EscapeDataString(s.SessionId)}"),
        _ => null,
    };

    internal static AppNotification For(ActiveReplSession s, ReplAlert alert) => alert switch
    {
        ReplAlert.Question => new(NotificationKind.Question, s.OwnerId, "A chat has a question", Trim(s.Question?.Question), "/chat"),
        ReplAlert.Failed   => new(NotificationKind.ChatFailed, s.OwnerId, "A chat stopped with an error", Trim(s.Error), "/chat"),
        _ => new(NotificationKind.ChatReplied, s.OwnerId, "A chat replied",
            Trim(Plain(s.Messages.LastOrDefault(m => m.Role == "assistant")?.Text)), "/chat"),
    };

    internal static AppNotification For(string daemon, DaemonTask t) => t.Status == DaemonTaskStatus.Succeeded
        ? new(NotificationKind.TaskFinished, t.OwnerId, $"Task on {daemon} finished", Trim(t.Task), "/daemons")
        : new(NotificationKind.TaskFailed, t.OwnerId, $"Task on {daemon} failed", Trim(t.ErrorMessage ?? t.Task), "/daemons");

    /// <summary>Drops the Markdown that would show up as stray characters in a plain-text notification.</summary>
    private static string Plain(string? markdown) =>
        (markdown ?? "").Replace("`", "").Replace("**", "").Replace("__", "");

    private static string Trim(string? text)
    {
        var flat = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= MaxBody ? flat : flat[..MaxBody] + "…";
    }

    public void Dispose()
    {
        _hitl.Added            -= OnApproval;
        _sessions.SessionEnded -= OnSessionEnded;
        _repl.Alert            -= OnReplAlert;
        _daemons.TaskFinished  -= OnTaskFinished;
    }
}
