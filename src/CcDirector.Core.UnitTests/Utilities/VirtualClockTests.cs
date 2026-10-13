using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Utilities;

/// <summary>
/// THE FAKE CLOCK STANDS STILL WHILE THE WORK IT TIMES IS RUNNING. These pin the three rules of
/// <see cref="VirtualClock"/>, each on the exact shape that failed a release gate on 12 October 2026 - and each
/// decided by the code alone: no test here waits, sleeps or reads the real clock, so a busy machine cannot turn one.
/// </summary>
public sealed class VirtualClockTests
{
    /// <summary>A test runner whose every thread is busy: work queued to it is kept and never run.</summary>
    private sealed class BusyRunnerContext : SynchronizationContext
    {
        public int Queued;
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Queued);
        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Queued);
    }

    [Fact]
    public async Task RunAsync_ATickerStartedUnderABusySynchronizationContext_TicksOnTimeBeforeTheClockMovesOn()
    {
        // Arrange: a ticker started the way a test method starts a scripted terminal's spinner - under the test
        // runner's synchronization context - and that context has no thread free to continue it.
        var clock = VirtualTime.NewClock();
        var busy = new BusyRunnerContext();
        var ticks = 0;
        async Task TickAsync()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(30), clock);
                ticks++;
            }
        }
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(busy);
        try
        {
            _ = TickAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }

        // Act: one second of work on the same clock.
        var ticksSeenByTheWork = -1;
        await VirtualTime.RunAsync(clock, async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), clock);
            ticksSeenByTheWork = ticks;
        });

        // Assert: every tick due inside the second happened inside it, on the thread moving the clock; nothing was
        // left for the busy context to get round to.
        Assert.Equal(33, ticksSeenByTheWork);
        Assert.Equal(0, busy.Queued);
    }

    [Fact]
    public async Task RunAsync_NothingWaitsOnTheClock_TheClockDoesNotMove()
    {
        // Arrange: work that waits on something real - here, the test - and never on the clock.
        var clock = VirtualTime.NewClock();
        var startedAt = clock.GetUtcNow();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var run = VirtualTime.RunAsync(clock, async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;
        release.SetResult();
        await run;

        // Assert: time was not what the work waited for, so no time passed.
        Assert.Equal(startedAt, clock.GetUtcNow());
    }

    [Fact]
    public async Task RunAsync_TheWorkFinishesOnAWait_TheClockStopsExactlyWhereThatWaitCameDue()
    {
        // Arrange
        var clock = VirtualTime.NewClock();
        var startedAt = clock.GetUtcNow();

        // Act: a later wait is pending too, and must not be run past.
        var later = Task.Delay(TimeSpan.FromSeconds(5), clock);
        await VirtualTime.RunAsync(clock, () => Task.Delay(TimeSpan.FromMilliseconds(1234), clock));

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(1234), clock.GetUtcNow() - startedAt);
        Assert.False(later.IsCompleted);
    }

    [Fact]
    public async Task RunAsync_TheWorkEndsOnAPlainCompletionSourceWhileATickerIsPending_TheClockStopsAtThatMoment()
    {
        // Arrange: a ticker that is always waiting on the clock, as a spinner is, and work that ends when something
        // else on the clock completes a plain completion source - the way a test waits for an outcome to be written.
        var clock = VirtualTime.NewClock();
        var startedAt = clock.GetUtcNow();
        async Task TickAsync()
        {
            while (true) await Task.Delay(TimeSpan.FromMilliseconds(30), clock);
        }
        var seen = new TaskCompletionSource();
        async Task WriteTheOutcomeAsync()
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), clock);
            seen.SetResult();
        }

        // Act
        await VirtualTime.RunAsync(clock, async () =>
        {
            _ = TickAsync();
            _ = WriteTheOutcomeAsync();
            await seen.Task;
        });

        // Assert: the end of the work ran inline, so the ticker did not carry the clock one tick further.
        Assert.Equal(TimeSpan.FromMilliseconds(100), clock.GetUtcNow() - startedAt);
    }

    [Fact]
    public async Task RunAsync_AWaitStartedByAThreadThatIsNotMovingTheClock_FailsTheRunAndSaysWhere()
    {
        // Arrange: work that leaves the moving thread and comes back to the clock from another one. The other thread
        // is started and joined inside the work, so this is the same on every run.
        var clock = VirtualTime.NewClock();

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => VirtualTime.RunAsync(clock, () =>
        {
            Task? startedElsewhere = null;
            var elsewhere = new Thread(() => startedElsewhere = Task.Delay(TimeSpan.FromSeconds(1), clock));
            elsewhere.Start();
            elsewhere.Join();
            return startedElsewhere!;
        }));

        // Assert
        Assert.Contains("by a thread that was not moving it", ex.Message);
        Assert.Contains(nameof(RunAsync_AWaitStartedByAThreadThatIsNotMovingTheClock_FailsTheRunAndSaysWhere), ex.Message);
    }

    [Fact]
    public async Task RunAsync_ASecondRunOnTheSameClockWhileTheFirstIsUnderWay_IsRefused()
    {
        // Arrange: a first run that is under way and waits on the test.
        var clock = VirtualTime.NewClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = VirtualTime.RunAsync(clock, async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => VirtualTime.RunAsync(clock, () => Task.CompletedTask));
        release.SetResult();
        await first;

        // Assert
        Assert.Contains("already being moved", ex.Message);
    }

    [Fact]
    public void CreateTimer_WhileAnotherWaitIsDue_RunsNothingOnTheThreadThatStartedIt()
    {
        // Arrange: two waits due at the same moment. While the first is being run, another thread starts a third
        // wait on the clock - the shape that, on the library fake clock, handed the second to that other thread
        // and let the advancing thread return with it still running.
        var clock = VirtualTime.NewClock();
        var movingThread = Environment.CurrentManagedThreadId;
        var ranOn = new List<int>();
        using var first = clock.CreateTimer(_ =>
        {
            ranOn.Add(Environment.CurrentManagedThreadId);
            var elsewhere = new Thread(() => clock.CreateTimer(_ => ranOn.Add(Environment.CurrentManagedThreadId), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan));
            elsewhere.Start();
            elsewhere.Join();
        }, null, TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);
        using var second = clock.CreateTimer(_ => ranOn.Add(Environment.CurrentManagedThreadId), null, TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(10));

        // Assert: all three ran, and every one of them on the thread that moved the clock.
        Assert.Equal(new[] { movingThread, movingThread, movingThread }, ranOn);
    }

    [Fact]
    public void Advance_WaitsDueOnTheWay_RunInOrderEachAtTheMomentItCameDue()
    {
        // Arrange: three waits started out of order, and one that repeats.
        var clock = VirtualTime.NewClock();
        var startedAt = clock.GetUtcNow();
        var ran = new List<string>();
        void Note(string name) => ran.Add($"{name}@{(clock.GetUtcNow() - startedAt).TotalMilliseconds:F0}");
        using var c = clock.CreateTimer(_ => Note("c"), null, TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
        using var a = clock.CreateTimer(_ => Note("a"), null, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);
        using var every = clock.CreateTimer(_ => Note("every"), null, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150));
        using var never = clock.CreateTimer(_ => Note("never"), null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(400));

        // Assert: in the order they came due - the earlier-started first when two share a moment - and the clock
        // ends where it was moved to, with the wait beyond it still pending.
        Assert.Equal(new[] { "a@100", "every@150", "c@300", "every@300" }, ran);
        Assert.Equal(TimeSpan.FromMilliseconds(400), clock.GetUtcNow() - startedAt);
    }

    [Fact]
    public void Dispose_APendingWait_NeverRuns()
    {
        // Arrange
        var clock = VirtualTime.NewClock();
        var ran = false;
        var timer = clock.CreateTimer(_ => ran = true, null, TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);

        // Act
        timer.Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));

        // Assert
        Assert.False(ran);
        Assert.False(timer.Change(TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan));
    }
}
