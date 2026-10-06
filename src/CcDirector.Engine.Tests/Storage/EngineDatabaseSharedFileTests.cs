using CcDirector.Engine.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Engine.Tests.Storage;

/// <summary>
/// The claim, the run owner and the forward migration that make one engine.db safe for several
/// Directors (devthrottle_internal#2311, live proof F6). Each "Director" here is an EngineDatabase
/// with its own owner on one shared temporary file; liveness is decided by a fake probe so a test
/// can say which owner is running and which is gone.
/// </summary>
public sealed class EngineDatabaseSharedFileTests : IDisposable
{
    private readonly string _dbPath;

    public EngineDatabaseSharedFileTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_sharedfile_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); }
            catch (IOException) { /* Test temp file cleanup -- best effort */ }
        }
    }

    private static EngineRunOwner Owner(string director, int pid) =>
        new(director, Environment.MachineName, pid, new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc));

    private EngineDatabase Director(string director, int pid, params int[] gonePids) =>
        new(_dbPath, Owner(director, pid), owner => gonePids.Contains(owner.ProcessId) ? OwnerLiveness.Gone : OwnerLiveness.Running);

    private static JobRecord Job(string name, DateTime? nextRun, int timeoutSeconds = 300) => new()
    {
        Name = name,
        Cron = "0 0 31 2 *",
        Command = "echo shared",
        TimeoutSeconds = timeoutSeconds,
        NextRun = nextRun
    };

    // -- Claim --

    [Fact]
    public async Task TryClaimRun_TwoDirectorsOneFile_ManyRounds_ExactlyOneClaimPerOccurrence()
    {
        const int rounds = 300;
        var a = Director("director-a", 1001);
        var b = Director("director-b", 2002);
        var jobId = a.AddJob(Job("race-job", DateTime.UtcNow.AddSeconds(-1)));

        for (var round = 1; round <= rounds; round++)
        {
            using var barrier = new Barrier(2);
            RunRecord? Claim(EngineDatabase db)
            {
                barrier.SignalAndWait();
                return db.TryClaimRun(jobId, DateTime.UtcNow);
            }

            var claims = await Task.WhenAll(Task.Run(() => Claim(a)), Task.Run(() => Claim(b)));

            var winners = claims.Where(r => r is not null).ToList();
            Assert.True(winners.Count == 1, $"round {round}: {winners.Count} Directors claimed the same occurrence");

            // While the winner holds it, the loser still cannot claim.
            var winnerDb = claims[0] is not null ? a : b;
            var loserDb = claims[0] is not null ? b : a;
            Assert.Null(loserDb.TryClaimRun(jobId, DateTime.UtcNow));

            // Finish the run and make the job due again for the next round.
            var run = winners[0]!;
            run.EndedAt = DateTime.UtcNow;
            run.ExitCode = 0;
            winnerDb.CompleteRun(run, DateTime.UtcNow.AddSeconds(-1));
        }

        Assert.Equal(rounds, a.ListRuns(jobName: "race-job", limit: rounds + 10).Count);
    }

    [Fact]
    public void TryClaimRun_StampsTheClaimingDirector()
    {
        var a = Director("director-a", 1001);
        var jobId = a.AddJob(Job("stamp-job", DateTime.UtcNow.AddSeconds(-1)));

        var run = a.TryClaimRun(jobId, DateTime.UtcNow);

        Assert.NotNull(run);
        var loaded = a.GetRun(run.Id);
        Assert.NotNull(loaded);
        Assert.Equal(Owner("director-a", 1001), loaded.Owner);
    }

    [Fact]
    public void TryClaimRun_NotDue_ReturnsNull()
    {
        var a = Director("director-a", 1001);
        var jobId = a.AddJob(Job("future-job", DateTime.UtcNow.AddHours(1)));

        Assert.Null(a.TryClaimRun(jobId, DateTime.UtcNow));
    }

    [Fact]
    public void CompleteRun_EndsTheRunAndMovesNextRunTogether()
    {
        var a = Director("director-a", 1001);
        var jobId = a.AddJob(Job("complete-job", DateTime.UtcNow.AddSeconds(-1)));
        var run = a.TryClaimRun(jobId, DateTime.UtcNow)!;
        var next = DateTime.UtcNow.AddHours(1);

        run.EndedAt = DateTime.UtcNow;
        run.ExitCode = 0;
        a.CompleteRun(run, next);

        Assert.NotNull(a.GetRun(run.Id)!.EndedAt);
        Assert.Equal(next, a.GetJob("complete-job")!.NextRun!.Value.ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.Null(a.TryClaimRun(jobId, DateTime.UtcNow));
    }

    // -- Cleanup --

    [Fact]
    public void CleanupOrphanedRuns_SecondDirectorStarting_FailsItsOwnStaleRunButNotTheFirstsLiveRun()
    {
        var first = Director("director-a", 1001);
        var firstJob = first.AddJob(Job("first-job", DateTime.UtcNow.AddSeconds(-1)));
        var firstRun = first.TryClaimRun(firstJob, DateTime.UtcNow)!;

        // Director B's previous launch (pid 2002) died with a run open.
        var previousB = Director("director-b", 2002);
        var staleJob = previousB.AddJob(Job("stale-job", DateTime.UtcNow.AddSeconds(-1)));
        var staleRun = previousB.TryClaimRun(staleJob, DateTime.UtcNow)!;

        // Director B starts again as pid 2003; pid 2002 is gone, Director A (1001) is running.
        var b = Director("director-b", 2003, gonePids: 2002);
        var failed = b.CleanupOrphanedRuns();

        Assert.Equal(1, failed);
        var stale = b.GetRun(staleRun.Id)!;
        Assert.NotNull(stale.EndedAt);
        Assert.Equal(-1, stale.ExitCode);
        Assert.Equal(EngineDatabase.InterruptedRunMessage, stale.Stderr);

        var live = b.GetRun(firstRun.Id)!;
        Assert.Null(live.EndedAt);
        Assert.Null(live.ExitCode);

        // The stale claim is released, so B can run that job now; A's job stays held by A.
        Assert.NotNull(b.TryClaimRun(staleJob, DateTime.UtcNow));
        Assert.Null(b.TryClaimRun(firstJob, DateTime.UtcNow));
    }

    [Fact]
    public void CleanupOrphanedRuns_UndecidableOwner_KeptUntilPastTimeoutPlusGrace()
    {
        var other = new EngineDatabase(_dbPath, Owner("director-x", 3003), _ => OwnerLiveness.Running);
        var jobId = other.AddJob(Job("remote-job", DateTime.UtcNow.AddSeconds(-1), timeoutSeconds: 60));
        var started = DateTime.UtcNow;
        var run = other.TryClaimRun(jobId, started)!;

        var me = new EngineDatabase(_dbPath, Owner("director-a", 1001), _ => OwnerLiveness.Unknown);

        var withinTimeout = started + TimeSpan.FromSeconds(60) + EngineDatabase.AbandonedRunGrace - TimeSpan.FromSeconds(5);
        Assert.Equal(0, me.CleanupOrphanedRuns(withinTimeout));
        Assert.Null(me.GetRun(run.Id)!.EndedAt);

        var pastTimeout = started + TimeSpan.FromSeconds(60) + EngineDatabase.AbandonedRunGrace + TimeSpan.FromSeconds(5);
        Assert.Equal(1, me.CleanupOrphanedRuns(pastTimeout));
        Assert.Equal(EngineDatabase.AbandonedRunMessage, me.GetRun(run.Id)!.Stderr);
    }

    [Fact]
    public void CleanupOrphanedRuns_ThisProcessesOwnOpenRun_IsKept()
    {
        // The probe would call it gone, but a run of THIS process is never failed by this process.
        var me = new EngineDatabase(_dbPath, Owner("director-a", 1001), _ => OwnerLiveness.Gone);
        var jobId = me.AddJob(Job("mine", DateTime.UtcNow.AddSeconds(-1)));
        var run = me.TryClaimRun(jobId, DateTime.UtcNow)!;

        Assert.Equal(0, me.CleanupOrphanedRuns());
        Assert.Null(me.GetRun(run.Id)!.EndedAt);
    }

    [Fact]
    public void CleanupOrphanedRuns_OwnerGoneAndItsCommandGone_IsFailed_ButCommandAlive_IsKept()
    {
        var dead = Director("director-a", 4242);
        var aliveJob = dead.AddJob(Job("alive-command", DateTime.UtcNow.AddSeconds(-1)));
        var goneJob = dead.AddJob(Job("gone-command", DateTime.UtcNow.AddSeconds(-1)));
        var aliveRun = dead.TryClaimRun(aliveJob, DateTime.UtcNow)!;
        var goneRun = dead.TryClaimRun(goneJob, DateTime.UtcNow)!;
        dead.RecordRunChild(aliveRun.Id, 7001, DateTime.UtcNow);
        dead.RecordRunChild(goneRun.Id, 7002, DateTime.UtcNow);

        // 4242 (the Director) and 7002 (one command) are gone; 7001 (the other command) still runs.
        var other = Director("director-b", 2002, gonePids: [4242, 7002]);

        Assert.Equal(1, other.CleanupOrphanedRuns());
        Assert.Null(other.GetRun(aliveRun.Id)!.EndedAt);
        Assert.Equal(7001, other.GetRun(aliveRun.Id)!.ChildProcessId);
        Assert.Equal(EngineDatabase.InterruptedRunMessage, other.GetRun(goneRun.Id)!.Stderr);
    }

    [Fact]
    public void CompleteRun_AfterTheClaimWasReleased_IsKeptAsLate_AndReportsNotApplied()
    {
        var owner = Director("director-a", 4242);
        var jobId = owner.AddJob(Job("late-job", DateTime.UtcNow.AddSeconds(-1)));
        var run = owner.TryClaimRun(jobId, DateTime.UtcNow)!;
        Assert.Equal(1, Director("director-b", 2002, gonePids: 4242).CleanupOrphanedRuns());

        run.EndedAt = DateTime.UtcNow;
        run.ExitCode = 0;
        var applied = owner.CompleteRun(run, DateTime.UtcNow.AddHours(1));

        Assert.False(applied);
        var loaded = owner.GetRun(run.Id)!;
        Assert.NotNull(loaded.LateCompletion);
        Assert.Contains("exit=0", loaded.LateCompletion);
        Assert.Equal(-1, loaded.ExitCode);
    }

    [Fact]
    public void CompleteRun_ByTheOwnerOfTheOpenClaim_Applies()
    {
        var owner = Director("director-a", 1001);
        var jobId = owner.AddJob(Job("own-job", DateTime.UtcNow.AddSeconds(-1)));
        var run = owner.TryClaimRun(jobId, DateTime.UtcNow)!;
        Assert.Equal(300, run.TimeoutSeconds);

        run.EndedAt = DateTime.UtcNow;
        run.ExitCode = 0;

        Assert.True(owner.CompleteRun(run, DateTime.UtcNow.AddHours(1)));
        Assert.Null(owner.GetRun(run.Id)!.LateCompletion);
        Assert.Equal(0, owner.GetRun(run.Id)!.ExitCode);
    }

    [Fact]
    public void ProcessOwnerLiveness_CurrentProcess_IsRunning_AndAReusedIdIsGone()
    {
        var current = EngineRunOwner.ForCurrentProcess("director-a");

        Assert.Equal(OwnerLiveness.Running, ProcessOwnerLiveness.Probe(current));
        Assert.Equal(OwnerLiveness.Gone, ProcessOwnerLiveness.Probe(current with { ProcessStartedAtUtc = current.ProcessStartedAtUtc.AddHours(-2) }));
        Assert.Equal(OwnerLiveness.Unknown, ProcessOwnerLiveness.Probe(current with { Machine = "SOME-OTHER-MACHINE" }));
    }

    // -- Migration --

    /// <summary>
    /// Fixtures/engine-e8f673ab9.db was written by the engine at commit e8f673ab9 (the code before
    /// this change): job "legacy-job", one finished run, and one run still open, started
    /// 2026-10-06T00:00:00Z. Opening it must add the owner columns and keep every row.
    /// </summary>
    [Fact]
    public void Open_DatabaseMadeByThePreviousEngine_MigratesForwardAndKeepsItsRows()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "engine-e8f673ab9.db"), _dbPath);
        Assert.DoesNotContain("owner_pid", RunColumns());

        var db = Director("director-a", 1001);
        // A second Director opening the same, already migrated file changes nothing.
        var again = Director("director-b", 2002);

        var columns = RunColumns();
        Assert.Contains("owner_director", columns);
        Assert.Contains("owner_machine", columns);
        Assert.Contains("owner_pid", columns);
        Assert.Contains("owner_process_started", columns);

        Assert.Contains("run_timeout_seconds", columns);
        Assert.Contains("child_pid", columns);
        Assert.Contains("child_process_started", columns);
        Assert.Contains("late_completion", columns);

        var runs = again.ListRuns(jobName: "legacy-job");
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Null(r.Owner));
        // Backfilled once, from the job's timeout at migration time.
        Assert.All(runs, r => Assert.Equal(300, r.TimeoutSeconds));
        var finished = runs.Single(r => r.EndedAt.HasValue);
        Assert.Equal("legacy", finished.Stdout);
        var open = runs.Single(r => !r.EndedAt.HasValue);

        // A pre-owner open run has no owner to probe: kept while it could still be running...
        var openedAt = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, db.CleanupOrphanedRuns(openedAt.AddMinutes(1)));
        // ...and it holds the job's claim meanwhile.
        Assert.Null(db.TryClaimRun(open.JobId, openedAt.AddMinutes(1)));
        // Past the 300 second timeout plus grace it is failed as abandoned, and the job runs again.
        Assert.Equal(1, db.CleanupOrphanedRuns(openedAt.AddMinutes(11)));
        Assert.Equal(EngineDatabase.AbandonedRunMessage, db.GetRun(open.Id)!.Stderr);
        var claimed = db.TryClaimRun(open.JobId, openedAt.AddMinutes(11));
        Assert.NotNull(claimed);
        Assert.Equal(Owner("director-a", 1001), db.GetRun(claimed.Id)!.Owner);
    }

    [Fact]
    public async Task Open_TwoDirectorsMigrateOneOldFileAtOnce_BothSucceed()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "engine-e8f673ab9.db"), _dbPath);

        using var barrier = new Barrier(2);
        EngineDatabase Open(string director, int pid)
        {
            barrier.SignalAndWait();
            return Director(director, pid);
        }

        await Task.WhenAll(Task.Run(() => Open("director-a", 1001)), Task.Run(() => Open("director-b", 2002)));

        Assert.Equal(4, RunColumns().Count(c => c.StartsWith("owner_", StringComparison.Ordinal)));
    }

    private List<string> RunColumns()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(runs)";
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        return columns;
    }
}
