using System.Runtime.Versioning;
using CcDirector.Core.ErrorReports;

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
    internal enum Verdict
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
    internal enum DisabledState
    {
        /// <summary>The list does not name the launcher, or names it as enabled.</summary>
        Enabled,
        /// <summary>The list names the launcher as disabled.</summary>
        Disabled,
        /// <summary>The list could not be read.</summary>
        Unknown,
    }

    /// <summary>The decision, in words, for the log and for tests.</summary>
    internal sealed record Decision(Verdict Verdict, string Reason);

    /// <summary>
    /// What to do about the launcher, from what the machine says. Pure.
    /// </summary>
    /// <param name="plistExists">Whether the launch agent property list is on disk.</param>
    /// <param name="jobLoaded">Whether launchctl print answered for the label (exit 0).</param>
    /// <param name="launchctlPrint">launchctl print's output when loaded.</param>
    /// <param name="installedLaunchersRunning">How many launcher processes run from the install folder.</param>
    /// <param name="disabled">What launchd's disabled list says about the launcher.</param>
    internal static Decision Decide(bool plistExists, bool jobLoaded, string? launchctlPrint, int installedLaunchersRunning, DisabledState disabled)
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
    /// Reads launchctl print-disabled's answer for the user domain, strictly. The answer is exactly one
    /// dictionary - the header <c>disabled services = {</c> as the FIRST line, one <c>"label" => value</c>
    /// line per service a person disabled (value disabled or true; enabled or false for one turned back on)
    /// and a closing brace as the LAST line. Only a complete, recognisable dictionary is believed: an empty,
    /// truncated or malformed answer, a header that merely contains the word, anything before the header or
    /// after the brace, a nested or second dictionary, a value this does not know on ANY label, or ANY label
    /// named twice is Unknown, and Unknown never repairs. A launcher a complete dictionary does not name is
    /// enabled. Pure.
    /// </summary>
    internal static DisabledState ParseDisabled(string? printDisabledOutput)
    {
        if (string.IsNullOrWhiteSpace(printDisabledOutput)) return DisabledState.Unknown;
        var lines = printDisabledOutput.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        // Exactly one dictionary: the header is the first line and the closing brace is the last. A prefix that
        // looks right followed by anything else - a second dictionary, trailing text, a nested block - is not
        // the answer this knows, and is Unknown.
        if (lines.Count < 2) return DisabledState.Unknown;
        // The one header launchctl prints for this answer - not a line that merely contains the word.
        if (!string.Equals(lines[0], "disabled services = {", StringComparison.OrdinalIgnoreCase)) return DisabledState.Unknown;
        if (lines[^1] != "}") return DisabledState.Unknown;

        var ours = "\"" + LauncherLaunchdAutostart.Label + "\"";
        var labels = new HashSet<string>(StringComparer.Ordinal);
        DisabledState? found = null;
        for (var i = 1; i < lines.Count - 1; i++)
        {
            var line = lines[i];
            // Every inner line is one entry: a quoted label, an arrow, and one of the four known values.
            if (!line.StartsWith('"')) return DisabledState.Unknown;
            var closingQuote = line.IndexOf('"', 1);
            if (closingQuote < 0) return DisabledState.Unknown;
            var rest = line[(closingQuote + 1)..].TrimStart();
            if (!rest.StartsWith("=>", StringComparison.Ordinal)) return DisabledState.Unknown;
            var state = rest[2..].Trim().ToLowerInvariant() switch
            {
                "disabled" or "true" => DisabledState.Disabled,
                "enabled" or "false" => DisabledState.Enabled,
                _ => DisabledState.Unknown,
            };
            if (state == DisabledState.Unknown) return DisabledState.Unknown;
            var label = line[..(closingQuote + 1)];
            if (!labels.Add(label)) return DisabledState.Unknown; // any label named twice: this is not one dictionary
            if (label == ours) found = state;
        }
        return found ?? DisabledState.Enabled;
    }

    /// <summary>How long a rebuilt job is given to show a process before the repair is called a failure: the
    /// installer's wait, which covers launchd's ten-second respawn throttle twice over.</summary>
    internal static readonly TimeSpan DefaultStartWait = LauncherMacInstaller.DefaultLaunchdPidWait;

    /// <summary>How often launchd is asked again while waiting for the process.</summary>
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// One pass, as the Director runs it at start-up: look once, rebuild when the decision says so, and send
    /// EXACTLY ONE report of what the pass came to through <paramref name="reporter"/> - rebuilt with the
    /// process launchd reports, left alone with its verdict and reason, or failed with its diagnostics (owner
    /// ruling of 7 October 2026). A pass that throws is reported as failed too, and its exception goes no further:
    /// this is the entry point of a background pass. The parameters after <paramref name="reporter"/> are
    /// <see cref="RunOnce"/>'s.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static LauncherRepairOutcome RunPass(InstallLayout layout, ErrorReporter reporter, LauncherLaunchdAutostart.CommandRunner? run = null,
        TimeSpan? startWait = null, TimeSpan? pollInterval = null, Func<int>? installedLaunchersRunning = null, string? plistPath = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(reporter);
        EngineLog.Write("[LauncherLaunchdRepair] RunPass: start");
        LauncherRepairOutcome outcome;
        IReadOnlyList<string> heldLines;
        OutcomeScope scope0;
        // Every error line the pass logs - a rebuild that throws logs its own FAILED line, and so does RunOnce
        // before it rethrows - is held for the pass's one report instead of becoming a second one.
        using (var scope = reporter.BeginOutcomeScope())
        {
            try
            {
                outcome = RunOnce(layout, run, startWait, pollInterval, installedLaunchersRunning, plistPath);
            }
            catch (Exception ex)
            {
                outcome = new(LauncherRepairResult.Failed, "Exception", $"the repair pass ended with {ex.GetType().Name}: {ex.Message}",
                    0, $"FAILED to finish the repair pass: {ex}");
            }
            // The pass's whole line is written to the log HERE, inside the scope: its text can carry a marker word
            // (a roll back that failed reads "Roll back FAILED (...)"), and outside the scope that would make it
            // a second report. The Director's own line after the pass carries only the result and the verdict.
            EngineLog.Write($"[LauncherLaunchdRepair] RunPass: {outcome.Line}");
            scope0 = scope;
        }
        // Read after the scope has closed, so nothing held while it was open can be missed. The pass's own line,
        // logged by RunOnce and above, is already the report's detail and is not repeated.
        var ownLines = new HashSet<string>(StringComparer.Ordinal)
        {
            $"[LauncherLaunchdRepair] RunOnce: {outcome.Line}",
            $"[LauncherLaunchdRepair] RunPass: {outcome.Line}",
        };
        heldLines = scope0.HeldLines.Where(l => !ownLines.Contains(l)).ToList();
        LauncherRepairReport.Send(outcome, heldLines, reporter);
        EngineLog.Write($"[LauncherLaunchdRepair] RunPass: {outcome.Result} ({outcome.Verdict})");
        return outcome;
    }

    /// <summary>
    /// Look once, and rebuild the job when the decision says so. Returns what was found and done; its
    /// <see cref="LauncherRepairOutcome.Line"/> is the one line the Director logs. Success is claimed only when
    /// launchd reports a process for the rebuilt job within <paramref name="startWait"/>. Reports nothing:
    /// <see cref="RunPass"/> is what sends the pass's one report, and it is the only way in from outside the engine:
    /// internal, so the Director cannot call this and send nothing.
    /// </summary>
    /// <param name="layout">Where the launcher is installed.</param>
    /// <param name="run">How launchctl and the id command (which answers the user identifier) are run; the bounded <see cref="LauncherLaunchdAutostart.DefaultRunner"/> in production.</param>
    /// <param name="startWait">How long to wait for launchd to report a process after the rebuild.</param>
    /// <param name="pollInterval">How often to ask launchd again while waiting.</param>
    /// <param name="installedLaunchersRunning">How many launcher processes run from the install folder; the process list in production.</param>
    /// <param name="plistPath">The launch agent property list; the real one in production.</param>
    [SupportedOSPlatform("macos")]
    internal static LauncherRepairOutcome RunOnce(InstallLayout layout, LauncherLaunchdAutostart.CommandRunner? run = null, TimeSpan? startWait = null,
        TimeSpan? pollInterval = null, Func<int>? installedLaunchersRunning = null, string? plistPath = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        EngineLog.Write("[LauncherLaunchdRepair] RunOnce: looking at the launcher's launch agent");
        try
        {
            var outcome = RunOnceCore(layout, run ?? LauncherLaunchdAutostart.DefaultRunner, startWait ?? DefaultStartWait,
                pollInterval ?? DefaultPollInterval,
                installedLaunchersRunning ?? (() => InstalledLauncherProcesses.Ours(layout.LauncherDir, InstalledLauncherProcesses.List()).Count),
                plistPath ?? LauncherLaunchdAutostart.PlistPath);
            EngineLog.Write($"[LauncherLaunchdRepair] RunOnce: {outcome.Line}");
            return outcome;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherLaunchdRepair] RunOnce FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static LauncherRepairOutcome RunOnceCore(InstallLayout layout, LauncherLaunchdAutostart.CommandRunner run, TimeSpan startWait,
        TimeSpan pollInterval, Func<int> installedLaunchersRunning, string plistPath)
    {
        var binary = layout.PathFor(ComponentRegistry.Launcher);
        if (!File.Exists(binary))
        {
            var noBinary = $"no launcher binary at {binary}; nothing to repair";
            return new(LauncherRepairResult.LeftAlone, "NoLauncherBinary", noBinary, 0, noBinary);
        }

        var (uidExit, uidOutput) = run("/usr/bin/id", "-u");
        if (uidExit != 0 || !int.TryParse(uidOutput.Trim(), out var uid))
            return Failed("NotChecked", $"the user id could not be resolved (exit {uidExit}); the launcher was not checked",
                $"FAILED to resolve the user id (exit {uidExit}); the launcher was not checked");

        var (printExit, print) = run("/bin/launchctl", $"print gui/{uid}/{LauncherLaunchdAutostart.Label}");
        var (disabledExit, disabledOutput) = run("/bin/launchctl", $"print-disabled gui/{uid}");
        var disabled = disabledExit == 0 ? ParseDisabled(disabledOutput) : DisabledState.Unknown;
        int running;
        try
        {
            running = installedLaunchersRunning();
        }
        catch (Exception ex)
        {
            return Failed("NotChecked", $"the process list could not be read ({ex.GetType().Name}: {ex.Message}); the launcher was not checked",
                $"FAILED to read the process list ({ex.GetType().Name}: {ex.Message}); the launcher was not checked");
        }

        var decision = Decide(File.Exists(plistPath), printExit == 0, print, running, disabled);
        if (decision.Verdict != Verdict.Repair)
            return new(LauncherRepairResult.LeftAlone, decision.Verdict.ToString(), decision.Reason, 0, $"{decision.Verdict}: {decision.Reason}");

        LauncherLaunchdAutostart.RebuildResult result;
        try
        {
            var logDir = Path.Combine(layout.LogsDir, "launcher");
            result = LauncherLaunchdAutostart.Rebuild(binary, LauncherTrayInstaller.InstalledArguments, run, plistPath, logDir);
        }
        catch (Exception ex)
        {
            return Failed(decision.Verdict.ToString(), $"the launch agent could not be rebuilt after finding: {decision.Reason}",
                $"FAILED to rebuild the launch agent after finding: {decision.Reason}. {ex.GetType().Name}: {ex.Message}");
        }

        // The kickstart is a request; the process comes a moment later. Success is a process launchd reports.
        var deadline = DateTime.UtcNow + startWait;
        string? after = result.AfterPrint;
        var pid = LauncherMacInstaller.ParseLaunchdPid(after);
        while (pid <= 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(pollInterval);
            var (againExit, againPrint) = run("/bin/launchctl", $"print gui/{uid}/{LauncherLaunchdAutostart.Label}");
            after = againExit == 0 ? againPrint : after;
            pid = LauncherMacInstaller.ParseLaunchdPid(after ?? "");
        }
        var done = $"Rebuilt the launch agent: {string.Join("; ", result.Steps)}";
        if (pid > 0)
            return new(LauncherRepairResult.Rebuilt, decision.Verdict.ToString(), decision.Reason, pid,
                $"{decision.Verdict}: {decision.Reason}. {done}. launchd reports the launcher running as process {pid}");
        var launchdSays = LaunchdDiagnostics.Explain(after, after is not null) ?? "no useful answer";
        return Failed(decision.Verdict.ToString(), $"the launcher did not start after the launch agent was rebuilt (found: {decision.Reason}); launchd says: {launchdSays}",
            $"FAILED to start the launcher after rebuilding the launch agent (found: {decision.Reason}). {done}. launchd still reports no process: {launchdSays}");
    }

    private static LauncherRepairOutcome Failed(string verdict, string reason, string line)
        => new(LauncherRepairResult.Failed, verdict, reason, 0, line);
}
