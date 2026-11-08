using Microsoft.Extensions.Options;
using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

/// <summary>
/// Runs <c>fuseraft schedule run</c> in every account's home once a minute, with that account's profile keys,
/// so each user's scheduled jobs run without a cron entry per home. fuseraft's own job locks keep a tick from
/// this and one from the host's cron from running a job twice. Each job a tick runs is reported by
/// <see cref="JobFinished"/>.
/// </summary>
public sealed class ScheduleTicker(
    FuseraftCli cli, UserStore users, ModelProfileRegistry profiles, IOptions<WebOptions> options, ILogger<ScheduleTicker> logger)
    : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A job of this user's ran; its <see cref="WireScheduledJob.LastExitCode"/> says how it went.</summary>
    public event Action<WebUser, WireScheduledJob>? JobFinished;

    // A tick runs due jobs to completion, so one user's long job must hold up neither the next tick nor anyone else.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunSchedules) return;
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                _ = TickAsync();
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    internal Task TickAsync()
    {
        var started = new List<Task>();
        foreach (var user in users.All.Where(u => !u.Disabled && Directory.Exists(Path.Combine(u.Home, "schedule"))))
        {
            if (_running.TryGetValue(user.Id, out var previous) && !previous.IsCompleted) continue;
            var run = RunAsync(user);
            _running[user.Id] = run;
            started.Add(run);
        }
        return Task.WhenAll(started);
    }

    private async Task RunAsync(WebUser user)
    {
        try
        {
            var started = DateTimeOffset.UtcNow;
            var home    = cli.ForHome(user.Home);
            var keys    = await profiles.For(user).KeyEnvironmentAsync();
            await home.RunAsync(keys, "schedule", "run");
            if (JobFinished is not null && AnyJobRewrittenSince(user, started))
                Report(users.Find(user.Id) ?? user, await home.ListAsync<WireScheduledJob>("schedule", "list", "--json"), started);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scheduled jobs of {User} didn't run", user.UserName);
        }
    }

    // fuseraft rewrites a job's file after running it, so most ticks, which run nothing, needn't list the jobs.
    // File times come from a coarser clock than UtcNow, so they can read a little early.
    private static bool AnyJobRewrittenSince(WebUser user, DateTimeOffset started) =>
        Directory.EnumerateFiles(Path.Combine(user.Home, "schedule"), "*.yaml")
            .Any(f => File.GetLastWriteTimeUtc(f) >= started.UtcDateTime - TimeSpan.FromSeconds(1));

    private void Report(WebUser user, IEnumerable<WireScheduledJob> jobs, DateTimeOffset started)
    {
        foreach (var job in jobs.Where(j => j.LastRun >= started))
            JobFinished?.Invoke(user, job);
    }
}
