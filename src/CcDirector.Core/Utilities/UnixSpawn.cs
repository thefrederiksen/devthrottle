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

    /// <summary>
    /// The same gate, for the one other place in the Director that spawns a child of its own -
    /// <c>UnixProcessHost</c>, which starts agent sessions on a pseudo-terminal. It must hold this
    /// while it spawns, or a session started in the microseconds between a <c>pipe()</c> here and
    /// the close-on-exec mark that follows it would inherit a write end of that pipe and keep it
    /// open for the whole life of the session - and the git read waiting on the other end would
    /// never reach end of file. Low odds, a permanent hang when it lands. Raised in review of
    /// devthrottle#4577.
    /// </summary>
    internal static object DescriptorGate => SpawnGate;

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

        /// <summary>
        /// Set once this child's exit status has been collected. AFTER THAT THE PID MUST NEVER BE
        /// USED AGAIN. A collected pid is free for the operating system to hand to a brand new
        /// process, so a late kill(-pid) would signal a process group that has nothing to do with
        /// us, and a late waitpid could take the exit status of a SIBLING child - another child of
        /// ours, or one the runtime started - and report it as this one's. Both were reachable
        /// before: the wait can succeed and the drain that follows it can still throw on
        /// cancellation, which sent the cancellation path at an already-collected pid. Caught in
        /// review of devthrottle#4577.
        /// </summary>
        private bool _collected;

        /// <summary>
        /// Wait for this child to exit and return its exit code, using the convention the .NET
        /// <see cref="System.Diagnostics.Process"/> class reports on Unix: the exit status for a
        /// normal exit, and 128 plus the signal number for a child a signal killed.
        /// </summary>
        public async Task<int> WaitAsync(CancellationToken ct)
        {
            var delayMs = 1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (TryReap(Pid, out var exitCode))
                {
                    _collected = true;
                    return exitCode;
                }

                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, PollCeilingMs);
            }
        }

        /// <summary>
        /// Kill this child and everything it started, then collect its status so it does not sit as
        /// a zombie for the life of the Director. Does NOTHING if the status has already been
        /// collected - see <see cref="_collected"/>; that is the whole point of the flag.
        /// </summary>
        public void KillAndCollect()
        {
            if (_collected || Pid <= 0) return;

            // A negative pid means "the process group with this id". This child leads a group of
            // its own whose id IS its pid, so this cannot reach anything outside what we started.
            kill(-Pid, SIGKILL);
            kill(Pid, SIGKILL);

            for (var attempt = 0; attempt < CollectAttempts; attempt++)
            {
                if (TryReap(Pid, out _)) { _collected = true; return; }
                Thread.Sleep(CollectPauseMs);
            }
        }

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

    /// <summary>The ceiling the exit poll backs off to, in milliseconds.</summary>
    private const int PollCeilingMs = 25;
    private const int CollectAttempts = 200;
    private const int CollectPauseMs = 5;

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

        // RESOLVE THE PROGRAM HERE, AGAINST THIS PROCESS'S OWN PATH, AND SPAWN AN ABSOLUTE PATH.
        //
        // posix_spawnp would search PATH itself - but the C library's PATH, which is NOT the one
        // this process thinks it has. Environment.SetEnvironmentVariable writes only to the managed
        // copy; .NET never pushes it down to the native environment. So a folder added to PATH at
        // run time is invisible to a libc PATH search, while the Process class it replaced resolved
        // in managed code and never had the problem.
        //
        // That is not hypothetical here: GitHubSetup.AddFoundFoldersToProcessPath exists precisely
        // to add the folders git and gh were found in after an install, and FleetToolPathRepair,
        // ToolShimPath and BrowserHarnessInstaller rewrite PATH the same way. Without this,
        // every caller passing a bare name - "git" in GitCommandRunner, GitStatusProvider and
        // GitSyncStatusProvider, "gh" and "az" in PullRequestService - reports "not installed" on a
        // Director whose git lives only in a folder added that way, which is the Finder-launched
        // Mac. Caught in review of devthrottle#4577 before it shipped.
        //
        // A name that resolves nowhere is ENOENT, the same answer and the same code the Process
        // class gave for it, without a spawn being attempted at all.
        // ONE KNOWN DIFFERENCE FROM THE PROCESS CLASS, raised in review and left as it is: on POSIX
        // the resolver checks that the file EXISTS, not that it can be executed, while a libc PATH
        // search skips a non-executable candidate and keeps looking. So a non-executable file named
        // git sitting earlier on PATH now shadows the real git further along, and the answer is
        // "Permission denied" instead of the real git running. Adding an access(X_OK) test belongs
        // in ExecutableResolver, which Windows shares and which several other callers depend on -
        // not in a change going out before a release.
        var resolved = ExecutableResolver.Resolve(fileName);
        if (resolved is null) return ENOENT;

        // argv[0] is the program name AS THE CALLER WROTE IT - a program that prints its own usage
        // should say what it was asked to be, not where it was found - then the arguments, then a
        // null terminator. Only the thing being executed is the resolved path.
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
                    // An absolute path, so posix_spawnp performs no PATH search of its own.
                    rc = posix_spawnp(out pid, resolved, fileActions, attr, argv, envp);
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
    /// One non-blocking collection attempt. False means "still running" - and also means it for a
    /// child somebody else already collected, which cannot happen for these children: the runtime's
    /// own child handling peeks with waitid(WNOWAIT) and only ever collects pids it started itself.
    ///
    /// This POLLS rather than blocking in waitpid, because a blocking wait would hold a thread-pool
    /// thread for the whole life of the child. The poll runs for that whole life - 1 ms doubling to
    /// 25 ms - which is a handful of system calls per child and nothing worth optimising.
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
