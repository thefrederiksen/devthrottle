using System.Diagnostics;
using System.Text;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.CliExplorer.Execution;

public sealed class ClaudeRunner
{
    private readonly string _claudePath;
    private readonly string _workingDirectory;
    private readonly int _timeoutMs;

    public ClaudeRunner(string claudePath, string workingDirectory, int timeoutMs = 30_000)
    {
        FileLog.Write($"[ClaudeRunner] Created: claude={claudePath}, workDir={workingDirectory}, timeout={timeoutMs}ms");
        _claudePath = claudePath;
        _workingDirectory = workingDirectory;
        _timeoutMs = timeoutMs;
    }

    public async Task<RunResult> RunAsync(string arguments, string? stdinText = null, int? timeoutMs = null)
    {
        var effectiveTimeout = timeoutMs ?? _timeoutMs;
        FileLog.Write($"[ClaudeRunner] RunAsync: args=\"{arguments}\", hasStdin={stdinText != null}, timeout={effectiveTimeout}ms");

        var psi = new ProcessStartInfo
        {
            FileName = _claudePath,
            Arguments = arguments,
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        // Clear CLAUDECODE env var so claude doesn't refuse to start inside another session
        psi.Environment.Remove("CLAUDECODE");

        var sw = Stopwatch.StartNew();
        var process = new Process { StartInfo = psi };
        process.Start();

        FileLog.Write($"[ClaudeRunner] Process started: PID={process.Id}");

        if (stdinText != null)
        {
            await process.StandardInput.WriteAsync(stdinText);
        }
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var exited = await WaitForExitAsync(process, effectiveTimeout);
        sw.Stop();

        if (!exited)
        {
            FileLog.Write($"[ClaudeRunner] Process timed out after {effectiveTimeout}ms, killing PID={process.Id}");
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited between check and kill
            }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var exitCode = exited ? process.ExitCode : -1;

        FileLog.Write($"[ClaudeRunner] RunAsync completed: exitCode={exitCode}, timedOut={!exited}, duration={sw.Elapsed.TotalSeconds:F1}s, stdoutLen={stdout.Length}, stderrLen={stderr.Length}");

        return new RunResult(stdout, stderr, exitCode, sw.Elapsed, !exited);
    }

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public static string? FindClaudeOnPath()
    {
        return ClaudeClient.FindClaudePath();
    }
}
