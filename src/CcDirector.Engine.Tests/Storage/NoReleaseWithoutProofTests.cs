using System.Diagnostics;
using CcDirector.Engine.Scheduling;
using CcDirector.Engine.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Engine.Tests.Storage;

/// <summary>
/// Review round 4 of the shared engine.db fix (devthrottle_internal#2311). EN-F8: a recorded command is
/// asked first, whatever the owner's state. EN-F9: no release is decided from a stale read, and no absence
/// is treated as proof - the end is conditional on the state it was decided from, the executor checks its
/// own writes, and a migrated open run is never taken for "never started".
/// </summary>
public sealed class NoReleaseWithoutProofTests : IDisposable
{
    private readonly string _dbPath;

    public NoReleaseWithoutProofTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_noproof_test_{Guid.NewGuid():N}.db");
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

    private static EngineRunOwner Owner(int pid) =>
        new($"director-{pid}", Environment.MachineName, pid, new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc));

    /// <summary>Listed fake Director ids answer as given; any other id (a real command) is probed for real.</summary>
    private EngineDatabase Director(int pid, int[]? running = null, int[]? gone = null, int[]? unknown = null) =>
        new(_dbPath, Owner(pid), owner =>
            running?.Contains(owner.ProcessId) == true ? OwnerLiveness.Running
            : gone?.Contains(owner.ProcessId) == true ? OwnerLiveness.Gone
            : unknown?.Contains(owner.ProcessId) == true ? OwnerLiveness.Unknown
            : ProcessOwnerLiveness.Probe(owner));

    private static Process StartLongCommand()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        return Process.Start(info)!;
    }

    private static int AddDueJob(EngineDatabase db, string name, int timeoutSeconds = 60, string command = "unused") =>
        db.AddJob(new JobRecord
        {
            Name = name, Cron = "0 0 31 2 *", Command = command, TimeoutSeconds = timeoutSeconds, NextRun = DateTime.UtcNow.AddSeconds(-1)
        });

    // -- EN-F8 --

    [Fact]
    public void OwnerUndecidable_RecordedCommandRunningPastTheDeadline_IsKept()
    {
        var owner = Director(4242);
        var jobId = AddDueJob(owner, "undecidable-owner");
        var run = owner.TryClaimRun(jobId, DateTime.UtcNow)!;
        using var command = StartLongCommand();
        try
        {
            owner.RecordRunChild(run.Id, command.Id, command.StartTime.ToUniversalTime());

            // Another Director cannot inspect 4242, but can see the recorded command is alive.
            var other = Director(2002, running: [2002], unknown: [4242]);
            var wellPastTheDeadline = run.StartedAt + TimeSpan.FromSeconds(60) + EngineDatabase.AbandonedRunGrace + TimeSpan.FromHours(1);

            Assert.Equal(0, other.CleanupOrphanedRuns(wellPastTheDeadline));
            Assert.Null(other.GetRun(run.Id)!.EndedAt);
            Assert.Null(other.TryClaimRun(jobId, wellPastTheDeadline));
        }
        finally
        {
            command.Kill(entireProcessTree: true);
        }
    }

    // -- EN-F10: the history purge never deletes an unfinished run --

    [Fact]
    public void HistoryPurge_KeepsAnOldUnfinishedRunWithARunningCommand_AndASecondDirectorCannotClaimIt()
    {
        var owner = Director(1001, running: [1001, 2002]);
        var longAgo = DateTime.UtcNow.AddDays(-40);
        var jobId = owner.AddJob(new JobRecord
        {
            Name = "forty-days", Cron = "0 0 31 2 *", Command = "unused", TimeoutSeconds = 60, NextRun = longAgo.AddSeconds(-1)
        });
        var run = owner.TryClaimRun(jobId, longAgo)!;
        using var command = StartLongCommand();
        try
        {
            owner.RecordRunChild(run.Id, command.Id, command.StartTime.ToUniversalTime());

            // The 30-day retention purge runs, then a second, live Director tries the still-due job.
            owner.CleanupOldRuns(retentionDays: 30);
            var second = Director(2002, running: [1001, 2002]);

            Assert.NotNull(second.GetRun(run.Id));
            Assert.Null(second.GetRun(run.Id)!.EndedAt);
            Assert.Null(second.TryClaimRun(jobId, DateTime.UtcNow));
        }
        finally
        {
            command.Kill(entireProcessTree: true);
        }
    }

    // -- EN-F9 (1): no decision from a stale snapshot --

    [Fact]
    public void CleanupReadsBeforeTheStartingMark_TheExecutorMarksInBetween_NoReleaseAndNoOverlap()
    {
        // This process owns the run; after an engine restart the new executor is not executing it,
        // while the old executor is just about to mark and start its command.
        var db = Director(1001, running: [1001, 2002]);
        var jobId = AddDueJob(db, "racing-start");
        var run = db.TryClaimRun(jobId, DateTime.UtcNow)!;

        db.AfterOpenRunsRead = () => db.MarkCommandStarting(run.Id);
        var ended = db.CleanupOrphanedRuns(DateTime.UtcNow, _ => false);
        db.AfterOpenRunsRead = null;

        Assert.Equal(0, ended);
        Assert.Null(db.GetRun(run.Id)!.EndedAt);
        Assert.Null(Director(2002, running: [1001, 2002]).TryClaimRun(jobId, DateTime.UtcNow));
    }

    // -- EN-F9 (2): the executor checks its own writes --

    [Fact]
    public async Task ChildRecordAffectsZeroRows_TheCommandIsKilled()
    {
        var db = Director(1001, running: [1001]);
        var slow = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30";
        AddDueJob(db, "ended-under-us", timeoutSeconds: 120, command: slow);
        var job = db.GetJob("ended-under-us")!;

        int? pid = null;
        DateTime started = default;
        var executor = new JobExecutor(db, JobExecutor.ProcessJobs, (runId, p, s) =>
        {
            pid = p;
            started = s;
            // The run is ended under the executor (its claim released) just before the record lands.
            using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                conn.Open();
                using var end = conn.CreateCommand();
                end.CommandText = "UPDATE runs SET ended_at = @now, exit_code = -1, stderr = 'released elsewhere' WHERE id = @id";
                end.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                end.Parameters.AddWithValue("@id", runId);
                end.ExecuteNonQuery();
            }
            db.RecordRunChild(runId, p, s);
        });
        var run = executor.TryClaim(job)!;
        var execution = executor.ExecuteClaimedAsync(job, run, CancellationToken.None);

        var sw = Stopwatch.StartNew();
        while (pid is null && sw.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(50);
        Assert.NotNull(pid);

        var command = new EngineRunOwner("command", Environment.MachineName, pid.Value, started);
        sw.Restart();
        while (ProcessOwnerLiveness.Probe(command) != OwnerLiveness.Gone && sw.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(100);
        Assert.True(ProcessOwnerLiveness.Probe(command) == OwnerLiveness.Gone,
            "the command kept running after its run was ended under it");

        await execution;
        Assert.Equal("released elsewhere", db.GetRun(run.Id)!.Stderr);
    }

    // -- EN-F9 (3): a migrated open run is never "never started" --

    [Fact]
    public void MigratedOpenOwnerBearingRunWithoutAMark_IsReleasedOnlyAtItsDeadline()
    {
        // A run claimed by an engine from before the starting mark existed: build it, then take the
        // column away again so the next open migrates it exactly as an old database would be.
        var old = Director(4242);
        var jobId = AddDueJob(old, "pre-mark");
        var run = old.TryClaimRun(jobId, DateTime.UtcNow)!;
        SqliteConnection.ClearAllPools();
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var drop = conn.CreateCommand();
            drop.CommandText = "ALTER TABLE runs DROP COLUMN command_starting_at";
            drop.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // A Director opens it (the migration runs); 4242 is gone.
        var other = Director(2002, running: [2002], gone: [4242]);
        var deadline = run.StartedAt + TimeSpan.FromSeconds(60) + EngineDatabase.AbandonedRunGrace;

        Assert.Equal(0, other.CleanupOrphanedRuns(DateTime.UtcNow));
        Assert.Null(other.TryClaimRun(jobId, DateTime.UtcNow));
        Assert.Equal(0, other.CleanupOrphanedRuns(deadline - TimeSpan.FromSeconds(5)));
        Assert.Equal(1, other.CleanupOrphanedRuns(deadline + TimeSpan.FromSeconds(5)));
        Assert.Equal(EngineDatabase.AbandonedRunMessage, other.GetRun(run.Id)!.Stderr);
    }
}
