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
        schedulerA.Start();
        schedulerB.Start();

        try
        {
            for (var round = 1; round <= rounds; round++)
            {
                dbA.UpdateNextRun(jobId, DateTime.UtcNow.AddSeconds(-1));

                // Wait for this round's run to finish, then give both loops two more ticks to run it
                // a second time if they are going to.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(20))
                {
                    var runs = dbA.ListRuns(jobName: "shared-job", limit: 1000);
                    if (runs.Count >= round && runs.All(r => r.EndedAt.HasValue))
                        break;
                    await Task.Delay(100);
                }
                await Task.Delay(2500);

                var all = dbA.ListRuns(jobName: "shared-job", limit: 1000);
                Assert.True(all.Count == round,
                    $"round {round}: expected {round} runs in total, found {all.Count}");
                Assert.All(all, r => Assert.Equal(0, r.ExitCode));
            }
        }
        finally
        {
            await schedulerA.StopAsync(5);
            await schedulerB.StopAsync(5);
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
