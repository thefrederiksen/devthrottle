using System.Diagnostics;
using System.Runtime.Versioning;
using CcDirector.Core.Configuration;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of a Launcher tray-app install, with the steps taken (for logs / UI).</summary>
/// <remarks><see cref="Diagnostics"/> is what the step gathered about WHY it failed (on macOS: what launchd says
/// about the job and the tail of the launcher's own error log) - shown in the installer's log and sent with the
/// failure report, so a failure never reaches us as a bare sentence again.</remarks>
public sealed record LauncherInstallResult(bool Success, string Message, IReadOnlyList<string> Steps, string? Diagnostics = null);

/// <summary>
/// Performs the post-download step the generic <see cref="UpdateRunner"/> cannot: the runner places
/// cc-launcher.exe but never starts it, so on a fresh install the launcher sits dormant and its
/// autostart Run key is never written. This:
///   1. stops any already-running installed launcher (so a fresh managed instance takes over),
///   2. starts the launcher tray app with <c>--managed</c> (it runs the periodic self-update check
///      and registers its own HKCU Run-key autostart on startup),
///   3. waits for the launcher's registration file to name the process it just started
///      (the launcher listens on nothing - remove-the-network-port mission, phase 6),
///   4. confirms the autostart Run key is registered.
///
/// The Launcher exe is already placed by the UpdateRunner at the Launcher component path before this
/// runs. Ships to BOTH roles (it is in <see cref="ComponentRegistry.Apps"/> for Workstation and
/// Gateway). Everything is per-user (%LOCALAPPDATA%): NO elevation, NO Windows service. Windows-only.
/// Idempotent: starting an already-running launcher is harmless (the second instance sees the
/// single-instance mutex held and exits). Mirrors <see cref="GatewayTrayInstaller"/>.
/// </summary>
public sealed class LauncherTrayInstaller
{
    /// <summary>The arguments the installed tray app runs with: managed mode runs the self-update check.</summary>
    public const string InstalledArguments = "--managed";

    /// <summary>
    /// The backstop for a launcher that is running but never registers. It is deliberately far beyond any
    /// plausible first start: the wait ends when the registration appears or when the started process is
    /// gone, so a generous ceiling costs nothing in the broken case and stops a slow machine being called
    /// broken in the healthy one. Twenty seconds was the number that failed a clean install (#1152) and
    /// the process it declared dead was healthy the whole time.
    /// </summary>
    public static readonly TimeSpan FirstStartHealthCeiling = TimeSpan.FromMinutes(5);

    /// <summary>How long a first start may take before the user is told it is a slow start, not a hang.</summary>
    private static readonly TimeSpan SayItIsSlowAfter = TimeSpan.FromSeconds(8);

    /// <summary>How often that reassurance is repeated while the wait continues.</summary>
    private static readonly TimeSpan RepeatTheReassuranceEvery = TimeSpan.FromSeconds(5);

    private readonly InstallLayout _layout;
    private readonly string _registrationPath;

    public LauncherTrayInstaller(InstallLayout layout, string? registrationPath = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _registrationPath = registrationPath ?? LauncherDiscovery.DefaultPath;
    }

