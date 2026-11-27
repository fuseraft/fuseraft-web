using fuseraft.Web.Models;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class ConfigServiceTests
{
    [Fact]
    public async Task Migrate_WritesInPlaceFromTheFilesDirectory_AndReturnsItsNotes()
    {
        var cli = new FakeCli();
        cli.RunInStderr.AddRange(["note: comments inside the file weren't carried over", "unrelated line"]);

        var notes = await new ConfigService(cli, WorkspaceAccess.Unrestricted).MigrateAsync("/w/.fuseraft/config/o.yaml");

        var (args, cwd) = Assert.Single(cli.RunsIn);
        Assert.Equal(["migrate", Path.GetFullPath("/w/.fuseraft/config/o.yaml"), "--write"], args);
        Assert.Equal(Path.GetFullPath("/w/.fuseraft/config"), cwd);
        Assert.Equal("comments inside the file weren't carried over", Assert.Single(notes));
    }

    [Fact]
    public async Task Migrate_OutsideTheWorkspaces_IsRefused()
    {
        var access = WorkspaceAccess.For(new WebUser { Id = "u1", UserName = "bob", Home = "/h", WorkspaceRoots = ["/srv/bob"] });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new ConfigService(new FakeCli(), access).MigrateAsync("/etc/o.yaml"));
    }

    [Theory]
    [InlineData("statemachine", "steps")]
    [InlineData("magentic", "magentic")]
    [InlineData(null, null)]
    public void Flow_UsesTeamFileWords(string? selectionType, string? flow) =>
        Assert.Equal(flow, new WireConfigSummary { SelectionType = selectionType }.Flow);

    [Fact]
    public void NeedsMigration_WhenFuseraftPointsAtMigrate()
    {
        const string old = "/w/o.yaml uses the old 'Orchestration:' format. Convert it with: fuseraft migrate /w/o.yaml";
        Assert.True(new WireConfigSummary { Error = old }.NeedsMigration);
        Assert.False(new WireConfigSummary { Error = "agents: unknown field 'foo'" }.NeedsMigration);
        Assert.True(new WireValidation { Issues = [new WireIssue { Level = "error", Message = old }] }.NeedsMigration);
        Assert.False(new WireValidation().NeedsMigration);
    }
}
