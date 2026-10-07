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
/// WHAT IS NOT REPAIRED, deliberately. Only a job launchd HOLDS and will not run is rebuilt - "spawn
/// failed", or a non-zero exit with nothing running. That state cannot be a person's choice: when a person
/// turns a login item off (System Settings, Login Items and Extensions) or disables it with launchctl, launchd
/// does not hold the job at all. So a job launchd does not hold is left alone, whatever the reason - a
/// property list nobody loaded, a bootout by hand, a switched-off item - because the Director cannot tell those
/// apart and must not override the one that was a choice. The installer registers; the Director only repairs.
/// A launcher somebody CLOSED is not a broken launcher either: the tray's Quit exits cleanly, and KeepAlive's
/// SuccessfulExit=false means launchd leaves it exited on purpose. launchd's own disabled list is read as well,
/// and when it cannot be read nothing is rebuilt: the repair fails closed. The decision is pure
/// (<see cref="Decide"/>) so every branch is testable without a Mac.
/// </summary>
public static class LauncherLaunchdRepair
{
    public enum Verdict
    {
        /// <summary>No launch agent property list: the person turned start-at-login off. Left alone.</summary>
        NoLaunchAgent,
        /// <summary>launchd reports the job running, or an installed launcher process runs. Nothing to do.</summary>
        Running,
        /// <summary>The job is loaded and exited cleanly, or was never run: a closed launcher. Left alone.</summary>
        ClosedOnPurpose,
        /// <summary>launchd has the job but refused or could not keep it: rebuild it.</summary>
        Repair,
        /// <summary>The property list exists but launchd does not hold the job. Left alone: that may be a person's choice.</summary>
        NotLoaded,
        /// <summary>launchd's disabled list names the launcher: a person switched it off. Left alone.</summary>
        Disabled,
        /// <summary>launchd's disabled list could not be read, so a switched-off item cannot be ruled out. Left alone.</summary>
        Unknown,
    }

    /// <summary>What launchd's disabled list says about the launcher.</summary>
    public enum DisabledState
    {
        /// <summary>The list does not name the launcher, or names it as enabled.</summary>
        Enabled,
        /// <summary>The list names the launcher as disabled.</summary>
        Disabled,
        /// <summary>The list could not be read.</summary>
        Unknown,
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
    /// <param name="disabled">What launchd's disabled list says about the launcher.</param>
    public static Decision Decide(bool plistExists, bool jobLoaded, string? launchctlPrint, int installedLaunchersRunning, DisabledState disabled)
    {
        if (!plistExists)
            return new(Verdict.NoLaunchAgent, "no launch agent property list: start at login is off, nothing is repaired");

        if (installedLaunchersRunning > 0)
            return new(Verdict.Running, $"{installedLaunchersRunning} installed launcher process(es) running");

        if (disabled == DisabledState.Unknown)
            return new(Verdict.Unknown, "launchd's disabled list could not be read, so a switched-off launcher cannot be ruled out; nothing is rebuilt");

        if (disabled == DisabledState.Disabled)
            return new(Verdict.Disabled, "launchd's disabled list names the launcher: somebody switched it off, and that is not overridden");

        if (!jobLoaded)
            return new(Verdict.NotLoaded, "the launch agent exists but launchd does not hold the job; a job nobody loaded may be a choice and is not rebuilt (the installer registers, the Director only repairs)");

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
    /// Reads launchctl print-disabled's answer for the user domain. The list names each service a person
    /// disabled, as <c>"label" => disabled</c> (older releases: <c>=> true</c>). A launcher it does not name
    /// is enabled. Pure.
    /// </summary>
    public static DisabledState ParseDisabled(string? printDisabledOutput)
    {
        if (printDisabledOutput is null) return DisabledState.Unknown;
        foreach (var raw in printDisabledOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("\"" + LauncherLaunchdAutostart.Label + "\"", StringComparison.Ordinal)) continue;
            var arrow = line.IndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0) continue;
            var value = line[(arrow + 2)..].Trim().ToLowerInvariant();
            return value is "disabled" or "true" ? DisabledState.Disabled : DisabledState.Enabled;
        }
        return DisabledState.Enabled;
    }

