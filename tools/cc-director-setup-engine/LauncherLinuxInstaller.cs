using CcDirector.Core.Configuration;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Performs the post-placement launcher step on Linux - the Linux twin of <see cref="LauncherTrayInstaller"/>
/// (Windows) and <see cref="LauncherMacInstaller"/> (macOS). The generic <see cref="UpdateRunner"/> places
/// the cc-launcher binary but never starts it. This:
///   1. writes and enables the systemd user unit (<see cref="LauncherSystemdAutostart"/>), so the launcher
///      starts at every login from now on,
///   2. stops any launcher of ours that is already running - an older build, or one started by hand that
///      systemd does not know about and that would hold the single-instance lock against the new one,
///   3. asks systemd to (re)start the unit, so the build just placed is the one running,
///   4. and certifies THAT process: the registration file must name the process id systemd reports.
///
/// WHY THE INSTALLER REGISTERS THE UNIT, AND NOT ONLY THE LAUNCHER. The launcher registers its own unit
/// on every start, but on Linux nothing starts the launcher until the unit exists - so a unit written only
/// by the launcher is never written at all. That is the reason a Linux Director downloaded every update
/// and installed none of them (issue #3091). macOS solved the same chicken-and-egg the same way: its
/// installer registers the launch agent before the launcher first runs.
///
/// Per-user throughout: no elevation, no system service. Every systemd call and the stop go through
/// injectable delegates, so the whole sequence is tested on any host.
/// </summary>
public sealed class LauncherLinuxInstaller
{
    private readonly InstallLayout _layout;
    private readonly LauncherSystemdAutostart.CommandRunner _run;
    private readonly Func<LauncherStopper.Result> _stopRunningLaunchers;
    private readonly Func<int, bool>? _processIsAlive;
    private readonly string _unitPath;
    private readonly string _registrationPath;
    private readonly TimeSpan _healthTimeout;
    private readonly TimeSpan _mainPidWait;

    /// <summary>The bound on any one systemctl call this step makes.</summary>
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    public LauncherLinuxInstaller(
        InstallLayout layout,
        LauncherSystemdAutostart.CommandRunner? runCommand = null,
        Func<LauncherStopper.Result>? stopRunningLaunchers = null,
        string? unitPath = null,
        string? registrationPath = null,
        TimeSpan? healthTimeout = null,
        TimeSpan? mainPidWait = null,
        Func<int, bool>? processIsAlive = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _run = runCommand ?? ((exe, args) => ProcessRunner.Run(exe, args, onStdoutLine: null, CommandTimeout));
        _stopRunningLaunchers = stopRunningLaunchers ?? (() => new LauncherStopper(layout).Stop());
        _unitPath = unitPath ?? LauncherSystemdAutostart.UnitPath;
        _registrationPath = registrationPath ?? LauncherDiscovery.DefaultPath;
        // The launcher is a single-file binary of about 100 MB that unpacks itself on its first run, so the
        // first start is slow exactly once. Generous, because the wait ends as soon as it registers.
        _healthTimeout = healthTimeout ?? TimeSpan.FromSeconds(60);
        _mainPidWait = mainPidWait ?? TimeSpan.FromSeconds(15);
        _processIsAlive = processIsAlive;
    }

