using System.Diagnostics;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Drives a CC Launcher self-update: decide if a newer Launcher exe is available, stage it (download
/// + verify) to a stable path, and launch the detached helper (the staged exe in
/// <c>--apply-update</c> mode) that performs the stop -> swap -> relaunch -> health -> rollback
/// (<see cref="LauncherSelfUpdate"/>). The helper runs from the STAGED copy so the installed exe is
/// free to overwrite once the running tray app exits; its health check reads the registration file
/// the relaunched launcher writes (there is no port to probe). Refresh-only and pin-aware. Everything
/// is per-user (no elevation): the Launcher is a tray app under %LOCALAPPDATA%. Mirrors
/// <see cref="GatewayUpdater"/>.
/// </summary>
public sealed class LauncherUpdater
{
    private readonly InstallLayout _layout;

    public LauncherUpdater(InstallLayout? layout = null)
    {
        _layout = layout ?? InstallLayout.Default();
    }

    /// <summary>
    /// The staging path the new Launcher build is downloaded to before the swap.
    ///
    /// THE NAME IS THE PLATFORM'S, NOT ALWAYS WINDOWS'S. This was <c>cc-launcher.exe</c> unconditionally
    /// - harmless-looking, since a name is only a name, and on macOS it would have staged a file whose
    /// extension says it is a Windows program into a directory a Mac reads. The suffix is what the
    /// operating system and every person reading a log uses to tell one from the other.
    /// </summary>
    public string StagedBuildPath =>
        Path.Combine(_layout.StateDir, "staged", OperatingSystem.IsWindows() ? "cc-launcher.exe" : "cc-launcher");

    /// <summary>
    /// The release asset holding a Launcher for THIS machine, or null when the release has none.
    ///
    /// This asked for <see cref="Component.WindowsAsset"/> by name, which is the whole reason no launcher
    /// on a Mac or a Linux box has ever updated itself. The Mac and Linux launcher assets are built and
    /// published in every release; nothing but the installer has ever asked for one.
    /// </summary>
    private static string? AssetName() => ComponentRegistry.Launcher.AssetFor(HostPlatform.Current);

    /// <summary>True when the release has a Launcher newer than the installed one (and it isn't pinned).</summary>
    public bool IsUpdateAvailable(ResolvedRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (AssetName() is not { } assetName) return false;
        var asset = release.Manifest.TryGetAsset(assetName);
        if (asset is null) return false;

        var installed = new InstalledStateReader(_layout).Read(ComponentRegistry.Launcher);
        if (!installed.Present) return false; // refresh-only

        if (PinStore.Load(_layout).IsPinned(ComponentRegistry.Launcher.Id, asset.Version)) return false;
        return VersionUtil.IsNewer(asset.Version, installed.Version);
    }

    /// <summary>
    /// Download + SHA-256 verify the new Launcher build to <see cref="StagedBuildPath"/>. Returns the
    /// staged path and version, or null if no update is available. Throws on a hash mismatch (never
    /// stages a corrupt build).
    ///
    /// RUNS ON EVERY PLATFORM, and only the APPLYING of it is platform-dependent. Staging is a download,
    /// a hash check and a file copy - nothing about it is Windows-shaped, and a Mac that stages is a Mac
    /// whose Director can install the result (<see cref="LauncherUpdateOwner"/>). Keeping the download
    /// behind the same Windows gate as the apply is what left every Mac with nothing to install.
    /// </summary>
    public async Task<(string StagedPath, string Version)?> StageAsync(ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);
        if (!IsUpdateAvailable(release)) return null;

        if (AssetName() is not { } assetName) return null;
        var asset = release.Manifest.TryGetAsset(assetName);
        if (asset is null) return null;

        var downloaded = await source.DownloadAssetAsync(asset.Name, release.DownloadUrls, ct);
        try
        {
            if (!Hashing.Sha256Matches(downloaded, asset.Sha256))
                throw new InvalidOperationException("Launcher asset SHA-256 mismatch; not staging.");

            var staged = StagedBuildPath;
            Directory.CreateDirectory(Path.GetDirectoryName(staged) ?? _layout.StateDir);

            // The sidecar is removed FIRST and rewritten LAST, so the window in which a reader could see
            // a new binary described by the previous version's sidecar does not exist. A staged build
            // with no sidecar is refused; one with the wrong sidecar would be installed and then
            // mis-recorded, which is the failure that cannot be seen afterwards.
            StagedBuildVersion.Delete(staged);
            File.Copy(downloaded, staged, overwrite: true);
            RunnableBuild.Prepare(staged);
            StagedBuildVersion.Write(staged, asset.Version);

            EngineLog.Write($"[LauncherUpdater] staged Launcher {asset.Version} ({asset.Name}) -> {staged}");
            return (staged, asset.Version);
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
    public Process LaunchDetachedUpdater(string stagedExePath, string newVersion)
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

        var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch the Launcher self-update helper.");
        EngineLog.Write($"[LauncherUpdater] launched detached updater pid={p.Id} for {newVersion}");
        return p;
    }

    /// <summary>
    /// Convenience: if an update is available, stage it and launch the detached helper. Returns the
    /// staged version, or null.
    ///
    /// WINDOWS ONLY, AND THAT IS NOT THE GAP THIS CHANGE CLOSED. The detached helper exists because on
    /// Windows the launcher has to replace its own locked executable, so something outside it must
    /// outlive it. Off Windows there is a better process available for the job: the DIRECTOR, which is
    /// already running, is not the file being replaced, and can witness the result
    /// (<see cref="LauncherUpdateOwner"/>). So a Mac stages and the Director installs; only the
    /// applying differs, never the staging.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<string?> CheckStageAndLaunchAsync(ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        var staged = await StageAsync(release, source, ct);
        if (staged is null) return null;
        LaunchDetachedUpdater(staged.Value.StagedPath, staged.Value.Version);
        return staged.Value.Version;
    }
}
