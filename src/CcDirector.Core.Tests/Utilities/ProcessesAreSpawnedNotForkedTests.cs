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
/// THE COUNTER WATCHES EVERY THREAD, AND IT HAS TO.
/// <see cref="ProcessRunner"/> starts its child on a thread-pool thread and not on the caller's
/// (<c>ProcessStartOffThread</c>, issue #4556), so a fork would happen on a thread the test never
/// touches. An earlier version of this test counted per-thread, on the measuring thread, and
/// therefore PASSED against the forking code - it was measuring a thread where nothing was ever
/// going to happen. That is why step 1 below does its control fork on a thread-pool thread too:
/// proving the handler fires is not enough, it has to be proved to fire somewhere other than here.
///
/// Nothing else in this assembly starts a process - no test file in it constructs one, and the
/// production paths it exercises all spawn rather than fork - so a process-wide count is stable.
/// If something did fork concurrently this test would FAIL rather than pass, which is the safe
/// direction for it to be wrong in, and a true signal that something still forks.
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

    /// <summary>Every fork in this process, on any thread. See the class note for why not per-thread.</summary>
    private static int _forksAnywhere;

    /// <summary>
    /// Held in a static for the life of the process. A <c>pthread_atfork</c> handler can NEVER be
    /// unregistered, so the delegate behind this pointer must never become collectable - if it did,
    /// the next fork anywhere in this process would call freed memory.
    /// </summary>
    private static readonly AtforkHandler PrepareHandler = () => Interlocked.Increment(ref _forksAnywhere);
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

        // Call both of the child's P/Invokes here, in the parent, so their interop stubs are
        // already compiled when the child reaches them. Signal 0 sends nothing at all; it only
        // asks whether this process could signal itself, which it can.
        Assert.Equal(0, kill(getpid(), 0));

        // STEP 1 - PROVE THE INSTRUMENT, ON A THREAD-POOL THREAD.
        // On the pool, because that is where ProcessRunner starts its children, and a counter that
        // could not see a fork there would pass no matter what the code did.
        Interlocked.Exchange(ref _forksAnywhere, 0);
        Task.Run(ForkOnceAndHaveTheChildKillItself).GetAwaiter().GetResult();
        Assert.Equal(1, Volatile.Read(ref _forksAnywhere));

        // STEP 2 - THE ASSERTION THIS TEST EXISTS FOR.
        Interlocked.Exchange(ref _forksAnywhere, 0);
        var lastExitCode = int.MinValue;
        var lastOutput = "";
        for (var i = 0; i < Starts; i++)
        {
            var result = ProcessRunner
                .RunAsync("/bin/echo", new[] { "ok" }, workingDirectory: null, CancellationToken.None)
                .GetAwaiter().GetResult();
            lastExitCode = result.ExitCode;
            lastOutput = result.StandardOutput;
        }

        // THE FIX. Before it, this read 100.
        Assert.Equal(0, Volatile.Read(ref _forksAnywhere));

        // And the children really ran, so that zero is not the zero of a loop that did nothing.
        Assert.Equal(0, lastExitCode);
        Assert.Equal("ok", lastOutput.Trim());
    }

    /// <summary>
    /// One real fork, whose child does exactly one thing and then stops existing.
    ///
    /// Only the forking thread survives a fork, so this child is a managed runtime with one thread
    /// and a copy of every lock the others were holding - the very situation this whole change
    /// exists to avoid. It therefore allocates nothing, touches no managed library, and signals
    /// itself dead through two interop stubs that were deliberately called in the parent first so
    /// neither needs to be compiled here. One fork in a test is not nothing, but it is one, and it
    /// is the only way to show that the instrument can see what it claims to measure.
    /// </summary>
    private static void ForkOnceAndHaveTheChildKillItself()
    {
        var pid = fork();
        if (pid == 0)
        {
            kill(getpid(), SIGKILL);
            return; // not reached
        }

        Assert.True(pid > 0, $"fork() failed, errno {Marshal.GetLastWin32Error()}");
        Assert.Equal(pid, waitpid(pid, out var status, 0));
        // Killed by signal 9: the low seven bits of the wait status are the signal number.
        Assert.Equal(SIGKILL, status & 0x7F);
    }
}
