using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static CcDirector.Core.UnixPty.UnixNativeMethods;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Starts a child process on macOS and Linux through <c>posix_spawn</c> instead of
/// <c>fork()</c>+<c>exec()</c>, with its standard streams on ordinary pipes.
///
/// WHY THIS EXISTS (devthrottle#4577). The .NET <see cref="System.Diagnostics.Process"/> class has
/// no other way to start a process on Unix than <c>fork()</c>: the runtime's own native library
/// imports <c>fork</c> and exports no spawn entry point, so there is no switch to turn this on.
/// <c>fork()</c> duplicates the address space of a process that has dozens of running threads, and
/// the locks inside the C allocator are duplicated along with it - held, by threads that do not
/// exist in the child. libmalloc installs <c>pthread_atfork</c> handlers to put those locks back,
/// and on 2026-11-04 the PARENT-side handler ran on a Director with 48 threads, eight of them
/// inside the allocator's own fork lock, and tried to release an <c>os_unfair_lock</c> whose owner
/// field did not name the forking thread. That is not an error an allocator reports; it is
/// <c>_os_unfair_lock_unowned_abort</c>, an unconditional abort, and macOS killed the whole
/// Director mid-session. The child was never the problem - the parent killed itself cleaning up.
///
/// <c>posix_spawn</c> closes the hole at the source rather than narrowing the window: the fork and
/// the exec happen inside one libc/kernel operation, with every argument, environment entry and
/// descriptor action marshalled in the parent BEFORE the call. No <c>pthread_atfork</c> handler
/// runs, so there is no lock to put back and no abort to hit. The same reasoning, and the same
/// call, is already used by the PTY host for exactly this reason.
///
/// Two further things this buys, both of them long-standing complaints of issue 516:
/// <list type="bullet">
/// <item>The child is made the leader of its OWN process group, so cancelling a call can kill the
/// child and everything the child started with a single <c>kill(-pid)</c> - no tree walk, and
/// nothing left behind because a grandchild was missed.</item>
/// <item>Arguments are passed as a real <c>argv</c> array, so there is no quoting layer at all.</item>
/// </list>
/// </summary>
internal static class UnixSpawn
{
    /// <summary>
    /// Serialises descriptor creation and the spawn itself. A pipe's PARENT ends must not be
    /// inherited by somebody else's child: a leaked write end means the read end never reaches
    /// end-of-file and a drain hangs forever. The parent ends are marked close-on-exec the instant
    /// they exist, but <c>pipe()</c> and that marking are two calls, and macOS has no atomic
    /// <c>pipe2</c> to make them one. Holding this gate across both of them, and across the spawn,
    /// means no spawn of ours can ever observe a descriptor of ours in the unmarked gap.
    ///
    /// It costs nothing worth measuring. On macOS <c>posix_spawn</c> is a single system call, and
    /// the Director's busiest measured day was 48,653 git commands plus 2,401 licence probes in
    /// seventeen hours - under one a second, against a call that takes a fraction of a millisecond.
    ///
    /// NOTE WHAT THIS GATE IS NOT. It is not a cure for the crash in the note above and would not
    /// have prevented it: that fork raced against the OTHER forty-seven threads allocating memory,
    /// not against a second fork, so serialising our own starts would have changed nothing. The
    /// gate is here for descriptors and nothing else.
    /// </summary>
    private static readonly object SpawnGate = new();

    /// <summary>ENOENT - "no such file or directory", the code for an executable that is not there.</summary>
    internal const int ENOENT = 2;

    private const int EINTR = 4;
    private const int ECHILD = 10;

    private const int StreamBufferSize = 4096;

    /// <summary>
    /// A started child and the parent's ends of its pipes. Disposing closes every descriptor; it
    /// does NOT wait for or kill the process - see <see cref="WaitAsync"/> and <see cref="KillGroup"/>.
    /// </summary>
    internal sealed class Child : IDisposable
    {
        public int Pid { get; init; }
        public FileStream? StandardInput { get; init; }
        public FileStream StandardOutput { get; init; } = null!;
        public FileStream StandardError { get; init; } = null!;

        public void Dispose()
        {
            TryDispose(StandardInput);
            TryDispose(StandardOutput);
            TryDispose(StandardError);
        }

