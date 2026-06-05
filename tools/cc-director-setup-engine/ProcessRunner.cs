using System.Diagnostics;
using System.Text;

namespace CcDirector.Setup.Engine;

/// <summary>Runs a short external command (sc.exe/reg.exe) and captures its exit code + output.</summary>
internal static class ProcessRunner
{
    public static (int exit, string output) Run(string exe, string arguments)
        => Run(exe, arguments, onStdoutLine: null);

    /// <summary>
    /// Runs a process, capturing stdout/stderr to a combined string AND streaming each stdout line to
    /// <paramref name="onStdoutLine"/> as it arrives. Use the streaming callback for long-running commands
    /// (e.g. pip install) so the UI/log can show live progress instead of waiting for the process to exit.
    /// Uses event-driven async pipe reads so neither pipe can deadlock the child by filling its buffer.
    /// </summary>
    public static (int exit, string output) Run(string exe, string arguments, Action<string>? onStdoutLine)
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
        p.WaitForExit();

        var stdout = stdoutBuf.ToString();
        var stderr = stderrBuf.ToString();
        return (p.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}");
    }
}
