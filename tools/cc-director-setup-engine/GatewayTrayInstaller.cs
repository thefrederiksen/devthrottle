using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of a Gateway tray-app install, with the steps taken (for logs / UI).</summary>
public sealed record GatewayInstallResult(bool Success, string Message, IReadOnlyList<string> Steps);

/// <summary>
/// Performs the Gateway-role first install that the generic <see cref="UpdateRunner"/> cannot:
///   1. extract the Cockpit .zip (the runner skips archive assets) into the per-user Cockpit dir,
///   2. ensure OPENAI_API_KEY is available to the user environment (the Gateway needs it),
///   3. start the Gateway tray app with <c>--managed</c> (it supervises the Cockpit and registers
///      its own HKCU Run-key autostart on startup),
///   4. wait for the Gateway (7878) and the supervised Cockpit (7470) to answer.
///
/// The Gateway exe itself is already placed by the UpdateRunner at the Gateway component path before
/// this runs. Everything is per-user (%LOCALAPPDATA%): NO elevation, NO Windows service
/// (docs/plans/gateway-tray-app.md). Windows-only.
/// </summary>
public sealed class GatewayTrayInstaller
{
    /// <summary>The Gateway's default port; kept here so the engine has no compile dependency on the Gateway exe.</summary>
    public const int GatewayDefaultPort = 7878;

    /// <summary>The Cockpit's default port (the Gateway supervises it there).</summary>
    public const int CockpitDefaultPort = 7470;

    /// <summary>The arguments the installed tray app runs with: managed mode supervises the Cockpit.</summary>
    public const string InstalledArguments = "--managed";

    private readonly InstallLayout _layout;
    private readonly HttpClient _http;

    public GatewayTrayInstaller(InstallLayout layout, HttpClient? http = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Install + start the Gateway tray app from an already-resolved release. The Gateway exe must
    /// already be placed (by the UpdateRunner) at <see cref="InstallLayout.PathFor"/> for the Gateway
    /// component. <paramref name="openAiKey"/> is written to the user environment when provided;
    /// otherwise it must already be present there.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<GatewayInstallResult> InstallAsync(
        ResolvedRelease release, ReleaseSource source, string? openAiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);

        var steps = new List<string>();
        EngineLog.Write("[GatewayTrayInstaller] InstallAsync begin");

        var gatewayExe = _layout.PathFor(ComponentRegistry.Gateway);
        if (!File.Exists(gatewayExe))
            return Fail(steps, $"Gateway exe not present at {gatewayExe}; the file swap must run first.");

