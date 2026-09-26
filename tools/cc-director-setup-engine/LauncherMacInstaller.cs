using System.Diagnostics;
using System.Runtime.Versioning;
using CcDirector.Core.Configuration;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Performs the post-placement launcher step on macOS - the macOS twin of
/// <see cref="LauncherTrayInstaller"/>. The generic <see cref="UpdateRunner"/> places the
/// cc-launcher binary but never starts it, so on a fresh install the launcher would sit dormant
/// and its launch agent would never be registered. This:
///   1. restarts the launcher under launchd ("launchctl kickstart -k") when its launch agent is
///      already registered (a reinstall or repair), so the newly placed binary takes over,
///   2. otherwise registers the launch agent here and lets launchd start it,
///   3. waits for the launcher's registration file to name the process launchd reports
///      (the launcher listens on nothing - remove-the-network-port mission, phase 6),
///   4. confirms the launch agent property list exists.
///
/// Everything is per-user (the launch agent lives in the user's LaunchAgents folder and the
/// binary under the per-user install root): no elevation, no system daemon. macOS-only.
/// </summary>
public sealed class LauncherMacInstaller
{
    /// <summary>Runs a short command and returns its exit code and combined output. Injectable so
    /// tests can fake launchctl without a real launchd.</summary>
    public delegate (int Exit, string Output) CommandRunner(string executable, string arguments);

    /// <summary>Starts the launcher process directly and returns its process id. Injectable so
    /// tests can fake the first start without a real binary.</summary>
    public delegate int ProcessStarter(string executablePath, string arguments, string workingDirectory);

    private readonly InstallLayout _layout;
    private readonly CommandRunner _runCommand;
    private readonly ProcessStarter _startProcess;
    private readonly string _launchAgentPlistPath;
    private readonly string _registrationPath;
    private readonly TimeSpan _healthTimeout;
    private readonly TimeSpan _launchdPidWait;

    /// <summary>
    /// How long to wait for launchd to report a process. It was ten seconds, which is EXACTLY launchd's
    /// respawn throttle: a launcher that died at start-up was restarted just after the window closed, so
    /// the wait could not see it even once. Twenty-five seconds covers the first start and two respawns.
    /// </summary>
    public static readonly TimeSpan DefaultLaunchdPidWait = TimeSpan.FromSeconds(25);

    public LauncherMacInstaller(
        InstallLayout layout,
        CommandRunner? runCommand = null,
        ProcessStarter? startProcess = null,
        string? launchAgentPlistPath = null,
        TimeSpan? healthTimeout = null,
        string? registrationPath = null,
        TimeSpan? launchdPidWait = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _runCommand = runCommand ?? ProcessRunner.Run;
        _startProcess = startProcess ?? StartDetachedProcess;
        _launchAgentPlistPath = launchAgentPlistPath ?? DefaultLaunchAgentPlistPath();
        _registrationPath = registrationPath ?? LauncherDiscovery.DefaultPath;
        _healthTimeout = healthTimeout ?? TimeSpan.FromSeconds(20);
        _launchdPidWait = launchdPidWait ?? DefaultLaunchdPidWait;
    }

    /// <summary>The folder the launcher's launchd output goes to (its stdout and stderr files).</summary>
    private string LauncherLogDir => Path.Combine(_layout.LogsDir, "launcher");

    /// <summary>
    /// Where to look, in words a Mac user can follow. Finder hides ~/Library, so "check this path" alone
    /// sent a user looking for a folder he could not see.
    /// </summary>
    private string HowToOpenTheLogs =>
        $"To see the logs: in Finder choose Go > Go to Folder... and paste {LauncherLogDir}";

    /// <summary>The user's launch agent property list for the launcher:
    /// ~/Library/LaunchAgents/com.devthrottle.cc-launcher.plist. Delegates to
    /// <see cref="LauncherLaunchdAutostart"/>, the canonical owner of the label and path.</summary>
    public static string DefaultLaunchAgentPlistPath() => LauncherLaunchdAutostart.PlistPath;

