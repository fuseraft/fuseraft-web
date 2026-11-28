using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;

namespace fuseraft.Web.Services;

/// <summary>The models a provider offers, for the Profiles form, listed by the CLI with the profile's key.</summary>
public sealed class ModelCatalogService(FuseraftCli cli, ModelProfileService profiles)
{
    /// <summary>The first fuseraft whose <c>models --json</c> takes <c>--provider</c> and <c>--endpoint</c>.</summary>
    public static readonly Version MinimumVersion = new(1, 34, 0);

    /// <summary>The models <paramref name="profile"/>'s connection offers, with its saved key.</summary>
    public Task<IReadOnlyList<string>> ListAsync(ModelProfile profile) =>
        ListAsync(profile.Provider, profile.Endpoint, null, profile.Id);

    /// <param name="apiKey">The key typed into the form; blank uses <paramref name="profileId"/>'s saved one.</param>
    public async Task<IReadOnlyList<string>> ListAsync(string? provider, string? endpoint, string? apiKey, string? profileId)
    {
        var connection = ReplService.ConnectionArgs(provider, endpoint).ToList();
        if (connection.Count == 0)
            throw new FuseraftCliException("Pick a provider or enter an API endpoint first.");

        var key = string.IsNullOrWhiteSpace(apiKey)
            ? profileId is null ? null : await profiles.GetKeyAsync(profileId)
            : apiKey.Trim();
        var environment = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(key)) environment["FUSERAFT_API_KEY"] = key;

        try
        {
            var models = await cli.ListAsync<WireModel>(environment, ["models", "--json", .. connection]);
            return [.. models.Select(m => m.Id)];
        }
        catch (FuseraftCliException)
        {
            // Asked only once the command has failed, so a development build, whose version can read as older, still works.
            if (await cli.VersionAsync() is { } v && v < MinimumVersion)
                throw new FuseraftCliException(
                    $"Listing models here needs fuseraft {MinimumVersion} or later; this server has {v}. Type the model ID instead, or update fuseraft.");
            throw;
        }
    }
}
