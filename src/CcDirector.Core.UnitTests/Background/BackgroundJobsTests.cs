using CcDirector.Core.Background;
using Xunit;

namespace CcDirector.Core.UnitTests.Background;

/// <summary>
/// The scheduler's promises, each as a test: a job runs no faster than its cadence, not while its
/// switch is off, never overlapping itself, on the pool with a parallelism limit, and counted.
/// Time is injected so no test waits on a real minute.
/// </summary>
public sealed class BackgroundJobsTests
{
    private static readonly DateTime T0 = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Clock
    {
        public DateTime Now = T0;
        public Func<DateTime> Read => () => Now;
    }

    /// <summary>
    /// A gate the job body opens as its FIRST act, so a test can wait for the run to have really
    /// begun.
    ///
    /// WHY NOT <c>Snapshot().Running</c>, WHICH IS WHAT TWO OF THESE TESTS USED TO WAIT ON.
    /// <c>Running</c> is set inside <c>Trigger</c>, before the job body is handed to the thread
    /// pool - it means "a run has been started or queued", which is the right thing for it to mean
    /// and the right thing for the Background work page to show. It does NOT mean the body has
    /// begun. Between the two there is a real gap: the run still has to take a slot and be
    /// scheduled.
    ///
    /// On a quiet machine that gap is microseconds and the tests passed. On the loaded build machine
    /// it was wide enough to lose, and two tests failed there while passing on every developer's
    /// machine - one of them, StopAsync_WaitsOutTheRunInFlight, in four continuous integration runs
    /// out of five. Neither was a defect in the product: a run cancelled before its body began is
    /// genuinely not in flight, and StopAsync is right to return at once. The tests were asserting
    /// on the wrong signal.
    /// </summary>
    private sealed class RunEntered
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Called by the job body. Safe to call on every run; only the first is recorded.</summary>
        public void Open() => _entered.TrySetResult();

        /// <summary>Wait for the body to have begun. Ten seconds, the same budget as WaitUntil.</summary>
        public Task Wait() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("waited 10 s for " + what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ATrigger_RunsTheJobOffTheCallersThread_AndCountsIt()
    {
        var clock = new Clock();
        var jobs = new BackgroundJobs(nowUtc: clock.Read);
        var caller = Environment.CurrentManagedThreadId;
        int ranOn = -1;
        var job = jobs.Register(new BackgroundJobSpec("probe", BackgroundJobTier.OnChange, null, "test ends"),
            _ => { ranOn = Environment.CurrentManagedThreadId; return Task.CompletedTask; });

        Assert.True(job.Trigger());
        await WaitUntil(() => job.Snapshot().RunsInLastHour == 1, "the run");

        Assert.NotEqual(caller, ranOn);
        var s = job.Snapshot();
        Assert.Equal(T0, s.LastRunUtc);
        Assert.False(s.Running);
        Assert.Null(s.CeilingPerHour);
    }

    [Fact]
    public async Task AJobWithACadence_IsNotRunFasterThanIt_ByTriggers()
    {
        var clock = new Clock();
        var jobs = new BackgroundJobs(nowUtc: clock.Read);
        var runs = 0;
        var job = jobs.Register(new BackgroundJobSpec("slow", BackgroundJobTier.SlowAndSteady, TimeSpan.FromMinutes(1), "test ends"),
            _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });

        Assert.True(job.Trigger());
        await WaitUntil(() => Volatile.Read(ref runs) == 1, "the first run");

        clock.Now = T0.AddSeconds(30);
        Assert.False(job.Trigger(), "half a minute after a run, a minute-cadence job must not run again");
        Assert.Equal(1, job.Snapshot().SkippedTooSoon);

        clock.Now = T0.AddSeconds(55); // inside the ten percent slack a timer's jitter is allowed
        Assert.True(job.Trigger());
        await WaitUntil(() => Volatile.Read(ref runs) == 2, "the second run");
        Assert.Equal(60, job.Snapshot().CeilingPerHour);
    }

    [Fact]
    public async Task AJobWhoseSwitchIsOff_IsSkippedAndCounted_NeverRun()
    {
        var jobs = new BackgroundJobs();
        var on = false;
        var runs = 0;
        var job = jobs.Register(new BackgroundJobSpec("view", BackgroundJobTier.OnView, null, "the page is hidden", () => on),
            _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });

        Assert.False(job.Trigger());
        Assert.False(job.Trigger());
        await Task.Delay(50);
        Assert.Equal(0, runs);
        Assert.Equal(2, job.Snapshot().SkippedOff);