    /// <summary>
    /// Start the already-placed launcher and verify it is healthy and registered as a launch
    /// agent. The launcher binary must already be placed (by the <see cref="UpdateRunner"/>) at
    /// <see cref="InstallLayout.PathFor"/> for the Launcher component.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public async Task<LauncherInstallResult> InstallAsync(CancellationToken ct = default)
    {
        var steps = new List<string>();
        EngineLog.Write("[LauncherMacInstaller] InstallAsync begin");

        var launcherBinary = _layout.PathFor(ComponentRegistry.Launcher);
        if (!File.Exists(launcherBinary))
            return Fail(steps, $"Launcher binary not present at {launcherBinary}; the file placement must run first.");

        // 0 means "no process to expect": on the launchd branch launchd owns the process and its id
        // is never learned here.
        var startedPid = 0;

        if (File.Exists(_launchAgentPlistPath))
        {
            // Reinstall or repair: the agent is registered, so launchd owns the process. A
            // kickstart restart makes launchd stop the old instance and start the newly placed
            // binary - never an in-place overwrite of a running file (the placement was
            // rename-based), and the restart hands over cleanly.
            RestartUnderLaunchd(steps);
        }
        else
        {
            // FIRST INSTALL: register the launch agent here and let launchd start it.
            //
            // This used to start the launcher directly and rely on the launcher registering its own
            // agent afterwards. That is where every unmanageable launcher came from: a process launchd
            // does not own, which the uninstall could not stop because it only asked launchd - so the
            // machine kept an orphan holding the launcher port and no later install could succeed. On
            // the machine where this was found the self-registration had also failed silently, so the
            // direct start was the only thing that ran, and it created exactly the process nothing
            // could clean up.
            //
            // Registering first inverts that: launchd owns the launcher from the very first install,
            // the property-list check below passes for a real reason, and the uninstall's launchd path
            // is sufficient for launchers created this way.
            steps.Add("registering the launch agent so launchd owns the launcher from the first install");
            try
            {
                if (!LauncherLaunchdAutostart.EnsureRegistered(launcherBinary, LauncherTrayInstaller.InstalledArguments))
                    return Fail(steps, "Could not register the launcher launch agent, so launchd would not own it. "
                                       + "Refusing to start it directly: that is what leaves a launcher no uninstall can stop.");
                steps.Add($"registered and bootstrapped the launch agent ({LauncherLaunchdAutostart.Label})");
            }
            catch (Exception ex)
            {
                return Fail(steps, $"Could not register the launcher launch agent: {ex.Message}");
            }
        }

        // Ask launchd which process it is running, so the health check below can demand an answer from
        // THAT process. Both branches now end with launchd owning the launcher, so both can do this -
        // previously the kickstart branch had no process to expect and trusted the version alone.
        startedPid = TryGetLaunchdPid(steps);

        // Identity-verified health: the registration must name THE PROCESS WE JUST STARTED, not
        // whatever launcher was already on the machine. This is the check that failed on
        // Sorens-Mac-mini: the installer started process 35158, the answer came from orphan 34084
        // which had been running for seventy-three minutes from a path just overwritten, and the
        // version comparison could not tell them apart because build metadata is stripped before
        // versions are compared.
        var expectedVersion = new InstalledStateReader(_layout).Read(ComponentRegistry.Launcher).Version;

        // No process to expect means we cannot tell our launcher from a pre-existing one, and the
        // version cannot tell them apart either (build metadata is stripped before comparison). Fail
        // rather than certify: a same-version orphan is the exact case that bricked a machine, and
        // "we could not check" must not read as "it is fine".
        if (startedPid == 0)
        {
            // Say WHY. launchd knows how the job last exited, how many times it ran and whether it is still
            // loaded; that used to be read and thrown away, leaving the person at the screen with nothing.
            var loaded = _lastLaunchdPrintExit == 0;
            var why = LaunchdDiagnostics.Explain(_lastLaunchdPrint, loaded)
                      ?? $"macOS did not report the launcher running within {_launchdPidWait.TotalSeconds:0} seconds";
            return Fail(steps, $"{why}. {HowToOpenTheLogs}");
        }

        var health = await LauncherHealthProbe.WaitForHealthyAsync(_registrationPath, expectedVersion, _healthTimeout, ct, startedPid);
        if (health is null)
        {
            steps.Add("launcher registration: never appeared");
            return Fail(steps, $"Launcher started but never wrote its registration. {HowToOpenTheLogs}");
        }
        if (!LauncherHealthProbe.Certifies(health, expectedVersion, startedPid))
        {
            steps.Add($"launcher registration: names process id {health.Pid} (version {health.Version ?? "unknown"})");
            return Fail(steps, startedPid > 0
                ? $"A launcher registration exists, but it names process {health.Pid} reporting version {health.Version ?? "unknown"} - not the process {startedPid} this install started. Refusing to certify: another launcher instance is running. Check {_layout.LogsDir}."
                : $"A launcher registration exists, but it reports version {health.Version ?? "unknown"}, not the freshly installed {expectedVersion} - refusing to certify this install. Another launcher instance is likely running; check {_layout.LogsDir}.");
        }
        steps.Add($"launcher registration: OK (version {health.Version ?? "unversioned"}, process id {health.Pid})");

        var registered = File.Exists(_launchAgentPlistPath);
        steps.Add($"launch agent property list: {(registered ? "registered" : "NOT registered")} at {_launchAgentPlistPath}");
        if (!registered)
            return Fail(steps, "Launcher is healthy but did not register its launch agent property list; check the launcher log.");

        EngineLog.Write("[LauncherMacInstaller] InstallAsync success");
        return new LauncherInstallResult(true,
            "Launcher installed, running, and registered as a launch agent.", steps);
    }

