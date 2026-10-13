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
    public void AJobWithoutACadence_CannotBeArmed()
    {
        var jobs = new BackgroundJobs();
        var job = jobs.Register(new BackgroundJobSpec("event", BackgroundJobTier.OnChange, null, "test ends"), _ => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(() => job.StartTimer());
    }
}
