using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Engine.Jobs;

public sealed class ProcessJob : IJob
{
    private readonly string _command;
    private readonly string? _workingDir;
    private readonly int _timeoutSeconds;
    private readonly Action<int, DateTime>? _onStarted;

    /// <summary>How long a killed command's process may take to exit before it counts as not proven stopped.</summary>
    internal static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    public string Name { get; }

    /// <param name="onStarted">
    /// Called with the command process's id and start time (UTC) as soon as it starts, so the caller
    /// can record which process it must prove stopped before it lets anyone run the command again.
    /// </param>
    public ProcessJob(string name, string command, string? workingDir = null, int timeoutSeconds = 300,
        Action<int, DateTime>? onStarted = null)
    {
        Name = name;
        _command = command;
        _workingDir = workingDir;
        _timeoutSeconds = timeoutSeconds;
        _onStarted = onStarted;
    }

    public async Task<JobResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        FileLog.Write($"[ProcessJob] Executing: name={Name}, command={_command}");

        var startInfo = ShellFor(_command);

        if (!string.IsNullOrEmpty(_workingDir))
            startInfo.WorkingDirectory = _workingDir;

        using var process = new Process { StartInfo = startInfo };

        process.Start();
        DateTime? startedAtUtc = null;
        try
        {
            startedAtUtc = process.StartTime.ToUniversalTime();
            _onStarted?.Invoke(process.Id, startedAtUtc.Value);
        }
        catch (Exception ex)
        {
            // The command could not be recorded, so nobody else could ever prove it gone. It must
            // not run unrecorded: kill it, and say whether it was seen to exit.
            var stopped = KillAndConfirm(process);
            FileLog.Write($"[ProcessJob] Recording the started command FAILED, killed it: name={Name}, pid={process.Id}, stopped={stopped}, error={ex.Message}");
            throw new CommandNotRecordedException(Name, process.Id, startedAtUtc, stopped, ex);
        }
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            FileLog.Write($"[ProcessJob] Timeout after {_timeoutSeconds}s: name={Name}");
            var stopped = KillAndConfirm(process);
            var stdout = await ReadSafe(stdoutTask);
            var stderr = await ReadSafe(stderrTask);
            var error = stopped
                ? $"Timed out after {_timeoutSeconds} seconds. {stderr}"
                : $"Timed out after {_timeoutSeconds} seconds and the process did not exit after it was killed. {stderr}";
            return new JobResult(false, stdout, error, TimedOut: true, ErrorOutput: stderr, ProcessStopped: stopped);
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled (a shutdown). The command must not outlive it: a command left
            // running while its run is ended would let another Director start it a second time.
            var stopped = KillAndConfirm(process);
            FileLog.Write($"[ProcessJob] Cancelled: name={Name}, processTreeStopped={stopped}");
            throw new JobCancelledException(Name, stopped, cancellationToken);
        }

        var finalStdout = await ReadSafe(stdoutTask);
        var finalStderr = await ReadSafe(stderrTask);
        var exitCode = process.ExitCode;

        FileLog.Write($"[ProcessJob] Completed: name={Name}, exitCode={exitCode}");

        return new JobResult(
            Success: exitCode == 0,
            Output: finalStdout,
            Error: exitCode != 0 ? $"Exit code {exitCode}. {finalStderr}" : null,
            ExitCode: exitCode,
            ErrorOutput: finalStderr
        );
    }

    /// <summary>
    /// The shell a command line runs under: <c>cmd.exe /c</c> on Windows, <c>/bin/sh -c</c> everywhere else. A
    /// Director runs on Linux too, and a command handed to <c>cmd.exe</c> there does not run at all.
    /// </summary>
    internal static ProcessStartInfo ShellFor(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/c {command}";
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        return startInfo;
    }

    /// <summary>
    /// Kills the command's whole process tree and returns whether the command process is then seen to
    /// exit within <see cref="KillWait"/>. False means it cannot be proven stopped.
    /// </summary>
    private static bool KillAndConfirm(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited before kill -- expected race condition
        }

        return process.WaitForExit(KillWait);
    }

    // Stream reads may fail when process is killed -- expected after timeout
    private static async Task<string> ReadSafe(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (ObjectDisposedException)
        {
            return string.Empty;
        }
    }
}