    /// <summary>
    /// Restart the launcher under launchd so the newly placed binary takes over. When the agent
    /// is not actually loaded (a property list left behind without a bootstrap - for example a
    /// crash between writing the file and loading it), kickstart fails; the launcher is then
    /// started directly, and on startup it re-bootstraps its own agent. That direct start is the
    /// A registered agent that refuses to start is a real failure and is reported as one - it is
    /// never worked around by starting the launcher outside launchd.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private void RestartUnderLaunchd(List<string> steps)
    {
        var (uidExit, uidOutput) = _runCommand("/usr/bin/id", "-u");
        if (uidExit != 0 || !int.TryParse(uidOutput.Trim(), out var uid))
        {
            // No silent direct start. Falling back to one is precisely how a launcher launchd does
            // not own comes into existence, and nothing can stop those afterwards. The property-list
            // check that follows fails this install with a reason the user can read.
            steps.Add($"could not resolve the user id (exit {uidExit}) - cannot restart the launch agent");
            return;
        }

        var serviceTarget = $"gui/{uid}/{LauncherLaunchdAutostart.Label}";
        var (kickExit, kickOutput) = _runCommand("/bin/launchctl", $"kickstart -k {serviceTarget}");
        if (kickExit == 0)
        {
            steps.Add($"restarted the launch agent with launchctl kickstart ({serviceTarget})");
            return;
        }

        // A registered agent that will not start is a real failure. Re-register, which bootstraps it,
        // and if that will not work either say so rather than starting an unmanaged process.
        EngineLog.Write($"[LauncherMacInstaller] launchctl kickstart failed (exit {kickExit}): {kickOutput.Trim()}");
        steps.Add($"launchctl kickstart failed (exit {kickExit}) - re-registering the launch agent");
        try
        {
            if (LauncherLaunchdAutostart.EnsureRegistered(
                    _layout.PathFor(ComponentRegistry.Launcher), LauncherTrayInstaller.InstalledArguments))
                steps.Add("re-registered and bootstrapped the launch agent");
            else
                steps.Add("could NOT re-register the launch agent");
        }
        catch (Exception ex)
        {
            steps.Add($"could NOT re-register the launch agent: {ex.Message}");
        }
    }

