namespace fuseraft.Web.Services.Users;

/// <summary>An account that can sign in, with its own fuseraft home and the directories it may work in.</summary>
public sealed class WebUser
{
    public required string Id       { get; init; }
    public required string UserName { get; set; }
    public string?         DisplayName { get; set; }
    public bool            IsAdmin  { get; set; }
    public bool            Disabled { get; set; }

    /// <summary>PBKDF2 hash from <see cref="PasswordHashing"/>; null for accounts that only sign in through OIDC.</summary>
    public string?         PasswordHash { get; set; }

    public List<ExternalLogin> Logins { get; init; } = [];

    /// <summary>The <c>FUSERAFT_HOME</c> every fuseraft process this user starts runs against.</summary>
    public required string Home { get; set; }

    /// <summary>Where a non-admin may run sessions and chats, browse, and pick configs. Admins aren't limited.</summary>
    public List<string> WorkspaceRoots { get; set; } = [];

    /// <summary>Changes whenever a sign-in should stop working (password reset, disabled, removed admin).</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Where notification emails go: confirmed by a link, taken from a sign-on provider, or set by an admin.</summary>
    public string? Email { get; set; }
    public EmailChoices EmailChoices { get; set; } = new();

    /// <summary>Which runs of each scheduled job, by name, to notify about; a job not listed notifies of failures.</summary>
    public Dictionary<string, ScheduleAlert> ScheduleAlerts { get; set; } = [];

    public ScheduleAlert AlertFor(string job) => ScheduleAlerts.GetValueOrDefault(job);

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;

    /// <summary>Whether this user may see and act on something <paramref name="ownerId"/> owns; null owners are admin-only.</summary>
    public bool CanAccess(string? ownerId) => IsAdmin || (ownerId is not null && ownerId == Id);
}

/// <summary>Which notifications a user wants by email; chat replies never are.</summary>
public sealed class EmailChoices
{
    /// <summary>An approval or question that has waited <c>Fuseraft:Email:WaitMinutes</c> unanswered.</summary>
    public bool Attention { get; set; } = true;
    /// <summary>A session, daemon task or scheduled job that finished or failed.</summary>
    public bool Results { get; set; } = true;
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ScheduleAlert>))]
public enum ScheduleAlert { Failures, Successes, Both, Off }

public static class ScheduleAlertExtensions
{
    public static bool Wants(this ScheduleAlert alert, bool succeeded) =>
        alert == ScheduleAlert.Both || alert == (succeeded ? ScheduleAlert.Successes : ScheduleAlert.Failures);
}

/// <summary>An OIDC identity linked to a <see cref="WebUser"/>, matched on issuer and subject.</summary>
public sealed record ExternalLogin(string Issuer, string Subject, string? Email);
