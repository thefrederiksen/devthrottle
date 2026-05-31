using System.Diagnostics;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Tools;

/// <summary>
/// Executes the declared health checks for a tool and reports honest pass/fail with the raw process
/// output captured for debugging.
///
/// Safety: the runner only ever executes a <see cref="ToolTest"/> that belongs to the tool's own
/// declared <see cref="ToolDescriptor.Tests"/> set (which is built from the trusted embedded
/// manifest). It refuses any test instance not in that set, so a smoke command can never come from
/// outside the manifest. There are no retries and no fallbacks - a failing check reports its real
/// output and exit code.
/// </summary>
public sealed class ToolTestRunner
{
    private readonly TimeSpan _timeout;

    // 90s, not a tighter bound: several tools are PyInstaller one-file exes that re-extract to a temp
    // dir on every launch. The heaviest (cc-pdf) cold-starts in ~45s even solo, so a 20s timeout
    // clipped them into false failures.
    public ToolTestRunner() : this(TimeSpan.FromSeconds(90)) { }

    public ToolTestRunner(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    /// <summary>Run a single declared check for a tool.</summary>
    public async Task<ToolTestResult> RunTestAsync(ToolDescriptor tool, ToolTest test, CancellationToken ct = default)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        if (test is null) throw new ArgumentNullException(nameof(test));
        if (!tool.Tests.Contains(test))
            throw new InvalidOperationException($"Refusing to run a test not declared for {tool.Name} (manifest is the only source of runnable commands).");

        FileLog.Write($"[ToolTestRunner] RunTest: {tool.Name} [{test.Kind}]");

        // OnPath is a pure file-existence check - no process.
        if (test.Kind == ToolTestKind.OnPath)
        {
            var exists = File.Exists(tool.BinaryPath);
            return new ToolTestResult(
                test.Kind, test.Label, exists, 0, null, "", "",
                exists ? $"found at {tool.BinaryPath}" : $"missing: {tool.BinaryPath}");
        }

        if (!File.Exists(tool.BinaryPath))
        {
            return new ToolTestResult(
                test.Kind, test.Label, false, 0, null, "", "",
                $"skipped: binary not built ({tool.BinaryPath})");
        }

        return await RunProcessAsync(tool, test, ct);
    }

    /// <summary>Run every declared check for a tool, in order, and return the results.</summary>
    public async Task<IReadOnlyList<ToolTestResult>> RunAllForToolAsync(ToolDescriptor tool, CancellationToken ct = default)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        var results = new List<ToolTestResult>(tool.Tests.Count);
        foreach (var test in tool.Tests)
            results.Add(await RunTestAsync(tool, test, ct));
        return results;
    }

    private async Task<ToolTestResult> RunProcessAsync(ToolDescriptor tool, ToolTest test, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool.BinaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(tool.BinaryPath) ?? Environment.CurrentDirectory,
        };
        foreach (var arg in test.Args)
            psi.ArgumentList.Add(arg);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var sw = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                sw.Stop();
                return new ToolTestResult(
                    test.Kind, test.Label, false, sw.ElapsedMilliseconds, null,
                    stdout.ToString(), stderr.ToString(),
                    $"timed out after {_timeout.TotalSeconds:0}s");
            }

            sw.Stop();
            var outText = stdout.ToString();
            var errText = stderr.ToString();
            var exit = process.ExitCode;

            var passed = exit == 0;
            string message;
            if (!passed)
            {
                message = $"exit {exit}";
            }
            else if (test.ExpectContains is { Length: > 0 } expect && !outText.Contains(expect, StringComparison.OrdinalIgnoreCase))
            {
                passed = false;
                message = $"exit 0 but output missing \"{expect}\"";
            }
            else
            {
                message = test.Kind == ToolTestKind.Version
                    ? FirstNonEmptyLine(outText) ?? "ok"
                    : "ok";
            }

            return new ToolTestResult(test.Kind, test.Label, passed, sw.ElapsedMilliseconds, exit, outText, errText, message);
        }
        catch (Exception ex)
        {
            sw.Stop();
            FileLog.Write($"[ToolTestRunner] {tool.Name} [{test.Kind}] error: {ex.Message}");
            return new ToolTestResult(
                test.Kind, test.Label, false, sw.ElapsedMilliseconds, null,
                stdout.ToString(), stderr.ToString(), $"launch error: {ex.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* process already gone; nothing to clean up */ }
    }

    private static string? FirstNonEmptyLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return null;
    }
}
