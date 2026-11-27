using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

/// <summary>Team files, listed, validated and migrated by the fuseraft CLI.</summary>
public sealed class ConfigService(FuseraftCli cli, WorkspaceAccess access)
{
    public Task<List<WireConfigSummary>> ListConfigsAsync(string directory) =>
        cli.ListAsync<WireConfigSummary>("config", "--list", "--dir", access.Check(directory), "--json");

    public Task<WireValidation> ValidateAsync(string path) =>
        cli.ItemAsync<WireValidation>("validate", access.Check(path), "--json");

    /// <summary>
    /// Converts an old-format config (or a team whose agents use <c>agentFile</c>) into a current team file in place,
    /// with <c>fuseraft migrate --write</c>. Returns migrate's notes about what didn't carry over.
    /// </summary>
    public async Task<IReadOnlyList<string>> MigrateAsync(string path)
    {
        var full = Path.GetFullPath(access.Check(path));
        var lines = await cli.RunInAsync(Path.GetDirectoryName(full)!, "migrate", full, "--write");
        return [.. lines.Where(l => l.StartsWith("note: ", StringComparison.Ordinal)).Select(l => l["note: ".Length..])];
    }
}