        on = true;
        Assert.True(job.Trigger());
        await WaitUntil(() => Volatile.Read(ref runs) == 1, "the run once the switch is on");
    }

    [Fact]
    public async Task ATriggerDuringARun_DoesNotOverlap_AndRunsOnceAfterwards()
    {
        var jobs = new BackgroundJobs();
        var release = new TaskCompletionSource();
        var entered = new RunEntered();
        var runs = 0;
        var concurrent = 0;
        var maxConcurrent = 0;
        var job = jobs.Register(new BackgroundJobSpec("busy", BackgroundJobTier.OnChange, null, "test ends"),
            async _ =>
            {
                entered.Open();
                var c = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, c);
                if (Interlocked.Increment(ref runs) == 1) await release.Task;
                Interlocked.Decrement(ref concurrent);
            });

        job.Trigger();
        // Same correction as StopAsync_WaitsOutTheRunInFlight: the count asserted below is the body's,
        // so the wait has to be the body's too - see RunEntered.
        await entered.Wait();
        job.Trigger();
        job.Trigger();
        job.Trigger();
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref runs));

        release.SetResult();
        await WaitUntil(() => Volatile.Read(ref runs) == 2 && !job.Snapshot().Running, "exactly one trailing run");
        await Task.Delay(50);
        Assert.Equal(2, runs);
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task TheRegistry_RunsNoMoreJobsAtOnceThanItsParallelism()
    {
        var jobs = new BackgroundJobs(parallelism: 1);
        var release = new TaskCompletionSource();
        var concurrent = 0;
        var maxConcurrent = 0;
        Func<CancellationToken, Task> work = async _ =>
        {
            var c = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, c);
            await release.Task;
            Interlocked.Decrement(ref concurrent);
        };
        var a = jobs.Register(new BackgroundJobSpec("a", BackgroundJobTier.OnChange, null, "test ends"), work);
        var b = jobs.Register(new BackgroundJobSpec("b", BackgroundJobTier.OnChange, null, "test ends"), work);

        a.Trigger();
        b.Trigger();
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref concurrent));

        release.SetResult();
        await WaitUntil(() => a.Snapshot().RunsInLastHour == 1 && b.Snapshot().RunsInLastHour == 1, "both runs");
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task ATimer_RunsTheJobOnItsCadence_AndTheSnapshotCountsTheHour()
    {
        var jobs = new BackgroundJobs();
        var runs = 0;
        var job = jobs.Register(new BackgroundJobSpec("tick", BackgroundJobTier.SlowAndSteady, TimeSpan.FromMilliseconds(40), "disposed"),
            _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });

        job.StartTimer(TimeSpan.Zero);
        await WaitUntil(() => Volatile.Read(ref runs) >= 3, "three ticks");
        job.Dispose();
        var after = Volatile.Read(ref runs);
        await Task.Delay(150);
        Assert.Equal(after, runs); // disposed: the timer is gone
        Assert.Empty(jobs.Snapshot()); // and so is the row
    }

    [Fact]
    public async Task RunsOlderThanAnHour_FallOutOfTheCount_AndTheCeilingIsVisible()
    {
        var clock = new Clock();
        var jobs = new BackgroundJobs(nowUtc: clock.Read);
        var job = jobs.Register(new BackgroundJobSpec("hourly", BackgroundJobTier.SlowAndSteady, TimeSpan.FromMinutes(30), "test ends"),
            _ => Task.CompletedTask);

        Assert.True(job.Trigger());
        await WaitUntil(() => job.Snapshot().RunsInLastHour == 1, "the first run");
        for (var i = 1; i <= 4; i++)
        {
            clock.Now = T0.AddMinutes(i * 5);
            Assert.False(job.Trigger(), $"trigger {i}: five minutes apart on a thirty-minute cadence is too soon");
        }
        Assert.Equal(1, job.Snapshot().RunsInLastHour);
        Assert.Equal(4, job.Snapshot().SkippedTooSoon);
        Assert.Equal(2, job.Snapshot().CeilingPerHour);
        Assert.False(job.Snapshot().OverCeiling);

        clock.Now = T0.AddHours(2);
        Assert.Equal(0, job.Snapshot().RunsInLastHour);
    }

    [Fact]
    public async Task AJobThatThrows_IsCountedAsAFailure_AndRunsAgainNextTime()
    {
        var jobs = new BackgroundJobs();
        var calls = 0;
        var job = jobs.Register(new BackgroundJobSpec("flaky", BackgroundJobTier.OnChange, null, "test ends"),
            _ => { if (Interlocked.Increment(ref calls) == 1) throw new IOException("locked"); return Task.CompletedTask; });

        job.Trigger();
        await WaitUntil(() => job.Snapshot().Failures == 1, "the failure");
        job.Trigger();
        await WaitUntil(() => job.Snapshot().RunsInLastHour == 2, "the second run");
    }

    [Fact]
    public async Task TwoInstancesOfOneJobKind_ShareOneRow_WithTheirCountsFolded()
    {
        var jobs = new BackgroundJobs();
        var spec = new BackgroundJobSpec("reaper", BackgroundJobTier.SlowAndSteady, TimeSpan.FromMinutes(1), "manager disposed");
        var first = jobs.Register(spec, _ => Task.CompletedTask);
        var second = jobs.Register(spec, _ => Task.CompletedTask);

        first.Trigger();
        second.Trigger();
        await WaitUntil(() => jobs.Snapshot().Single().RunsInLastHour == 2, "both instances to run");

        var row = jobs.Snapshot().Single();
        Assert.Equal("reaper", row.Name);
        Assert.Equal(2, row.Instances);
        Assert.Equal(60, row.CeilingPerHour);
        Assert.False(row.OverCeiling); // the ceiling is per instance

        first.Dispose();
        Assert.Equal(1, jobs.Snapshot().Single().Instances);
        second.Dispose();
        Assert.Empty(jobs.Snapshot());
    }

    /// <summary>
    /// THE GAP THE TWO CORRECTED TESTS USED TO RACE, asserted directly so it cannot be argued away.
    ///
    /// <c>Running</c> is set when a run is STARTED OR QUEUED. A run that is queued behind the
    /// parallelism limit has not begun, and this pins that down with one slot and it already taken:
    /// the second job reports Running while its body has provably never been entered.
    ///
    /// It also pins the behaviour that made <c>StopAsync_WaitsOutTheRunInFlight</c> fail on the build
    /// machine, and shows it is CORRECT rather than a defect: stopping a job whose body never began
    /// returns at once, because there is nothing in flight to wait out. A test that wants to observe
    /// StopAsync waiting must therefore wait for the body, not for the flag.
    /// </summary>
    [Fact]
    public async Task Running_IsTrueForARunStillQueuedBehindTheParallelismLimit_AndStoppingItDoesNotWait()
    {
        var jobs = new BackgroundJobs(parallelism: 1);
        var releaseTheHolder = new TaskCompletionSource();
        var holderEntered = new RunEntered();

        // One job takes the only slot and stays in its body.
        var holder = jobs.Register(new BackgroundJobSpec("holder", BackgroundJobTier.OnChange, null, "test ends"),
            async _ => { holderEntered.Open(); await releaseTheHolder.Task; });
        holder.Trigger();
        await holderEntered.Wait();

        // A second job is triggered. There is no slot, so its body cannot begin.
        var queuedBodyRan = false;
        var queued = jobs.Register(new BackgroundJobSpec("queued", BackgroundJobTier.OnChange, null, "test ends"),
            _ => { queuedBodyRan = true; return Task.CompletedTask; });
        queued.Trigger();

        Assert.True(queued.Snapshot().Running, "Running is set by Trigger, before the body is scheduled");
        Assert.False(Volatile.Read(ref queuedBodyRan), "the body cannot have begun - the only slot is taken");

        // Stopping it returns immediately, and its body never runs. That is correct.
        await queued.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Volatile.Read(ref queuedBodyRan));

        releaseTheHolder.SetResult();
        await holder.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task StopAsync_WaitsOutTheRunInFlight_SoStoppedMeansStopped()
    {
        var jobs = new BackgroundJobs();
        var release = new TaskCompletionSource();
        var entered = new RunEntered();
        var finished = false;
        var job = jobs.Register(new BackgroundJobSpec("winding", BackgroundJobTier.OnChange, null, "stopped"),
            async _ => { entered.Open(); await release.Task; finished = true; });

        job.Trigger();
        // The BODY has begun, not merely the flag - see RunEntered. Waiting on Running instead let
        // StopAsync cancel the run away before it started, and then it was right to return at once.
        await entered.Wait();
        Assert.True(job.Snapshot().Running);

        var stopping = job.StopAsync();
        await Task.Delay(100);
        Assert.False(stopping.IsCompleted, "StopAsync must wait for the run in flight");
        Assert.Empty(jobs.Snapshot()); // but the job has already left the registry

        release.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(finished);
    }

    [Fact]
    public void AJobWithoutACadence_CannotBeArmed()
    {
        var jobs = new BackgroundJobs();
        var job = jobs.Register(new BackgroundJobSpec("event", BackgroundJobTier.OnChange, null, "test ends"), _ => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(() => job.StartTimer());
    }
}
