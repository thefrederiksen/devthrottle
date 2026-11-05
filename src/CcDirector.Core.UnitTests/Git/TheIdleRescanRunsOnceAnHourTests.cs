using CcDirector.Core.Background;
using CcDirector.Core.Git;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Git;

/// <summary>
/// The full rescan of every repository is a SAFETY NET, and it runs once an hour (the owner's
/// decision, 5 November 2026). It ran every five minutes, unconditionally - 288 rescans a day of
/// every repository on the machine whether or not anything had changed, which is where most of one
/// Mac's 42,093 git commands in sixteen hours came from with nobody at it.
///
/// What keeps a screen current is the file events: a save, a commit, a checkout or a fetch is
/// recomputed within the settle window. These tests pin the idle cadence and keep the register in
/// step with it, because the register is where the next session will look to find out what the
/// Director does when nobody is watching - and that row contradicted the code for months.
/// </summary>
public sealed class TheIdleRescanRunsOnceAnHourTests
{
    [Fact]
    public void DefaultReconcileInterval_IsOneHour()
    {
        Assert.Equal(TimeSpan.FromHours(1), RepositoryWatcher.DefaultReconcileInterval);
    }

    [Fact]
    public void Watcher_RegistersItsRescanOnceAnHour_WithACeilingOfOne()
    {
        var jobs = new BackgroundJobs();
        var monitor = new RepositoryMonitor(enumerate: _ => Array.Empty<string>());
        var watcher = new RepositoryWatcher(monitor, jobs: jobs);

        var row = Assert.Single(jobs.Snapshot());
        Assert.Equal("Repository watcher: full reconciliation", row.Name);
        Assert.Equal(TimeSpan.FromHours(1), row.Cadence);
        Assert.Equal(BackgroundJobTier.SlowAndSteady, row.Tier);
        Assert.Equal(1, row.CeilingPerHour);

        watcher.Dispose();
    }

    [Fact]
    public void TheRegisterRow_StatesTheCadenceTheCodeActuallyRegisters()
    {
        // The expected words are DERIVED from the cadence the job registers with, not written down
        // here: a row test holding its own literal would still pass after the code changed, which is
        // the exact drift this change was made to end (outside review of pull request 4593).
        var jobs = new BackgroundJobs();
        var monitor = new RepositoryMonitor(enumerate: _ => Array.Empty<string>());
        var watcher = new RepositoryWatcher(monitor, jobs: jobs);
        var cadence = Assert.Single(jobs.Snapshot()).Cadence;
        watcher.Dispose();

        var register = File.ReadAllText(Path.Combine(RepositorySourceIndex.Root, "docs", "BackgroundWork.md"));
        var row = Assert.Single(register.Split('\n'), l =>
            l.StartsWith("| Repository watcher:", StringComparison.Ordinal));

        Assert.Contains($"full rescan {CadenceInWords(cadence)}", row, StringComparison.Ordinal);
    }

    /// <summary>The register's own way of writing a cadence, so the row and the code are one fact.</summary>
    private static string CadenceInWords(TimeSpan? cadence)
    {
        Assert.NotNull(cadence);
        var c = cadence.Value;
        if (c == TimeSpan.FromHours(1)) return "once an hour";
        return c.TotalHours >= 1 ? $"every {c.TotalHours:0.#} h" : $"every {c.TotalMinutes:0.#} min";
    }
}
