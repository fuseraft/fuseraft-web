using System.Diagnostics;
using fuseraft.Web.Services;

namespace FuseraftWeb.Tests;

public sealed class WorkspaceFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fw-files-{Guid.NewGuid():N}");

    public WorkspaceFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string relative, string text = "x\n")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", .. args])
            psi.ArgumentList.Add(a);
        // No global config: this machine's may sign commits, which would wait on a key.
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }

    [Fact]
    public void Suggest_PutsNameMatchesFirst_AndSkipsGeneratedFoldersAndSpaces()
    {
        Write("src/Parser.cs");
        Write("docs/parser-notes.md");
        Write("node_modules/parser/index.js");
        Write("my parser.txt");

        var found = WorkspaceFiles.Suggest(_root, "pars");

        Assert.Equal(["src/Parser.cs", "docs/parser-notes.md"], found);
    }

    [Fact]
    public void Suggest_WithNothingTyped_ListsTheTopLevel_FoldersEndingInSlash()
    {
        Write("src/a.cs");
        Write("README.md");

        Assert.Equal(["src/", "README.md"], WorkspaceFiles.Suggest(_root, ""));
    }

    [Fact]
    public async Task Diff_OfAnEditedFile_IsAgainstTheLastCommit()
    {
        Write("a.txt", "one\n");
        Git("init", "-q");
        Git("add", "-A");
        Git("commit", "-qm", "init");
        Write("a.txt", "two\n");

        var (diff, reason) = await WorkspaceFiles.DiffAsync(_root, "a.txt");

        Assert.Null(reason);
        Assert.Contains("-one", diff);
        Assert.Contains("+two", diff);
    }

    [Fact]
    public async Task Diff_OfANewFile_ShowsItWhole_WithARelativePath()
    {
        Git("init", "-q");
        Write("notes/new.txt", "hello\n");

        var (diff, _) = await WorkspaceFiles.DiffAsync(_root, "notes/new.txt");

        Assert.Contains("+++ b/notes/new.txt", diff);
        Assert.Contains("+hello", diff);
    }

    [Fact]
    public async Task Diff_OutsideTheWorkspace_IsRefused()
    {
        var (diff, reason) = await WorkspaceFiles.DiffAsync(_root, "../elsewhere.txt");

        Assert.Null(diff);
        Assert.Contains("outside the chat's workspace", reason);
    }

    [Fact]
    public async Task Diff_WithoutGit_SaysWhy()
    {
        Write("a.txt");

        var (diff, reason) = await WorkspaceFiles.DiffAsync(_root, "a.txt");

        Assert.Null(diff);
        Assert.Contains("isn't a git repository", reason);
    }
}
