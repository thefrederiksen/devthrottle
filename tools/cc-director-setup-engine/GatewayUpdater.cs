using System.Diagnostics;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Drives a Gateway self-update: decide if a newer Gateway exe is available, stage it (download +
/// verify) to a stable path, and launch the detached helper (the staged exe in <c>--apply-update</c>
/// mode) that performs the stop -> swap -> relaunch -> health -> rollback
/// (<see cref="GatewaySelfUpdate"/>). The helper runs from the STAGED copy so the installed exe is
/// free to overwrite once the running tray app exits. Refresh-only and pin-aware. Everything is
/// per-user (no elevation): the Gateway is a tray app under %LOCALAPPDATA%.
/// </summary>
public sealed class GatewayUpdater
{
    private readonly InstallLayout _layout;

    public GatewayUpdater(InstallLayout? layout = null)
    {
        _layout = layout ?? InstallLayout.Default();
    }

    /// <summary>The staging path the new Gateway exe is downloaded to before the swap.</summary>
    public string StagedExePath => Path.Combine(_layout.StateDir, "staged", "devthrottle-gateway.exe");

    /// <summary>
    /// The staging path the matching mobile app zip is downloaded to before a self-update (issue
    /// #809), so the detached update helper can lay <c>wwwroot/m</c> beside the swapped exe with no
    /// download. Staged + SHA-verified by <see cref="StageAsync"/>.
    /// </summary>
    public string StagedMobileZipPath => Path.Combine(_layout.StateDir, "staged", MobilePackage.AssetName);

    /// <summary>True when the release has a Gateway newer than the installed one (and it isn't pinned).</summary>
    public bool IsUpdateAvailable(ResolvedRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var asset = release.Manifest.TryGetAsset(ComponentRegistry.Gateway.WindowsAsset);
        if (asset is null) return false;

        var installed = new InstalledStateReader(_layout).Read(ComponentRegistry.Gateway);
        if (!installed.Present) return false; // refresh-only

        if (PinStore.Load(_layout).IsPinned(ComponentRegistry.Gateway.Id, asset.Version)) return false;
        return VersionUtil.IsNewer(asset.Version, installed.Version);
    }

    /// <summary>
    /// Download + SHA-256 verify the new Gateway exe to <see cref="StagedExePath"/>. Returns the staged
    /// path and version, or null if no update is available. Throws on a hash mismatch (never stages a
    /// corrupt build).
    /// </summary>
    public async Task<(string StagedPath, string Version)?> StageAsync(ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);
        if (!IsUpdateAvailable(release)) return null;

        var asset = release.Manifest.TryGetAsset(ComponentRegistry.Gateway.WindowsAsset);
        if (asset is null) return null;

        var downloaded = await source.DownloadAssetAsync(asset.Name, release.DownloadUrls, ct);
        try
        {
            if (!Hashing.Sha256Matches(downloaded, asset.Sha256))
                throw new InvalidOperationException("Gateway asset SHA-256 mismatch; not staging.");

            Directory.CreateDirectory(Path.GetDirectoryName(StagedExePath) ?? _layout.StateDir);
            File.Copy(downloaded, StagedExePath, overwrite: true);
            EngineLog.Write($"[GatewayUpdater] staged Gateway {asset.Version} -> {StagedExePath}");

            // Issue #809: stage the matching mobile app zip next to the staged exe so the update helper
            // can lay wwwroot/m beside the swapped Gateway with no download (the single-file exe carries
            // no loose content). The exe and its mobile app ship from the SAME release, so they stage
            // together.
            await StageMobileZipAsync(release, source, ct);
            return (StagedExePath, asset.Version);
        }
        finally
        {
            try { if (File.Exists(downloaded)) File.Delete(downloaded); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Stage the matching mobile app zip (issue #809) to <see cref="StagedMobileZipPath"/>. SHA-verified
    /// here (never stage a corrupt build). When the release carries no mobile asset (a release that
    /// predates issue #806), any stale staged zip is removed so the helper never applies an out-of-date
    /// mobile build over a newer one.
    /// </summary>
    private async Task StageMobileZipAsync(ResolvedRelease release, ReleaseSource source, CancellationToken ct)
    {
        var asset = release.Manifest.TryGetAsset(MobilePackage.AssetName);
        if (asset is null)
        {
            try { if (File.Exists(StagedMobileZipPath)) File.Delete(StagedMobileZipPath); } catch { /* best effort */ }
            EngineLog.Write($"[GatewayUpdater] release has no {MobilePackage.AssetName}; no mobile files staged");
            return;
        }

        var downloaded = await source.DownloadAssetAsync(asset.Name, release.DownloadUrls, ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(asset.Sha256) && !Hashing.Sha256Matches(downloaded, asset.Sha256))
                throw new InvalidOperationException("Mobile app zip SHA-256 mismatch; not staging.");

            Directory.CreateDirectory(Path.GetDirectoryName(StagedMobileZipPath) ?? _layout.StateDir);
            File.Copy(downloaded, StagedMobileZipPath, overwrite: true);
            EngineLog.Write($"[GatewayUpdater] staged {MobilePackage.AssetName} {asset.Version} -> {StagedMobileZipPath}");
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
    public Process LaunchDetachedUpdater(string stagedExePath, string newVersion, int port = GatewayTrayInstaller.GatewayDefaultPort)
    {
        var target = _layout.PathFor(ComponentRegistry.Gateway);
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

        var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch the Gateway self-update helper.");
        EngineLog.Write($"[GatewayUpdater] launched detached updater pid={p.Id} for {newVersion}");
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
