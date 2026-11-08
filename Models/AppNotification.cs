namespace fuseraft.Web.Models;

public enum NotificationKind { Approval, Question, SessionFinished, SessionFailed, ChatReplied, ChatFailed, TaskFinished, TaskFailed }

/// <summary>Something that happened in a user's sessions, chats or daemon tasks, for the browser to tell them about.</summary>
/// <param name="OwnerId">The account it belongs to; null means only admins see it.</param>
/// <param name="Link">The page that shows it.</param>
public sealed record AppNotification(NotificationKind Kind, string? OwnerId, string Title, string Body, string Link)
{
    public Guid           Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>False for an approval admins may answer too; everything else tells only its owner.</summary>
    public bool OwnerOnly { get; init; } = true;

    public bool IsFor(Services.Users.WebUser user) =>
        OwnerId is null ? user.IsAdmin : OwnerOnly ? OwnerId == user.Id : user.CanAccess(OwnerId);

    /// <summary>The group the browser lets people turn off: <c>attention</c>, <c>results</c> or <c>replies</c>.</summary>
    public string Category => Kind switch
    {
        NotificationKind.Approval or NotificationKind.Question => "attention",
        NotificationKind.ChatReplied                           => "replies",
        _                                                      => "results",
    };

    public string Level => Kind switch
    {
        NotificationKind.Approval or NotificationKind.Question                                => "warn",
        NotificationKind.SessionFailed or NotificationKind.ChatFailed or NotificationKind.TaskFailed => "err",
        NotificationKind.SessionFinished or NotificationKind.TaskFinished                     => "ok",
        _                                                                                     => "info",
    };
}
