using CcDirector.Engine.Scheduling;
using CcDirector.Engine.Storage;
using Xunit;

namespace CcDirector.Engine.Tests.Scheduling;

/// <summary>
/// Review round 1 of the shared engine.db fix (devthrottle_internal#2311). EN-F1: a claim must cover the
/// COMMAND, not only the Director - a command that outlives a cancellation or its Director keeps the
/// claim. EN-F2: a run is judged by the timeout it was claimed with, a live owner is never aged out,
/// and a revoked owner's late completion changes nothing. These use only API that existed at the
/// reviewed head 3823c55c5, so this file runs red against it.
/// </summary>
[Collection("RealProcesses")]
public sealed class ClaimCoversTheCommandTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _logPath;

    public ClaimCoversTheCommandTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_claimcmd_test_{id}.db");
        _logPath = Path.Combine(Path.GetTempPath(), $"engine_claimcmd_log_{id}.txt");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _logPath })
        {
            try { File.Delete(path); }
            catch (IOException) { /* Test temp file cleanup -- best effort; a surviving command may hold it */ }
        }
    }

    private static EngineRunOwner Owner(int pid) =>
        new($"director-{pid}", Environment.MachineName, pid, new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc));

    /// <summary>The listed fake Director ids answer as given; any other id (a real command process) is probed for real.</summary>
    private EngineDatabase Director(int pid, int[]? running = null, int[]? gone = null) =>
        new(_dbPath, Owner(pid), owner =>
            running?.Contains(owner.ProcessId) == true ? OwnerLiveness.Running
            : gone?.Contains(owner.ProcessId) == true ? OwnerLiveness.Gone
            : ProcessOwnerLiveness.Probe(owner));

    // -- EN-F2 --

    [Fact]
    public void JobEditedToAShorterTimeoutMidRun_UndecidableOwner_IsJudgedByTheTimeoutItWasClaimedWith()
    {
        var owner = Director(1001);
        var jobId = owner.AddJob(new JobRecord
        {
            Name = "edited-job", Cron = "0 0 31 2 *", Command = "echo x", TimeoutSeconds = 3600,
            NextRun = DateTime.UtcNow.AddSeconds(-1)
        });
        var started = DateTime.UtcNow;
        var run = owner.TryClaimRun(jobId, started)!;

        var job = owner.GetJob("edited-job")!;
        job.TimeoutSeconds = 1;
        owner.UpdateJob(job);

        var other = new EngineDatabase(_dbPath, Owner(2002), _ => OwnerLiveness.Unknown);

        Assert.Equal(0, other.CleanupOrphanedRuns(started.AddSeconds(400)));
        Assert.Null(other.GetRun(run.Id)!.EndedAt);
        Assert.Equal(1, other.CleanupOrphanedRuns(started.AddSeconds(3600) + EngineDatabase.AbandonedRunGrace + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void LiveOwner_IsNeverAgedOutByAnotherDirector()
    {
        var owner = Director(1001);
        var jobId = owner.AddJob(new JobRecord
        {
            Name = "live-job", Cron = "0 0 31 2 *", Command = "echo x", TimeoutSeconds = 60,
            NextRun = DateTime.UtcNow.AddSeconds(-1)
        });
        var run = owner.TryClaimRun(jobId, DateTime.UtcNow)!;

        var other = new EngineDatabase(_dbPath, Owner(2002), _ => OwnerLiveness.Running);

        Assert.Equal(0, other.CleanupOrphanedRuns(DateTime.UtcNow.AddDays(30)));
        Assert.Null(other.GetRun(run.Id)!.EndedAt);
    }

    [Fact]
    public void RevokedOwnersLateCompletion_ChangesNeitherTheVerdictNorTheSchedule()
    {
        var owner = Director(4242);
        var due = DateTime.UtcNow.AddSeconds(-1);
        var jobId = owner.AddJob(new JobRecord
        {
            Name = "revoked-job", Cron = "0 0 31 2 *", Command = "echo x", TimeoutSeconds = 60, NextRun = due
        });
        var run = owner.TryClaimRun(jobId, DateTime.UtcNow)!;

        // Another Director proves 4242 gone and releases the claim.
        var other = Director(2002, running: [2002], gone: [4242]);
        Assert.Equal(1, other.CleanupOrphanedRuns());

        // 4242 was not gone after all, and reports its result late.
        run.EndedAt = DateTime.UtcNow;
        run.ExitCode = 0;
        run.Stdout = "late";
        owner.CompleteRun(run, DateTime.UtcNow.AddHours(1));

        var loaded = other.GetRun(run.Id)!;
        Assert.Equal(-1, loaded.ExitCode);
        Assert.Equal(EngineDatabase.InterruptedRunMessage, loaded.Stderr);
        Assert.Null(loaded.Stdout);
        Assert.Equal(due, other.GetJob("revoked-job")!.NextRun!.Value.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }
}
