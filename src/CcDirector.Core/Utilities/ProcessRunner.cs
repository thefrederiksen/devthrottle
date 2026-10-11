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
    public static async Task<Result> RunAsync(
        string fileName, IReadOnlyList<string> args, string? workingDirectory, string? standardInput, CancellationToken ct)
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

        using var proc = new Process { StartInfo = psi };

        // Start does NOT return false when the executable is missing - it throws Win32Exception ("The
        // system cannot find the file specified"). Without this catch the Started=false result was
        // unreachable for the one case it names, and every caller had to carry its own catch-all to
        // survive a machine with no git (devthrottle_internal issue #1048). Catching it here is what
        // makes the documented contract on Started true.
        try
        {
            if (!proc.Start())
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
                await WriteStandardInputAsync(proc, standardInput, ct);
            await proc.WaitForExitAsync(ct);
            var stdout = await outTask;
            var stderr = await errTask;
            return new Result(proc.ExitCode, stdout, stderr, Started: true);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(proc);
            // Observe the drain tasks so a killed-pipe read does not surface as an unobserved fault.
            await ObserveQuietlyAsync(outTask);
            await ObserveQuietlyAsync(errTask);
            throw;
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
