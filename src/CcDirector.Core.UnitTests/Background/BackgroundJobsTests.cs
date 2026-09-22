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
        var runs = 0;
        var concurrent = 0;
        var maxConcurrent = 0;
        var job = jobs.Register(new BackgroundJobSpec("busy", BackgroundJobTier.OnChange, null, "test ends"),
            async _ =>
            {
                var c = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, c);
                if (Interlocked.Increment(ref runs) == 1) await release.Task;
                Interlocked.Decrement(ref concurrent);
            });

        job.Trigger();
        await WaitUntil(() => job.Snapshot().Running, "the first run to start");
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

    [Fact]
    public async Task StopAsync_WaitsOutTheRunInFlight_SoStoppedMeansStopped()
    {
        var jobs = new BackgroundJobs();
        var release = new TaskCompletionSource();
        var finished = false;
        var job = jobs.Register(new BackgroundJobSpec("winding", BackgroundJobTier.OnChange, null, "stopped"),
            async _ => { await release.Task; finished = true; });

        job.Trigger();
        await WaitUntil(() => job.Snapshot().Running, "the run to start");

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
