using CcDirector.Core.Background;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Background;

/// <summary>
/// A job moved onto the scheduler (docs/BackgroundWork.md) runs through it and nowhere else: it
/// registers under the name in its register row, with the cadence it always had, runs on the
/// scheduler's timer, and leaves the registry when disposed. A private registry is injected so the
/// process-wide one stays empty.
/// </summary>
public sealed class ScheduledJobMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccd-scheduled-" + Guid.NewGuid().ToString("N")[..8]);

    public ScheduledJobMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("waited 10 s for " + what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task TheBackupCleaner_RunsAsARegisteredJob_OnItsOwnCadence_AndLeavesWhenDisposed()
    {
        var jobs = new BackgroundJobs();
        var backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, "broken.json"), "{ not json");
        var interval = TimeSpan.FromMilliseconds(50);
        using var cleaner = new BackupCleaner(backups, scanInterval: interval, minFileAge: TimeSpan.Zero, jobs: jobs);

        cleaner.Start();

        var row = Assert.Single(jobs.Snapshot());
        Assert.Equal("Backup cleaner", row.Name);
        Assert.Equal(BackgroundJobTier.SlowAndSteady, row.Tier);
        Assert.Equal(interval, row.Cadence);
        await WaitUntil(() => !File.Exists(Path.Combine(backups, "broken.json")), "the scan to delete the broken backup");
        await WaitUntil(() => jobs.Snapshot().Single().RunsInLastHour >= 2, "a second tick on the cadence");

        cleaner.Dispose();
        Assert.Empty(jobs.Snapshot());
    }

    [Fact]
    public void TheDeletionReaper_RegistersOnePerSessionManager_OnItsThirtySecondCadence_AndLeavesWhenDisposed()
    {
        var jobs = new BackgroundJobs();
        var first = new CcDirector.Core.Sessions.SessionManager(new CcDirector.Core.Configuration.AgentOptions(), jobs: jobs);
        var second = new CcDirector.Core.Sessions.SessionManager(new CcDirector.Core.Configuration.AgentOptions(), jobs: jobs);

        var row = Assert.Single(jobs.Snapshot());
        Assert.Equal("Deletion reaper", row.Name);
        Assert.Equal(2, row.Instances);
        Assert.Equal(TimeSpan.FromSeconds(30), row.Cadence);
        Assert.Equal(BackgroundJobTier.SlowAndSteady, row.Tier);

        first.Dispose();
        Assert.Equal(1, jobs.Snapshot().Single().Instances);
        second.Dispose();
        Assert.Empty(jobs.Snapshot());
    }

    [Fact]
    public void TheUncommittedCountProbe_RegistersUnderItsRegisterName_WithItsInterval_AndLeavesWhenDisposed()
    {
        var jobs = new BackgroundJobs();
        using var manager = new CcDirector.Core.Sessions.SessionManager(new CcDirector.Core.Configuration.AgentOptions(), jobs: jobs);
        var monitor = new CcDirector.Core.Git.SessionGitStatusMonitor(manager, interval: TimeSpan.FromSeconds(15), jobs: jobs);

        monitor.Start();

        var row = Assert.Single(jobs.Snapshot(), r => r.Name == "Per-session uncommitted count");
        Assert.Equal(TimeSpan.FromSeconds(15), row.Cadence);
        Assert.Equal(BackgroundJobTier.SlowAndSteady, row.Tier);

        monitor.Dispose();
        Assert.DoesNotContain(jobs.Snapshot(), r => r.Name == "Per-session uncommitted count");
    }

    [Fact]
    public void ThePointerSweep_RegistersUnderItsRegisterName_WithItsTwoSecondCadence_AndLeavesWhenDisposed()
    {
        var jobs = new BackgroundJobs();
        using var manager = new CcDirector.Core.Sessions.SessionManager(new CcDirector.Core.Configuration.AgentOptions(), jobs: jobs);
        var box = Path.Combine(_root, "pointers");
        var watcher = new CcDirector.Core.Sessions.SessionPointerWatcher(manager, box, jobs: jobs);

        watcher.Start();

        var row = Assert.Single(jobs.Snapshot(), r => r.Name == "Session pointer sweep");
        Assert.Equal(TimeSpan.FromSeconds(2), row.Cadence);

        watcher.Dispose();
        Assert.DoesNotContain(jobs.Snapshot(), r => r.Name == "Session pointer sweep");
    }
}
