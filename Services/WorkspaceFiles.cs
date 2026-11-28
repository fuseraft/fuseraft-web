using System.Collections.Concurrent;
using System.Diagnostics;

namespace fuseraft.Web.Services;

/// <summary>Files in a chat's workspace: suggestions for <c>@</c> mentions, and what changed in one, from git.</summary>
public static class WorkspaceFiles
{
    // Folders that are big, generated or private, and never worth mentioning.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "target", "artifacts", ".venv", "venv",
        "__pycache__", ".next", ".cache", ".idea", ".vs",
    };

    private const int MaxScanned = 20_000;
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, string[] Paths)> Listings = new();

    /// <summary>
    /// Up to <paramref name="max"/> paths under <paramref name="root"/> matching <paramref name="query"/>, relative and with
    /// <c>/</c>, folders ending in <c>/</c>. Names first, then shorter paths. Paths with spaces are left out: a mention ends at one.
    /// </summary>
    public static IReadOnlyList<string> Suggest(string root, string query, int max = 30)
    {
        query = query.Trim().Replace('\\', '/');
        var paths = List(root);
        return [.. paths
            .Select(p => (Path: p, Rank: Rank(p, query)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank).ThenBy(x => x.Path.Length).ThenBy(x => x.Path, StringComparer.Ordinal)
            .Take(max)
            .Select(x => x.Path)];
    }

    // 0: the name starts with the query; 1: the path does; 2: the name contains it; 3: the path does; -1: no match.
    internal static int Rank(string path, string query)
    {
        if (query.Length == 0) return path.Count(c => c == '/') - (path.EndsWith('/') ? 1 : 0) == 0 ? 0 : -1;
        var name = Path.GetFileName(path.TrimEnd('/'));
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (path.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return path.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3 : -1;
    }

    private static string[] List(string root)
    {
        var full = Path.GetFullPath(root);
        if (Listings.TryGetValue(full, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheFor) return cached.Paths;

        var found = new List<string>();
        var queue = new Queue<string>([full]);
        while (queue.Count > 0 && found.Count < MaxScanned)
        {
            var dir = queue.Dequeue();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.Ordinal).ToList(); }
            catch { continue; }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name.Contains(' ')) continue;
                var relative = Path.GetRelativePath(full, entry).Replace('\\', '/');
                if (Directory.Exists(entry))
                {
                    if (Skipped.Contains(name) || IsLink(entry)) continue;
                    found.Add(relative + "/");
                    queue.Enqueue(entry);
                }
                else found.Add(relative);
                if (found.Count >= MaxScanned) break;
            }
        }
        var paths = found.ToArray();
        Listings[full] = (DateTimeOffset.UtcNow, paths);
        return paths;
    }

    private static bool IsLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget is not null; }
        catch { return true; }
    }

    /// <summary>
    /// The uncommitted changes to <paramref name="path"/> (relative to <paramref name="root"/>) as a unified diff, against
    /// the last commit, or the whole file when git doesn't track it yet. Null with a reason when there's none to show.
    /// </summary>
    public static async Task<(string? Diff, string? Reason)> DiffAsync(string root, string path)
    {
        var full = Path.GetFullPath(path, Path.GetFullPath(root));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.Ordinal))
            return (null, "That file is outside the chat's workspace.");

        var (code, inside, _) = await GitAsync(root, "rev-parse", "--is-inside-work-tree");
        if (code != 0 || inside.Trim() != "true")
            return (null, "The workspace isn't a git repository, so there's no earlier version to compare with.");

        var (_, status, _) = await GitAsync(root, "status", "--porcelain", "--", full);
        string diff;
        if (status.StartsWith("??"))
            (_, diff, _) = await GitAsync(root, "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-index", "--", "/dev/null",
                Path.GetRelativePath(root, full).Replace('\\', '/'));
        else
        {
            var (headCode, _, _) = await GitAsync(root, "rev-parse", "--verify", "--quiet", "HEAD");
            (_, diff, _) = headCode == 0
                ? await GitAsync(root, "diff", "--no-color", "--no-ext-diff", "--no-textconv", "HEAD", "--", full)
                : await GitAsync(root, "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--cached", "--", full);
        }
        return string.IsNullOrWhiteSpace(diff)
            ? (null, "No uncommitted changes: it matches the last commit.")
            : (diff, null);
    }

    private const int MaxDiff = 400_000;

    private static async Task<(int Code, string Stdout, string Stderr)> GitAsync(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        // Never prompt, and never run a pager or anything the repository's config names.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.fsmonitor=false");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("diff.external=");
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return (-1, "", "git took too long"); }
            var text = await stdout;
            return (proc.ExitCode, text.Length > MaxDiff ? text[..MaxDiff] + "\n… (diff cut short)" : text, await stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
