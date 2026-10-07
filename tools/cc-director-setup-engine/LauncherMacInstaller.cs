using System.Diagnostics;
using System.Runtime.Versioning;
using CcDirector.Core.Configuration;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Performs the post-placement launcher step on macOS - the macOS twin of
/// <see cref="LauncherTrayInstaller"/>. The generic <see cref="UpdateRunner"/> places the
/// cc-launcher binary but never starts it, so on a fresh install the launcher would sit dormant
/// and its launch agent would never be registered. This:
///   1. rebuilds the launch agent from scratch on EVERY install - boots out whatever job launchd held,
///      writes the current property list, bootstraps and kickstarts it (<see cref="LauncherLaunchdAutostart.Rebuild"/>),
///   2. waits for the launcher's registration file to name the process launchd reports
///      (the launcher listens on nothing - remove-the-network-port mission, phase 6),
///   3. confirms the launch agent property list exists,
///   4. and when launchd will not run it, gathers everything macOS knows about why, for the report.
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

    /// <summary>The bound on any one command this step runs.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

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
        // Every command this step runs is short (id, launchctl, and the failure-only diagnostics), so a
        // minute bounds a wedged one without holding a failure report for ProcessRunner's 15-minute default.
        _runCommand = runCommand ?? ((exe, args) => ProcessRunner.Run(exe, args, onStdoutLine: null, CommandTimeout));
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

        // launchd refuses a launcher whose folder or log files belong to root, before it runs a line (#3411).
        if (EnsureOwnedByUser(steps) is { } ownershipFailure)
            return Fail(steps, ownershipFailure);

        // THE JOB IS REBUILT ON EVERY INSTALL - never restarted as found. The install used to kickstart a
        // job whose property list was already on disk, and on one user's Mac that job was the one the first
        // install had registered and launchd refused to spawn ("78: EX_CONFIG", "spawn failed"); five installs
        // over two weeks restarted that same refused job, so no later change to registration could ever reach
        // the machine. Rebuild boots the old job out, writes the current definition, creates the log folder,
        // bootstraps and kickstarts (the kickstart is what starts a job in an "on-demand-only" user domain,
        // which that Mac's log named). What launchd held before is kept for the report.
        try
        {
            var rebuilt = LauncherLaunchdAutostart.Rebuild(launcherBinary, LauncherTrayInstaller.InstalledArguments,
                new LauncherLaunchdAutostart.CommandRunner(_runCommand), _launchAgentPlistPath, LauncherLogDir);
            _previousLaunchdPrint = rebuilt.PreviousPrint;
            steps.AddRange(rebuilt.Steps);
        }
        catch (Exception ex)
        {
            steps.Add($"could NOT rebuild the launch agent: {ex.Message}");
            return Fail(steps, $"Could not register the launcher with macOS: {ex.Message}. {HowToOpenTheLogs}");
        }

        // Ask launchd which process it is running, so the health check below can demand an answer from
        // THAT process. 0 means "no process to expect".
        var startedPid = TryGetLaunchdPid(steps);

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
    /// The install folder and the launch agent property list must belong to the user, or launchd refuses
    /// the launcher with "78: EX_CONFIG" and an empty stderr. The setup wizard (which offers the repair
    /// prompt) and the command line check this before they write anything; this is the last guard before
    /// launchd is asked to start the launcher, so it reports and never prompts.
    /// </summary>
    private string? EnsureOwnedByUser(List<string> steps) =>
        MacFileOwnership.EnsureOwnedByUser(new MacFileOwnership.CommandRunner(_runCommand),
            MacFileOwnership.InstallTargets(_layout, _launchAgentPlistPath), offerPrompt: false, steps.Add);

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

    // What launchd held for the label BEFORE this install rebuilt the job (null when it held nothing): the
    // history of every earlier attempt on this machine, gone from launchd the moment the job was booted out.
    private string? _previousLaunchdPrint;

    private LauncherInstallResult Fail(List<string> steps, string message)
    {
        EngineLog.Write($"[LauncherMacInstaller] FAILED: {message}");
        var diagnostics = GatherDiagnostics(steps);
        EngineLog.Write($"[LauncherMacInstaller] diagnostics:\n{diagnostics}");
        return new LauncherInstallResult(false, message, steps, diagnostics);
    }

    /// <summary>
    /// Everything that explains a failure, gathered at the moment it happens: launchd's view of the job
    /// (asked afresh, because the job may have changed since the wait gave up) and of the job this install
    /// REPLACED, the launch agent as written on disk, the tail of the launcher's launchd stderr and stdout,
    /// the tail of the launcher's own log, the steps taken, and what macOS itself says about the binary and
    /// the folders launchd opens. Gathering is itself fallible - launchctl can be missing or refuse - and
    /// when it is, the report SAYS so rather than going quiet.
    ///
    /// Everything here is for the reader on OUR side: one user's Mac failed five installs in a row (#3411)
    /// and each report answered fewer questions than the next round trip needed. A report must settle the
    /// cause without another message to the user.
    /// </summary>
    private string GatherDiagnostics(List<string> steps)
    {
        string? print = _lastLaunchdPrint;
        var loaded = _lastLaunchdPrintExit == 0;
        string? gatherError = null;
        int? uid = null;
        try
        {
            var (uidExit, uidOutput) = _runCommand("/usr/bin/id", "-u");
            if (uidExit == 0 && int.TryParse(uidOutput.Trim(), out var parsedUid))
            {
                uid = parsedUid;
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

        var sections = new List<LaunchdDiagnostics.Section>();
        if (gatherError is not null) sections.Add(new("launchd query failed", gatherError, false));
        sections.Add(new(loaded ? "launchctl print (useful lines)" : "launchctl print: the job is NOT loaded",
            string.Join('\n', LaunchdDiagnostics.UsefulLines(print)), false));
        sections.Add(new("launchd-stderr.log (last lines)",
            LaunchdDiagnostics.Tail(Path.Combine(LauncherLogDir, "launchd-stderr.log"), 40) ?? "(missing or empty)", true));
        sections.Add(new("launchd-stdout.log (last lines)",
            LaunchdDiagnostics.Tail(Path.Combine(LauncherLogDir, "launchd-stdout.log"), 15) ?? "(missing or empty)", true));
        sections.Add(new("launcher log (last lines)", LaunchdDiagnostics.LauncherLogTail(LauncherLogDir, 60) ?? "(missing or empty)", true));
        sections.Add(_previousLaunchdPrint is null
            ? new("launchd held no job for the launcher before this install", "(nothing was registered)", false)
            : new("launchd held this job BEFORE this install rebuilt it (useful lines)",
                string.Join('\n', LaunchdDiagnostics.UsefulLines(_previousLaunchdPrint)), false));
        sections.Add(new("steps", string.Join('\n', steps), true));
        sections.AddRange(GatherMachineChecks(uid, print));
        // One budget for the whole text. The Gateway keeps the first characters of a report and drops the
        // rest without a word, so a section that does not fit is cut HERE, where the cut is said, longest
        // first - and the launchd answer, the ownership listing and Gatekeeper's verdict are never the part
        // that falls off the end behind a long system log.
        // Scrubbed HERE as well as on the Gateway: a credential-shaped value in any answer is redacted before
        // it leaves the machine, not after it has crossed the network.
        return CcDirector.Core.ErrorReports.ErrorTextScrubber.Scrub(LaunchdDiagnostics.Fit(sections, LaunchdDiagnostics.DiagnosticsBudget));
    }

    /// <summary>
    /// What macOS itself thinks of the launcher and of the places launchd touches to start it. launchd only
    /// knows THAT it refused the program ("78: EX_CONFIG", "spawn failed"); the reason sits elsewhere - who
    /// owns the files it opens, whether the log folder exists at all, the file's quarantine flag and
    /// signature, Gatekeeper's verdict, a switched-off background item, a device-management policy, the
    /// domain's own mode, the security log. Each answer travels with every failure, with its exit code, so an
    /// empty answer and a failed command read differently.
    ///
    /// The direct run is the one check that separates the two halves of a refusal: a launcher that prints its
    /// version when run by hand is a program macOS WILL execute, so a launchd refusal is about the job (its
    /// log paths, its folders, the domain); one that is killed when run by hand is refused as a program.
    /// </summary>
    /// <param name="uid">The user id GatherDiagnostics resolved, or null when it could not.</param>
    /// <param name="launchctlPrint">launchd's current answer for the job, for the full-text block.</param>
    private List<LaunchdDiagnostics.Section> GatherMachineChecks(int? uid, string? launchctlPrint)
    {
        var binary = _layout.PathFor(ComponentRegistry.Launcher);
        var checks = new List<LaunchdDiagnostics.Section>();

        // Each answer is its own section, titled with the exit code, so an empty answer and a failed command
        // read differently. keepEnd is for the one answer whose end matters (a log); a listing keeps its start.
        void Add(string name, int exit, string output, bool keepEnd = false)
            => checks.Add(new($"{name} -> exit {exit}", output, keepEnd));

        void Check(string name, string exe, string args, Func<string, string>? keep = null, bool keepEnd = false)
        {
            try
            {
                var (exit, output) = _runCommand(exe, args);
                Add(name, exit, keep is null ? output : keep(output), keepEnd);
            }
            catch (Exception ex)
            {
                Add(name, -1, $"could not run {exe} ({ex.GetType().Name}): {ex.Message}", keepEnd);
            }
        }

        static string Quote(string p) => "\"" + p + "\"";

        // launchd's whole answer, not only the useful lines: a field nobody thought to keep is exactly the one
        // the next failure turns on.
        Add("launchctl print (full, without the environment block)", launchctlPrint is null ? -1 : 0,
            launchctlPrint is null ? "(launchd was not asked)" : LaunchdDiagnostics.WithoutEnvironmentBlocks(launchctlPrint));

        // The user domain itself. A domain in "on-demand-only" mode starts nothing by itself; one user's Mac
        // logged "pending spawn, domain in on-demand-only mode" at every install. Only the domain's own named
        // fields travel: the full answer lists every service and the login environment, which can hold anything
        // a person exported in a shell profile.
        if (uid is { } domainUid)
            Check("launchctl print gui/<uid> (the domain: named facts only)", "/bin/launchctl", $"print gui/{domainUid}",
                LaunchdDiagnostics.DomainFacts);

        // The launch agent as it is on disk, so the report never has to guess which paths launchd was given.
        Check("launch agent property list on disk", "/bin/cat", Quote(_launchAgentPlistPath));

        // launchd opens the log files as the user before it starts the program: a root-owned one is refused
        // with the same "78: EX_CONFIG" and empty stderr as a refused program (#3411). A log folder that does
        // not exist is refused the same way.
        string[] owned = [_layout.LocalRoot, _layout.LogsDir, LauncherLogDir,
            Path.Combine(LauncherLogDir, "launchd-stdout.log"), Path.Combine(LauncherLogDir, "launchd-stderr.log"),
            Path.GetDirectoryName(binary)!, binary, _launchAgentPlistPath];
        var missing = owned.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        Check("ls -ldO (who owns the files launchd opens, and their flags)", "/bin/ls",
            "-ldO " + string.Join(' ', owned.Where(p => File.Exists(p) || Directory.Exists(p)).Select(Quote)));
        Add("paths launchd would open that do NOT exist", 0, missing.Count == 0 ? "(none - every path exists)" : string.Join('\n', missing));
        Check("ls -la (the launcher log folder)", "/bin/ls", "-la " + Quote(LauncherLogDir));

        // Where the home folder really is: a home on an external or network volume, or behind a symbolic
        // link, is one launchd's pre-start file opens can be refused on.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Check("home folder (stat: name, link target, device, flags, owner)", "/usr/bin/stat",
            "-f \"%N -> %Y | device %Sd | flags %Sf | owner %u\" " + Quote(home) + " " + Quote(Path.Combine(home, "Library")) + " " + Quote(_layout.LocalRoot));
        Check("df -h (the volume the home folder is on)", "/bin/df", "-h " + Quote(home));

        Check("xattr -l (quarantine flag)", "/usr/bin/xattr", "-l " + Quote(binary));
        Check("codesign -dv (signature)", "/usr/bin/codesign", "-dv --verbose=2 " + Quote(binary));
        Check("codesign --verify (does the signature still match the file)", "/usr/bin/codesign", "--verify --verbose=2 " + Quote(binary));
        Check("spctl --assess (Gatekeeper's verdict on running it)", "/usr/sbin/spctl", "--assess --type execute --verbose=2 " + Quote(binary));

        // Does the program run at all when asked directly? Only a launcher build that answers --version is
        // asked (an older one would start for real and leave a process nothing owns).
        var version = new InstalledStateReader(_layout).Read(ComponentRegistry.Launcher).Version;
        if (AnswersVersionFlag(version))
            Check("run the launcher directly: cc-launcher --version (does macOS execute the program at all)", binary, "--version");
        else
            Add("run the launcher directly: cc-launcher --version", -1, $"not run: launcher {version ?? "(unknown)"} predates --version ({FirstVersionAnsweringVersionFlag})");

        // A background item the user (or macOS) switched off is refused without a word from launchd.
        // The list names every service on the machine; only ours matters.
        if (uid is { } knownUid)
            Check("launchctl print-disabled (is our background item switched off)", "/bin/launchctl", $"print-disabled gui/{knownUid}",
                output =>
                {
                    var ours = output.Split('\n').Where(l => l.Contains("devthrottle", StringComparison.OrdinalIgnoreCase)).ToList();
                    return ours.Count > 0
                        ? string.Join('\n', ours)
                        : "no devthrottle entry: our background item is not switched off";
                });
        else
            Add("launchctl print-disabled (is our background item switched off)", -1, "not run: the user id could not be resolved");

        // A company-managed Mac can refuse unsigned background programs by policy.
        Check("profiles status (device management)", "/usr/bin/profiles", "status -type enrollment");
        Check("sw_vers", "/usr/bin/sw_vers", "");

        // The security and launch subsystems' own words. The predicate used to match only lines naming the
        // binary, and launchd's refusal ("Service could not initialize", from xpcproxy) and the kernel's
        // code-signing kill do not always name it. Everything xpcproxy, amfid and syspolicyd said in the last
        // minutes is kept, plus every launchd and kernel line that names us.
        Check("log show (last 3 minutes: launchd and kernel lines naming us, and all of xpcproxy, amfid, syspolicyd)", "/usr/bin/log",
            "show --last 3m --style compact --predicate \"((process == 'launchd' OR process == 'kernel') AND (eventMessage CONTAINS 'cc-launcher' OR eventMessage CONTAINS 'devthrottle')) OR process == 'xpcproxy' OR process == 'amfid' OR process == 'syspolicyd' OR eventMessage CONTAINS 'cc-launcher'\"",
            output => string.Join('\n', output.Split('\n').Where(l => !l.Contains("log run noninteractively", StringComparison.Ordinal))),
            keepEnd: true);

        return checks;
    }

    /// <summary>The first launcher build that answers <c>--version</c> and exits (nothing else is started).</summary>
    internal const string FirstVersionAnsweringVersionFlag = "2.17.0";

    /// <summary>Whether an installed launcher of this version answers <c>--version</c>. Unknown is no.</summary>
    internal static bool AnswersVersionFlag(string? installedVersion)
    {
        var installed = VersionUtil.TryParse(installedVersion);
        var first = VersionUtil.TryParse(FirstVersionAnsweringVersionFlag);
        return installed is not null && first is not null && installed >= first;
    }
}
