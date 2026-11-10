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
