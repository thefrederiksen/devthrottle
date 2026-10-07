using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The CC Launcher's per-user autostart on macOS: a launchd user launch agent at
/// ~/Library/LaunchAgents/com.devthrottle.cc-launcher.plist. The macOS twin of
/// <see cref="LauncherAutostart"/> (the Windows Run key), living in the engine for the
/// same reason: the installer, the uninstaller, and the launcher itself must agree on
/// one label, one property list path, and one command-line format.
///
/// The agent is registered with RunAtLoad (start at login) and KeepAlive with
/// SuccessfulExit=false: launchd resurrects the launcher after a crash or kill, but a
/// CLEAN exit (the tray Quit item, or the POST /shutdown a self-update helper sends)
/// stays exited - otherwise launchd would race the self-update helper by relaunching
/// the old binary the moment it stopped.
///
/// Registration is a two-step: write the property list, then hand it to launchd with
/// "launchctl bootstrap gui/&lt;uid&gt;". Bootstrap also starts the agent immediately
/// (RunAtLoad applies at bootstrap time); when the launcher registers itself at startup
/// this spawns a short-lived duplicate that exits through the single-instance mutex.
/// </summary>
public static class LauncherLaunchdAutostart
{
    /// <summary>The launchd service label (also the property list file name).</summary>
    public const string Label = "com.devthrottle.cc-launcher";

    /// <summary>The user launch-agent property list path: ~/Library/LaunchAgents/{Label}.plist.</summary>
    public static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    /// <summary>
    /// The full property list for the given executable and arguments. Pure, for tests.
    /// Standard output and error go to logDir so a crash before FileLog starts is not silent.
    /// </summary>
    public static string PlistContent(string exePath, string? arguments, string logDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDir);

        var argElements = new StringBuilder();
        argElements.Append($"        <string>{Xml(exePath)}</string>\n");
        foreach (var arg in SplitArguments(arguments))
            argElements.Append($"        <string>{Xml(arg)}</string>\n");

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
            {argElements.ToString().TrimEnd('\n')}
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                    <key>SuccessfulExit</key>
                    <false/>
                </dict>
                <key>ProcessType</key>
                <string>Interactive</string>
                <key>StandardOutPath</key>
                <string>{Xml(Path.Combine(logDir, "launchd-stdout.log"))}</string>
                <key>StandardErrorPath</key>
                <string>{Xml(Path.Combine(logDir, "launchd-stderr.log"))}</string>
            </dict>
            </plist>

