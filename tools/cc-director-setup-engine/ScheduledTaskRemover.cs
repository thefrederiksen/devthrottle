using System.Diagnostics;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of trying to remove one scheduled task.</summary>
public sealed record ScheduledTaskResult(string TaskName, bool Present, bool Removed, string? Error);

/// <summary>
/// Removes the Windows Scheduled Tasks CC Director creates for clean-parentage launches
/// (issue #257). Per Assumption 4 these may have been created by the installer OR by agent
/// tooling - the uninstaller removes them if present regardless of creator, and treats an
/// absent task as a no-op (idempotent), never an error.
///
/// The OS call (schtasks.exe) is isolated behind the <see cref="Runner"/> seam so the
/// removal policy is unit-testable without elevated rights or real tasks; the default runner
/// is the only piece that touches the OS.
/// </summary>
public static class ScheduledTaskRemover
{
    /// <summary>The task names CC Director owns. Both are launch helpers documented in CLAUDE.md.</summary>
    public static readonly IReadOnlyList<string> TaskNames = new[]
    {
        "cc-director-launch",
        "cc-director-gateway-launch",
    };

    /// <summary>Query-and-delete one task by name. The seam: tests substitute a fake; production
    /// uses <see cref="DefaultRunner"/> (schtasks.exe).</summary>
    public delegate ScheduledTaskResult Runner(string taskName);

    /// <summary>Remove every owned task that is present. Absent tasks are reported as skipped,
    /// not errored (idempotent). Pure with respect to the injected runner.</summary>
    public static IReadOnlyList<ScheduledTaskResult> RemoveAll(Runner? runner = null)
    {
        var run = runner ?? DefaultRunner;
        return TaskNames.Select(t => run(t)).ToList();
    }

    /// <summary>schtasks.exe-backed runner (Windows only). On other platforms there are no
    /// scheduled tasks, so every task reports as not present.</summary>
    public static ScheduledTaskResult DefaultRunner(string taskName)
    {
        if (!OperatingSystem.IsWindows())
            return new ScheduledTaskResult(taskName, Present: false, Removed: false, Error: null);
        return WindowsRunner(taskName);
    }

    [SupportedOSPlatform("windows")]
    private static ScheduledTaskResult WindowsRunner(string taskName)
    {
        // External-process boundary: own the try-catch here (a failure to talk to schtasks must
        // become a per-task error in the report, never crash the whole uninstall).
        try
        {
            var query = RunSchtasks($"/Query /TN \"{taskName}\"");
            if (query.ExitCode != 0)
                return new ScheduledTaskResult(taskName, Present: false, Removed: false, Error: null);

            var delete = RunSchtasks($"/Delete /TN \"{taskName}\" /F");
            return delete.ExitCode == 0
                ? new ScheduledTaskResult(taskName, Present: true, Removed: true, Error: null)
                : new ScheduledTaskResult(taskName, Present: true, Removed: false,
                    Error: string.IsNullOrWhiteSpace(delete.Error) ? $"schtasks exit {delete.ExitCode}" : delete.Error.Trim());
        }
        catch (Exception ex)
        {
            return new ScheduledTaskResult(taskName, Present: true, Removed: false, Error: ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static (int ExitCode, string Output, string Error) RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start schtasks.exe");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, stdout, stderr);
    }
}
