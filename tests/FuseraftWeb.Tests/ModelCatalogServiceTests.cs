using fuseraft.Web.Models;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Cli;

namespace FuseraftWeb.Tests;

public sealed class ModelCatalogServiceTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"fw-catalog-{Guid.NewGuid():N}");
    private readonly FakeCli _cli = new() { Version = new(1, 34, 0) };
    private readonly ModelProfileService _profiles;

    public ModelCatalogServiceTests() => _profiles = new ModelProfileService(_home, "u/", useKeychain: false);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private ModelCatalogService Catalog => new(_cli, _profiles);

    private const string Listing = """{"protocolVersion":1,"items":[{"id":"grok-4","current":false},{"id":"grok-code","current":false}]}""";

    [Fact]
    public async Task List_PassesTheConnectionAndTheTypedKey()
    {
        _cli.Responses["models --json --provider xai"] = Listing;

        var models = await Catalog.ListAsync("xai", "", " sk-typed ", profileId: null);

        Assert.Equal(["grok-4", "grok-code"], models);
        Assert.Equal("sk-typed", Assert.Single(_cli.QueryEnvironments)!["FUSERAFT_API_KEY"]);
    }

    [Fact]
    public async Task List_WithNoKeyTyped_UsesTheProfilesSavedKey()
    {
        await _profiles.SaveAsync(new ModelProfile { Id = "p1", Name = "x", ModelId = "grok-4", Provider = "xai" }, "sk-saved");
        _cli.Responses["models --json --provider xai"] = Listing;

        await Catalog.ListAsync("xai", null, "", profileId: "p1");

        Assert.Equal("sk-saved", Assert.Single(_cli.QueryEnvironments)!["FUSERAFT_API_KEY"]);
    }

    [Fact]
    public async Task List_ForALocalServer_SendsNoKey()
    {
        _cli.Responses["models --json --endpoint http://gpu:8000/v1"] = Listing;

        await Catalog.ListAsync("other", " http://gpu:8000/v1 ", null, profileId: null);

        Assert.Empty(Assert.Single(_cli.QueryEnvironments)!);
    }

    [Fact]
    public async Task List_WithoutAProviderOrEndpoint_AsksForOne()
    {
        var ex = await Assert.ThrowsAsync<FuseraftCliException>(() => Catalog.ListAsync("other", " ", "k", null));

        Assert.Contains("Pick a provider", ex.Message);
        Assert.Empty(_cli.Queries);
    }

    [Fact]
    public async Task List_OnAnOlderFuseraft_SaysWhichVersionItNeeds()
    {
        _cli.Version = new(1, 33, 0);

        var ex = await Assert.ThrowsAsync<FuseraftCliException>(() => Catalog.ListAsync("xai", null, "k", null));

        Assert.Contains("needs fuseraft 1.34.0", ex.Message);
    }
}
