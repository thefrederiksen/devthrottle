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

    /// <summary>
    /// ISSUE #4852. A run is counted at the time it STARTED, not the time it ended.
    ///
    /// <para>This is what makes the <c>ceiling + 1</c> tolerance in
    /// <see cref="BackgroundJobSnapshot.OverCeiling"/> provably tight instead of nearly tight. A cadence
    /// bounds the times a job FIRES, so a trailing hour holds at most <c>ceiling + 1</c> fires. An end
    /// time is a fire shifted by a duration that varies run to run, so a window over end times can hold
    /// one post more than any window over fires - which would make the bound wrong exactly when a job's
    /// runs last nearly a whole cadence. Counting starts removes that case instead of documenting it.</para>
    ///
    /// <para>Pinned with a run that takes half an hour of the injected clock: its post belongs at the
    /// start of that half hour, so an hour later it has left the window. Were the end time enqueued, the
    /// post would still be inside it.</para>
    /// </summary>
    [Fact]
    public async Task ARunIsCountedAtTheTimeItStarted_NotTheTimeItEnded()
    {
        var now = T0;
        var jobs = new BackgroundJobs(parallelism: 1, nowUtc: () => now);
        var job = jobs.Register(
            new BackgroundJobSpec("a long run", BackgroundJobTier.SlowAndSteady, TimeSpan.FromHours(1), "test ends"),
            _ => { now = T0.AddMinutes(30); return Task.CompletedTask; });   // the run takes half an hour

        await RunOnceAndSettle(job);

        // Exactly an hour after it STARTED. The half-open window excludes a post at the horizon, so a
        // run counted at its start has left; a run counted at its end (T0 + 30 minutes) has not.
        now = T0.AddHours(1);
        Assert.Equal(0, job.Snapshot().RunsInLastHour);
    }

    [Fact]
    public void AJobWithoutACadence_CannotBeArmed()
    {
        var jobs = new BackgroundJobs();
        var job = jobs.Register(new BackgroundJobSpec("event", BackgroundJobTier.OnChange, null, "test ends"), _ => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(() => job.StartTimer());
    }

    /// <summary>
    /// Trigger one run and await it to COMPLETION - body, accounting and all - deterministically.
    ///
    /// <para>No sleep and no clock read: the run's timestamp is enqueued and the window trimmed in
    /// <c>RunAsync</c>'s <c>finally</c>, so the test awaits the run task itself rather than polling a
    /// snapshot behind a timeout. The injected clock must not be advanced until this has returned,
    /// because the run is stamped with the time it ENDS.</para>
    ///
    /// <para>THE IDLE ASSERTION IS LOAD-BEARING, not tidiness. <c>InFlightRunForTest</c> is whatever run
    /// is in flight, and a <c>Trigger</c> that arrives while one is running does not start another - it
    /// sets the ask-again flag. Asserting the job is idle FIRST makes the task we then read necessarily
    /// the run this call started, and makes a future test that triggers into a run in flight fail here
    /// rather than silently await the wrong task.</para>
    ///
    /// <para>IT IS SUFFICIENT BECAUSE NO TEST IN THIS FILE ARMS THE TIMER. With no timer running,
    /// nothing but this helper can change <c>Running</c> between the snapshot and the trigger. A test
    /// that called <c>StartTimer</c> would break that: a tick landing in the gap starts a run, this
    /// trigger would then only set ask-again, and the task read would be the timer's run. A test that
    /// needs a timer needs a different helper, not this one.</para>
    /// </summary>
    private static async Task RunOnceAndSettle(BackgroundJob job)
    {
        Assert.False(job.Snapshot().Running, "the job must be idle before this helper triggers, or the task it awaits is the previous run");
        Assert.True(job.Trigger(), "the trigger was skipped - the cadence guard or the off switch refused it");
        var run = job.InFlightRunForTest;
        Assert.NotNull(run);
        await run;
        Assert.False(job.Snapshot().Running, "the run has completed, so the job is idle and its run is accounted for");
    }

    private static BackgroundJob OnAnHourlyCadence(BackgroundJobs jobs, string name) =>
        jobs.Register(
            new BackgroundJobSpec(name, BackgroundJobTier.SlowAndSteady, TimeSpan.FromHours(1), "test ends"),
            _ => Task.CompletedTask);

    /// <summary>
    /// ISSUE #4852, THE CASE THE MACHINE ACTUALLY PRODUCES. A job whose runs land a shade under its
    /// cadence apart is not over its ceiling.
    ///
    /// <para>This is the test that matters, and the first fix for #4852 did not pass it. That fix only
    /// made the trailing-hour window half-open, which changes the count ONLY when a run's timestamp
    /// equals the horizon to the tick - something a clock read from <c>DateTime.UtcNow</c> with
    /// millisecond timer jitter effectively never does. What really decides whether the hour holds
    /// <c>ceiling</c> or <c>ceiling + 1</c> runs is whether the span of the last <c>ceiling</c>
    /// intervals measures shorter or longer than an hour on the wall clock, which is set by timer
    /// lateness and by the wall clock's rate against the monotonic clock the timer schedules on. On a
    /// machine whose wall clock runs slightly slow, every job on a fixed cadence shows
    /// <c>ceiling + 1</c> for ever - which is what put 19,590 lines into one day's log and marked
    /// sixteen healthy jobs red.</para>
    ///
    /// <para>One hour of cadence and two runs an hour-minus-a-millisecond apart is the smallest case
    /// that reproduces it: one interval, two fenceposts, and the older run still inside the window
    /// under EITHER trim.</para>
    /// </summary>
    [Fact]
    public async Task AJobWhoseRunsLandJustInsideItsCadence_IsNotOverItsCeiling()
    {
        var now = T0;
        var jobs = new BackgroundJobs(parallelism: 1, nowUtc: () => now);
        var job = OnAnHourlyCadence(jobs, "slightly-slow-clock reconciliation");

        await RunOnceAndSettle(job);
        now = T0.AddHours(1).AddMilliseconds(-1);
        await RunOnceAndSettle(job);

        var snapshot = job.Snapshot();

        // Both runs are inside the hour - this is the fencepost, and it is not a runaway.
        Assert.Equal(2, snapshot.RunsInLastHour);
        Assert.Equal(1, snapshot.CeilingPerHour);
        Assert.False(
            snapshot.OverCeiling,
            $"a job running at its own cadence is not over its ceiling, but it reported " +
            $"{snapshot.RunsInLastHour} runs against an allowance of {snapshot.CeilingPerHour}");
    }

    /// <summary>
    /// ISSUE #4852, the ideal-clock case. With the window half-open, a job running at EXACTLY its
    /// cadence shows exactly its allowance, so the number the Background work page displays means what
    /// the ceiling means. This is the tidy-up rather than the fix; the test above is the fix.
    /// </summary>
    [Fact]
    public async Task AJobRunningExactlyAtItsCadence_ShowsExactlyItsAllowance()
    {
        var now = T0;
        var jobs = new BackgroundJobs(parallelism: 1, nowUtc: () => now);
        var job = OnAnHourlyCadence(jobs, "ideal-clock reconciliation");

        await RunOnceAndSettle(job);
        now = T0.AddHours(1);
        await RunOnceAndSettle(job);

        var snapshot = job.Snapshot();

        // The run from exactly an hour ago has left the half-open window.
        Assert.Equal(1, snapshot.RunsInLastHour);
        Assert.False(snapshot.OverCeiling);
    }

    /// <summary>
    /// ISSUE #4852, THE OTHER SIDE OF THE BOUNDARY: the alarm must survive the repair. Asserted on
    /// <see cref="BackgroundJobSnapshot"/> directly because the scheduler cannot be driven into a real
    /// runaway through its public surface - <c>Trigger</c> refuses a non-timer run inside 90% of the
    /// cadence, so the most it will produce is the fencepost itself. The snapshot is the thing the
    /// Background work page reads, so this is the boundary that matters.
    /// </summary>
    [Theory]
    // allowance, runs in the hour, instances, is it a runaway
    [InlineData(1800, 1800, 1, false)]   // exactly on cadence
    [InlineData(1800, 1801, 1, false)]   // the fencepost - the whole defect
    [InlineData(1800, 1802, 1, true)]    // one more than the cadence can explain
    [InlineData(1800, 3600, 1, true)]    // twice the rate: unmistakable
    [InlineData(1, 2, 1, false)]         // the hourly reconciliation job from the issue's table
    [InlineData(1, 3, 1, true)]
    [InlineData(60, 183, 3, false)]      // three instances, each at its own fencepost: 3 * 61
    [InlineData(60, 184, 3, true)]       // one more than every instance can explain
    public void OverCeiling_ToleratesTheFencepost_AndStillReportsARealRunaway(
        int ceiling, int runsInLastHour, int instances, bool expectedOverCeiling)
    {
        var cadence = TimeSpan.FromHours(1) / ceiling;
        var snapshot = new BackgroundJobSnapshot(
            "a job", BackgroundJobTier.SlowAndSteady, cadence, ceiling,
            LastRunUtc: T0, LastDuration: TimeSpan.Zero,
            RunsInLastHour: runsInLastHour,
            SkippedOff: 0, SkippedTooSoon: 0, Failures: 0, Running: false, Instances: instances);

        Assert.Equal(expectedOverCeiling, snapshot.OverCeiling);
    }

}