        // 1. OPENAI_API_KEY: the Gateway process needs it (dictation cleanup, recap). Write it to the
        // user environment when provided; otherwise require it to already be there. No silent degrade.
        var keyInUserEnv = Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(openAiKey))
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", openAiKey, EnvironmentVariableTarget.User);
            steps.Add("wrote OPENAI_API_KEY to the user environment");
        }
        else if (string.IsNullOrWhiteSpace(keyInUserEnv))
        {
            return Fail(steps,
                "OPENAI_API_KEY is not set in the user environment and was not provided. " +
                "Set it (setx OPENAI_API_KEY <key>) and re-run the Gateway install.");
        }
        else
        {
            steps.Add("OPENAI_API_KEY already present in the user environment");
        }

        // 2. Stop any already-running installed Gateway/Cockpit so the Cockpit files unlock before
        // extraction (re-install / repair path). Scoped to processes under the install dirs only.
        StopInstalledProcesses(steps);

        // 3. Extract the Cockpit zip (the runner skips archive assets). The tray app supervises this exe.
        try
        {
            var cockpitExe = await CockpitPackage.ExtractAsync(_layout, release, source, ct);
            steps.Add($"extracted {CockpitPackage.AssetName} -> {_layout.CockpitDir}");
            EngineLog.Write($"[GatewayTrayInstaller] cockpit exe at {cockpitExe}");
        }
        catch (Exception ex)
        {
            return Fail(steps, $"Cockpit extraction failed: {ex.Message}");
        }

        // 4. Start the tray app. It registers its own HKCU Run-key autostart (pointing at itself with
        // the same arguments) on startup, so install-time registration and app self-registration can
        // never disagree.
        try
        {
            var psi = BuildTrayLaunchInfo(gatewayExe, _layout.GatewayDir);
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            steps.Add($"started Gateway tray app pid={p.Id} ({InstalledArguments})");
        }
        catch (Exception ex)
        {
            return Fail(steps, $"Failed to start the Gateway tray app: {ex.Message}");
        }

        // 5. Wait for health: the Gateway itself, then the Cockpit it supervises.
        var gatewayUp = await WaitForHttpAsync($"http://127.0.0.1:{GatewayDefaultPort}/healthz", TimeSpan.FromSeconds(20), ct);
        steps.Add($"gateway healthz on {GatewayDefaultPort}: {(gatewayUp ? "OK" : "no response")}");
        if (!gatewayUp)
            return Fail(steps, $"Gateway tray app started but did not answer on {GatewayDefaultPort}. Check {_layout.LogsDir}.");

        var cockpitUp = await WaitForHttpAsync($"http://127.0.0.1:{CockpitDefaultPort}/", TimeSpan.FromSeconds(30), ct);
        steps.Add($"cockpit on {CockpitDefaultPort}: {(cockpitUp ? "OK" : "no response")}");
        if (!cockpitUp)
            return Fail(steps, $"Gateway is up but the supervised Cockpit did not answer on {CockpitDefaultPort}. Check {_layout.LogsDir}.");

        var registered = GatewayAutostart.IsRegistered();
        steps.Add($"autostart Run key: {(registered ? "registered" : "NOT registered")}");
        if (!registered)
            return Fail(steps, "Gateway is healthy but did not register its autostart Run key; check the gateway log.");

        EngineLog.Write("[GatewayTrayInstaller] InstallAsync success");
        return new GatewayInstallResult(true, $"Gateway tray app installed and running; Cockpit live at {TailnetResolver.FrontDoorUrl()}.", steps);
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> for launching the long-lived tray Gateway.
    ///
    /// <c>UseShellExecute=true</c> (with NO <c>RedirectStandard*</c>) so the Gateway does NOT inherit
    /// the setup CLI's stdout/stderr handles. An inherited stdout pipe keeps the caller's pipe open
    /// for the Gateway's whole lifetime, so any caller that PIPES the CLI
    /// (e.g. "... | Select-Object", "... | tail") hangs forever after the CLI exits. This mirrors the
    /// proven fix on the GatewayApp self-update relaunch path (see
    /// src/CcDirector.GatewayApp/Program.cs startGateway). See issue #175.
    /// </summary>
    public static ProcessStartInfo BuildTrayLaunchInfo(string gatewayExe, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayExe);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        return new ProcessStartInfo
        {
            FileName = gatewayExe,
            Arguments = InstalledArguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
        };
    }

    /// <summary>
    /// Kill installed Gateway/Cockpit processes (image under the install dirs ONLY) so their files
    /// unlock. A dev gateway running from a repo checkout is never touched.
    /// </summary>
    private void StopInstalledProcesses(List<string> steps)
    {
        var stopped = 0;
        foreach (var name in new[] { "cc-director-gateway", "cc-director-cockpit" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = p.MainModule?.FileName ?? "";
                    var owned = path.StartsWith(_layout.GatewayDir, StringComparison.OrdinalIgnoreCase)
                             || path.StartsWith(_layout.CockpitDir, StringComparison.OrdinalIgnoreCase);
                    if (!owned) continue;
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    stopped++;
                }
                catch (Exception ex) { EngineLog.Write($"[GatewayTrayInstaller] stop {name} pid={p.Id}: {ex.Message}"); }
                finally { p.Dispose(); }
            }
        }
        if (stopped > 0) steps.Add($"stopped {stopped} running installed Gateway/Cockpit process(es)");
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

    private static GatewayInstallResult Fail(List<string> steps, string message)
    {
        EngineLog.Write($"[GatewayTrayInstaller] FAILED: {message}");
        return new GatewayInstallResult(false, message, steps);
    }
}
