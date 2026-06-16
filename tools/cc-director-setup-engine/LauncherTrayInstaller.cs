using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of a Launcher tray-app install, with the steps taken (for logs / UI).</summary>
public sealed record LauncherInstallResult(bool Success, string Message, IReadOnlyList<string> Steps);

/// <summary>
/// Performs the post-download step the generic <see cref="UpdateRunner"/> cannot: the runner places
/// cc-launcher.exe but never starts it, so on a fresh install the launcher sits dormant and its
/// autostart Run key is never written. This:
///   1. stops any already-running installed launcher (so a fresh managed instance takes over),
///   2. starts the launcher tray app with <c>--managed</c> (it runs the periodic self-update check
///      and registers its own HKCU Run-key autostart on startup),
///   3. waits for the launcher (7900) to answer /healthz,
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
    /// <summary>The Launcher's default loopback REST port; kept here so the engine has no compile dependency on the Launcher exe.</summary>
    public const int LauncherDefaultPort = 7900;

    /// <summary>The arguments the installed tray app runs with: managed mode runs the self-update check.</summary>
    public const string InstalledArguments = "--managed";

    private readonly InstallLayout _layout;
    private readonly HttpClient _http;

    public LauncherTrayInstaller(InstallLayout layout, HttpClient? http = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Start the already-placed Launcher tray app in managed mode and verify it is healthy and
    /// autostart-registered. The Launcher exe must already be placed (by the UpdateRunner) at
    /// <see cref="InstallLayout.PathFor"/> for the Launcher component.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<LauncherInstallResult> InstallAsync(CancellationToken ct = default)
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
        try
        {
            var psi = BuildTrayLaunchInfo(launcherExe, _layout.LauncherDir);
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            steps.Add($"started Launcher tray app pid={p.Id} ({InstalledArguments})");
        }
        catch (Exception ex)
        {
            return Fail(steps, $"Failed to start the Launcher tray app: {ex.Message}");
        }

        // 3. Wait for health.
        var up = await WaitForHttpAsync($"http://127.0.0.1:{LauncherDefaultPort}/healthz", TimeSpan.FromSeconds(20), ct);
        steps.Add($"launcher healthz on {LauncherDefaultPort}: {(up ? "OK" : "no response")}");
        if (!up)
            return Fail(steps, $"Launcher tray app started but did not answer on {LauncherDefaultPort}. Check {_layout.LogsDir}.");

        // 4. Verify the autostart Run key (written by the app on startup).
        var registered = LauncherAutostart.IsRegistered();
        steps.Add($"autostart Run key: {(registered ? "registered" : "NOT registered")}");
        if (!registered)
            return Fail(steps, "Launcher is healthy but did not register its autostart Run key; check the launcher log.");

        EngineLog.Write("[LauncherTrayInstaller] InstallAsync success");
        return new LauncherInstallResult(true, $"Launcher tray app installed and running on {LauncherDefaultPort}.", steps);
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
                if (!path.StartsWith(_layout.LauncherDir, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                stopped++;
            }
            catch (Exception ex) { EngineLog.Write($"[LauncherTrayInstaller] stop cc-launcher pid={p.Id}: {ex.Message}"); }
            finally { p.Dispose(); }
        }
        if (stopped > 0) steps.Add($"stopped {stopped} running installed Launcher process(es)");
    }

    private async Task<bool> WaitForHttpAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch
            {
                // not up yet
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private static LauncherInstallResult Fail(List<string> steps, string message)
    {
        EngineLog.Write($"[LauncherTrayInstaller] FAILED: {message}");
        return new LauncherInstallResult(false, message, steps);
    }
}
