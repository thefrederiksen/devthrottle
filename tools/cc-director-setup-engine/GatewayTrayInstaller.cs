using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of a Gateway tray-app install, with the steps taken (for logs / UI).</summary>
public sealed record GatewayInstallResult(bool Success, string Message, IReadOnlyList<string> Steps);

/// <summary>
/// Performs the Gateway-role first install that the generic <see cref="UpdateRunner"/> cannot:
///   1. extract the Cockpit .zip (the runner skips archive assets) into the per-user Cockpit dir,
///   2. start the Gateway tray app with <c>--managed</c> (it supervises the Cockpit and registers
///      its own HKCU Run-key autostart on startup),
///   3. wait for the Gateway (7878) and the supervised Cockpit (7470) to answer.
///
/// The install path does NOT ask for or write an OPENAI_API_KEY: inference routes through
/// DevThrottle's account-minted <c>dt_live_</c> key, which the managed Gateway runtime auto-mints and
/// stores itself after account sign-in (AccountInferenceKeyProvisioner / TranscriptionKeyAutoProvisioner).
/// The Gateway exe itself is already placed by the UpdateRunner at the Gateway component path before
/// this runs. Everything is per-user (%LOCALAPPDATA%): NO elevation, NO Windows service
/// (docs/plans/gateway-tray-app.md). Windows-only.
/// </summary>
public sealed class GatewayTrayInstaller
{
    /// <summary>The Gateway's default port; kept here so the engine has no compile dependency on the Gateway exe.</summary>
    public const int GatewayDefaultPort = 7878;

    /// <summary>The arguments the installed tray app runs with (managed mode: self-update on).</summary>
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
    /// component. No OPENAI_API_KEY is asked for or written: the managed Gateway runtime auto-mints and
    /// stores the account <c>dt_live_</c> inference key itself after sign-in.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<GatewayInstallResult> InstallAsync(
        ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);

        var steps = new List<string>();
        EngineLog.Write("[GatewayTrayInstaller] InstallAsync begin");

        var gatewayExe = _layout.PathFor(ComponentRegistry.Gateway);
        if (!File.Exists(gatewayExe))
            return Fail(steps, $"Gateway exe not present at {gatewayExe}; the file swap must run first.");

        // 1. Stop any already-running installed Gateway (and a legacy Cockpit child from a
        // pre-cutover install) so their files unlock before extraction (re-install / repair path).
        // Scoped to processes under the install dirs only.
        StopInstalledProcesses(steps);

        // 2. Extract the mobile app (issue #809) into wwwroot/mobile beside the Gateway exe so /mobile
        // serves on a clean install with no manual copy. The single-file exe carries no loose content, so
        // this side-car zip is the delivery. A release that predates the mobile app (#806) has no such
        // asset and simply serves no /mobile (ExtractAsync returns null).
        try
        {
            var mobileDir = await MobilePackage.ExtractAsync(_layout, release, source, ct);
            steps.Add(mobileDir is null
                ? "no mobile app asset in this release (no /mobile)"
                : $"extracted {MobilePackage.AssetName} -> {mobileDir}");
            EngineLog.Write($"[GatewayTrayInstaller] mobile app at {(mobileDir ?? "(none)")}");
        }
        catch (Exception ex)
        {
            return Fail(steps, $"Mobile app extraction failed: {ex.Message}");
        }

        // 2b. Extract the React Cockpit (epic #967 cutover, issue #979) into wwwroot/c beside the exe so
        // the Gateway serves the Cockpit at the site root on a clean install. Same side-car delivery as
        // the mobile app above (the single-file exe carries no loose content). A release that predates
        // the cutover has no such asset and serves no Cockpit (ExtractAsync returns null).
        try
        {
            var cockpitDir = await CockpitAssetPackage.ExtractAsync(_layout, release, source, ct);
            steps.Add(cockpitDir is null
                ? "no Cockpit asset in this release (no Cockpit UI)"
                : $"extracted {CockpitAssetPackage.AssetName} -> {cockpitDir}");
            EngineLog.Write($"[GatewayTrayInstaller] Cockpit at {(cockpitDir ?? "(none)")}");
        }
        catch (Exception ex)
        {
            return Fail(steps, $"Cockpit extraction failed: {ex.Message}");
        }

        // 2c. Extract the bundled ffmpeg (issue #1186) beside the Gateway exe so the long-clip WebM/Opus
        // -> PCM WAV transcode (issue #1139) works on a clean install with no manual copy. Same side-car
        // delivery as the mobile app / Cockpit above (the single-file exe carries no loose content), but
        // ffmpeg.exe lands in the Gateway dir root where FfmpegAudioTranscoder resolves it. A release that
        // predates #1186 has no such asset and simply cannot transcode over-budget non-WAV clips
        // (ExtractAsync returns null).
        try
        {
            var ffmpegExe = await FfmpegPackage.ExtractAsync(_layout, release, source, ct);
            steps.Add(ffmpegExe is null
                ? "no ffmpeg asset in this release (no long non-WAV transcode)"
                : $"extracted {FfmpegPackage.AssetName} -> {ffmpegExe}");
            EngineLog.Write($"[GatewayTrayInstaller] ffmpeg at {(ffmpegExe ?? "(none)")}");
        }
        catch (Exception ex)
        {
            return Fail(steps, $"ffmpeg extraction failed: {ex.Message}");
        }

        // 3. Start the tray app. It registers its own HKCU Run-key autostart (pointing at itself with
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

        // 4. Wait for health: the Gateway itself. It serves the React Cockpit in-process (issue #979),
        // so a healthy Gateway IS a live Cockpit - there is no separate Cockpit process to health-check.
        var gatewayUp = await WaitForHttpAsync($"http://127.0.0.1:{GatewayDefaultPort}/healthz", TimeSpan.FromSeconds(20), ct);
        steps.Add($"gateway healthz on {GatewayDefaultPort}: {(gatewayUp ? "OK" : "no response")}");
        if (!gatewayUp)
            return Fail(steps, $"Gateway tray app started but did not answer on {GatewayDefaultPort}. Check {_layout.LogsDir}.");

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
        // Both old (pre-rename) and new names: updating an old install must stop the
        // still-running cc-director-gateway/cockpit before the devthrottle-* swap.
        foreach (var name in new[] { "cc-director-gateway", "cc-director-cockpit", "devthrottle-gateway", "devthrottle-cockpit" })
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
                catch (Exception ex) { EngineLog.Write($"[GatewayTrayInstaller] stop {name} pid={p.Id} FAILED: {ex.Message}"); }
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
