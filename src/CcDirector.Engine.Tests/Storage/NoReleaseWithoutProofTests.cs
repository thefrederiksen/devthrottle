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
[Collection("RealProcesses")]
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

    private static int AddDueJob(EngineDatabase db, string name, int timeoutSeconds = 60, string command = "unused") =>
        db.AddJob(new JobRecord
        {
            Name = name, Cron = "0 0 31 2 *", Command = command, TimeoutSeconds = timeoutSeconds, NextRun = DateTime.UtcNow.AddSeconds(-1)
        });

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
