using System.Diagnostics.CodeAnalysis;

namespace fuseraft.Web.Services.Email;

public enum EmailSecurity { StartTls, Tls, None }

/// <summary>The SMTP server notification emails go through, from <c>Fuseraft:Email</c>. Without a host and sender, there's no email.</summary>
public sealed class EmailOptions
{
    public string? Host { get; set; }
    public int     Port { get; set; } = 587;
    /// <summary>StartTls upgrades a plain connection (port 587), Tls connects encrypted (465), None never encrypts.</summary>
    public EmailSecurity Security { get; set; } = EmailSecurity.StartTls;
    /// <summary>The sender, such as <c>fuseraft &lt;fuseraft@example.com&gt;</c>.</summary>
    public string? From { get; set; }
    /// <summary>Set to sign in to the server; the password comes from <see cref="PasswordVariable"/>.</summary>
    public string? UserName { get; set; }
    /// <summary>How long an approval or question waits unanswered before it's emailed.</summary>
    public int WaitMinutes { get; set; } = 5;
    /// <summary>How long to collect a user's notifications into one email.</summary>
    public int BatchSeconds { get; set; } = 60;

    public const string PasswordVariable = "FUSERAFT_WEB_SMTP_PASSWORD";

    [MemberNotNullWhen(true, nameof(Host), nameof(From))]
    public bool Enabled => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}