        private static void TryDispose(Stream? s)
        {
            try { s?.Dispose(); }
            catch (IOException) { /* the pipe is already broken; the descriptor is gone either way */ }
        }
    }

    /// <summary>
    /// Start <paramref name="fileName"/> with <paramref name="args"/>. Returns 0 and a
    /// <paramref name="child"/> on success, or the operating system's own error number with
    /// <paramref name="child"/> null - <see cref="ENOENT"/> being the one every caller reads,
    /// because it means the same thing on every platform: there is no such executable.
    /// </summary>
    /// <param name="fileName">Program to run. Resolved on PATH when it contains no directory separator.</param>
    /// <param name="environment">Added to this process's environment for the child only. Null adds nothing.</param>
    internal static int TrySpawn(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        bool redirectStandardInput,
        IReadOnlyDictionary<string, string>? environment,
        out Child? child)
    {
        child = null;

        // argv[0] is the program name as invoked, then the arguments, then a null terminator.
        var argv = new string?[args.Count + 2];
        argv[0] = fileName;
        for (var i = 0; i < args.Count; i++) argv[i + 1] = args[i];
        argv[^1] = null;

        var envp = BuildEnvironment(environment);

        // posix_spawnattr_t and posix_spawn_file_actions_t are opaque. They are passed as pointers
        // to caller-allocated, zeroed buffers: on macOS the typedefs are themselves pointers and
        // libc stores its handle in the first word, and a generous buffer also covers the struct
        // glibc lays out on Linux. This is the shape the PTY host has used in production.
        var fileActions = AllocZeroed(1024);
        var attr = AllocZeroed(1024);

        var stdoutPipe = new int[] { -1, -1 };
        var stderrPipe = new int[] { -1, -1 };
        var stdinPipe = new int[] { -1, -1 };
        var pid = -1;
        var errno = 0;

        try
        {
            lock (SpawnGate)
            {
                try
                {
                    if (!TryCreatePipe(stdoutPipe, out errno)) return errno;
                    if (!TryCreatePipe(stderrPipe, out errno)) return errno;
                    if (redirectStandardInput && !TryCreatePipe(stdinPipe, out errno)) return errno;

                    var rc = posix_spawnattr_init(attr);
                    if (rc != 0) return rc;

                    // The child leads its own process group, so kill(-pid) reaches it AND anything
                    // it starts. Nothing here asks for a new SESSION: POSIX_SPAWN_SETSID is for the
                    // PTY host, which needs a controlling terminal. A plain captured-output child
                    // must not be given one, and the flag's numeric value differs by platform
                    // anyway, which is a trap worth not walking into twice.
                    rc = posix_spawnattr_setflags(attr, POSIX_SPAWN_SETPGROUP);
                    if (rc != 0) return rc;
                    rc = posix_spawnattr_setpgroup(attr, 0);
                    if (rc != 0) return rc;

                    rc = posix_spawn_file_actions_init(fileActions);
                    if (rc != 0) return rc;

                    // Bind the child's ends onto 0/1/2. dup2 clears close-on-exec on the copy, so
                    // the three standard descriptors survive the exec while the originals - marked
                    // close-on-exec at creation - do not.
                    if (redirectStandardInput)
                    {
                        rc = posix_spawn_file_actions_adddup2(fileActions, stdinPipe[0], STDIN_FILENO);
                        if (rc != 0) return rc;
                    }
                    rc = posix_spawn_file_actions_adddup2(fileActions, stdoutPipe[1], STDOUT_FILENO);
                    if (rc != 0) return rc;
                    rc = posix_spawn_file_actions_adddup2(fileActions, stderrPipe[1], STDERR_FILENO);
                    if (rc != 0) return rc;

                    // Close every pipe end the child has no business holding. The dup2 actions
                    // above already gave it copies at 0, 1 and 2, and close-on-exec would drop the
                    // originals at exec anyway - but SAYING SO HERE is what makes it certain
                    // rather than dependent on a flag having been applied to six descriptors by a
                    // variadic libc call. Actions run in the order they were added, so each
                    // original is closed only after it has been duplicated.
                    CloseInChild(fileActions, stdoutPipe[0]);
                    CloseInChild(fileActions, stdoutPipe[1]);
                    CloseInChild(fileActions, stderrPipe[0]);
                    CloseInChild(fileActions, stderrPipe[1]);
                    CloseInChild(fileActions, stdinPipe[0]);
                    CloseInChild(fileActions, stdinPipe[1]);

                    if (!string.IsNullOrEmpty(workingDirectory))
                    {
                        rc = posix_spawn_file_actions_addchdir_np(fileActions, workingDirectory);
                        if (rc != 0) return rc;
                    }

                    // posix_spawnp reports an exec failure as its RETURN VALUE - it does not throw
                    // and does not leave the answer in errno for us to guess at. A missing
                    // executable is ENOENT here, the same number the .NET Process class surfaced as
                    // Win32Exception.NativeErrorCode, so the published contract on StartErrorCode
                    // is unchanged.
                    rc = posix_spawnp(out pid, fileName, fileActions, attr, argv, envp);
                    if (rc != 0)
                    {
                        pid = -1;
                        return rc;
                    }
                }
                finally
                {
                    // The child holds its own copies now. The parent MUST release the child's ends
                    // or the read ends never see end-of-file when the child exits, and the drain
                    // hangs for the life of the process.
                    CloseIfOpen(ref stdoutPipe[1]);
                    CloseIfOpen(ref stderrPipe[1]);
                    CloseIfOpen(ref stdinPipe[0]);
                }
            }

            child = new Child
            {
                Pid = pid,
                StandardInput = redirectStandardInput ? OwnStream(ref stdinPipe[1], FileAccess.Write) : null,
                StandardOutput = OwnStream(ref stdoutPipe[0], FileAccess.Read),
                StandardError = OwnStream(ref stderrPipe[0], FileAccess.Read),
            };
            return 0;
        }
        finally
        {
            posix_spawn_file_actions_destroy(fileActions);
            posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(fileActions);
            Marshal.FreeHGlobal(attr);

            // Anything a FileStream did not take ownership of is still ours to close. On the success
            // path these are all -1 already; on every failure path they are the descriptors that
            // would otherwise leak, one pair per failed start.
            CloseIfOpen(ref stdoutPipe[0]);
            CloseIfOpen(ref stdoutPipe[1]);
            CloseIfOpen(ref stderrPipe[0]);
            CloseIfOpen(ref stderrPipe[1]);
            CloseIfOpen(ref stdinPipe[0]);
            CloseIfOpen(ref stdinPipe[1]);
        }
    }