    /// <summary>
    /// Register, start and certify the already-placed launcher. The launcher binary must already be placed
    /// at <see cref="InstallLayout.PathFor"/> for the Launcher component.
    /// </summary>
    public async Task<LauncherInstallResult> InstallAsync(CancellationToken ct = default)
    {
        var steps = new List<string>();
        EngineLog.Write("[LauncherLinuxInstaller] InstallAsync begin");

        var launcherBinary = _layout.PathFor(ComponentRegistry.Launcher);
        if (!File.Exists(launcherBinary))
            return Fail(steps, $"Launcher binary not present at {launcherBinary}; the file placement must run first.");

        // 1. The unit first, so the launcher starts at the next login even if everything below fails.
        try
        {
            var changed = LauncherSystemdAutostart.EnsureRegistered(
                launcherBinary, LauncherSystemdAutostart.Arguments, _run, _unitPath);
            steps.Add(changed
                ? $"systemd user unit written and enabled: {_unitPath}"
                : $"systemd user unit already up to date and enabled: {_unitPath}");
        }
        catch (Exception ex)
        {
            steps.Add($"could NOT register the systemd user unit: {ex.Message}");
            return Fail(steps, $"Could not register the launcher to start at login: {ex.Message}");
        }

        // 2. Clear the way. An older launcher holds the single-instance lock, and the new one would see it
        //    and exit cleanly - leaving the OLD build running and this install certifying nothing.
        var stop = _stopRunningLaunchers();
        steps.AddRange(stop.Steps);
        if (!stop.Stopped)
            return Fail(steps, "A launcher that was already running could not be stopped, so the new build cannot take over.");

        // 3. One operation owned by the supervisor - it starts the unit when it is not running.
        if (!LauncherSystemdAutostart.Restart(_run))
        {
            steps.Add($"systemctl --user restart {LauncherSystemdAutostart.UnitName} failed");
            return Fail(steps, "systemd would not start the launcher.");
        }
        steps.Add($"asked systemd to start {LauncherSystemdAutostart.UnitName}");

        // 4. Which process is systemd running? The registration must name THAT one, not whatever launcher
        //    happens to answer - the orphan that certified a broken install on a Mac (see LauncherMacInstaller).
        var pid = await WaitForMainPidAsync(ct);
        if (pid == 0)
        {
            steps.Add($"systemd did not report a process for the launcher within {_mainPidWait.TotalSeconds:0} seconds");
            return Fail(steps, "systemd did not report the launcher running.");
        }
        steps.Add($"systemd is running the launcher as process {pid}");

        var expectedVersion = new InstalledStateReader(_layout).Read(ComponentRegistry.Launcher).Version;
        var health = await LauncherHealthProbe.WaitForHealthyAsync(
            _registrationPath, expectedVersion, _healthTimeout, ct, pid, _processIsAlive);
        if (health is null)
        {
            steps.Add("launcher registration: never appeared");
            return Fail(steps, "The launcher started but never wrote its registration.");
        }
        if (!LauncherHealthProbe.Certifies(health, expectedVersion, pid))
        {
            steps.Add($"launcher registration: names process id {health.Pid} (version {health.Version ?? "unknown"})");
            return Fail(steps, $"A launcher registration exists, but it names process {health.Pid} reporting version "
                               + $"{health.Version ?? "unknown"} - not the process {pid} systemd started. Refusing to certify.");
        }
        steps.Add($"launcher registration: OK (version {health.Version ?? "unversioned"}, process id {health.Pid})");

        if (!LauncherSystemdAutostart.IsEnabled(_run))
        {
            steps.Add("systemd user unit: NOT enabled after registering");
            return Fail(steps, "The launcher is running but is not enabled to start at login.");
        }
        steps.Add("systemd user unit: enabled - the launcher starts at every login");

        EngineLog.Write("[LauncherLinuxInstaller] InstallAsync success");
        return new LauncherInstallResult(true,
            "Launcher installed, running, and set to start at login (systemd user unit).", steps);
    }

    private async Task<int> WaitForMainPidAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _mainPidWait;
        while (true)
        {
            var pid = LauncherSystemdAutostart.MainPid(_run);
            if (pid > 0 || DateTime.UtcNow >= deadline) return pid;
            await Task.Delay(250, ct);
        }
    }

    private LauncherInstallResult Fail(List<string> steps, string message)
    {
        var logs = Path.Combine(_layout.LogsDir, "launcher");
        var full = $"{message} Check `systemctl --user status {LauncherSystemdAutostart.UnitName}` and {logs}.";
        EngineLog.Write($"[LauncherLinuxInstaller] FAILED: {full}");
        string diagnostics;
        try
        {
            var (exit, status) = _run("systemctl", $"--user status --no-pager --lines=30 {LauncherSystemdAutostart.UnitName}");
            diagnostics = $"systemctl --user status -> exit {exit}\n{status}\n\nsteps:\n{string.Join('\n', steps)}";
        }
        catch (Exception ex)
        {
            diagnostics = $"could not ask systemd for the unit's status ({ex.GetType().Name}): {ex.Message}\n\nsteps:\n{string.Join('\n', steps)}";
        }
        return new LauncherInstallResult(false, full, steps,
            CcDirector.Core.ErrorReports.ErrorTextScrubber.Scrub(diagnostics));
    }
}
