using System.IO.Compression;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Handles the Cockpit's archive asset (cc-director-cockpit-win-x64.zip), which the generic
/// <see cref="UpdateRunner"/> skips. On a Gateway first install this downloads the zip, verifies its
/// SHA-256 against the manifest, and extracts it into the Cockpit dir so the Gateway service can
/// supervise the resulting exe. Kept separate from the (Windows-only) service registration so the
/// extraction is testable on any OS without elevation.
/// </summary>
public static class CockpitPackage
{
    public const string AssetName = "cc-director-cockpit-win-x64.zip";

    /// <summary>
    /// Download + verify + extract the Cockpit zip into <see cref="InstallLayout.CockpitDir"/>,
    /// replacing any prior contents. Returns the path to the extracted Cockpit exe. Throws on a
    /// missing asset, a SHA-256 mismatch, or a missing exe after extraction (no silent degrade).
    /// </summary>
    public static async Task<string> ExtractAsync(
        InstallLayout layout, ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);

        var asset = release.Manifest.TryGetAsset(AssetName)
            ?? throw new InvalidOperationException($"Release has no {AssetName} asset.");

        var staged = await source.DownloadAssetAsync(AssetName, release.DownloadUrls, ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(asset.Sha256) && !Hashing.Sha256Matches(staged, asset.Sha256))
                throw new InvalidOperationException("Cockpit zip SHA-256 mismatch; download rejected.");

            // Clean the target so a re-install never leaves a stale assembly behind: a mismatched
            // Cockpit + Contracts dll pair renders a blank Blazor circuit.
            if (Directory.Exists(layout.CockpitDir))
                Directory.Delete(layout.CockpitDir, recursive: true);
            Directory.CreateDirectory(layout.CockpitDir);

            ZipFile.ExtractToDirectory(staged, layout.CockpitDir, overwriteFiles: true);
            EngineLog.Write($"[CockpitPackage] extracted {AssetName} -> {layout.CockpitDir}");

            var exe = layout.PathFor(ComponentRegistry.Cockpit);
            if (!File.Exists(exe))
                throw new InvalidOperationException($"Cockpit exe not found after extraction at {exe}.");

            // Record the placed version so the planner has a reliable installed version for the Cockpit
            // (it ships as a zip, so it never goes through UpdateRunner's bookkeeping). Best-effort.
            try
            {
                var m = InstalledManifest.Load(layout);
                m.Set(ComponentRegistry.Cockpit.Id, asset.Version);
                m.Save(layout);
            }
            catch (Exception ex)
            {
                EngineLog.Write($"[CockpitPackage] recording Cockpit version failed: {ex.Message}");
            }
            return exe;
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { /* best effort */ }
        }
    }
}
