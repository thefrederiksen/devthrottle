using CcDirector.ControlApi.Triggers;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// THE DIRECTOR RUNS THE CHECK (the Website Business Factory mission, product track): it runs what the Gateway hands
/// it, when each interval is up, never twice at once - and when the Gateway hands it nothing because the switch is
/// off, it runs NOTHING, proven by a check runner that counts its calls. The real process half is proven against
/// real commands at the end.
/// </summary>
public sealed class DirectorTriggerRunnerTests
{
    private const string Director = "dir-north-1";
    private DateTime _now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
    private readonly FakeGateway _gateway = new();
    private readonly List<string> _checked = new();

    private static TriggerAssignmentDto Trigger(string id = "t1", int interval = 300) => new()
    {
        Id = id, Name = "website-new-mail", CheckCommand = "check", RepoPath = ".", IntervalSeconds = interval,
        TimeoutSeconds = 30,
    };

    private DirectorTriggerRunner Runner(TriggerCheckRunner? run = null) => new(Director, () => _gateway,
        run ?? ((t, _) =>
        {
            lock (_checked) _checked.Add(t.Id);
            return Task.FromResult(new TriggerCheckReport { CheckedAtUtc = _now, ExitCode = 0, Output = "{\"count\": 0}" });
        }),
        () => _now);

    private sealed class FakeGateway : ITriggerGateway
    {
        public TriggerFetch Next { get; set; } = TriggerFetch.Off("factory agents are off");
        public List<(string director, string trigger, TriggerCheckReport report)> Reports { get; } = new();

        public Task<TriggerFetch> FetchTriggersAsync(string directorId, CancellationToken ct) => Task.FromResult(Next);

        public Task<string?> ReportTriggerCheckAsync(string directorId, string triggerId, TriggerCheckReport report, CancellationToken ct)
        {
            lock (Reports) Reports.Add((directorId, triggerId, report));
            return Task.FromResult<string?>(null);
        }
    }

    private static TriggerFetch Assigned(params TriggerAssignmentDto[] triggers)
        => new(TriggerFetchKind.Assigned, triggers, null);

    [Fact]
    public async Task SwitchOff_TheGatewayHandsOutNothing_AndTheDirectorRunsNothing()
    {
        var runner = Runner();

        for (var i = 0; i < 3; i++)
        {
            var started = await runner.TickAsync(CancellationToken.None);
            Assert.Empty(started);
            _now = _now.AddMinutes(10);
        }

        Assert.Empty(_checked);
        Assert.Empty(_gateway.Reports);
    }

    [Fact]
    public async Task NoGateway_RunsNothing()
    {
        var runner = new DirectorTriggerRunner(Director, () => null, (_, _) => throw new InvalidOperationException("must not run"), () => _now);
        Assert.Empty(await runner.TickAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnAssignedTrigger_IsCheckedAndReported_ThenNotAgainUntilItsIntervalIsUp()
    {
        _gateway.Next = Assigned(Trigger());
        var runner = Runner();

        await Task.WhenAll(await runner.TickAsync(CancellationToken.None));
        Assert.Equal(new[] { "t1" }, _checked);
        var (director, trigger, report) = Assert.Single(_gateway.Reports);
        Assert.Equal((Director, "t1"), (director, trigger));
        Assert.Equal("{\"count\": 0}", report.Output);

        _now = _now.AddSeconds(299);
        Assert.Empty(await runner.TickAsync(CancellationToken.None));

        _now = _now.AddSeconds(1);
        await Task.WhenAll(await runner.TickAsync(CancellationToken.None));
        Assert.Equal(2, _checked.Count);
        Assert.Equal(2, _gateway.Reports.Count);
    }

    [Fact]
    public async Task ACheckStillRunning_IsNotStartedAgain()
    {
        _gateway.Next = Assigned(Trigger(interval: 60));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var runner = Runner(async (_, _) =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
            return new TriggerCheckReport { CheckedAtUtc = _now, ExitCode = 0, Output = "{\"count\": 0}" };
        });

        var first = await runner.TickAsync(CancellationToken.None);
        _now = _now.AddMinutes(10);
        var second = await runner.TickAsync(CancellationToken.None);

        _ = Assert.Single(first);
        Assert.Empty(second);
        release.SetResult();
        await Task.WhenAll(first);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task TheSwitchTurningOff_StopsTheChecks()
    {
        _gateway.Next = Assigned(Trigger());
        var runner = Runner();
        await Task.WhenAll(await runner.TickAsync(CancellationToken.None));

        _gateway.Next = TriggerFetch.Off("factory agents are off");
        _now = _now.AddHours(1);
        Assert.Empty(await runner.TickAsync(CancellationToken.None));
        Assert.Single(_checked);
    }

    // ---------- the real process: RunCheckAsync over ProcessJob ----------

    private static string Echo(string json) => OperatingSystem.IsWindows() ? $"echo {json}" : $"echo '{json}'";

    private static TriggerAssignmentDto Real(string command, int timeoutSeconds = 30) => new()
    {
        Id = "t1", Name = "real", CheckCommand = command, RepoPath = Path.GetTempPath(), IntervalSeconds = 60,
        TimeoutSeconds = timeoutSeconds,
    };

    [Fact]
    public async Task RunCheckAsync_ACommandThatPrintsACount_ReportsExitZeroAndTheOutput()
    {
        var report = await DirectorTriggerRunner.RunCheckAsync(Real(Echo("{\"count\": 2}")), CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.False(report.TimedOut);
        Assert.Null(report.StartError);
        Assert.Contains("\"count\": 2", report.Output);
    }

    [Fact]
    public async Task RunCheckAsync_ACommandThatExitsOne_ReportsExitOne()
    {
        var report = await DirectorTriggerRunner.RunCheckAsync(Real("exit 1"), CancellationToken.None);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public async Task RunCheckAsync_ACommandPastItsTimeout_IsKilledAndReportedAsATimeout()
    {
        var slow = OperatingSystem.IsWindows() ? "ping -n 20 127.0.0.1 >nul" : "sleep 20";
        var report = await DirectorTriggerRunner.RunCheckAsync(Real(slow, timeoutSeconds: 1), CancellationToken.None);

        Assert.True(report.TimedOut);
        Assert.Null(report.ExitCode);
    }

    [Fact]
    public async Task RunCheckAsync_AFolderThatDoesNotExist_IsReportedAsACheckThatCouldNotStart()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"));
        var trigger = Real(Echo("{\"count\": 1}"));
        trigger.RepoPath = missing;

        var report = await DirectorTriggerRunner.RunCheckAsync(trigger, CancellationToken.None);

        Assert.Equal($"the folder '{missing}' does not exist on this machine", report.StartError);
        Assert.Null(report.ExitCode);
    }
}
