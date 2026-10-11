using CcDirector.Engine.Events;
using CcDirector.Engine.Scheduling;
using CcDirector.Engine.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Engine.Tests.Scheduling;

public sealed class SchedulerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly EngineDatabase _db;

    public SchedulerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_sched_test_{Guid.NewGuid():N}.db");
        _db = new EngineDatabase(_dbPath);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); }
        catch (IOException) { /* Test temp file cleanup -- best effort */ }
    }

    [Fact]
    public async Task Start_CleansUpOrphanedRuns()
    {
        // An orphaned run (no EndedAt) left by a previous launch whose process is gone.
        var previousLaunch = new EngineRunOwner("director-a", Environment.MachineName, 4242, DateTime.UtcNow.AddHours(-1));
        var before = new EngineDatabase(_dbPath, previousLaunch, _ => OwnerLiveness.Running);
        var jobId = before.AddJob(new JobRecord { Name = "orphan", Cron = "0 0 31 2 *", Command = "echo test" });
        before.CreateRun(new RunRecord { JobId = jobId, JobName = "orphan", StartedAt = DateTime.UtcNow.AddMinutes(-1) });

        var db = new EngineDatabase(_dbPath,
            new EngineRunOwner("director-a", Environment.MachineName, 5151, DateTime.UtcNow),
            owner => owner.ProcessId == 4242 ? OwnerLiveness.Gone : OwnerLiveness.Running);
        var executor = new JobExecutor(db);
        using var scheduler = new Scheduler(db, executor, checkIntervalSeconds: 3600, runRetentionDays: 30);
        scheduler.Start();
        await scheduler.StopAsync(5);

        var runs = _db.ListRuns(jobName: "orphan");
        Assert.Single(runs);
        Assert.NotNull(runs[0].EndedAt);
        Assert.Equal(-1, runs[0].ExitCode);
        Assert.Equal("Interrupted by shutdown", runs[0].Stderr);
    }

    [Fact]
    public async Task Start_InitializesNextRunForJobs()
    {
        _db.AddJob(new JobRecord { Name = "init-next", Cron = "*/5 * * * *", Command = "echo test" });

        var executor = new JobExecutor(_db);
        using var scheduler = new Scheduler(_db, executor, checkIntervalSeconds: 3600, runRetentionDays: 30);
        scheduler.Start();
        await scheduler.StopAsync(5);

        var job = _db.GetJob("init-next");
        Assert.NotNull(job);
        Assert.NotNull(job.NextRun);
    }

    [Fact]
    public async Task Scheduler_RaiseEvents()
    {
        // Create a job due immediately with a fast command
        var job = new JobRecord
        {
            Name = "event-job",
            Cron = "0 0 31 2 *",  // Feb 31 = never (we manually set next_run)
            Command = "echo event-test",
            NextRun = DateTime.UtcNow.AddSeconds(-1)
        };
        _db.AddJob(job);

        var events = new List<EngineEvent>();
        var executor = new JobExecutor(_db);
        using var scheduler = new Scheduler(_db, executor, checkIntervalSeconds: 1, runRetentionDays: 30);
        scheduler.OnEvent += e => events.Add(e);

        // One tick, driven here: it returns once the job it started has completed, so both events are
        // facts by then - no polling against a clock.
        await scheduler.TickAsync();

        Assert.Contains(events, e => e.Type == EngineEventType.JobStarted && e.JobName == "event-job");
        Assert.Contains(events, e => e.Type == EngineEventType.JobCompleted && e.JobName == "event-job");
    }

    /// <summary>
    /// The LOOP, on its clock: Start ticks at once, then waits one interval on the injected clock before the
    /// next tick. Moving that clock is what wakes it - nothing else does - and stopping ends the wait at once
    /// rather than after the interval. The one real wait here is for the command process the first tick
    /// started; the loop itself is never waited on.
    /// </summary>
    [Fact]
    public async Task Loop_TicksWhenItsClockMoves_StartsADueJobOnce_AndStopEndsTheWait()
    {
        var clock = new FakeTimeProvider();
        var started = new List<string>();
        var firstRunEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = _db.AddJob(new JobRecord
        {
            Name = "loop-job", Cron = "0 0 31 2 *", Command = "echo loop", TimeoutSeconds = 60,
            NextRun = DateTime.UtcNow.AddSeconds(-1)
        });
        using var scheduler = new Scheduler(_db, new JobExecutor(_db), checkIntervalSeconds: 60, runRetentionDays: 30, clock);
        scheduler.OnEvent += e =>
        {
            if (e.Type == EngineEventType.JobStarted) lock (started) started.Add(e.JobName!);
            if (e.Type == EngineEventType.JobCompleted) firstRunEnded.TrySetResult();
        };

        scheduler.Start();
        try
        {
            // The first tick is Start's own; the command is a real process, so this is the one real wait.
            await firstRunEnded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitForTicks(scheduler, 1);
            Assert.Equal(new[] { "loop-job" }, started);

            // Due again - but the loop is asleep on its clock, and nothing but the clock wakes it.
            _db.UpdateNextRun(jobId, DateTime.UtcNow.AddSeconds(-1));
            Assert.Equal(1, scheduler.TicksRun);

            // Short of the interval: still asleep.
            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.Equal(1, scheduler.TicksRun);

            // The interval passes: the loop wakes, ticks, and the due occurrence runs exactly once more.
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitForTicks(scheduler, 2);
            Assert.Equal(new[] { "loop-job", "loop-job" }, started);

            // Another interval while the occurrence is NOT due: a tick, and nothing started by it.
            clock.Advance(TimeSpan.FromSeconds(60));
            await WaitForTicks(scheduler, 3);
            Assert.Equal(2, started.Count);
        }
        finally
        {
            // Stop ends the clock wait at once: the loop task itself has ended, so the stop did not sit out
            // its shutdown allowance waiting for a loop that never woke.
            await scheduler.StopAsync(shutdownTimeoutSeconds: 5);
            Assert.True(scheduler.LoopTask!.IsCompleted, "stopping did not end the loop's wait on its clock");
        }
    }

    /// <summary>The loop runs its tick on a thread-pool thread after the clock wakes it; this waits for that
    /// tick to have run, bounded, so an assertion about what the tick did is made after it did it.</summary>
    private static async Task WaitForTicks(Scheduler scheduler, int atLeast)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (scheduler.TicksRun < atLeast)
        {
            Assert.True(DateTime.UtcNow < deadline, $"the loop never ran tick {atLeast}; it ran {scheduler.TicksRun}");
            await Task.Delay(10);
        }
    }
}
