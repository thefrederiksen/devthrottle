using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// <see cref="RepositoryMonitor.HasCompletedAScan"/> - has this Director's view of its own disk settled?
/// (the one-repository-list mission, "the registry reaches the Gateway").
///
/// It exists because <c>ControlApiHost.SnapshotRepositories</c> must not fold the machine's registered
/// repository list into its push until the answer is yes. The Gateway reads a push with no unverified
/// row in it as a COMPLETE statement of what a Director knows, and reconciles against it - so a push
/// made before the first scan had run, carrying the registry and nothing else, would say "every
/// repository under every watched folder is gone".
///
/// Neither of the two facts the monitor already published can answer the question, which is the whole
/// reason for a third: <see cref="RepositoryMonitor.IsScanning"/> is false both after a scan and before
/// the first one, and those are opposite states; an empty <see cref="RepositoryMonitor.Snapshot"/> means
/// "nothing found" after a scan and "nothing looked at" before one.
/// </summary>
public class RepositoryMonitorHasCompletedAScanTests
{
    private static RepositoryStatus Status(string path) => new()
    {
        Path = path,
        Name = System.IO.Path.GetFileName(path),
        Provider = RepoProvider.GitHub,
        Branch = "main",
        IsClean = true,
        Success = true,
    };

    private static Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> NoSessions
        => _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());

    private static RepositoryMonitor MonitorOver(IReadOnlyList<string> paths)
        => new(enumerate: _ => paths, compute: (p, _, _) => Task.FromResult(Status(p)))
        { LiveSessionsProvider = NoSessions };

    [Fact]
    public void BeforeAnyScan_ItIsFalse_EvenThoughNoScanIsRunning()
    {
        var monitor = MonitorOver(new[] { "/r/a" });

        Assert.False(monitor.HasCompletedAScan);
        Assert.False(monitor.IsScanning);   // the two are not the same question
    }

    [Fact]
    public async Task AfterAScanFinishes_ItIsTrue_AndAgreesWithScanCompleted()
    {
        var monitor = MonitorOver(new[] { "/r/a", "/r/b" });
        bool completedRaised = false;
        monitor.ScanCompleted += () => completedRaised = true;

        await monitor.RescanAsync(new[] { "/r" });

        Assert.True(monitor.HasCompletedAScan);
        Assert.True(completedRaised);
    }

    [Fact]
    public async Task AScanThatFoundNothing_StillCounts()
    {
        // A machine with no watched folders. "Looked and found nothing" is a settled view, and reading
        // it as an unsettled one would keep that machine's hand-built repository list off the Gateway
        // forever - which is precisely the user this change exists for.
        var monitor = MonitorOver(Array.Empty<string>());

        await monitor.RescanAsync(Array.Empty<string>());

        Assert.True(monitor.HasCompletedAScan);
        Assert.Empty(monitor.Snapshot());
    }

    [Fact]
    public async Task AWarmStartCacheAlone_DoesNotCount()
    {
        // The cache fills the model before anything has been verified. A model with rows in it is
        // therefore not evidence that a scan has run, and the flag must not be inferred from one.
        var cachePath = Path.Combine(Path.GetTempPath(), "ccd-monitor-cache-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var seed = new RepositoryMonitor(enumerate: _ => new[] { "/r/a" },
                compute: (p, _, _) => Task.FromResult(Status(p)), cachePath: cachePath)
            { LiveSessionsProvider = NoSessions };
            await seed.RescanAsync(new[] { "/r" });

            var coldStart = new RepositoryMonitor(enumerate: _ => new[] { "/r/a" },
                compute: (p, _, _) => Task.FromResult(Status(p)), cachePath: cachePath)
            { LiveSessionsProvider = NoSessions };
            coldStart.LoadCache();

            Assert.NotEmpty(coldStart.Snapshot());
            Assert.False(coldStart.HasCompletedAScan);
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    [Fact]
    public async Task AScanThatWasCancelled_DoesNotCount()
    {
        var gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/r/a" },
            compute: async (p, _, ct) => { gate.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Status(p); })
        { LiveSessionsProvider = NoSessions };

        var scan = monitor.RescanAsync(new[] { "/r" }, cts.Token);
        await gate.Task;
        cts.Cancel();
        await scan;

        Assert.False(monitor.HasCompletedAScan);
    }

    [Fact]
    public async Task ALaterScanStarting_DoesNotMakeTheViewUnknownAgain()
    {
        // The model keeps its entries throughout a rescan rather than emptying first, so a machine whose
        // view has settled once does not become unknown while it is being re-checked. Were the flag
        // cleared at the start of every scan, the registered list would drop out of the push and back
        // into it on every rescan, and the Gateway would see it vanish and return.
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var second = false;
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/r/a" },
            compute: async (p, _, _) =>
            {
                if (second) { started.TrySetResult(); await release.Task; }
                return Status(p);
            })
        { LiveSessionsProvider = NoSessions };

        await monitor.RescanAsync(new[] { "/r" });
        Assert.True(monitor.HasCompletedAScan);

        second = true;
        var rescan = monitor.RescanAsync(new[] { "/r" });
        await started.Task;

        Assert.True(monitor.IsScanning);
        Assert.True(monitor.HasCompletedAScan);   // still settled, while being re-checked

        release.SetResult();
        await rescan;
        Assert.True(monitor.HasCompletedAScan);
    }
}
