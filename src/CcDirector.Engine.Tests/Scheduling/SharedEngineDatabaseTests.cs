using CcDirector.Engine.Scheduling;
using CcDirector.Engine.Storage;
using Xunit;

namespace CcDirector.Engine.Tests.Scheduling;

/// <summary>
/// Two Directors on ONE engine.db (devthrottle_internal#2311, live proof F6). When CC_VAULT_PATH is
/// set at the user level every Director on the machine opens the same engine.db, so the engine must
/// be safe there: each due occurrence runs exactly once, and one Director starting never fails
/// another live Director's run. In the double-run test each simulated Director has its own owner
/// identity, as two real Director processes do.
/// </summary>
public sealed class SharedEngineDatabaseTests : IDisposable
{
    private readonly string _dbPath;

    public SharedEngineDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_shared_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); }
            catch (IOException) { /* Test temp file cleanup -- best effort */ }
        }
    }

    /// <summary>
    /// The two schedulers' ticks are driven by the test, not by their timers. A tick claims and starts its
    /// job before it hands back its task, so in each round Director A claims and starts the occurrence, then
    /// Director B ticks while that run is OPEN and must leave it alone (the open run is the claim), and once
    /// the run has ended each ticks again alone and must find nothing due. Nothing here waits on a clock, so
    /// a second run can only come from the product: a claim granted beside an open run, or a completed run
    /// leaving the job due. The timer-driven form of this test went red four times on 7 and 8 October 2026
    /// ("expected 1 runs in total, found 2") and the data could not tell a product double-run from a race in
    /// the test's waits. Two claims arriving at the same instant are the other product-side way to a double
    /// run; <c>EngineDatabaseSharedFileTests.TryClaimRun_TwoDirectorsOneFile_ManyRounds_ExactlyOneClaimPerOccurrence</c>
    /// races two claims through a barrier for three hundred rounds and covers it.
    /// </summary>
    [Fact]
    public async Task TwoSchedulers_OneFile_SameDueJob_RunsExactlyOncePerRound()
    {
        const int rounds = 4;
        // Two Directors are two processes. Both schedulers here live in ONE test process, so each gets
        // its own owner identity - otherwise each takes the other's freshly claimed run for one of its
        // own that it is not executing, ends it as orphaned, and runs the occurrence again.
        var ownerA = EngineRunOwner.ForCurrentProcess("director-a");
        var ownerB = ownerA with { Director = "director-b", ProcessId = ownerA.ProcessId + 100_000 };
        var dbA = new EngineDatabase(_dbPath, ownerA, _ => OwnerLiveness.Running);
        var dbB = new EngineDatabase(_dbPath, ownerB, _ => OwnerLiveness.Running);

        // Feb 31 never comes, so after a run the job is only due again when the test makes it due.
        var jobId = dbA.AddJob(new JobRecord
        {
            Name = "shared-job",
            Cron = "0 0 31 2 *",
            Command = "echo shared",
            TimeoutSeconds = 60
        });

        using var schedulerA = new Scheduler(dbA, new JobExecutor(dbA), checkIntervalSeconds: 1, runRetentionDays: 30);
        using var schedulerB = new Scheduler(dbB, new JobExecutor(dbB), checkIntervalSeconds: 1, runRetentionDays: 30);

        for (var round = 1; round <= rounds; round++)
        {
            dbA.UpdateNextRun(jobId, DateTime.UtcNow.AddSeconds(-1));

            // Director A claims and starts the occurrence (a tick does that before handing back its task);
            // Director B then ticks beside the open run and must not claim it. Each tick completes when the
            // run it started, if any, has ended.
            await Task.WhenAll(schedulerA.TickAsync(), schedulerB.TickAsync());

            var afterBothTicked = dbA.ListRuns(jobName: "shared-job", limit: 1000);
            Assert.True(afterBothTicked.Count == round,
                $"round {round}: expected {round} runs in total after Director A claimed and Director B ticked beside the open run, found {afterBothTicked.Count}");
            Assert.All(afterBothTicked, r => Assert.True(r.EndedAt.HasValue, "a tick returned before its run had ended"));

            // The run is over and next_run has moved on: a later tick on either Director finds nothing due.
            await schedulerA.TickAsync();
            await schedulerB.TickAsync();

            var all = dbA.ListRuns(jobName: "shared-job", limit: 1000);
            Assert.True(all.Count == round,
                $"round {round}: expected {round} runs in total after each Director ticked again alone, found {all.Count}");
            Assert.All(all, r => Assert.Equal(0, r.ExitCode));
        }
    }

    [Fact]
    public void SecondDirectorStarting_LeavesTheFirstDirectorsRunInProgressOpen()
    {
        var first = new EngineDatabase(_dbPath);
        var jobId = first.AddJob(new JobRecord { Name = "long-job", Cron = "0 0 31 2 *", Command = "echo long" });
        var run = new RunRecord { JobId = jobId, JobName = "long-job", StartedAt = DateTime.UtcNow };
        run.Id = first.CreateRun(run);

        // A second Director opens the same file and runs its start-up cleanup.
        var second = new EngineDatabase(_dbPath);
        var failed = second.CleanupOrphanedRuns();

        Assert.Equal(0, failed);
        var loaded = first.GetRun(run.Id);
        Assert.NotNull(loaded);
        Assert.Null(loaded.EndedAt);
        Assert.Null(loaded.ExitCode);
    }
}
