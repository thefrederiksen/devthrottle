using System.Diagnostics;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Runs a child process and captures its stdout and stderr safely. Two hazards this exists to avoid
/// (issue 516):
///   1. Pipe deadlock. Reading one redirected stream to end before the other lets the child block
///      writing to the second pipe once its buffer fills (about 64 KB), while the parent blocks
///      waiting for end-of-stream on the first - neither can progress. Both streams are drained
///      CONCURRENTLY here so a full stderr can never stall stdout, or the reverse.
///   2. Orphaned children on cancellation. Disposing a <see cref="Process"/> does NOT terminate the
///      operating-system process. When the caller cancels, the whole process tree is killed so a
///      hung child (a stuck credential or network prompt) does not outlive the cancelled call.
///   3. fork() in a process with many threads (devthrottle#4577). ON macOS AND LINUX THIS CLASS
///      DOES NOT USE <see cref="Process"/> AT ALL - it spawns through <see cref="UnixSpawn"/>,
///      which calls posix_spawn. The .NET Process class has no other way to start a process on
///      Unix than fork(), and forking a Director with dozens of allocating threads duplicates the
///      C allocator's locks in a held state; libmalloc's own parent-side recovery handler then
///      aborted the WHOLE Director on 2026-11-04. posix_spawn performs the fork and the exec inside
///      one libc operation with nothing marshalled in between, so no such handler ever runs.
///      Windows has no fork and keeps the Process path unchanged.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// The captured result. <see cref="Started"/> is false when the process could not start, and
    /// <see cref="StartErrorCode"/> then carries the operating system's own error number so the
    /// caller can tell "there is no such executable" apart from "it exists and refused to run" -
    /// a distinction the message text alone cannot be trusted to carry.
    /// </summary>
    public readonly record struct Result(
        int ExitCode, string StandardOutput, string StandardError, bool Started, int StartErrorCode = 0);

    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="args"/> in
    /// <paramref name="workingDirectory"/>, drains both pipes concurrently, and returns once the
    /// process exits. On cancellation the child process tree is killed and
    /// <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    public static Task<Result> RunAsync(
        string fileName, IReadOnlyList<string> args, string? workingDirectory, CancellationToken ct = default)
        => RunAsync(fileName, args, workingDirectory, standardInput: null, ct);

    /// <summary>
    /// As <see cref="RunAsync(string, IReadOnlyList{string}, string?, CancellationToken)"/>, and also writes
    /// <paramref name="standardInput"/> to the child's standard input (UTF-8, no byte order mark) and then closes
    /// it, so the child reads to end of input. This is how a caller hands a child something that must never sit
    /// in its argument list or environment, where any process on the machine can read it. Null leaves standard
    /// input unredirected, exactly as the overload without it.
    /// </summary>
    public static Task<Result> RunAsync(
        string fileName, IReadOnlyList<string> args, string? workingDirectory, string? standardInput, CancellationToken ct)
        => RunAsync(fileName, args, workingDirectory, standardInput, environment: null, ct);

    /// <summary>
    /// As the overload without it, and also adds <paramref name="environment"/> to the child's environment only - this
    /// process's own environment is not changed. Null or empty adds nothing.
    /// </summary>
    public static Task<Result> RunAsync(
        string fileName, IReadOnlyList<string> args, string? workingDirectory, string? standardInput,
        IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
        => OperatingSystem.IsWindows()
            ? RunWithProcessClassAsync(fileName, args, workingDirectory, standardInput, environment, ct)
            : RunWithPosixSpawnAsync(fileName, args, workingDirectory, standardInput, environment, ct);

    /// <summary>
    /// WINDOWS. There is no fork() here, so the <see cref="Process"/> class carries no such hazard
    /// and this is the path it has always taken.
    /// </summary>
    private static async Task<Result> RunWithProcessClassAsync(
        string fileName, IReadOnlyList<string> args, string? workingDirectory, string? standardInput,
        IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (standardInput is not null)
            psi.StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (name, value) in environment)
                psi.Environment[name] = value;

        using var proc = new Process { StartInfo = psi };

        // Start does NOT return false when the executable is missing - it throws Win32Exception ("The
        // system cannot find the file specified"). Without this catch the Started=false result was
        // unreachable for the one case it names, and every caller had to carry its own catch-all to
        // survive a machine with no git (devthrottle_internal issue #1048). Catching it here is what
        // makes the documented contract on Started true.
        try
        {
            // Started on the thread pool, never on the caller's thread: a caller on the desktop's UI
            // thread must not wait out a slow, lock-serialised Process.Start (issue #4556).
            if (!await ProcessStartOffThread.StartAsync(proc).ConfigureAwait(false))
                return new Result(-1, "", $"{fileName} could not start", Started: false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new Result(-1, "", ex.Message, Started: false, StartErrorCode: ex.NativeErrorCode);
        }

        // Start draining BOTH pipes immediately and concurrently - neither read waits on the other.
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            if (standardInput is not null)
                await WriteStandardInputAsync(proc, standardInput, ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            var stderr = await errTask.ConfigureAwait(false);
            return new Result(proc.ExitCode, stdout, stderr, Started: true);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(proc);
            await WaitForKilledExitAsync(proc, fileName).ConfigureAwait(false);
            // Observe the drain tasks so a killed-pipe read does not surface as an unobserved fault.
            await ObserveQuietlyAsync(outTask).ConfigureAwait(false);
            await ObserveQuietlyAsync(errTask).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// macOS AND LINUX. Starts the child with posix_spawn (see <see cref="UnixSpawn"/>) and keeps
    /// every term of the contract above: both pipes drained concurrently, the same Started and
    /// StartErrorCode answers for a child that could not start, and nothing left running when the
    /// caller cancels - here by signalling the process group the child leads, which reaches a
    /// grandchild a kill aimed at the child alone would miss.
    /// </summary>
    private static async Task<Result> RunWithPosixSpawnAsync(
        string fileName, IReadOnlyList<string> args, string? workingDirectory, string? standardInput,
        IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        // posix_spawnp REPORTS a failed start rather than throwing: the reason comes back as the
        // return value, so the Started=false result the contract above documents is reached by the
        // ordinary path instead of a catch. ENOENT is 2 here exactly as it was from
        // Win32Exception.NativeErrorCode, which is the number every caller reads.
        //
        // Started on the thread pool through the same seam as the Windows path, never on the
        // caller's thread (issue #4556). posix_spawn is a fraction of the cost of the fork it
        // replaces, but a caller on the desktop's UI thread still must not be the one that waits
        // for a child to come up - and the rule holding for every caller is what makes it true.
        UnixSpawn.Child? child = null;
        var errno = 0;
        await ProcessStartOffThread.StartAsync(() =>
        {
            errno = UnixSpawn.TrySpawn(
                fileName, args, workingDirectory,
                redirectStandardInput: standardInput is not null, environment, out child);
            return errno == 0;
        }).ConfigureAwait(false);

        if (errno != 0 || child is null)
            // The OPERATING SYSTEM'S REASON ALONE - "No such file or directory", "Permission
            // denied". Not a sentence of our own around it: callers put this inside one of theirs
            // (GitLaunchFailure writes "git could not be started: " in front of it), and a message
            // that introduced itself as well read "git could not be started: /path/notgit could
            // not be started: Permission denied". Caught in review of devthrottle#4577.
            return new Result(
                -1, "", CcDirector.Core.UnixPty.UnixNativeMethods.StrError(errno),
                Started: false, StartErrorCode: errno);

        using (child)
        {
            // Pipes carry bytes; git and the command-line tools speak UTF-8. Stated rather than
            // inherited from the console, because the Director is a desktop application and may
            // have no console whose encoding could be asked.
            using var outReader = new StreamReader(child.StandardOutput, Utf8NoByteOrderMark);
            using var errReader = new StreamReader(child.StandardError, Utf8NoByteOrderMark);

            // Start draining BOTH pipes immediately and concurrently - neither read waits on the
            // other. This is hazard 1 above, and it is no less real on this path.
            var outTask = outReader.ReadToEndAsync(ct);
            var errTask = errReader.ReadToEndAsync(ct);
            try
            {
                if (standardInput is not null)
                    await WriteStandardInputAsync(child.StandardInput!, standardInput, ct).ConfigureAwait(false);
                var exitCode = await child.WaitAsync(ct).ConfigureAwait(false);
                var stdout = await outTask.ConfigureAwait(false);
                var stderr = await errTask.ConfigureAwait(false);
                return new Result(exitCode, stdout, stderr, Started: true);
            }
            catch (OperationCanceledException)
            {
                // Kill the child's whole process group and collect its status. Without the collect
                // every cancelled call would leave a zombie for the life of the Director - the
                // Process class did that for us, and taking the start over means taking the ending
                // over too. It happens FIRST, so a cancelled call has stopped costing anything the
                // moment it returns, whatever the drains are doing.
                child.KillAndCollect();

                // THEN give the drains a bounded moment, and no more.
                //
                // These reads are on synchronous pipe descriptors, where a read already blocked in
                // the kernel does not come back for a cancellation token - it comes back when the
                // last write end closes. Killing the process group closes the ones the child and
                // its descendants hold, which is the normal case. But a descendant that called
                // setsid has left that group, and anything the child passed the pipe to may outlive
                // it: then the final write end closes whenever that unrelated process feels like
                // it, and awaiting the drains outright made the cancelled call wait exactly that
                // long. Measured in review of devthrottle#4577: twelve seconds against under one
                // for the path this replaced, which killed by walking the process tree instead.
                //
                // Cancellation has to mean the call returns. Output is forfeit on this path - it is
                // thrown away with the exception either way - so the drains get a grace period for
                // the common case and are then abandoned, still observed so neither surfaces later
                // as an unobserved fault.
                await AbandonDrainsAsync(outTask, errTask).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// How long a cancelled call waits for its drains to finish before walking away from them.
    ///
    /// Short on purpose. The captured output is DISCARDED on this path - the caller gets an
    /// exception, not a result - so there is nothing to wait for on its account. The only reason to
    /// wait at all is that a killed child's pipes close in microseconds, and waiting that long
    /// means the reads finish and their thread-pool threads come back instead of being abandoned
    /// mid-read. Past that there is nothing to gain, and a cancelled call must return.
    /// </summary>
    private static readonly TimeSpan DrainGraceAfterKill = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Wait briefly for both drains, then abandon them - while making sure neither can raise an
    /// unobserved exception afterwards. A read left blocked on a descriptor somebody else still
    /// holds open is not ours to wait for; see the cancellation path above.
    /// </summary>
    private static async Task AbandonDrainsAsync(Task outTask, Task errTask)
    {
        // ObserveQuietlyAsync swallows whatever its task throws, so these two can never fault and
        // an abandoned one cannot come back later as an unobserved exception. That is what makes
        // walking away from them safe; nothing further is needed to silence them.
        var both = Task.WhenAll(ObserveQuietlyAsync(outTask), ObserveQuietlyAsync(errTask));
        await Task.WhenAny(both, Task.Delay(DrainGraceAfterKill)).ConfigureAwait(false);
    }

    private static readonly System.Text.Encoding Utf8NoByteOrderMark =
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// As <see cref="WriteStandardInputAsync(Process, string, CancellationToken)"/>, for a raw pipe.
    /// </summary>
    private static async Task WriteStandardInputAsync(Stream pipe, string standardInput, CancellationToken ct)
    {
        try
        {
            await using var writer = new StreamWriter(pipe, Utf8NoByteOrderMark, leaveOpen: true);
            await writer.WriteAsync(standardInput.AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The child closed its end first. Its exit code carries the outcome.
        }
        finally
        {
            // Closing the write end is what makes the child read to END of input rather than
            // waiting forever for more.
            try { pipe.Dispose(); }
            catch (IOException) { /* the pipe is already broken; nothing is left to close */ }
        }
    }

    /// <summary>
    /// Writes the input and closes the pipe. A child that exits without reading all of it breaks the pipe; that is
    /// not this method's failure to report - the child's own exit code and output say what happened, and the caller
    /// reads those once the process has exited.
    /// </summary>
    private static async Task WriteStandardInputAsync(Process proc, string standardInput, CancellationToken ct)
    {
        try
        {
            await proc.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
            await proc.StandardInput.FlushAsync(ct);
        }
        catch (IOException)
        {
            // The child closed its end first. Its exit code carries the outcome.
        }
        finally
        {
            try { proc.StandardInput.Close(); }
            catch (IOException) { /* the pipe is already broken; nothing is left to close */ }
        }
    }

    private static void TryKillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited, or the operating system refused - best effort, nothing more to do.
        }
    }

    /// <summary>How long a cancelled call waits for the child it killed to be gone. A kill lands in milliseconds; this
    /// bounds only a child the operating system will not let us end.</summary>
    internal static readonly TimeSpan KilledExitLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// WAIT FOR THE KILLED CHILD TO BE GONE BEFORE THE CANCELLED CALL RETURNS (Factory Inbox mission, 4 November 2026).
    /// <see cref="Process.Kill(bool)"/> only asks the operating system to end the tree; it returns while the child may
    /// still hold its working folder, so a caller that cancelled and then removed that folder raced the kill. The stop
    /// verb did exactly that: its git probe in a factory seat's kit folder outran its three-second budget on a starved
    /// machine, was cancelled, and the kit folder delete that followed failed on a folder the dying git still held - so
    /// a seat ended for good kept its folder. Bounded by <see cref="KilledExitLimit"/>, and logged when the child
    /// outlives it, because a child that cannot be ended must not hold the caller up for ever.
    ///
    /// THE KERNEL WAIT, NOT <see cref="Process.WaitForExitAsync"/> (review of this change, finding 1, measured). On
    /// Windows the asynchronous wait checks <see cref="Process.HasExited"/> first, which is true as soon as the exit code
    /// is set - and <c>TerminateProcess</c> sets it at once and tears the process down afterwards, releasing its working
    /// folder only then. So after a kill it returned at once and 32 of 40 deletes still failed. The synchronous
    /// <see cref="Process.WaitForExit(int)"/> waits on the process handle itself, which is signalled only when the
    /// process is gone; with it 0 of 20 failed. It blocks, so it runs on the thread pool, never on the caller's thread.
    /// </summary>
    private static async Task WaitForKilledExitAsync(Process proc, string fileName)
    {
        try
        {
            var gone = await Task.Run(() => proc.WaitForExit((int)KilledExitLimit.TotalMilliseconds)).ConfigureAwait(false);
            if (!gone)
                FileLog.Write($"[ProcessRunner] WaitForKilledExitAsync FAILED: {fileName} was killed but had not exited after " +
                              $"{KilledExitLimit.TotalSeconds:F0}s; returning without it");
        }
        catch (InvalidOperationException ex)
        {
            // not-an-error: the process object holds no process any more (it was never started or is already reaped)
            FileLog.Write($"[ProcessRunner] WaitForKilledExitAsync: {fileName} has no process to wait for: {ex.Message}");
        }
    }

    private static async Task ObserveQuietlyAsync(Task task)
    {
        try { await task; }
        catch { /* the read was cancelled or the pipe was closed by the kill - expected */ }
    }
}