    /// <summary>
    /// Start the already-placed Launcher tray app in managed mode and verify it is healthy and
    /// autostart-registered. The Launcher exe must already be placed (by the UpdateRunner) at
    /// <see cref="InstallLayout.PathFor"/> for the Launcher component.
    /// </summary>
    /// <param name="progress">
    /// Optional running commentary for the caller's screen while the launcher is still starting. A cold
    /// first start of a 134 MB single-file binary can take a while, and a screen that says nothing for
    /// that long reads as frozen.
    /// </param>
    [SupportedOSPlatform("windows")]
    public async Task<LauncherInstallResult> InstallAsync(CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var steps = new List<string>();
        EngineLog.Write("[LauncherTrayInstaller] InstallAsync begin");

        var launcherExe = _layout.PathFor(ComponentRegistry.Launcher);
        if (!File.Exists(launcherExe))
            return Fail(steps, $"Launcher exe not present at {launcherExe}; the file swap must run first.");

        // 1. Stop any already-running installed launcher so a fresh managed instance takes over (a
        // re-install / repair path). Scoped to processes under the install dir only - a dev launcher
        // running from a repo checkout is never touched.
        StopInstalledProcesses(steps);

        // 2. Start the tray app. It registers its own HKCU Run-key autostart (pointing at itself with
        // the same arguments) on startup, so install-time registration and app self-registration can
        // never disagree.
        Process started;
        try
        {
            var psi = BuildTrayLaunchInfo(launcherExe, _layout.LauncherDir);
            started = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            steps.Add($"started Launcher tray app pid={started.Id} ({InstalledArguments})");
        }
        catch (Exception ex)
        {
            return Fail(steps, $"Failed to start the Launcher tray app: {ex.Message}");
        }

        // 3. Wait for readiness. Two things are being checked at once, and both matter.
        //
        // IDENTITY: the registration must name THE PROCESS WE JUST STARTED, not whatever launcher was
        // already on the machine. Version alone could not do this - build metadata is stripped when
        // versions are compared, so an orphan reporting "1.8.4+abc" and a freshly placed "1.8.4"
        // matched, and a same-version reinstall certified against the orphan.
        //
        // READINESS: the wait ends on the CONDITION, not on a clock. A fixed twenty seconds, tuned on a
        // warm developer machine, declared this install failed on a clean Windows 11 machine while the
        // launcher was still unpacking - and the launcher it said had never answered was healthy, from
        // the very process this installer started (issue #1152). So the wait ends when the launcher's
        // registration certifies, or when the process we started is gone; the ceiling is only a backstop
        // for a launcher that is alive and wedged.
        var expectedVersion = new InstalledStateReader(_layout).Read(ComponentRegistry.Launcher).Version;
        var startedPid = started.Id;
        LauncherWaitResult wait;
        try
        {
            var lastNote = TimeSpan.Zero;
            wait = await LauncherHealthProbe.WaitForReadyAsync(
                _registrationPath,
                expectedVersion,
                startedPid,
                () => StarterIsRunning(started),
                FirstStartHealthCeiling,
                ct,
                onStillWaiting: elapsed =>
                {
                    if (elapsed < SayItIsSlowAfter || elapsed - lastNote < RepeatTheReassuranceEvery) return;
                    lastNote = elapsed;
                    progress?.Report(
                        "Starting the Launcher tray app. Its first start unpacks a large file and is slow "
                        + $"only once - still waiting after {(int)elapsed.TotalSeconds} seconds...");
                });
        }
        finally
        {
            started.Dispose();
        }

        var health = wait.Health;
        var up = wait.Stop == LauncherWaitStop.Healthy;
        steps.Add(up
            ? $"launcher registration: OK (version {health!.Version ?? "unversioned"}, process id {health.Pid})"
            : health is null
                ? $"launcher registration: never appeared ({DescribeStop(wait.Stop)})"
                : $"launcher registration: names process id {health.Pid} (version {health.Version ?? "unknown"}), alive={health.Ok} ({DescribeStop(wait.Stop)})");
        if (!up)
            return Fail(steps, DescribeFailure(wait, startedPid, expectedVersion));

        // 4. Verify the autostart Run key (written by the app on startup).
        var registered = LauncherAutostart.IsRegistered();
        steps.Add($"autostart Run key: {(registered ? "registered" : "NOT registered")}");
        if (!registered)
            return Fail(steps, "Launcher is healthy but did not register its autostart Run key; check the launcher log.");

        EngineLog.Write("[LauncherTrayInstaller] InstallAsync success");
        return new LauncherInstallResult(true, "Launcher tray app installed and running.", steps);
    }

