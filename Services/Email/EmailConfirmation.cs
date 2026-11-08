using System.Collections.Concurrent;
using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services.Email;

/// <summary>
/// Signed links that prove someone can read an address before it gets their notifications, so nobody can point
/// the server's mail at a stranger. A link names its account and the address it replaces, so it works once.
/// </summary>
public sealed class EmailConfirmation(IDataProtectionProvider protection)
{
    private readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector("fuseraft-web.email-confirmation").ToTimeLimitedDataProtector();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(24);
    /// <summary>How soon an account may ask for another link.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The address as it should be stored, or null if it isn't one.</summary>
    public static string? Normalize(string? address)
    {
        var trimmed = address?.Trim();
        return trimmed is { Length: > 0 and <= 254 } && !trimmed.Any(char.IsWhiteSpace) &&
               MailAddress.TryCreate(trimmed, out var parsed) && parsed.Address == trimmed && parsed.DisplayName.Length == 0
            ? trimmed
            : null;
    }

    public string Token(WebUser user, string address) =>
        _protector.Protect(string.Join('\n', user.Id, address, user.Email ?? ""), Lifetime);

    /// <summary>The address a link confirms for <paramref name="user"/>, or null if it's someone else's, used or expired.</summary>
    public string? Check(WebUser user, string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        string payload;
        try { payload = _protector.Unprotect(token); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        var parts = payload.Split('\n');
        return parts.Length == 3 && parts[0] == user.Id && parts[2] == (user.Email ?? "") ? Normalize(parts[1]) : null;
    }

    /// <summary>False while the account's last link is under <see cref="Cooldown"/> old.</summary>
    public bool TryReserve(WebUser user)
    {
        var now = DateTimeOffset.UtcNow;
        while (true)
        {
            if (!_lastSent.TryGetValue(user.Id, out var last))
            {
                if (_lastSent.TryAdd(user.Id, now)) return true;
                continue;
            }
            if (now - last < Cooldown) return false;
            if (_lastSent.TryUpdate(user.Id, now, last)) return true;
        }
    }
}
