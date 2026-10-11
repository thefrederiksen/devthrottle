using Microsoft.Extensions.Time.Testing;

namespace CcDirector.TestInfrastructure;

/// <summary>
/// Drives a send on a fake clock. The send path waits on a <see cref="TimeProvider"/> - the echo deadline, the paste
/// taken in, the quiet before a clear, the arrival window, the composer release watch - and so does the scripted
/// terminal it is sent to. A test hands both the same fake clock and runs the send through <see cref="RunAsync"/>,
/// which advances the clock in small steps until the send is done. A two-second allowance then costs microseconds,
/// and a test that used to wait out a sixty-second window decides itself when the window ends.
///
/// A send that never finishes is reported, not waited for: if the real clock passes <see cref="RealLimit"/> while
/// the fake one is advanced, something in the path still waits on real time, and the exception says so.
///
/// Linked into the test projects that drive the send path (Core.UnitTests, Core.Tests) by name, not through the
/// shared item group in Directory.Build.props, because it needs the fake-clock package those projects reference.
/// </summary>
public static class VirtualTime
{
    /// <summary>How long, in real time, a virtual run may take before it is called stuck.</summary>
    public static readonly TimeSpan RealLimit = TimeSpan.FromSeconds(60);

    /// <summary>How far the fake clock may be advanced before the run is called stuck.</summary>
    public static readonly TimeSpan VirtualLimit = TimeSpan.FromHours(1);

    /// <summary>The step the clock is advanced by between looks at the task.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    /// <summary>A fake clock that starts NOW, so a timestamp it hands out compares sensibly with a file time.</summary>
    public static FakeTimeProvider NewClock() => new(DateTimeOffset.UtcNow);

    /// <summary>Start <paramref name="start"/> and advance <paramref name="clock"/> until it completes; return its result or throw its failure.</summary>
    public static async Task<T> RunAsync<T>(FakeTimeProvider clock, Func<Task<T>> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        Task<T>? task = null;
        await RunCoreAsync(clock, () => task = start());
        return await task!;
    }

    /// <summary>
    /// Start <paramref name="start"/> and advance <paramref name="clock"/> until it completes; throw its failure.
    ///
    /// The work is started HERE, on a thread-pool thread with no synchronization context, and not by the caller. A
    /// test method runs under xUnit's context, and an await inside the send path would capture it, so every
    /// continuation after a wait would be queued behind the other tests' work while this loop kept advancing the
    /// clock: the send saw its two-second windows end before its poll had run, one run in five. With no context, a
    /// wait that comes due runs its continuation inline, inside <see cref="FakeTimeProvider.Advance"/>, before the
    /// next step - the fake clock cannot move while the send is doing something.
    /// </summary>
    public static Task RunAsync(FakeTimeProvider clock, Func<Task> start) => RunCoreAsync(clock, start);

    private static Task RunCoreAsync(FakeTimeProvider clock, Func<Task> start)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(start);
        return Task.Run(async () =>
        {
            var task = start();
            var startedAt = clock.GetUtcNow();
            var realStart = Environment.TickCount64;
            while (!task.IsCompleted)
            {
                if (Environment.TickCount64 - realStart > RealLimit.TotalMilliseconds)
                    throw new TimeoutException(
                        $"the task did not finish within {RealLimit.TotalSeconds:F0}s of real time while the fake clock was advanced " +
                        $"{(clock.GetUtcNow() - startedAt).TotalSeconds:F0}s: something in the path still waits on the real clock, or is stuck");
                if (clock.GetUtcNow() - startedAt > VirtualLimit)
                    throw new TimeoutException(
                        $"the task did not finish within {VirtualLimit.TotalMinutes:F0} minutes of virtual time: no wait in the path is that long, so it is stuck");
                clock.Advance(Step);
                await Task.Yield();
            }
            await task;
        });
    }
}