    /// <summary>
    /// Is the launcher we started still alive? This is the fact that separates "not ready yet" from
    /// "never going to be ready".
    ///
    /// When the state cannot be read at all, the answer is "still running". That keeps the wait going
    /// to its ceiling instead of ending it early, which is the safe direction: reporting a healthy
    /// launcher as failed is the defect this whole wait exists to stop, and the ceiling still bounds it.
    /// </summary>
    private static bool StarterIsRunning(Process started)
    {
        try { return !started.HasExited; }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherTrayInstaller] could not read the started launcher's process state: {ex.Message}");
            return true;
        }
    }

    /// <summary>The reason the wait ended, for the log line that records it.</summary>
    private static string DescribeStop(LauncherWaitStop stop) => stop switch
    {
        LauncherWaitStop.Healthy => "certified",
        LauncherWaitStop.StarterExited => "the process this install started has exited",
        LauncherWaitStop.CeilingReached => "still running but unregistered when the wait ceiling elapsed",
        LauncherWaitStop.Cancelled => "the install was cancelled",
        _ => stop.ToString(),
    };

    /// <summary>
    /// What to tell the user. Each end of the wait is a different fact and deserves a different
    /// sentence: a launcher that died is not a launcher that is slow, and neither is a pre-existing
    /// launcher still holding the registration. Every one of them names the log directory, because that
    /// is the only actionable part.
    /// </summary>
    private string DescribeFailure(LauncherWaitResult wait, int startedPid, string? expectedVersion)
    {
        var health = wait.Health;
        var logs = _layout.LogsDir;

        if (wait.Stop == LauncherWaitStop.Cancelled)
            return $"Waiting for the Launcher tray app was cancelled. Check {logs}.";

        if (health is null)
            return wait.Stop == LauncherWaitStop.StarterExited
                ? $"The Launcher tray app started as process {startedPid} but exited before it wrote its registration. Check {logs}."
                : $"The Launcher tray app is still running as process {startedPid} but did not write its registration within {(int)FirstStartHealthCeiling.TotalMinutes} minutes. Check {logs}.";

        if (health.Pid == startedPid)
            return $"The Launcher tray app (process {startedPid}) wrote its registration but did not report itself healthy"
                   + (LauncherHealthProbe.VersionMatches(expectedVersion, health.Version)
                       ? $" (alive={health.Ok}). Check {logs}."
                       : $": it reports version {health.Version ?? "unknown"}, not the freshly installed {expectedVersion}. Check {logs}.");

        return $"A launcher registration exists, but it names process {health.Pid} reporting version {health.Version ?? "unknown"} - not the process {startedPid} this install started. Refusing to certify: another launcher instance is running. Check {logs}.";
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> for launching the long-lived tray Launcher.
    ///
    /// <c>UseShellExecute=true</c> (with NO <c>RedirectStandard*</c>) so the Launcher does NOT inherit
    /// the caller's stdout/stderr handles. An inherited stdout pipe keeps the caller's pipe open for
    /// the Launcher's whole lifetime, so any caller that PIPES the CLI hangs forever after it exits
    /// (the same fix as the GatewayApp relaunch path; see issue #175).
    /// </summary>
    public static ProcessStartInfo BuildTrayLaunchInfo(string launcherExe, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherExe);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        return new ProcessStartInfo
        {
            FileName = launcherExe,
            Arguments = InstalledArguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
        };
    }

    /// <summary>
    /// Kill installed launcher processes (image under the install dir ONLY) so its file unlocks. A dev
    /// launcher running from a repo checkout is never touched.
    /// </summary>
    private void StopInstalledProcesses(List<string> steps)
    {
        var stopped = 0;
        foreach (var p in Process.GetProcessesByName("cc-launcher"))
        {
            try
            {
                var path = p.MainModule?.FileName ?? "";
                // A directory boundary is required: a bare prefix also matches a sibling such as
                // "<LauncherDir>-dev", which is somebody else's launcher. Same bug as the one fixed in
                // LauncherStopper, in a second copy of the same idea.
                var installedPrefix = _layout.LauncherDir.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(installedPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                stopped++;
            }
            catch (Exception ex) { EngineLog.Write($"[LauncherTrayInstaller] stop cc-launcher pid={p.Id}: {ex.Message}"); }
            finally { p.Dispose(); }
        }
        if (stopped > 0) steps.Add($"stopped {stopped} running installed Launcher process(es)");
    }

    private static LauncherInstallResult Fail(List<string> steps, string message)
    {
        EngineLog.Write($"[LauncherTrayInstaller] FAILED: {message}");
        return new LauncherInstallResult(false, message, steps);
    }
}
