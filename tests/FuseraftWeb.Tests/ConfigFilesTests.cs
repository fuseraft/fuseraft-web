using fuseraft.Web.Services;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class ConfigFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fw-configs-{Guid.NewGuid():N}");
    private readonly ConfigFiles _files = new(WorkspaceAccess.Unrestricted);

    public ConfigFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string At(string relative) => Path.Combine(_root, relative);

    [Fact]
    public async Task Save_WritesWhenTheFileIsUnchanged_AndLeavesNoTempFile()
    {
        File.WriteAllText(At("team.yaml"), "team: A\n");
        var opened = await _files.ReadAsync(At("team.yaml"));

        var stamp = await _files.SaveAsync(opened.Path, "team: B\n", opened.Stamp);

        Assert.Equal("team: B\n", File.ReadAllText(At("team.yaml")));
        Assert.Equal((await _files.ReadAsync(At("team.yaml"))).Stamp, stamp);
        Assert.Equal(["team.yaml"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Save_RefusesWhenTheFileChangedOnDisk_UnlessOverwriting()
    {
        File.WriteAllText(At("team.yaml"), "team: A\n");
        var opened = await _files.ReadAsync(At("team.yaml"));
        File.WriteAllText(At("team.yaml"), "team: Theirs\n");

        await Assert.ThrowsAsync<ConfigFileConflictException>(() => _files.SaveAsync(opened.Path, "team: Mine\n", opened.Stamp));
        Assert.Equal("team: Theirs\n", File.ReadAllText(At("team.yaml")));

        await _files.SaveAsync(opened.Path, "team: Mine\n", opened.Stamp, overwrite: true);
        Assert.Equal("team: Mine\n", File.ReadAllText(At("team.yaml")));
    }

    [Fact]
    public async Task MissingAgentFile_OpensEmpty_AndSavingCreatesIt_ButNotOverAFileThatAppeared()
    {
        var opened = await _files.ReadAsync(At("agents/dev.md"));
        Assert.Null(opened.Stamp);
        Assert.Equal("", opened.Text);

        await _files.SaveAsync(opened.Path, "---\n---\nbuild\n", null);
        Assert.Equal("---\n---\nbuild\n", File.ReadAllText(At("agents/dev.md")));

        await Assert.ThrowsAsync<ConfigFileConflictException>(() => _files.SaveAsync(opened.Path, "again", null));
    }

    [Theory]
    [InlineData("run.sh")]
    [InlineData(".bashrc")]
    [InlineData("notes.txt")]
    public async Task OnlyConfigAndMarkdownFiles_AreEditable(string name)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _files.ReadAsync(At(name)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _files.SaveAsync(At(name), "x", null));
        Assert.False(File.Exists(At(name)));
    }

    [Fact]
    public async Task NonAdmin_CantReadOrWriteOutsideTheirRoots()
    {
        var mine = Path.Combine(_root, "mine");
        var other = At("other/team.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "team: X\n");
        var files = new ConfigFiles(WorkspaceAccess.For(new WebUser
        {
            Id = "u1", UserName = "u", Home = "/h", IsAdmin = false, WorkspaceRoots = [mine],
        }));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.ReadAsync(other));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.SaveAsync(Path.Combine(mine, "../other/team.yaml"), "team: Y\n", null, overwrite: true));
        Assert.Equal("team: X\n", File.ReadAllText(other));
    }

    [Fact]
    public async Task TooLargeToSave_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _files.SaveAsync(At("big.yaml"), new string('x', ConfigFiles.MaxBytes + 1), null));
        Assert.Contains("too large", ex.Message);
        Assert.False(File.Exists(At("big.yaml")));
    }
}