    /// <summary>
    /// Which process is launchd running for our label? Parsed from launchctl print, whose output
    /// carries a "pid = NNNN" field while the service is up. Returns 0 when it cannot be determined,
    /// which the health check reads as "no process to expect".
    /// </summary>
    private int TryGetLaunchdPid(List<string> steps)
    {
        var (uidExit, uidOutput) = _runCommand("/usr/bin/id", "-u");
        if (uidExit != 0 || !int.TryParse(uidOutput.Trim(), out var uid))
        {
            steps.Add($"could not resolve the user id (exit {uidExit}) - cannot ask launchd which process it runs");
            return 0;
        }

        // A freshly bootstrapped job does not have a process id the instant launchctl is asked, so a
        // single look returns 0 - and 0 means "expect nothing", which lets any responder on the port
        // certify the install. That is exactly the hole this was meant to close, so wait for it.
        var deadline = DateTime.UtcNow + _launchdPidWait;
        while (DateTime.UtcNow < deadline)
        {
            var (printExit, printOutput) = _runCommand(
                "/bin/launchctl", $"print gui/{uid}/{LauncherLaunchdAutostart.Label}");
            // Kept, so a failure can say what launchd last said instead of only that it said no pid.
            _lastLaunchdPrintExit = printExit;
            _lastLaunchdPrint = printOutput;
            if (printExit == 0)
            {
                var pid = ParseLaunchdPid(printOutput);
                if (pid > 0)
                {
                    steps.Add($"launchd is running the launcher as process {pid}");
                    return pid;
                }
            }
            Thread.Sleep(500);
        }

        steps.Add($"launchd did not report a process id for the launcher within {_launchdPidWait.TotalSeconds:0} seconds");
        return 0;
    }