    /// <summary>How long a rebuilt job is given to show a process before the repair is called a failure.</summary>
    public static readonly TimeSpan DefaultStartWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Look once, and rebuild the job when the decision says so. Returns one line saying what was found and
    /// done; a line that starts with "FAILED" is one the error reporter carries to the Gateway. Success is
    /// claimed only when launchd reports a process for the rebuilt job within <paramref name="startWait"/>.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static string RunOnce(InstallLayout layout, LauncherLaunchdAutostart.CommandRunner? run = null, TimeSpan? startWait = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        EngineLog.Write("[LauncherLaunchdRepair] RunOnce: looking at the launcher's launch agent");
        try
        {
            var outcome = RunOnceCore(layout, run ?? LauncherLaunchdAutostart.DefaultRunner, startWait ?? DefaultStartWait);
            EngineLog.Write($"[LauncherLaunchdRepair] RunOnce: {outcome}");
            return outcome;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherLaunchdRepair] RunOnce FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    [SupportedOSPlatform("macos")]
    private static string RunOnceCore(InstallLayout layout, LauncherLaunchdAutostart.CommandRunner run, TimeSpan startWait)
    {
        var binary = layout.PathFor(ComponentRegistry.Launcher);
        if (!File.Exists(binary))
            return $"no launcher binary at {binary}; nothing to repair";

        var plistPath = LauncherLaunchdAutostart.PlistPath;
        var (uidExit, uidOutput) = run("/usr/bin/id", "-u");
        if (uidExit != 0 || !int.TryParse(uidOutput.Trim(), out var uid))
            return $"FAILED to resolve the user id (exit {uidExit}); the launcher was not checked";

        var (printExit, print) = run("/bin/launchctl", $"print gui/{uid}/{LauncherLaunchdAutostart.Label}");
        var (disabledExit, disabledOutput) = run("/bin/launchctl", $"print-disabled gui/{uid}");
        var disabled = disabledExit == 0 ? ParseDisabled(disabledOutput) : DisabledState.Unknown;
        int running;
        try
        {
            running = InstalledLauncherProcesses.Ours(layout.LauncherDir, InstalledLauncherProcesses.List()).Count;
        }
        catch (Exception ex)
        {
            return $"FAILED to read the process list ({ex.GetType().Name}: {ex.Message}); the launcher was not checked";
        }

        var decision = Decide(File.Exists(plistPath), printExit == 0, print, running, disabled);
        if (decision.Verdict != Verdict.Repair)
            return $"{decision.Verdict}: {decision.Reason}";

        LauncherLaunchdAutostart.RebuildResult result;
        try
        {
            var logDir = Path.Combine(layout.LogsDir, "launcher");
            result = LauncherLaunchdAutostart.Rebuild(binary, LauncherTrayInstaller.InstalledArguments, run, plistPath, logDir);
        }
        catch (Exception ex)
        {
            return $"FAILED to rebuild the launch agent after finding: {decision.Reason}. {ex.GetType().Name}: {ex.Message}";
        }

        // The kickstart is a request; the process comes a moment later. Success is a process launchd reports.
        var deadline = DateTime.UtcNow + startWait;
        var after = result.AfterPrint;
        var pid = LauncherMacInstaller.ParseLaunchdPid(after ?? "");
        while (pid <= 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
            var (againExit, againPrint) = run("/bin/launchctl", $"print gui/{uid}/{LauncherLaunchdAutostart.Label}");
            after = againExit == 0 ? againPrint : after;
            pid = LauncherMacInstaller.ParseLaunchdPid(after ?? "");
        }
        var done = $"Rebuilt the launch agent: {string.Join("; ", result.Steps)}";
        return pid > 0
            ? $"{decision.Verdict}: {decision.Reason}. {done}. launchd reports the launcher running as process {pid}"
            : $"FAILED to start the launcher after rebuilding the launch agent (found: {decision.Reason}). {done}. launchd still reports no process: {LaunchdDiagnostics.Explain(after, after is not null) ?? "no useful answer"}";
    }
}
