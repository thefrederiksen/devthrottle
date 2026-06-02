namespace CcDirector.Setup.Engine;

/// <summary>
/// Decides whether a newer Cockpit is available and applies it by extracting the new zip. The Gateway
/// service (which supervises the Cockpit child) drives this while the child is stopped, so the Cockpit
/// files are not locked. Refresh-only (won't install a Cockpit that isn't already present) and honors
/// rollback pins. Records the new version in installed.json via CockpitPackage.
/// </summary>
public sealed class CockpitUpdater
{
    private readonly InstallLayout _layout;

    public CockpitUpdater(InstallLayout? layout = null)
    {
        _layout = layout ?? InstallLayout.Default();
    }

    /// <summary>True when the release has a Cockpit newer than the installed one (and it isn't pinned).</summary>
    public bool IsUpdateAvailable(ResolvedRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var asset = release.Manifest.TryGetAsset(CockpitPackage.AssetName);
        if (asset is null) return false;

        var installed = new InstalledStateReader(_layout).Read(ComponentRegistry.Cockpit);
        if (!installed.Present) return false; // refresh-only: a missing Cockpit is the installer's job

        if (PinStore.Load(_layout).IsPinned(ComponentRegistry.Cockpit.Id, asset.Version)) return false;
        return VersionUtil.IsNewer(asset.Version, installed.Version);
    }

    /// <summary>
    /// If a newer Cockpit is available, extract it (caller must have stopped the child first) and return
    /// the new version; otherwise null. Idempotent: no-op when already current/pinned/absent.
    /// </summary>
    public async Task<string?> ApplyAsync(ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);
        if (!IsUpdateAvailable(release)) return null;

        await CockpitPackage.ExtractAsync(_layout, release, source, ct);
        var version = release.Manifest.TryGetAsset(CockpitPackage.AssetName)?.Version;
        EngineLog.Write($"[CockpitUpdater] updated Cockpit to {version}");
        return version;
    }
}