    /// <summary>Testable parse of a launchctl print block: the "pid = NNNN" field, or 0.</summary>
    public static int ParseLaunchdPid(string launchctlPrintOutput)
    {
        if (string.IsNullOrEmpty(launchctlPrintOutput)) return 0;
        foreach (var raw in launchctlPrintOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("pid ", StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            if (int.TryParse(line[(eq + 1)..].Trim(), out var pid) && pid > 0) return pid;
        }
        return 0;
    }

    /// <summary>Starts the launcher as a plain detached child: no shell, no redirected pipes (a
    /// redirected pipe would tie the launcher's lifetime to the wizard's stdio), fresh
    /// environment inherited from the wizard.</summary>
    private static int StartDetachedProcess(string executablePath, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null");
        return process.Id;
    }

    // What launchctl print last answered while waiting for a process id (-1 = never asked).
    private int _lastLaunchdPrintExit = -1;
    private string? _lastLaunchdPrint;

    private LauncherInstallResult Fail(List<string> steps, string message)
    {
        EngineLog.Write($"[LauncherMacInstaller] FAILED: {message}");
        var diagnostics = GatherDiagnostics(steps);
        EngineLog.Write($"[LauncherMacInstaller] diagnostics:\n{diagnostics}");
        return new LauncherInstallResult(false, message, steps, diagnostics);
    }

    /// <summary>
    /// Everything that explains a failure, gathered at the moment it happens: launchd's view of the job
    /// (asked afresh, because the job may have changed since the wait gave up), the tail of the launcher's
    /// launchd stderr and stdout, and the steps taken. Gathering is itself fallible - launchctl can be
    /// missing or refuse - and when it is, the report SAYS so rather than going quiet.
    /// </summary>
    private string GatherDiagnostics(List<string> steps)
    {
        string? print = _lastLaunchdPrint;
        var loaded = _lastLaunchdPrintExit == 0;
        string? gatherError = null;
        try
        {
            var (uidExit, uidOutput) = _runCommand("/usr/bin/id", "-u");
            if (uidExit == 0 && int.TryParse(uidOutput.Trim(), out var uid))
            {
                var (printExit, printOutput) = _runCommand(
                    "/bin/launchctl", $"print gui/{uid}/{LauncherLaunchdAutostart.Label}");
                print = printOutput;
                loaded = printExit == 0;
            }
            else
            {
                gatherError = $"could not resolve the user id (exit {uidExit})";
            }
        }
        catch (Exception ex)
        {
            gatherError = $"could not ask launchd ({ex.GetType().Name}): {ex.Message}";
        }

        var composed = LaunchdDiagnostics.Compose(print, loaded,
        [
            ("launchd-stderr.log (last lines)", LaunchdDiagnostics.Tail(Path.Combine(LauncherLogDir, "launchd-stderr.log"), 40)),
            ("launchd-stdout.log (last lines)", LaunchdDiagnostics.Tail(Path.Combine(LauncherLogDir, "launchd-stdout.log"), 15)),
        ]);
        var header = gatherError is null ? "" : $"launchd query failed: {gatherError}\n";
        // The binary checks go LAST: the Gateway keeps the first 16,000 characters of a report, and the
        // system log is the only part long enough to be cut.
        return header + composed + "\nsteps:\n  " + string.Join("\n  ", steps) + "\n" + GatherBinaryChecks();
    }

    /// <summary>
    /// What macOS itself thinks of the launcher. launchd only knows THAT it refused the program
    /// ("78: EX_CONFIG", "spawn failed"); the reason sits elsewhere - the file's quarantine flag and
    /// signature, a switched-off background item, a device-management policy, the security log. A
    /// user's Mac failed three installs in a row with nothing else to go on (#3411), so these answers
    /// travel with every failure.
    /// </summary>
    private string GatherBinaryChecks()
    {
        var binary = _layout.PathFor(ComponentRegistry.Launcher);
        var checks = new List<(string Name, int Exit, string Output)>();

        void Check(string name, string exe, string args, Func<string, string>? keep = null)
        {
            try
            {
                var (exit, output) = _runCommand(exe, args);
                checks.Add((name, exit, keep is null ? output : keep(output)));
            }
            catch (Exception ex)
            {
                checks.Add((name, -1, $"could not run {exe} ({ex.GetType().Name}): {ex.Message}"));
            }
        }

        Check("xattr -l (quarantine flag)", "/usr/bin/xattr", $"-l \"{binary}\"");
        Check("codesign -dv (signature)", "/usr/bin/codesign", $"-dv --verbose=2 \"{binary}\"");

        // A background item the user (or macOS) switched off is refused without a word from launchd.
        // The list names every service on the machine; only ours matters.
        var (uidExit, uidOutput) = _runCommand("/usr/bin/id", "-u");
        if (uidExit == 0 && int.TryParse(uidOutput.Trim(), out var uid))
            Check("launchctl print-disabled (is our background item switched off)", "/bin/launchctl", $"print-disabled gui/{uid}",
                output => string.Join('\n', output.Split('\n').Where(l => l.Contains("devthrottle", StringComparison.OrdinalIgnoreCase))));
        else
            checks.Add(("launchctl print-disabled (is our background item switched off)", uidExit, "could not resolve the user id"));

        // A company-managed Mac can refuse unsigned background programs by policy.
        Check("profiles status (device management)", "/usr/bin/profiles", "status -type enrollment");
        Check("log show (last 3 minutes mentioning cc-launcher)", "/usr/bin/log",
            "show --last 3m --style compact --predicate \"eventMessage CONTAINS 'cc-launcher'\"");

        return LaunchdDiagnostics.ComposeBinaryChecks(checks);
    }
}
