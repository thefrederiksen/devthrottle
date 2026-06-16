using System.Diagnostics;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Drives a CC Launcher self-update: decide if a newer Launcher exe is available, stage it (download
/// + verify) to a stable path, and launch the detached helper (the staged exe in
/// <c>--apply-update</c> mode) that performs the stop -> swap -> relaunch -> health -> rollback
/// (<see cref="LauncherSelfUpdate"/>). The helper runs from the STAGED copy so the installed exe is
/// free to overwrite once the running tray app exits. Refresh-only and pin-aware. Everything is
/// per-user (no elevation): the Launcher is a tray app under %LOCALAPPDATA%. Mirrors
/// <see cref="GatewayUpdater"/>.
/// </summary>
public sealed class LauncherUpdater
{
    private readonly InstallLayout _layout;

    public LauncherUpdater(InstallLayout? layout = null)
    {
        _layout = layout ?? InstallLayout.Default();
    }

    /// <summary>The staging path the new Launcher exe is downloaded to before the swap.</summary>
    public string StagedExePath => Path.Combine(_layout.StateDir, "staged", "cc-launcher.exe");

    /// <summary>True when the release has a Launcher newer than the installed one (and it isn't pinned).</summary>
    public bool IsUpdateAvailable(ResolvedRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var asset = release.Manifest.TryGetAsset(ComponentRegistry.Launcher.WindowsAsset);
        if (asset is null) return false;

        var installed = new InstalledStateReader(_layout).Read(ComponentRegistry.Launcher);
        if (!installed.Present) return false; // refresh-only

        if (PinStore.Load(_layout).IsPinned(ComponentRegistry.Launcher.Id, asset.Version)) return false;
        return VersionUtil.IsNewer(asset.Version, installed.Version);
    }

    /// <summary>
    /// Download + SHA-256 verify the new Launcher exe to <see cref="StagedExePath"/>. Returns the
    /// staged path and version, or null if no update is available. Throws on a hash mismatch (never
    /// stages a corrupt build).
    /// </summary>
    public async Task<(string StagedPath, string Version)?> StageAsync(ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);
        if (!IsUpdateAvailable(release)) return null;

        var asset = release.Manifest.TryGetAsset(ComponentRegistry.Launcher.WindowsAsset);
        if (asset is null) return null;

        var downloaded = await source.DownloadAssetAsync(asset.Name, release.DownloadUrls, ct);
        try
        {
            if (!Hashing.Sha256Matches(downloaded, asset.Sha256))
                throw new InvalidOperationException("Launcher asset SHA-256 mismatch; not staging.");

            Directory.CreateDirectory(Path.GetDirectoryName(StagedExePath) ?? _layout.StateDir);
            File.Copy(downloaded, StagedExePath, overwrite: true);
            EngineLog.Write($"[LauncherUpdater] staged Launcher {asset.Version} -> {StagedExePath}");
            return (StagedExePath, asset.Version);
        }
        finally
        {
            try { if (File.Exists(downloaded)) File.Delete(downloaded); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Launch the detached helper that applies the staged update. It runs from the staged exe (not the
    /// installed one), so it survives the running tray app exiting and can overwrite the installed exe.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public Process LaunchDetachedUpdater(string stagedExePath, string newVersion, int port = LauncherTrayInstaller.LauncherDefaultPort)
    {
        var target = _layout.PathFor(ComponentRegistry.Launcher);
        var psi = new ProcessStartInfo
        {
            FileName = stagedExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add("--new-version");
        psi.ArgumentList.Add(newVersion);
        psi.ArgumentList.Add("--target");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString());

        var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch the Launcher self-update helper.");
        EngineLog.Write($"[LauncherUpdater] launched detached updater pid={p.Id} for {newVersion}");
        return p;
    }

    /// <summary>Convenience: if an update is available, stage it and launch the detached helper. Returns the staged version, or null.</summary>
    [SupportedOSPlatform("windows")]
    public async Task<string?> CheckStageAndLaunchAsync(ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        var staged = await StageAsync(release, source, ct);
        if (staged is null) return null;
        LaunchDetachedUpdater(staged.Value.StagedPath, staged.Value.Version);
        return staged.Value.Version;
    }
}
