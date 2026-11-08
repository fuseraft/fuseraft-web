using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using fuseraft.Web.Models;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class ScheduleTickerTests : IDisposable
{
    private readonly FakeCli  _cli      = new();
    private readonly Accounts _accounts = new();
    private readonly ScheduleTicker _ticker;
    private readonly List<(WebUser User, WireScheduledJob Job)> _finished = [];

    public ScheduleTickerTests()
    {
        _ticker = new ScheduleTicker(_cli, _accounts.Store, new ModelProfileRegistry { UseKeychain = false },
            Options.Create(new WebOptions()), NullLogger<ScheduleTicker>.Instance);
        _ticker.JobFinished += (u, j) => { lock (_finished) _finished.Add((u, j)); };
    }

    public void Dispose() => _accounts.Dispose();

    private static string Job(string name, DateTimeOffset lastRun, int exitCode) =>
        $$"""{"name":"{{name}}","cron":"0 2 * * *","task":"t","enabled":true,"createdAt":"2026-10-31T00:00:00+00:00","lastRun":"{{lastRun:O}}","lastExitCode":{{exitCode}}}""";

    [Fact]
    public async Task Tick_ReportsOnlyTheJobsItRan()
    {
        var bob = _accounts.Add("bob");
        var dir = Directory.CreateDirectory(Path.Combine(bob.Home, "schedule")).FullName;
        _cli.OnRun = _ =>
        {
            File.WriteAllText(Path.Combine(dir, "nightly.yaml"), "");
            _cli.Responses["schedule list --json"] =
                $$"""{"protocolVersion":1,"items":[{{Job("nightly", DateTimeOffset.UtcNow, 1)}},{{Job("weekly", DateTimeOffset.UtcNow.AddDays(-1), 0)}}]}""";
        };

        await _ticker.TickAsync();

        var (user, job) = Assert.Single(_finished);
        Assert.Equal((bob.Id, "nightly", 1), (user.Id, job.Name, job.LastExitCode));
        Assert.Equal(["schedule", "run"], Assert.Single(_cli.Runs));
    }

    [Fact]
    public async Task Tick_ThatRanNothing_DoesNotListTheJobs()
    {
        var bob = _accounts.Add("bob");
        Directory.CreateDirectory(Path.Combine(bob.Home, "schedule"));
        File.WriteAllText(Path.Combine(bob.Home, "schedule", "nightly.yaml"), "");
        File.SetLastWriteTimeUtc(Path.Combine(bob.Home, "schedule", "nightly.yaml"), DateTime.UtcNow.AddHours(-1));

        await _ticker.TickAsync();

        Assert.Single(_cli.Runs);
        Assert.Empty(_cli.Queries);
        Assert.Empty(_finished);
    }
}
