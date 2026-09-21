using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Engine.Jobs;

public sealed class ProcessJob : IJob
{
    private readonly string _command;
    private readonly string? _workingDir;
    private readonly int _timeoutSeconds;

    public string Name { get; }

    public ProcessJob(string name, string command, string? workingDir = null, int timeoutSeconds = 300)
    {
        Name = name;
        _command = command;
        _workingDir = workingDir;
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task<JobResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        FileLog.Write($"[ProcessJob] Executing: name={Name}, command={_command}");

        var startInfo = ShellFor(_command);

        if (!string.IsNullOrEmpty(_workingDir))
            startInfo.WorkingDirectory = _workingDir;

        using var process = new Process { StartInfo = startInfo };

        process.Start();
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
            KillProcess(process);
            var stdout = await ReadSafe(stdoutTask);
            var stderr = await ReadSafe(stderrTask);
            return new JobResult(false, stdout, $"Timed out after {_timeoutSeconds} seconds. {stderr}", TimedOut: true,
                ErrorOutput: stderr);
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

    // Kill may race with natural exit -- InvalidOperationException is expected
    private static void KillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited before kill -- expected race condition
        }
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
