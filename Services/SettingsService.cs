using System.Collections.Concurrent;
using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;

namespace fuseraft.Web.Services;

/// <summary>One user's fuseraft settings (<c>fuseraft settings</c>), read and changed through the CLI, which checks every value.</summary>
public sealed class SettingsService(FuseraftCli cli)
{
    /// <summary>The first fuseraft whose <c>settings</c> commands take <c>--json</c>.</summary>
    public static readonly Version MinimumVersion = new(1, 23, 0);

    // Model profiles pass the model, provider and endpoint to every chat, and these only shape the terminal REPL.
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "provider.modelId", "provider.endpoint", "provider.type", "provider.apiKeyEnvVar",
        "repl.theme", "repl.dock", "repl.bell", "repl.noBanner",
    };

    // Each set is a read-modify-write of the same file, so two at once for one home could lose one.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Writes = new();

    public async Task<List<WireSetting>> ListAsync()
    {
        try
        {
            return [.. (await cli.ListAsync<WireSetting>("settings", "show", "--json")).Where(s => !Hidden.Contains(s.Key))];
        }
        catch (FuseraftCliException)
        {
            // Asked only once the command has failed, so a development build, whose version can read as older, still works.
            if (await cli.VersionAsync() is { } v && v < MinimumVersion)
                throw new FuseraftCliException(
                    $"Editing settings here needs fuseraft {MinimumVersion} or later; this server has {v}. Update it with 'fuseraft update'.");
            throw;
        }
    }

    /// <summary>The settings the Tools section edits; with the section shown, they aren't listed as text too.</summary>
    public static readonly string[] ToolKeys = ["repl.plugins", "repl.disabledTools"];

    /// <summary>
    /// The tool categories new chats start with on or off (<c>fuseraft settings tools</c>). Null with a fuseraft from before
    /// v1.27.0, which leaves <see cref="ToolKeys"/> editable as lists.
    /// </summary>
    public async Task<List<WireToolCategory>?> ListToolsAsync()
    {
        try { return await cli.ListAsync<WireToolCategory>("settings", "tools", "--json"); }
        catch (FuseraftCliException) { return null; }
    }

    /// <summary>
    /// The <see cref="ToolKeys"/> setting and value that turn <paramref name="tool"/> (or, when null, the whole category)
    /// on or off for new chats: an optional plugin is on when <c>repl.plugins</c> names it, anything else is on unless
    /// <c>repl.disabledTools</c> names it.
    /// </summary>
    public static (string Key, string Value) ToolChange(IReadOnlyList<WireSetting> settings, WireToolCategory category, string? tool, bool on)
    {
        var name = tool ?? category.Name;
        var key  = tool is null && category.Optional ? "repl.plugins" : "repl.disabledTools";
        var list = (settings.FirstOrDefault(s => s.Key == key)?.Value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !x.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // repl.plugins lists what's on; repl.disabledTools what's off.
        if (on == (key == "repl.plugins")) list.Add(name);
        return (key, string.Join(',', list));
    }

    /// <summary>Saves <paramref name="value"/> (<c>""</c> to reset) and returns the setting as saved; a rejected value throws with the CLI's reason.</summary>
    public async Task<WireSetting> SetAsync(string key, string value)
    {
        if (Hidden.Contains(key))
            throw new FuseraftCliException($"{key} isn't set here.");
        var gate = Writes.GetOrAdd(cli.Home ?? "", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await cli.ItemAsync<WireSetting>("settings", "set", key, value, "--json");
        }
        finally { gate.Release(); }
    }
}
