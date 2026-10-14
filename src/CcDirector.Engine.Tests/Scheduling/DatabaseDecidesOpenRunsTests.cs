using System.Diagnostics;
using CcDirector.Engine.Jobs;
using CcDirector.Engine.Scheduling;
using CcDirector.Engine.Storage;
using Xunit;

namespace CcDirector.Engine.Tests.Scheduling;

/// <summary>
/// Review round 3 of the shared engine.db fix (devthrottle_internal#2311). Every decision about an open
/// run comes from the DATABASE: an engine restarted in a live process still releases an unconfirmed stop
/// when its command exits (EN-F7), and a command whose identity was never recorded is released only at
/// the run's stored deadline plus the margin - never earlier, never held forever (EN-F6, EN-F7). These use
/// only API that existed at the reviewed head b369273b4, so this file runs red against it.
/// </summary>
[Collection("RealProcesses")]
public sealed class DatabaseDecidesOpenRunsTests : IDisposable
{
    private readonly string _dbPath;

    public DatabaseDecidesOpenRunsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_dbdecides_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); }
            catch (IOException) { /* Test temp file cleanup -- best effort */ }
        }
    }

    private static EngineRunOwner Owner(int pid) =>
        new($"director-{pid}", Environment.MachineName, pid, new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc));

    /// <summary>Listed fake Director ids answer as given; any other id (a real command) is probed for real.</summary>
    private EngineDatabase Director(int pid, int[]? running = null, int[]? gone = null) =>
        new(_dbPath, Owner(pid), owner =>
            running?.Contains(owner.ProcessId) == true ? OwnerLiveness.Running
            : gone?.Contains(owner.ProcessId) == true ? OwnerLiveness.Gone
            : ProcessOwnerLiveness.Probe(owner));

    private sealed class Jobs
    {
        public int Quick;
        public Func<Action<int, DateTime>, Task<JobResult>>? First;

        public IJob Create(JobRecord job, int timeoutSeconds, Action<int, DateTime> onStarted) => new Job(this, onStarted);

        private sealed class Job(Jobs owner, Action<int, DateTime> onStarted) : IJob
        {
            public string Name => "fake";

            public Task<JobResult> ExecuteAsync(CancellationToken cancellationToken)
            {
                var first = Interlocked.Exchange(ref owner.First, null);
                if (first is not null)
                    return first(onStarted);
                Interlocked.Increment(ref owner.Quick);
                return Task.FromResult(new JobResult(true, "quick", ExitCode: 0));
            }
        }
    }

    // -- EN-F6 / EN-F7: a command whose identity was never recorded --

    private async Task<RunRecord> ClaimAndLoseTheCommandIdentity(EngineDatabase owner, bool startTimeReadable)
    {
        owner.AddJob(new JobRecord
        {
            Name = "lost-identity", Cron = "0 0 31 2 *", Command = "unused", TimeoutSeconds = 60, NextRun = DateTime.UtcNow.AddSeconds(-1)
        });
        var jobs = new Jobs();
        jobs.First = onStarted =>
        {
            // The command started; recording it failed (or its start time could not be read), and the
            // kill could not be confirmed.
            if (!startTimeReadable)
                throw new CommandNotRecordedException("lost-identity", 424242, null, processStopped: false,
                    new InvalidOperationException("simulated: the start time could not be read"));
            try
            {
                onStarted(424242, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                throw new CommandNotRecordedException("lost-identity", 424242, DateTime.UtcNow, processStopped: false, ex);
            }
            throw new InvalidOperationException("the record was expected to fail");
        };
        var executor = new JobExecutor(owner, jobs.Create, (_, _, _) => throw new IOException("simulated: the database write failed"));
        var job = owner.GetJob("lost-identity")!;
        var run = executor.TryClaim(job)!;
        await executor.ExecuteClaimedAsync(job, run, CancellationToken.None);
        Assert.Null(owner.GetRun(run.Id)!.EndedAt);
        return run;
    }

    private void AssertHeldUntilTheDeadlineThenReleased(RunRecord run)
    {
        // The owning Director is now gone; another Director judges the run from the database.
        var other = Director(2002, running: [2002], gone: [4242]);
        var deadline = run.StartedAt + TimeSpan.FromSeconds(60) + EngineDatabase.AbandonedRunGrace;

        Assert.Equal(0, other.CleanupOrphanedRuns(DateTime.UtcNow));
        Assert.Null(other.TryClaimRun(run.JobId, DateTime.UtcNow));

        Assert.Equal(0, other.CleanupOrphanedRuns(deadline - TimeSpan.FromSeconds(5)));
        Assert.Null(other.TryClaimRun(run.JobId, deadline - TimeSpan.FromSeconds(5)));

        Assert.Equal(1, other.CleanupOrphanedRuns(deadline + TimeSpan.FromSeconds(5)));
        Assert.Equal(EngineDatabase.AbandonedRunMessage, other.GetRun(run.Id)!.Stderr);
        Assert.NotNull(other.TryClaimRun(run.JobId, deadline + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RecordFailed_OwnerThenGone_NoSecondStartBeforeTheDeadlinePlusMargin_AStartAfterIt()
    {
        var run = await ClaimAndLoseTheCommandIdentity(Director(4242), startTimeReadable: true);
        AssertHeldUntilTheDeadlineThenReleased(run);
    }

    [Fact]
    public async Task StartTimeUnreadable_OwnerThenGone_NoSecondStartBeforeTheDeadlinePlusMargin_AStartAfterIt()
    {
        var run = await ClaimAndLoseTheCommandIdentity(Director(4242), startTimeReadable: false);
        AssertHeldUntilTheDeadlineThenReleased(run);
    }

    [Fact]
    public async Task StartTimeUnreadable_OwnerStillLive_TheOwnerItselfReleasesItAtTheDeadlinePlusMargin()
    {
        var owner = Director(4242, running: [4242]);
        var run = await ClaimAndLoseTheCommandIdentity(owner, startTimeReadable: false);
        var deadline = run.StartedAt + TimeSpan.FromSeconds(60) + EngineDatabase.AbandonedRunGrace;

        // The owner is not executing it any more; it judges its own row, never earlier than the deadline.
        Assert.Equal(0, owner.CleanupOrphanedRuns(deadline - TimeSpan.FromSeconds(5), _ => false));
        Assert.Equal(1, owner.CleanupOrphanedRuns(deadline + TimeSpan.FromSeconds(5), _ => false));
    }

    [Fact]
    public void OwnerGone_CommandNeverStarted_IsReleasedAtOnce()
    {
        // Claimed, but the Director died before marking the command as starting: nothing can be running.
        var dead = Director(4242);
        var jobId = dead.AddJob(new JobRecord
        {
            Name = "never-started", Cron = "0 0 31 2 *", Command = "unused", TimeoutSeconds = 60, NextRun = DateTime.UtcNow.AddSeconds(-1)
        });
        var run = dead.TryClaimRun(jobId, DateTime.UtcNow)!;

        var other = Director(2002, running: [2002], gone: [4242]);
        Assert.Equal(1, other.CleanupOrphanedRuns(DateTime.UtcNow));
        Assert.Equal(EngineDatabase.InterruptedRunMessage, other.GetRun(run.Id)!.Stderr);
    }
}
