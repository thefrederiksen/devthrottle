using System.Diagnostics;
using System.Text;

namespace CcDirector.Setup.Engine;

/// <summary>Runs a short external command (sc.exe/reg.exe) and captures its exit code + output.</summary>
internal static class ProcessRunner
{
    /// <summary>
    /// The default bound for a single process run. Long enough that a legitimate offline pip install of
    /// roughly twenty wheels (several minutes of disk-bound work) never trips it, but bounded so a hung
    /// pip or venv step fails loudly instead of stalling the wizard forever. The hang-forever case is the
    /// exact failure that left tools half-installed in the field (issue #577).
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The exit code returned when a run is killed for exceeding its timeout.</summary>
    public const int TimeoutExitCode = -559038737; // 0xDEADBEEF as a signed int - a sentinel no real exe returns.

    public static (int exit, string output) Run(string exe, string arguments)
        => Run(exe, arguments, onStdoutLine: null, timeout: DefaultTimeout);

    public static (int exit, string output) Run(string exe, string arguments, Action<string>? onStdoutLine)
        => Run(exe, arguments, onStdoutLine, DefaultTimeout);

    /// <summary>
    /// Runs a process, capturing stdout/stderr to a combined string AND streaming each stdout line to
    /// <paramref name="onStdoutLine"/> as it arrives. Use the streaming callback for long-running commands
    /// (e.g. pip install) so the UI/log can show live progress instead of waiting for the process to exit.
    /// Uses event-driven async pipe reads so neither pipe can deadlock the child by filling its buffer.
    ///
    /// The run is bounded by <paramref name="timeout"/>: when it elapses, the whole process tree is killed
    /// and a loud failure is returned (exit code <see cref="TimeoutExitCode"/>) carrying the partial output
    /// captured so far. A bounded wait is what stops a hung pip step from stalling the install forever.
    /// </summary>
    public static (int exit, string output) Run(string exe, string arguments, Action<string>? onStdoutLine, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}.");

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdoutBuf.AppendLine(e.Data);
            onStdoutLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrBuf.AppendLine(e.Data);
        };

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var timeoutMs = timeout <= TimeSpan.Zero ? 0 : (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue);
        if (!p.WaitForExit(timeoutMs))
        {
            // Bounded wait expired: kill the whole tree (pip spawns child processes) and fail loud with
            // whatever output we have. Never silently hang - that is the root defect this guards against.
            EngineLog.Write($"[ProcessRunner] TIMEOUT after {timeout.TotalSeconds:F0}s, killing process tree: {exe} {arguments}");
            try { p.Kill(entireProcessTree: true); } catch (Exception ex) { EngineLog.Write($"[ProcessRunner] kill after timeout failed: {ex.Message}"); }
            try { p.WaitForExit(5_000); } catch { /* best-effort drain after kill */ }

            var partial = Combine(stdoutBuf, stderrBuf);
            return (TimeoutExitCode, $"TIMEOUT: '{exe}' exceeded {timeout.TotalSeconds:F0}s and was killed.\n{partial}");
        }

        return (p.ExitCode, Combine(stdoutBuf, stderrBuf));
    }

    private static string Combine(StringBuilder stdoutBuf, StringBuilder stderrBuf)
    {
        var stdout = stdoutBuf.ToString();
        var stderr = stderrBuf.ToString();
        return string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
    }
}