            """;
    }

    /// <summary>
    /// Ensure the launch agent is registered and loaded for the given executable and
    /// arguments. Idempotent: returns true if a write or a launchd (re)bootstrap was
    /// performed, false if everything was already correct.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool EnsureRegistered(string exePath, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var logDir = Path.Combine(InstallLayout.Default().LogsDir, "launcher");
        var desired = PlistContent(exePath, arguments, logDir);
        EngineLog.Write($"[LauncherLaunchdAutostart] EnsureRegistered: exe={exePath}, args={arguments ?? "(none)"}");

        var plistUnchanged = File.Exists(PlistPath)
            && string.Equals(File.ReadAllText(PlistPath), desired, StringComparison.Ordinal);

        if (plistUnchanged && IsLoaded())
        {
            EngineLog.Write("[LauncherLaunchdAutostart] EnsureRegistered: already up to date and loaded");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        Directory.CreateDirectory(logDir);
        File.WriteAllText(PlistPath, desired);
        EngineLog.Write($"[LauncherLaunchdAutostart] EnsureRegistered: wrote {PlistPath}");

        // A changed definition must be re-bootstrapped: launchd caches the loaded plist.
        if (IsLoaded())
        {
            // The launcher itself calls this at startup, from inside this very job. A bootout
            // there kills the caller before it reaches bootstrap, and the job is gone until the
            // next login (product #3575). Leave the loaded job alone; launchd reads the new
            // definition at the next login or the next install.
            if (!MayReload(Environment.GetEnvironmentVariable("XPC_SERVICE_NAME")))
            {
                EngineLog.Write("[LauncherLaunchdAutostart] EnsureRegistered: running inside the job - wrote new definition, not booting out");
                return true;
            }

            var (outExit, outText) = ProcessRunner.Run("/bin/launchctl", $"bootout gui/{UserId()}/{Label}");
            EngineLog.Write($"[LauncherLaunchdAutostart] bootout -> exit={outExit} {Trim(outText)}");
        }

        var (exit, text) = ProcessRunner.Run("/bin/launchctl", $"bootstrap gui/{UserId()} \"{PlistPath}\"");
        if (exit != 0)
            throw new InvalidOperationException(
                $"launchctl bootstrap failed (exit {exit}): {Trim(text)}");

        EngineLog.Write("[LauncherLaunchdAutostart] EnsureRegistered: bootstrapped launch agent");
        return true;
    }

    /// <summary>Runs a short command and returns its exit code and combined output. Injectable so the
    /// rebuild can be exercised without a real launchd.</summary>
    public delegate (int Exit, string Output) CommandRunner(string executable, string arguments);

    /// <summary>What launchd held for the label before a rebuild, and what the rebuild did.</summary>
    /// <param name="PreviousPrint">launchctl print's answer before the rebuild, when launchd held the job.</param>
    /// <param name="PreviousLoaded">Whether launchd held the job before the rebuild.</param>
    /// <param name="Steps">What was done, in order, for the install steps and the report.</param>
    /// <param name="AfterPrint">launchctl print's answer after the kickstart, when launchd answered.</param>
    public sealed record RebuildResult(string? PreviousPrint, bool PreviousLoaded, IReadOnlyList<string> Steps, string AfterPrint);

    /// <summary>
    /// Define the launch agent from scratch and make launchd run it NOW, whatever it held before.
    ///
    /// WHY NOT KICKSTART WHAT IS THERE. An install used to look for the property list on disk and, finding
    /// one, only ask launchd to restart the job it already had. On one user's Mac that job was the one the
    /// very first install had registered, and launchd refused to spawn it ("78: EX_CONFIG", "spawn failed")
    /// on 23 September, 24 September, 25 September, 28 September and 6 October 2026 - every install after
    /// the first restarted the same refused job, and nothing any later installer did to registration could
    /// reach the machine, because registration was never run again. A job launchd holds is not evidence
    /// that it is a job launchd will run.
    ///
    /// So this boots the old job out (whoever submitted it: our bootstrap, or the system's own login
    /// loading of the property list), writes the property list the current build defines, makes sure the
    /// folder its log paths point into exists, bootstraps it, and then kickstarts it. The kickstart matters:
    /// a user domain can be in launchd's "on-demand-only" mode, in which RunAtLoad at bootstrap only leaves a
    /// "pending spawn" and nothing starts - the user's Mac logged exactly that line. A kickstart is an
    /// explicit demand and is honoured in that mode.
    ///
    /// NEVER FROM INSIDE THE JOB: booting out one's own job ends the caller before the bootstrap (#3575).
    /// The installer and the Director call this; the launcher keeps <see cref="EnsureRegistered"/>.
    /// </summary>
    /// <param name="run">How launchctl and the id command (which answers the user identifier) are run; <see cref="DefaultRunner"/> in production.</param>
    /// <param name="plistPath">The property list path; the user's launch agent in production.</param>
    /// <param name="logDir">Where the launchd stdout and stderr files go; the launcher log folder in production.</param>
    [SupportedOSPlatform("macos")]
    /// <summary>
    /// The bound on any one launchctl or id run. launchd can leave a kickstart hanging - on a real Mac, a
    /// kickstart of a program macOS refuses to run never answers - so a minute bounds it, instead of
    /// ProcessRunner's fifteen-minute default holding a repair or a report for that long.
    /// </summary>
    internal static TimeSpan CommandTimeout => LauncherMacInstaller.CommandTimeout;

    /// <summary>
    /// How long a rebuild waits for a competing one to finish. The setup wizard installs while a running
    /// Director may be repairing, and the one that holds the lock may be sitting in a hanging kickstart for a
    /// whole command bound, so this is longer than one.
    /// </summary>
    internal static readonly TimeSpan LockWait = TimeSpan.FromSeconds(90);

    public static RebuildResult Rebuild(string exePath, string? arguments, CommandRunner? run = null,
        string? plistPath = null, string? logDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        EngineLog.Write($"[LauncherLaunchdAutostart] Rebuild: exe={exePath}");
        try
        {
            var result = RebuildCore(exePath, arguments, run ?? DefaultRunner, plistPath ?? PlistPath,
                logDir ?? Path.Combine(InstallLayout.Default().LogsDir, "launcher"));
            EngineLog.Write($"[LauncherLaunchdAutostart] Rebuild: done - {result.Steps[^1]}");
            return result;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherLaunchdAutostart] Rebuild FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// The rebuild, in an order that leaves the Mac as it was found when any step is refused: one rebuild at
    /// a time across processes, nothing asked of launchd until the replacement is staged on disk, launchd's
    /// first answer believed only when it is "held" or "not found" (an unknown answer stops the rebuild with
    /// nothing changed), the old job counted as gone only when launchd says it does not know it, and every
    /// failure after the old job is booted out - the write, the bootstrap, the kickstart, the last question -
    /// rolled back to the previous definition, with a replacement launchd took booted out first. Every
    /// launchctl answer is checked, and the staging file is removed on every path.
    /// </summary>
    private static RebuildResult RebuildCore(string exePath, string? arguments, CommandRunner run, string plistPath, string logDir)
    {
        if (!MayReload(Environment.GetEnvironmentVariable("XPC_SERVICE_NAME")))
            throw new InvalidOperationException("Rebuild must not run inside the launcher's own launchd job: booting the job out would end this process (#3575).");
        var steps = new List<string>();
        string? previousPrint = null;
        Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
        // One rebuild at a time, across processes: the wizard installs while a Director may be running its own
        // repair, and two rebuilds interleaved would boot out each other's job. The lock is a file beside the
        // property list, held until launchd has been asked what it holds at the end.
        using var held = RebuildLock.Acquire(plistPath + ".lock", LockWait);
        try
        {
            return RebuildSteps(exePath, arguments, run, plistPath, logDir, steps, print => previousPrint = print);
        }
        catch (Exception ex)
        {
            // The steps taken and launchd's answer before the rebuild travel with the failure, whatever its
            // type: a report that says only "bootstrap failed" has thrown away the history of every earlier
            // attempt on that Mac.
            throw new RebuildException(ex.Message, steps, previousPrint, ex);
        }
    }

    /// <summary>A rebuild that did not finish, with what it did and what launchd held before it started.</summary>
    public sealed class RebuildException(string message, IReadOnlyList<string> steps, string? previousPrint, Exception inner)
        : InvalidOperationException(message, inner)
    {
        /// <summary>What was done before the failure, in order.</summary>
        public IReadOnlyList<string> Steps { get; } = steps;

        /// <summary>launchctl print's answer before the rebuild, when launchd held the job.</summary>
        public string? PreviousPrint { get; } = previousPrint;
    }

    /// <summary>launchd's one answer that means "this job is not here": exit 113, "Could not find service".
    /// Anything else - a timeout, another error - is not proof of absence.</summary>
    internal static bool ServiceNotFound(int exit, string output)
        => exit == 113 || (output ?? "").Contains("Could not find service", StringComparison.OrdinalIgnoreCase);

    private static RebuildResult RebuildSteps(string exePath, string? arguments, CommandRunner run, string plistPath,
        string logDir, List<string> steps, Action<string> keepPreviousPrint)
    {
        var (uidExit, uidOutput) = run("/usr/bin/id", "-u");
        if (uidExit != 0 || !int.TryParse(uidOutput.Trim(), out var uid))
            throw new InvalidOperationException($"could not resolve the current user identifier (id -u exit {uidExit}): {Trim(uidOutput)}");
        var target = $"gui/{uid}/{Label}";

        // The replacement is written beside the old definition under a name of its own (two rebuilds never
        // share one). Whatever happens below, the one cleanup path at the end removes it when it is still
        // there: a rebuild that stops early leaves no staging file behind.
        var staged = $"{plistPath}.{Guid.NewGuid():N}.new";
        try
        {
            // 1. Stage the replacement before anything is asked of launchd: the log folder, and the new
            //    definition written and read back. A disk that refuses the write leaves launchd untouched.
            Directory.CreateDirectory(logDir);
            var desired = PlistContent(exePath, arguments, logDir);
            File.WriteAllText(staged, desired);
            if (File.ReadAllText(staged) != desired)
                throw new InvalidOperationException($"the staged launch agent at {staged} did not read back as written");
            var previousPlist = File.Exists(plistPath) ? File.ReadAllText(plistPath) : null;
            steps.Add($"staged the launch agent ({Label}) and created {logDir}");

            // 2. What launchd has, classified three ways and believed only when it is one of the two it can
            //    be: exit 0 means launchd holds the job; launchd's own "could not find service" means it does
            //    not; anything else - a timeout, a domain error, launchctl failing for its own reasons - is
            //    unknown, and an unknown state is never treated as absence. The rebuild stops with nothing
            //    changed. What launchd held is kept for the report: the state the user's Mac was in BEFORE
            //    this install is the history of every earlier attempt, gone the moment the job is booted out.
            var (printExit, printOutput) = run("/bin/launchctl", $"print {target}");
            var wasLoaded = printExit == 0;
            if (!wasLoaded && !ServiceNotFound(printExit, printOutput))
                throw new InvalidOperationException(
                    $"could not learn whether launchd holds the job (launchctl print answered exit {printExit}: {Trim(printOutput)}); nothing was changed");
            if (wasLoaded) keepPreviousPrint(printOutput);
            steps.Add(wasLoaded
                ? $"launchd already held the job: {Summarize(printOutput)}"
                : "launchd did not hold the job");

            // 3. Boot the old job out, and believe launchd rather than the exit code. The job is gone only
            //    when launchd says it does not know it. One it still holds is fatal here, with nothing on disk
            //    changed. An answer that is neither (a timeout, another error) proves nothing: the old
            //    definition is bootstrapped again so a job that was booted out comes back, and the rebuild stops.
            var bootedOut = false;
            if (wasLoaded)
            {
                var (outExit, outText) = run("/bin/launchctl", $"bootout {target}");
                var (stillExit, stillText) = run("/bin/launchctl", $"print {target}");
                if (stillExit == 0)
                    throw new InvalidOperationException(
                        $"launchctl bootout answered exit {outExit} ({Trim(outText)}) and launchd still holds the job; nothing was changed");
                if (!ServiceNotFound(stillExit, stillText))
                {
                    var (backExit, backText) = run("/bin/launchctl", $"bootstrap gui/{uid} \"{plistPath}\"");
                    throw new InvalidOperationException(
                        $"could not confirm the old job was booted out (launchctl print answered exit {stillExit}: {Trim(stillText)}); "
                        + $"the previous launch agent was bootstrapped again (exit {backExit}: {Trim(backText)}) and nothing on disk was changed");
                }
                bootedOut = true;
                steps.Add(outExit == 0
                    ? "booted the old job out"
                    : $"booted the old job out (bootout answered exit {outExit}: {Trim(outText)}; launchd no longer holds the job)");
            }

            // 4. The new definition takes the old one's place, is bootstrapped, started, and launchd is asked
            //    what it holds - all inside one transaction. Any failure from here - a file that will not
            //    move, a bootstrap launchd refuses, a runner that breaks on the kickstart or on the last
            //    question - is rolled back: a replacement launchd took is booted out again and confirmed gone,
            //    the previous file returns, and it is loaded again when it was loaded before.
            var replacementBootstrapped = false;
            try
            {
                File.Move(staged, plistPath, overwrite: true);
                steps.Add("wrote the launch agent");
                var (bootExit, bootText) = run("/bin/launchctl", $"bootstrap gui/{uid} \"{plistPath}\"");
                if (bootExit != 0)
                    throw new InvalidOperationException($"launchctl bootstrap failed (exit {bootExit}): {Trim(bootText)}");
                replacementBootstrapped = true;
                steps.Add("bootstrapped the launch agent");

                // 5. The explicit start. Its exit code is recorded, never believed on its own: on a real Mac a
                //    kickstart of a job whose program macOS refuses to run does not answer at all - launchd keeps
                //    the request open until our bound kills launchctl - and the only honest answer is what launchd
                //    reports afterwards (step 6), which the callers read.
                var (kickExit, kickText) = run("/bin/launchctl", $"kickstart -k {target}");
                steps.Add(kickExit == 0
                    ? "kickstarted the launch agent (an explicit demand, honoured even in an on-demand-only domain)"
                    : $"kickstart answered exit {kickExit}: {Trim(kickText)} (launchd's answer below is what counts)");

                // 6. What launchd says now, so a caller reports a start that happened rather than one that was
                //    asked for. The rebuild succeeded only when launchd answers exit 0 for the job here: a
                //    replacement launchd no longer knows ("could not find service"), a timeout, any other answer
                //    is the transaction failing at its last step, and is rolled back like the others. A success
                //    returned on any other last answer left a property list on disk with no loaded job - the one
                //    state the Director's repair deliberately leaves alone - and nothing would ever have fixed it.
                var (afterExit, afterPrint) = run("/bin/launchctl", $"print {target}");
                if (afterExit != 0)
                    throw new InvalidOperationException(ServiceNotFound(afterExit, afterPrint)
                        ? $"launchd no longer holds the job after the bootstrap and kickstart (launchctl print answered exit {afterExit}: {Trim(afterPrint)})"
                        : $"launchd gave no usable answer for the job after the kickstart (launchctl print answered exit {afterExit}: {Trim(afterPrint)})");
                steps.Add($"launchd now reports: {Summarize(afterPrint)}");

                return new RebuildResult(wasLoaded ? printOutput : null, wasLoaded, steps, afterPrint);
            }
            catch (Exception ex)
            {
                var rollback = RollBack(run, uid, target, plistPath, previousPlist, wasLoaded && bootedOut, replacementBootstrapped);
                throw new InvalidOperationException($"{ex.Message}. {rollback}", ex);
            }
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    /// <summary>
    /// Puts the previous launch agent back after a failure past the bootout. A replacement launchd took is
    /// booted out first and counted as gone only when launchd says it does not know it; the file is restored
    /// only then, so the file on disk and the job launchd holds never disagree. Says what it managed.
    /// </summary>
    private static string RollBack(CommandRunner run, int uid, string target, string plistPath, string? previousPlist,
        bool reload, bool replacementBootstrapped)
    {
        try
        {
            var bootedOutReplacement = "";
            if (replacementBootstrapped)
            {
                var (outExit, outText) = run("/bin/launchctl", $"bootout {target}");
                var (stillExit, stillText) = run("/bin/launchctl", $"print {target}");
                if (!ServiceNotFound(stillExit, stillText))
                    return $"Roll back stopped: launchd still holds the replacement job after bootout (bootout exit {outExit}: {Trim(outText)}; print exit {stillExit}: {Trim(stillText)}); "
                           + "the launch agent on disk is the replacement, which is what launchd holds.";
                bootedOutReplacement = " The replacement job was booted out first.";
            }
            if (previousPlist is null)
            {
                if (File.Exists(plistPath)) File.Delete(plistPath);
                return "Rolled back: the new launch agent was removed; there was none before." + bootedOutReplacement;
            }
            File.WriteAllText(plistPath, previousPlist);
            if (!reload)
                return "Rolled back: the previous launch agent is back on disk; launchd did not hold it before either." + bootedOutReplacement;
            var (exit, text) = run("/bin/launchctl", $"bootstrap gui/{uid} \"{plistPath}\"");
            return (exit == 0
                ? "Rolled back: the previous launch agent is back on disk and loaded again."
                : $"Rolled back the file, but launchd refused to load the previous launch agent again (exit {exit}: {Trim(text)}).") + bootedOutReplacement;
        }
        catch (Exception ex)
        {
            return $"Roll back FAILED ({ex.GetType().Name}: {ex.Message}); the launch agent on disk may not match what launchd holds.";
        }
    }

    /// <summary>
    /// An exclusive, cross-process lock on a file: .NET opens it with no sharing, which the operating system
    /// enforces between processes (a sharing violation on Windows, an exclusive advisory lock on macOS).
    /// </summary>
    private static class RebuildLock
    {
        public static IDisposable Acquire(string lockPath, TimeSpan wait)
        {
            var deadline = DateTime.UtcNow + wait;
            while (true)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(200);
                }
                catch (IOException ex)
                {
                    throw new InvalidOperationException(
                        $"another rebuild of the launch agent is in progress and did not finish within {wait.TotalSeconds:0} seconds (lock {lockPath}): {ex.Message}");
                }
            }
        }
    }

    /// <summary>The runner for production use: launchctl and id, each bounded by <see cref="CommandTimeout"/>.</summary>
    internal static readonly CommandRunner DefaultRunner = (exe, args) => ProcessRunner.Run(exe, args, onStdoutLine: null, LauncherMacInstaller.CommandTimeout);

    /// <summary>One line of the fields that say what became of a job: state, runs, last exit, job state.</summary>
    private static string Summarize(string launchctlPrint)
    {
        var fields = new[] { "state", "runs", "last exit code", "last exit reason", "job state" };
        var parts = new List<string>();
        foreach (var f in fields)
            if (LaunchdDiagnostics.Field(launchctlPrint, f) is { } v) parts.Add($"{f} = {v}");
        return parts.Count == 0 ? "(no state fields)" : string.Join(", ", parts);
    }

    /// <summary>
    /// Whether a process may boot out and re-bootstrap the job. launchd sets XPC_SERVICE_NAME to
    /// the job's label in every process the job starts (and its children inherit it), so a match
    /// means a bootout would kill the caller. Pure, for tests.
    /// </summary>
    internal static bool MayReload(string? xpcServiceName) =>
        !string.Equals(xpcServiceName, Label, StringComparison.Ordinal);

    /// <summary>The registered command line (ProgramArguments joined), or null when the property list does not exist.</summary>
    public static string? Registered()
    {
        if (!File.Exists(PlistPath)) return null;
        var content = File.ReadAllText(PlistPath);
        var strings = new List<string>();
        var start = content.IndexOf("<array>", StringComparison.Ordinal);
        var end = content.IndexOf("</array>", StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;
        var body = content[start..end];
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("<string>", StringComparison.Ordinal) && trimmed.EndsWith("</string>", StringComparison.Ordinal))
                strings.Add(Unxml(trimmed["<string>".Length..^"</string>".Length]));
        }
        return strings.Count == 0 ? null : string.Join(' ', strings);
    }

    /// <summary>True if the launch-agent property list exists.</summary>
    public static bool IsRegistered() => File.Exists(PlistPath);

    /// <summary>
    /// Unload the launch agent from launchd and remove the property list.
    /// Returns true if anything was removed.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool Unregister()
    {
        EngineLog.Write("[LauncherLaunchdAutostart] Unregister");
        var existed = File.Exists(PlistPath);

        // The launcher's tray toggle calls this from inside the job, and a bootout there kills the caller
        // (product #3575). Removing the plist is enough to stop the start at the next login.
        var inJob = !MayReload(Environment.GetEnvironmentVariable("XPC_SERVICE_NAME"));
        if (inJob)
            EngineLog.Write("[LauncherLaunchdAutostart] Unregister: running inside the job - removing the plist, not booting out");

        if (!inJob && IsLoaded())
        {
            var (exit, text) = ProcessRunner.Run("/bin/launchctl", $"bootout gui/{UserId()}/{Label}");
            EngineLog.Write($"[LauncherLaunchdAutostart] bootout -> exit={exit} {Trim(text)}");
        }

        if (existed)
        {
            File.Delete(PlistPath);
            EngineLog.Write($"[LauncherLaunchdAutostart] Unregister: removed {PlistPath}");
        }
        return existed;
    }

    /// <summary>
    /// Unregister, and say whether the job was actually BOOTED OUT. <see cref="Unregister"/> logged a
    /// nonzero bootout and then reported success, which mattered: a job still loaded keeps its
    /// KeepAlive definition, so launchd restarts the launcher after a stop has already been certified
    /// and the restart binds the port again while files are being deleted.
    /// </summary>
    /// <param name="failure">Why it could not be unregistered, or null on success.</param>
    [SupportedOSPlatform("macos")]
    public static bool UnregisterVerified(out string? failure)
    {
        failure = null;
        EngineLog.Write("[LauncherLaunchdAutostart] UnregisterVerified");
        var existed = File.Exists(PlistPath);

        if (IsLoaded())
        {
            var (exit, text) = ProcessRunner.Run("/bin/launchctl", $"bootout gui/{UserId()}/{Label}");
            EngineLog.Write($"[LauncherLaunchdAutostart] bootout -> exit={exit} {Trim(text)}");
            // Still loaded after asking it to go is the case that used to pass for success.
            if (exit != 0 && IsLoaded())
                failure = $"launchctl bootout failed (exit {exit}): {Trim(text)}";
        }

        try
        {
            if (existed) File.Delete(PlistPath);
        }
        catch (Exception ex)
        {
            failure ??= $"could not delete {PlistPath}: {ex.Message}";
        }

        return failure is null;
    }


    /// <summary>
    /// Stop and restart the launch agent, so a launcher build that has just been REPLACED on disk
    /// becomes the one that is running.
    ///
    /// WITHOUT THIS THE SWAP IS INVISIBLE. The replaced file sits on disk while the old process keeps
    /// running - Unix hands a running process the inode it started from, so it neither notices nor
    /// cares that the path now points somewhere else - and the machine goes on serving the old build
    /// until the next login. The update looks done and has not happened.
    ///
    /// KICKSTART, NOT A STOP FOLLOWED BY A START. KeepAlive is SuccessfulExit=false, so a launcher that
    /// exits CLEANLY is deliberately not respawned: a stop on its own would leave the machine with no
    /// launcher at all until the user logs in again. <c>kickstart -k</c> is the one operation that both
    /// ends the running instance and starts the replacement, performed by the supervisor that owns the
    /// job rather than raced against it.
    ///
    /// IT RETURNS WHETHER THE REQUEST WAS ACCEPTED, NEVER WHETHER THE NEW BUILD IS ANY GOOD. launchd
    /// also throttles respawns to roughly ten seconds, so a check made immediately after this returns
    /// finds nothing and means nothing. The caller's health check is what decides.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool Kickstart()
    {
        try
        {
            var (exit, text) = ProcessRunner.Run("/bin/launchctl", $"kickstart -k gui/{UserId()}/{Label}");
            EngineLog.Write($"[LauncherLaunchdAutostart] kickstart -k -> exit={exit} {Trim(text)}");
            return exit == 0;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherLaunchdAutostart] kickstart FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>Whether launchd currently has the agent loaded in this user's gui domain.</summary>
    [SupportedOSPlatform("macos")]
    public static bool IsLoaded()
    {
        var (exit, _) = ProcessRunner.Run("/bin/launchctl", $"print gui/{UserId()}/{Label}");
        return exit == 0;
    }

    private static string UserId()
    {
        var (exit, output) = ProcessRunner.Run("/usr/bin/id", "-u");
        if (exit != 0 || !int.TryParse(output.Trim(), out var uid))
            throw new InvalidOperationException($"could not resolve the current user id (id -u exit {exit})");
        return uid.ToString();
    }

    /// <summary>Split the stored argument string on whitespace (no quoting in launcher arguments today).</summary>
    private static IEnumerable<string> SplitArguments(string? arguments) =>
        string.IsNullOrWhiteSpace(arguments)
            ? Array.Empty<string>()
            : arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Xml(string value) => SecurityElement.Escape(value);

    private static string Unxml(string value) => value
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
        .Replace("&apos;", "'").Replace("&amp;", "&");

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "..." : text.Trim();
}
