namespace fuseraft.Web.Services.Webhooks;

/// <summary><c>Fuseraft:Webhooks</c>: who may add webhooks, and where they may point.</summary>
public sealed class WebhookOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Only admins may add webhooks; others' existing ones stop.</summary>
    public bool AdminsOnly { get; set; }

    /// <summary>
    /// Hosts a webhook may reach even though they're on a private network or this machine, and over plain
    /// <c>http</c>. Every other host must be public and use <c>https</c>.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 10;

    public bool MayAdd(Users.WebUser user) => Enabled && (!AdminsOnly || user.IsAdmin);

    public bool IsAllowedHost(string host) =>
        AllowedHosts.Any(h => string.Equals(h.Trim().TrimEnd('.'), host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
}
