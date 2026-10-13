namespace CcDirector.TestInfrastructure;

/// <summary>
/// Drives a send on a fake clock. The send path waits on a <see cref="TimeProvider"/> - the echo deadline, the paste
/// taken in, the quiet before a clear, the arrival window, the composer release watch - and so does the scripted
/// terminal it is sent to. A test hands both the same <see cref="VirtualClock"/> and runs the send through
/// <see cref="RunAsync"/>, which moves the clock from one pending wait to the next until the send is done. A
/// two-second allowance then costs microseconds, and a test that used to wait out a sixty-second window decides
/// itself when the window ends.
///
/// THE CLOCK NEVER MOVES WHILE THE WORK IT TIMES IS STILL RUNNING - <see cref="VirtualClock"/> says how. An earlier
/// version of this class advanced a library fake clock ten milliseconds at a time in a loop, as fast as the loop
/// would turn, and was only right while every continuation in the path happened to run inside that loop's own
/// advance. Under a full release gate they did not, fake seconds passed while one file write was in flight, and
/// tests on a fake clock failed for being on a busy machine (12 October 2026: "never started a turn within 8
/// beats", and a ten-second bound missed by 68.7 seconds).
///
/// A send that never finishes is reported, not waited for: when nothing in the path waits on the clock and the work
/// has still not finished after <see cref="RealLimit"/>, it waits on something real or is stuck, and the exception
/// says so.
///
/// Linked into the test projects that drive the send path (Core.UnitTests, Core.Tests, Gateway.UnitTests) by name,
/// together with VirtualClock.cs, not through the shared item group in Directory.Build.props.
/// </summary>
public static class VirtualTime
{
    /// <summary>How long, in real time, a run may sit with nothing waiting on the clock before it is called stuck.
    /// A watchdog only: it turns a hang into a named failure and never decides a pass.</summary>
    public static readonly TimeSpan RealLimit = TimeSpan.FromSeconds(60);

    /// <summary>How far the fake clock may be moved before the run is called stuck.</summary>
    public static readonly TimeSpan VirtualLimit = TimeSpan.FromHours(1);

    /// <summary>A small step for a test that moves the clock by hand until something happens.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    /// <summary>A fake clock that starts NOW, so a timestamp it hands out compares sensibly with a file time.</summary>
    public static VirtualClock NewClock() => new(DateTimeOffset.UtcNow);

    /// <summary>Start <paramref name="start"/> and move <paramref name="clock"/> until it completes; return its result or throw its failure.</summary>
    public static async Task<T> RunAsync<T>(VirtualClock clock, Func<Task<T>> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        Task<T>? task = null;
        await RunCoreAsync(clock, () => task = start());
        return await task!;
    }

    /// <summary>
    /// Start <paramref name="start"/> and move <paramref name="clock"/> until it completes; throw its failure.
    ///
    /// The work is started HERE, by the thread that moves the clock and with no synchronization context, and not
    /// by the caller: every wait in it that comes due is then continued by the moving thread before the clock
    /// moves again.
    /// </summary>
    public static Task RunAsync(VirtualClock clock, Func<Task> start) => RunCoreAsync(clock, start);

    private static Task RunCoreAsync(VirtualClock clock, Func<Task> start)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(start);
        return Task.Run(async () =>
        {
            using var run = clock.BeginRun();
            var task = clock.Start(start);
            var startedAt = clock.GetUtcNow();
            while (!task.IsCompleted)
            {
                clock.ThrowIfAWaitStartedOffTheMovingThread();
                if (clock.GetUtcNow() - startedAt > VirtualLimit)
                    throw new TimeoutException(
                        $"the task did not finish within {VirtualLimit.TotalMinutes:F0} minutes of virtual time: no wait in the path is that long, so it is stuck");
                // Taken before the look, so a wait started between the look and the await below is not missed.
                var waitStarted = clock.NextWaitStarted;
                if (clock.RunNext()) continue;
                // NOTHING WAITS ON THE CLOCK. The work is running on another thread, or waits on something real.
                // Time is not what it is waiting for, so the clock stays where it is until the work comes back to
                // it or finishes.
                try
                {
                    await Task.WhenAny(task, waitStarted).WaitAsync(RealLimit);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException(
                        $"the task did not finish, and nothing in its path waited on the fake clock, for {RealLimit.TotalSeconds:F0}s of real time " +
                        $"({(clock.GetUtcNow() - startedAt).TotalSeconds:F1}s into the run on the fake clock): it waits on something real - the real clock, " +
                        "an event that never comes - or it is stuck");
                }
            }
            clock.ThrowIfAWaitStartedOffTheMovingThread(task.IsFaulted ? task.Exception!.GetBaseException() : null);
            await task;
        });
    }
}
