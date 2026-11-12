using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace fuseraft.Web.Services.Mcp;

/// <summary>An MCP server every account's chats and runs can use, reached over Streamable HTTP.</summary>
public sealed record McpServer
{
    public string Id      { get; init; } = "";
    public string Name    { get; init; } = "";
    public string Url     { get; init; } = "";
    public bool   HasToken { get; init; }
    public bool   Enabled { get; init; } = true;
    public string? AddedBy { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}

/// <summary>
/// <c>&lt;data&gt;/mcp-servers.json</c>: the MCP servers admins add on the MCP servers page, handed to every fuseraft
/// this server starts (<see cref="Environment"/>). Tokens are encrypted with the server's data-protection keys.
/// </summary>
public sealed partial class McpServerStore(string path, IDataProtectionProvider protection, ILogger<McpServerStore>? logger = null)
{
    /// <summary>The first fuseraft that reads <c>FUSERAFT_MCP_SERVERS</c>; older ones ignore it.</summary>
    public static readonly Version MinimumCliVersion = new(1, 24, 0);

    public const int MaxServers = 50;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IDataProtector _protector = protection.CreateProtector("fuseraft-web.mcp.token");
    private readonly Lock _lock = new();
    private List<Saved>? _servers;

    /// <summary>Raised after any change, so open pages refresh.</summary>
    public event Action? Changed;

    public IReadOnlyList<McpServer> All()
    {
        lock (_lock) return [.. Servers.Select(View)];
    }

    public McpServer? Find(string id)
    {
        lock (_lock) return Servers.FirstOrDefault(s => s.Id == id) is { } s ? View(s) : null;
    }

    /// <summary>The server's token, or null when it has none or it can't be decrypted.</summary>
    public string? Token(string id)
    {
        lock (_lock) return Servers.FirstOrDefault(s => s.Id == id) is { } s ? Unprotect(s) : null;
    }

    /// <summary>Adds a server; throws <see cref="ArgumentException"/> with what's wrong with it.</summary>
    public McpServer Add(string name, string url, string? token, string? addedBy)
    {
        name = name.Trim();
        url  = url.Trim();
        lock (_lock)
        {
            if (Servers.Count >= MaxServers) throw new ArgumentException($"There can be at most {MaxServers} MCP servers.");
            Check(name, url, exceptId: null);
            var saved = new Saved
            {
                Id      = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)),
                Name    = name,
                Url     = url,
                Token   = Protect(token),
                AddedBy = addedBy,
                AddedAt = DateTimeOffset.UtcNow,
            };
            Servers.Add(saved);
            Save();
            return View(saved);
        }
    }

    /// <summary>Changes a server's URL, and its token unless <paramref name="token"/> is null ("" removes it).</summary>
    public void Update(string id, string url, string? token)
    {
        url = url.Trim();
        lock (_lock)
        {
            var s = Servers.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("That MCP server is gone.");
            Check(s.Name, url, exceptId: id);
            s.Url = url;
            if (token is not null) s.Token = Protect(token);
            Save();
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            if (Servers.FirstOrDefault(x => x.Id == id) is not { } s || s.Enabled == enabled) return;
            s.Enabled = enabled;
            Save();
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            if (Servers.RemoveAll(x => x.Id == id) == 0) return;
            Save();
        }
    }

    /// <summary>
    /// The environment that gives a fuseraft process the enabled servers: <c>FUSERAFT_MCP_SERVERS</c> lists them, and
    /// each token travels in a variable of its own that the list names, so fuseraft masks its value in tool output.
    /// Empty when there are none.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment()
    {
        var env     = new Dictionary<string, string>();
        var servers = new List<object>();
        lock (_lock)
        {
            foreach (var s in Servers.Where(s => s.Enabled))
            {
                var headers = new Dictionary<string, string>();
                if (s.Token is not null)
                {
                    if (Unprotect(s) is not { } token) continue; // logged; better no server than one that 401s every call
                    var variable = $"FUSERAFT_WEB_MCP_{servers.Count + 1}_TOKEN";
                    env[variable] = token;
                    headers["Authorization"] = $"Bearer ${{{variable}}}";
                }
                servers.Add(new
                {
                    s.Name, Transport = "http", s.Url, TransportMode = "streamable-http", Headers = headers,
                });
            }
        }
        if (servers.Count > 0) env["FUSERAFT_MCP_SERVERS"] = JsonSerializer.Serialize(servers);
        return env;
    }

    // A name agents put in Plugins, so it can't hold anything a YAML list or a tool category would trip on.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,39}$")]
    private static partial Regex NamePattern();

    private void Check(string name, string url, string? exceptId)
    {
        if (!NamePattern().IsMatch(name))
            throw new ArgumentException("A name starts with a letter and has only letters, digits, - and _ (40 at most).");
        if (Servers.Any(s => s.Id != exceptId && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There's already an MCP server called '{name}'.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The URL must be an http:// or https:// address, such as http://localhost:8080/mcp.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Put the credentials in the token, not the URL.");
    }

    private static McpServer View(Saved s) => new()
    {
        Id = s.Id, Name = s.Name, Url = s.Url, HasToken = s.Token is not null, Enabled = s.Enabled,
        AddedBy = s.AddedBy, AddedAt = s.AddedAt,
    };

    private string? Protect(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : _protector.Protect(token.Trim());

    private string? Unprotect(Saved s)
    {
        if (s.Token is null) return null;
        try { return _protector.Unprotect(s.Token); }
        catch (CryptographicException)
        {
            logger?.LogWarning("The token saved for MCP server {Name} can't be decrypted (the server's keys changed); " +
                "it's left out until its token is entered again", s.Name);
            return null;
        }
    }

    private List<Saved> Servers => _servers ??= Load();

    private List<Saved> Load()
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (JsonException ex)
        {
            logger?.LogError(ex, "{Path} isn't valid JSON; starting with no MCP servers", path);
            return [];
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Servers, Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
        Changed?.Invoke();
    }

    private sealed class Saved
    {
        public string  Id      { get; set; } = "";
        public string  Name    { get; set; } = "";
        public string  Url     { get; set; } = "";
        /// <summary>Encrypted.</summary>
        public string? Token   { get; set; }
        public bool    Enabled { get; set; } = true;
        public string? AddedBy { get; set; }
        public DateTimeOffset AddedAt { get; set; }
    }
}
