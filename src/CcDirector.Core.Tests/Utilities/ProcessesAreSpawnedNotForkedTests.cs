using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

/// <summary>
/// THE GATE FOR devthrottle#4577. Proves that starting a child process through
/// <see cref="ProcessRunner"/> on macOS and Linux does not call <c>fork()</c>.
///
/// WHY A FORK COUNTER AND NOT A STRESS TEST. The defect was a race. <c>fork()</c> duplicates a
/// multi-threaded address space, libmalloc's parent-side recovery handler then released an
/// <c>os_unfair_lock</c> it did not own, and macOS aborted the entire Director. It took more than
/// 51,000 forks across seventeen hours, on a machine under memory pressure, to land once. A test
/// that tried to reproduce THAT would pass on a good day for the wrong reason, and its passing would
/// mean nothing, because the defect's signature is a crash that usually does not happen. So this
/// test does not hunt the crash. It asserts the one condition the crash cannot occur without, and
/// that condition is deterministic: no fork, no race, no abort.
///
/// HOW THE INSTRUMENT IS PROVED. A <c>pthread_atfork</c> prepare handler is called by the C library
/// on the forking thread immediately before every <c>fork()</c>, and never for a <c>posix_spawn</c>,
/// which does its fork and exec inside one libc operation that runs no such handler. "The handler
/// never fired" is therefore exactly the kind of absence that can also mean the handler was never
/// registered, the library never loaded, or the loop never ran - a check that would certify a run
/// that never happened. So the counter is read TWICE and the FIRST read is a positive demonstration:
/// a real <c>fork()</c> is called on purpose and the counter must read 1. Only then does a zero
/// across a hundred starts mean anything. If this file's handler ever stops being wired up, the
/// first assertion fails rather than the second quietly becoming vacuous.
///
/// EVERYTHING RUNS ON ONE DEDICATED THREAD, and the counter is <see cref="ThreadStaticAttribute"/>.
/// The suite runs up to four tests at once and other tests do start processes; a fork on another
/// thread must not be counted as ours, and ours must not be hidden among theirs. A prepare handler
/// always runs on the thread that called <c>fork()</c>, so counting per-thread makes this test
/// immune to whatever else the assembly is doing at the time.
///
/// THE CONTROL CHILD TOUCHES NOTHING. Only the forking thread survives a fork, so the child of the
/// control fork is a managed runtime with one thread and a copy of every lock the others held. It
/// therefore does exactly one thing - signals itself dead - through two P/Invoke stubs that were
/// deliberately called in the PARENT first, so neither needs to be compiled in the child. Nothing
/// is allocated, no managed library is touched, and the child never returns.
/// </summary>
public sealed class ProcessesAreSpawnedNotForkedTests
{
    /// <summary>Enough starts that a per-start fork could not be missed, and still under a second.</summary>
    private const int Starts = 100;

    private const int SIGKILL = 9;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AtforkHandler();

    [DllImport("libc", SetLastError = true)]
    private static extern int pthread_atfork(IntPtr prepare, IntPtr parent, IntPtr child);

    [DllImport("libc", SetLastError = true)]
    private static extern int fork();

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    // No SetLastError on the two the CHILD calls: a simpler stub to have ready, and the child has
    // nobody to report an error number to anyway.
    [DllImport("libc")]
    private static extern int getpid();

    [DllImport("libc")]
    private static extern int kill(int pid, int sig);

    /// <summary>Counted per thread. See the class note: the suite is parallel and other tests fork.</summary>
    [ThreadStatic]
    private static int _forksOnThisThread;

    /// <summary>
    /// Held in a static for the life of the process. A <c>pthread_atfork</c> handler can NEVER be
    /// unregistered, so the delegate behind this pointer must never become collectable - if it did,
    /// the next fork anywhere in this process would call freed memory.
    /// </summary>
    private static readonly AtforkHandler PrepareHandler = () => _forksOnThisThread++;
    private static readonly IntPtr PrepareHandlerPointer = Marshal.GetFunctionPointerForDelegate(PrepareHandler);
    private static int _registered;

    private static void RegisterForkCounterOnce()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            Assert.Equal(0, pthread_atfork(PrepareHandlerPointer, IntPtr.Zero, IntPtr.Zero));
    }

    [UnixOnlyFact("fork() and pthread_atfork are POSIX. Windows has no fork, so it cannot have this defect, and ProcessRunner deliberately still uses the Process class there.")]
    public void ProcessRunner_StartsAHundredChildren_WithoutCallingForkOnce()
    {
        RegisterForkCounterOnce();

        var forksFromARealFork = -1;
        var forksFromAHundredStarts = -1;
        var lastExitCode = int.MinValue;
        var lastOutput = "";
        Exception? failure = null;

        var measuring = new Thread(() =>
        {
            try
            {
                // Call both of the child's P/Invokes here, in the parent, so their interop stubs are
                // already compiled when the child reaches them. Signal 0 sends nothing at all; it
                // only asks whether this process could signal itself, which it can.
                var self = getpid();
                Assert.Equal(0, kill(self, 0));

                // STEP 1 - PROVE THE INSTRUMENT with a fork nobody can argue about.
                _forksOnThisThread = 0;
                var pid = fork();
                if (pid == 0)
                {
                    // THE CHILD. One thread, somebody else's locks, nothing safe to call.
                    kill(getpid(), SIGKILL);
                    // Not reached. If it ever were, the parent's waitpid below would still collect
                    // this process, and the test would fail on the exit status rather than hang.
                    return;
                }
                Assert.True(pid > 0, $"fork() failed, errno {Marshal.GetLastWin32Error()}");
                Assert.Equal(pid, waitpid(pid, out var status, 0));
                // Killed by signal 9: the low seven bits of the status are the signal number.
                Assert.Equal(SIGKILL, status & 0x7F);
                forksFromARealFork = _forksOnThisThread;

                // STEP 2 - THE ASSERTION THIS TEST EXISTS FOR.
                _forksOnThisThread = 0;
                for (var i = 0; i < Starts; i++)
                {
                    var result = ProcessRunner
                        .RunAsync("/bin/echo", new[] { "ok" }, workingDirectory: null, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    lastExitCode = result.ExitCode;
                    lastOutput = result.StandardOutput;
                }
                forksFromAHundredStarts = _forksOnThisThread;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        measuring.Start();
        // A watchdog, not a wait on a result: it turns a hang into a named failure.
        Assert.True(measuring.Join(TimeSpan.FromMinutes(2)), "The measuring thread did not finish.");
        if (failure is not null) throw failure;

        // THE INSTRUMENT WORKS. If this line fails, nothing below it means anything - which is
        // precisely why it is asserted rather than assumed.
        Assert.Equal(1, forksFromARealFork);

        // THE FIX. Before it, a hundred starts through ProcessRunner read 100 here.
        Assert.Equal(0, forksFromAHundredStarts);

        // And the children really ran, so that zero is not the zero of a loop that did nothing.
        Assert.Equal(0, lastExitCode);
        Assert.Equal("ok", lastOutput.Trim());
    }
}
