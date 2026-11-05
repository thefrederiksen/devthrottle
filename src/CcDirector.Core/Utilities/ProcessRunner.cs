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
            return new Result(
                -1, "", $"{fileName} could not be started: {CcDirector.Core.UnixPty.UnixNativeMethods.StrError(errno)}",
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
                var exitCode = await UnixSpawn.WaitAsync(child.Pid, ct).ConfigureAwait(false);
                var stdout = await outTask.ConfigureAwait(false);
                var stderr = await errTask.ConfigureAwait(false);
                return new Result(exitCode, stdout, stderr, Started: true);
            }
            catch (OperationCanceledException)
            {
                UnixSpawn.KillGroup(child.Pid);
                // Observe the drain tasks so a killed-pipe read does not surface as an unobserved fault.
                await ObserveQuietlyAsync(outTask).ConfigureAwait(false);
                await ObserveQuietlyAsync(errTask).ConfigureAwait(false);
                // Collect the status of what we just killed. Without this every cancelled call
                // would leave a zombie for the life of the Director - the Process class did this
                // for us, and taking the start over means taking the ending over too.
                UnixSpawn.ReapBestEffort(child.Pid);
                throw;
            }
        }
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

    private static async Task ObserveQuietlyAsync(Task task)
    {
        try { await task; }
        catch { /* the read was cancelled or the pipe was closed by the kill - expected */ }
    }
}
