using System.Diagnostics;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Starts a child process on a thread-pool thread, never on the caller's thread (issue #4556).
///
/// WHY. <see cref="Process.Start()"/> is synchronous and, on Windows, .NET serialises it behind a
/// process-wide lock. On the owner's machine one start measured 39 to 1003 ms on its own, and the
/// Director starts git one or two times a second in the background all day. Every async git read
/// runs synchronously up to its first await - which sits AFTER the start - so a git read begun from
/// the desktop's UI thread used to start its process on the UI thread, and session switching spent
/// a mean of 2.05 s there. Starting here moves the start to the pool for every caller at once, so a
/// UI-thread caller cannot block on it whoever it is.
/// </summary>
public static class ProcessStartOffThread
{
    /// <summary>Runs <paramref name="start"/> (normally <c>process.Start</c>) on the thread pool and
    /// returns its answer. An exception the start throws - Win32Exception for a missing executable -
    /// reaches the awaiting caller unchanged.</summary>
    public static Task<bool> StartAsync(Func<bool> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        return Task.Run(start);
    }

    /// <summary>Starts <paramref name="process"/> on the thread pool. See <see cref="StartAsync(Func{bool})"/>.</summary>
    public static Task<bool> StartAsync(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return StartAsync(process.Start);
    }
}
