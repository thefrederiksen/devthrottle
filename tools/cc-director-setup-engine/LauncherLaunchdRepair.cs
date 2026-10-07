using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The Director's repair of a launcher macOS will not start. The Director already owns the launcher's
/// UPDATE (<see cref="LauncherUpdateOwner"/>, #2719), for one reason: whatever fixes a broken launcher has
/// to be something other than the broken launcher, and the Director is the one process on the machine that
/// a person opens and that updates itself. The same reason makes it the owner of the launcher's REGISTRATION.
///
/// One user's Mac (#3411) held a launch agent launchd refused to spawn for two weeks ("78: EX_CONFIG",
/// "spawn failed"). Every path to a fix on that machine was a command the user had to type, because the only
/// thing that re-registered the agent was the installer. With this, a Director that finds the job refused
/// rebuilds it (<see cref="LauncherLaunchdAutostart.Rebuild"/>) the way the installer now does - and the
/// Director reaches the machine through its own update, which the launcher's absence does not block.
///
/// WHAT IS NOT REPAIRED, deliberately. A launcher somebody CLOSED is not a broken launcher: the tray's Quit
/// exits cleanly, and KeepAlive's SuccessfulExit=false means launchd leaves it exited on purpose. A launch
/// agent somebody turned OFF (no property list) is a choice. Only a job launchd itself refused - "spawn
/// failed", or a non-zero exit with nothing running - is rebuilt. The decision is pure (<see cref="Decide"/>)
/// so every branch is testable without a Mac.
/// </summary>
public static class LauncherLaunchdRepair
{
    public enum Verdict
    {
        /// <summary>No launch agent property list: the person turned start-at-login off. Left alone.</summary>
        NoLaunchAgent,
        /// <summary>launchd reports the job running. Nothing to do.</summary>
        Running,
        /// <summary>The job is loaded and exited cleanly, or was never run: a closed launcher. Left alone.</summary>
        ClosedOnPurpose,
        /// <summary>launchd has the job but refused or could not keep it: rebuild it.</summary>
        Repair,
        /// <summary>The property list exists but launchd does not hold the job and no launcher runs: rebuild it.</summary>
        RepairNotLoaded,
    }

    /// <summary>The decision, in words, for the log and for tests.</summary>
    public sealed record Decision(Verdict Verdict, string Reason);

    /// <summary>
    /// What to do about the launcher, from what the machine says. Pure.
    /// </summary>
    /// <param name="plistExists">Whether the launch agent property list is on disk.</param>
    /// <param name="jobLoaded">Whether launchctl print answered for the label (exit 0).</param>
    /// <param name="launchctlPrint">launchctl print's output when loaded.</param>
    /// <param name="installedLaunchersRunning">How many launcher processes run from the install folder.</param>
    public static Decision Decide(bool plistExists, bool jobLoaded, string? launchctlPrint, int installedLaunchersRunning)
    {
        if (!plistExists)
            return new(Verdict.NoLaunchAgent, "no launch agent property list: start at login is off, nothing is repaired");

        if (installedLaunchersRunning > 0)
            return new(Verdict.Running, $"{installedLaunchersRunning} installed launcher process(es) running");

        if (!jobLoaded)
            return new(Verdict.RepairNotLoaded, "the launch agent exists but launchd does not hold the job and no launcher runs");

        var pid = LauncherMacInstaller.ParseLaunchdPid(launchctlPrint ?? "");
        if (pid > 0)
            return new(Verdict.Running, $"launchd reports the launcher running as process {pid}");

        var jobState = LaunchdDiagnostics.Field(launchctlPrint, "job state");
        var exitCode = LaunchdDiagnostics.Field(launchctlPrint, "last exit code");
        var exitReason = LaunchdDiagnostics.Field(launchctlPrint, "last exit reason");
        var signal = LaunchdDiagnostics.Field(launchctlPrint, "last terminating signal");

        if (string.Equals(jobState, "spawn failed", StringComparison.OrdinalIgnoreCase)
            || (exitCode is not null && exitCode.StartsWith("78", StringComparison.Ordinal)))
            return new(Verdict.Repair, $"launchd refused to spawn the launcher (job state = {jobState ?? "unknown"}, last exit code = {exitCode ?? "unknown"})");
        if (!string.IsNullOrEmpty(signal))
            return new(Verdict.Repair, $"the launcher was stopped by a signal ({signal}) and is not running");
        if (!string.IsNullOrEmpty(exitReason))
            return new(Verdict.Repair, $"the launcher was ended by macOS ({exitReason}) and is not running");
        if (!string.IsNullOrEmpty(exitCode) && exitCode != "(never exited)" && !exitCode.StartsWith("0", StringComparison.Ordinal))
            return new(Verdict.Repair, $"the launcher exited with code {exitCode} and is not running");

        return new(Verdict.ClosedOnPurpose, $"the job is loaded and not running after a clean exit (last exit code = {exitCode ?? "(never exited)"}); a closed launcher is not reopened");
    }

    /// <summary>
    /// Look once, and rebuild the job when the decision says so. Returns one line saying what was found and
    /// done; a line that starts with "FAILED" is one the error reporter carries to the Gateway.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static string RunOnce(InstallLayout layout, LauncherLaunchdAutostart.CommandRunner? run = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        run ??= LauncherLaunchdAutostart.DefaultRunner;

        var binary = layout.PathFor(ComponentRegistry.Launcher);
        if (!File.Exists(binary))
            return $"no launcher binary at {binary}; nothing to repair";

        var plistPath = LauncherLaunchdAutostart.PlistPath;
        var (uidExit, uidOutput) = run("/usr/bin/id", "-u");
        if (uidExit != 0 || !int.TryParse(uidOutput.Trim(), out var uid))
            return $"FAILED to resolve the user id (exit {uidExit}); the launcher was not checked";

        var (printExit, print) = run("/bin/launchctl", $"print gui/{uid}/{LauncherLaunchdAutostart.Label}");
        int running;
        try
        {
            running = InstalledLauncherProcesses.Ours(layout.LauncherDir, InstalledLauncherProcesses.List()).Count;
        }
        catch (Exception ex)
        {
            return $"FAILED to read the process list ({ex.GetType().Name}: {ex.Message}); the launcher was not checked";
        }

        var decision = Decide(File.Exists(plistPath), printExit == 0, print, running);
        if (decision.Verdict is not (Verdict.Repair or Verdict.RepairNotLoaded))
            return $"{decision.Verdict}: {decision.Reason}";

        try
        {
            var logDir = Path.Combine(layout.LogsDir, "launcher");
            var result = LauncherLaunchdAutostart.Rebuild(binary, LauncherTrayInstaller.InstalledArguments, run, plistPath, logDir);
            return $"{decision.Verdict}: {decision.Reason}. Rebuilt the launch agent: {string.Join("; ", result.Steps)}";
        }
        catch (Exception ex)
        {
            return $"FAILED to rebuild the launch agent after finding: {decision.Reason}. {ex.GetType().Name}: {ex.Message}";
        }
    }
}