    /// <summary>
    /// Wait for the child to exit and return its exit code, using the same convention the .NET
    /// <see cref="System.Diagnostics.Process"/> class reports on Unix: the exit status for a normal
    /// exit, and 128 plus the signal number for a child a signal killed.
    ///
    /// This POLLS rather than blocking in <c>waitpid</c>. A blocking wait would hold a thread-pool
    /// thread for the entire life of the child, and a child that closes its output early but keeps
    /// running would hold it with nothing to show for it. The first poll succeeds for a child that
    /// has already gone, which is the normal case here because the caller waits only after both
    /// pipes have reached end-of-file.
    /// </summary>
    internal static async Task<int> WaitAsync(int pid, CancellationToken ct)
    {
        var delayMs = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (TryReap(pid, out var exitCode)) return exitCode;

            await Task.Delay(delayMs, ct).ConfigureAwait(false);
            delayMs = Math.Min(delayMs * 2, 25);
        }
    }

    /// <summary>
    /// Kill the child and every process it started, by signalling the whole process group the child
    /// leads. This is why <see cref="TrySpawn"/> asks for POSIX_SPAWN_SETPGROUP: cancelling a call
    /// has to leave nothing running, and a hung grandchild - a credential prompt, a stalled network
    /// read - is exactly what outlives a kill aimed at the child alone.
    /// </summary>
    internal static void KillGroup(int pid)
    {
        if (pid <= 0) return;
        // Negative pid means "the process group with this id". The child leads a group of its own
        // whose id IS its pid, so this can never reach anything outside what we started.
        kill(-pid, SIGKILL);
        kill(pid, SIGKILL);
    }

    /// <summary>
    /// Collect the exit status of a child that has been killed, so it does not sit as a zombie for
    /// the life of the Director. Gives up rather than waiting: there is nobody left to tell.
    /// </summary>
    internal static void ReapBestEffort(int pid)
    {
        if (pid <= 0) return;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (TryReap(pid, out _)) return;
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// One non-blocking collection attempt. False means "still running" - and also means it for a
    /// child somebody else already collected, which cannot happen for these children: the runtime's
    /// own child handling peeks with waitid(WNOWAIT) and only ever collects pids it started itself.
    /// </summary>
    private static bool TryReap(int pid, out int exitCode)
    {
        exitCode = -1;
        while (true)
        {
            var r = waitpid(pid, out var status, WNOHANG);
            if (r == pid)
            {
                exitCode = WIFEXITED(status) ? WEXITSTATUS(status)
                    : WIFSIGNALED(status) ? 128 + WTERMSIG(status)
                    : -1;
                return true;
            }
            if (r == 0) return false;               // alive

            var errno = Marshal.GetLastWin32Error();
            if (errno == EINTR) continue;           // a signal arrived; ask again
            if (errno == ECHILD) return true;       // already collected; nothing to report but -1
            return true;                            // no further news is coming
        }
    }

    /// <summary>
    /// A pipe whose BOTH ends are close-on-exec. The child's end gets a non-close-on-exec copy from
    /// dup2 inside the spawn, so marking both here costs the child nothing and stops either end
    /// leaking into an unrelated child started elsewhere in the Director.
    /// </summary>
    private static bool TryCreatePipe(int[] fds, out int errno)
    {
        errno = 0;
        if (pipe(fds) != 0)
        {
            errno = Marshal.GetLastWin32Error();
            fds[0] = -1;
            fds[1] = -1;
            return false;
        }
        // Not best-effort. An unmarked end inherited by an unrelated child is a pipe that never
        // reaches end of file, which is a hang rather than an error - so a refusal here fails the
        // start instead of being carried into one.
        if (!SetCloseOnExec(fds[0]) || !SetCloseOnExec(fds[1]))
        {
            errno = Marshal.GetLastWin32Error();
            CloseIfOpen(ref fds[0]);
            CloseIfOpen(ref fds[1]);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Hand a descriptor to a <see cref="FileStream"/> that owns it, and clear our copy of the
    /// number so the clean-up path does not close it a second time. Not asynchronous: a pipe has no
    /// completion port to hang an overlapped read on, and this is exactly what the .NET Process
    /// class does with its own redirected pipes on Unix.
    /// </summary>
    private static FileStream OwnStream(ref int fd, FileAccess access)
    {
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        fd = -1;
        return new FileStream(handle, access, StreamBufferSize, isAsync: false);
    }

    /// <summary>
    /// Ask the spawn to close this descriptor in the child. Numbers 0, 1 and 2 are skipped: those
    /// are the standard streams the dup2 actions just created, and closing one would hand the child
    /// a missing descriptor - where the next file it opened would silently become its own stdin.
    /// A descriptor that was never created (-1) is skipped too.
    /// </summary>
    private static void CloseInChild(IntPtr fileActions, int fd)
    {
        if (fd > STDERR_FILENO) posix_spawn_file_actions_addclose(fileActions, fd);
    }

    private static void CloseIfOpen(ref int fd)
    {
        if (fd < 0) return;
        close(fd);
        fd = -1;
    }

    /// <summary>
    /// The child's whole environment. posix_spawn REPLACES the environment rather than adding to
    /// it, so this process's own variables are copied in first and the caller's overrides applied
    /// on top - which is what "adds to the child's environment only" has always meant here.
    /// </summary>
    private static string?[] BuildEnvironment(IReadOnlyDictionary<string, string>? overrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && e.Value is string v) merged[k] = v;
        }
        if (overrides is not null)
        {
            foreach (var (name, value) in overrides) merged[name] = value;
        }

        var envp = new string?[merged.Count + 1];
        var i = 0;
        foreach (var (name, value) in merged) envp[i++] = $"{name}={value}";
        envp[i] = null;
        return envp;
    }

    private static IntPtr AllocZeroed(int size)
    {
        var p = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        return p;
    }
}
