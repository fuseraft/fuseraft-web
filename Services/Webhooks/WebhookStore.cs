using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace fuseraft.Web.Services.Webhooks;

/// <summary>
/// <c>&lt;data&gt;/webhooks.json</c> (mode 600). URLs and signing secrets are encrypted with the server's
/// data-protection keys.
/// </summary>
public sealed class WebhookStore(string path, IDataProtectionProvider protection, ILogger<WebhookStore>? logger = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    };
    public const int MaxPerUser = 10;

    private readonly IDataProtector _protector = protection.CreateProtector("fuseraft-web.webhooks.secret");
    private readonly IDataProtector _urls      = protection.CreateProtector("fuseraft-web.webhooks.url");
    private readonly Lock _lock = new();
    private List<Saved>? _hooks;

    /// <summary>Copies, so a caller can't change one without <see cref="Update"/>.</summary>
    public IReadOnlyList<Webhook> All()
    {
        lock (_lock) return [.. Hooks.Select(s => s.Hook.Copy())];
    }

    public IReadOnlyList<Webhook> For(string ownerId) => [.. All().Where(h => h.OwnerId == ownerId)];

    public Webhook? Find(string id) => All().FirstOrDefault(h => h.Id == id);

    public Webhook Add(Webhook hook)
    {
        lock (_lock)
        {
            if (Hooks.Count(s => s.Hook.OwnerId == hook.OwnerId) >= MaxPerUser)
                throw new InvalidOperationException($"You can have at most {MaxPerUser} webhooks.");
            Hooks.Add(new Saved { Hook = hook.Copy(), Secret = _protector.Protect(NewSecret()) });
            Save();
            return hook.Copy();
        }
    }

    public void Update(string id, Action<Webhook> change)
    {
        lock (_lock)
        {
            var saved = Hooks.FirstOrDefault(s => s.Hook.Id == id) ?? throw new KeyNotFoundException("No such webhook.");
            change(saved.Hook);
            Save();
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
            if (Hooks.RemoveAll(s => s.Hook.Id == id) > 0) Save();
    }

    /// <summary>The <c>whsec_</c> secret its requests are signed with; null if the server's keys changed since.</summary>
    public string? Secret(string id)
    {
        string? protectedSecret;
        lock (_lock) protectedSecret = Hooks.FirstOrDefault(s => s.Hook.Id == id)?.Secret;
        if (protectedSecret is null) return null;
        try { return _protector.Unprotect(protectedSecret); }
        catch (CryptographicException)
        {
            logger?.LogWarning("Webhook {Id}'s secret can't be decrypted (the server's keys changed); make a new one", id);
            return null;
        }
    }

    public string RegenerateSecret(string id)
    {
        var secret = NewSecret();
        lock (_lock)
        {
            var saved = Hooks.FirstOrDefault(s => s.Hook.Id == id) ?? throw new KeyNotFoundException("No such webhook.");
            saved.Secret = _protector.Protect(secret);
            Save();
        }
        return secret;
    }

    private static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private List<Saved> Hooks => _hooks ??= Load();

    private List<Saved> Load()
    {
        if (!File.Exists(path)) return [];
        try
        {
            var saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(path), Json) ?? [];
            foreach (var s in saved)
            {
                try { s.Hook.Url = _urls.Unprotect(s.Url); }
                catch (CryptographicException)
                {
                    logger?.LogWarning("Webhook {Name}'s URL can't be decrypted (the server's keys changed); it's off until it's added again", s.Hook.Name);
                    s.Hook.Enabled = false;
                }
            }
            return saved;
        }
        catch (JsonException ex)
        {
            logger?.LogError(ex, "{Path} isn't valid JSON; starting with no webhooks", path);
            return [];
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var s in Hooks) s.Url = _urls.Protect(s.Hook.Url);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Hooks, Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    private sealed class Saved
    {
        public required Webhook Hook { get; init; }
        /// <summary>Encrypted.</summary>
        public required string Secret { get; set; }
        /// <summary>Encrypted.</summary>
        public string Url { get; set; } = "";
    }
}
