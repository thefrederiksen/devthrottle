using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Onboarding;

/// <summary>Outcome of an in-wizard Claude Code install attempt.</summary>
public sealed record ClaudeInstallResult(bool Success, string Message);

/// <summary>
/// Installs Claude Code from inside the first-run wizard by running the OFFICIAL claude.ai
/// installer script for the current platform - the same one the docs tell a user to paste into a
/// terminal. Windows runs the PowerShell script, macOS and Linux run the shell script. Output
/// lines stream to the caller so the wizard can show live progress, and the exit code decides
/// success. Nothing is guessed: a non-zero exit is a failure with the script's own words.
///
/// The installer script places the binary in ~/.local/bin, which agent detection probes directly
/// (see ClaudeAgentPlugin), so a re-scan straight after a successful install finds it even though
/// this process' PATH predates the install.
/// </summary>
public sealed class ClaudeCodeInstaller
{
    /// <summary>Windows: official PowerShell installer.</summary>
    internal const string WindowsInstallCommand = "irm https://claude.ai/install.ps1 | iex";

    /// <summary>macOS / Linux: official shell installer.</summary>
    internal const string UnixInstallCommand = "curl -fsSL https://claude.ai/install.sh | bash";

    // Test seam: replaces the real process run. Receives the start info, streams progress lines,
    // returns the exit code. Null -> run the real process.
    internal Func<ProcessStartInfo, IProgress<string>, CancellationToken, Task<int>>? RunProcessSeam;

    /// <summary>Build the platform's installer invocation. Internal so tests can pin its shape.</summary>
    internal static ProcessStartInfo BuildStartInfo()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{WindowsInstallCommand}\"")
            : new ProcessStartInfo("/bin/bash", $"-c \"{UnixInstallCommand}\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        return psi;
    }

    /// <summary>
    /// Run the official installer, streaming its output lines to <paramref name="progress"/>.
    /// Success is the script exiting 0; failure carries the exit code so the wizard can show it.
    /// </summary>
    public async Task<ClaudeInstallResult> InstallAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        FileLog.Write("[ClaudeCodeInstaller] InstallAsync: starting official installer");
        var psi = BuildStartInfo();

        var run = RunProcessSeam ?? RunRealProcessAsync;
        int exitCode;
        try
        {
            exitCode = await run(psi, progress, ct);
        }
        catch (OperationCanceledException)
        {
            // not-an-error: the install was cancelled on purpose
            FileLog.Write("[ClaudeCodeInstaller] InstallAsync: cancelled");
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeCodeInstaller] InstallAsync FAILED to run installer: {ex.Message}");
            return new ClaudeInstallResult(false, $"Could not run the installer: {ex.Message}");
        }

        if (exitCode == 0)
        {
            FileLog.Write("[ClaudeCodeInstaller] InstallAsync: installer exited 0");
            return new ClaudeInstallResult(true, "Claude Code installed.");
        }

        FileLog.Write($"[ClaudeCodeInstaller] InstallAsync: installer exited {exitCode}");
        return new ClaudeInstallResult(false, $"The installer exited with code {exitCode}. See the output above for the reason.");
    }

    private static async Task<int> RunRealProcessAsync(ProcessStartInfo psi, IProgress<string> progress, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress.Report(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress.Report(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException("The installer process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
