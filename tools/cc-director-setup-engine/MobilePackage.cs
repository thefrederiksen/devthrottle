using System.IO.Compression;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Handles the mobile app's archive asset (devthrottle-gateway-mobile-win-x64.zip): the built React
/// PWA (issue #806) the Gateway serves at <c>/m</c>. The single-file Gateway exe carries NO loose
/// content, so a delivery of only the exe drops the mobile app and <c>/m</c> answers 404 on every
/// installed / self-updated / redeployed Gateway (issue #809). The build ships the mobile app as a
/// side-car zip (the Cockpit-zip pattern, <see cref="CockpitPackage"/>) that the setup engine unpacks
/// into <c>wwwroot/m</c> BESIDE the Gateway exe - exactly where
/// <c>MobileApp.WebRoot</c> (<c>AppContext.BaseDirectory/wwwroot/m</c>, see
/// <see cref="InstallLayout.GatewayMobileDir"/>) looks. Kept separate from the Windows-only tray work
/// so extraction is testable on any OS without elevation.
/// </summary>
public static class MobilePackage
{
    /// <summary>The release asset carrying the built mobile app (the contents of wwwroot/m).</summary>
    public const string AssetName = "devthrottle-gateway-mobile-win-x64.zip";

    private const string IndexFile = "index.html";

    /// <summary>
    /// Download + SHA-256 verify the mobile zip and extract it into <c>wwwroot/m</c> beside the
    /// Gateway exe (the clean-install path). Returns the <c>wwwroot/m</c> directory, or <c>null</c>
    /// when the release carries no mobile asset (a release that predates issue #806 - the Gateway
    /// simply serves no <c>/m</c>). Throws on a SHA-256 mismatch or a missing <c>index.html</c> after
    /// extraction (no silent degrade).
    /// </summary>
    public static async Task<string?> ExtractAsync(
        InstallLayout layout, ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);

        var asset = release.Manifest.TryGetAsset(AssetName);
        if (asset is null)
        {
            EngineLog.Write($"[MobilePackage] release has no {AssetName}; the Gateway will serve no /m (release predates #806)");
            return null;
        }

        var staged = await source.DownloadAssetAsync(AssetName, release.DownloadUrls, ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(asset.Sha256) && !Hashing.Sha256Matches(staged, asset.Sha256))
                throw new InvalidOperationException("Mobile app zip SHA-256 mismatch; download rejected.");

            var dir = ExtractZip(staged, layout);
            EngineLog.Write($"[MobilePackage] extracted {AssetName} {asset.Version} -> {dir}");
            return dir;
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Extract an ALREADY-staged-and-verified mobile zip (the self-update path: the running Gateway
    /// staged + SHA-verified it via <see cref="GatewayUpdater.StagedMobileZipPath"/> before launching
    /// the update helper, so no download or release source is needed here). Returns the
    /// <c>wwwroot/m</c> directory, or <c>null</c> when no staged zip is present (a release without the
    /// mobile asset - nothing to apply). Throws on a missing <c>index.html</c> after extraction.
    /// </summary>
    public static string? ExtractStagedZip(InstallLayout layout, string stagedZipPath)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedZipPath);

        if (!File.Exists(stagedZipPath))
        {
            EngineLog.Write($"[MobilePackage] no staged mobile zip at {stagedZipPath}; leaving wwwroot/m unchanged");
            return null;
        }

        var dir = ExtractZip(stagedZipPath, layout);
        EngineLog.Write($"[MobilePackage] applied staged {AssetName} -> {dir}");
        return dir;
    }

    /// <summary>
    /// Replace <c>wwwroot/m</c> beside the Gateway exe with the zip's contents. The zip's root is the
    /// contents of <c>wwwroot/m</c> (<c>index.html</c> + the hashed <c>assets/</c>), so it extracts
    /// directly into the mobile dir. Cleans the target first so a re-install never leaves a stale
    /// hashed asset behind (the Cockpit-extract precedent).
    /// </summary>
    private static string ExtractZip(string zipPath, InstallLayout layout)
    {
        var mobileDir = layout.GatewayMobileDir;
        if (Directory.Exists(mobileDir))
            Directory.Delete(mobileDir, recursive: true);
        Directory.CreateDirectory(mobileDir);

        ZipFile.ExtractToDirectory(zipPath, mobileDir, overwriteFiles: true);

        var index = Path.Combine(mobileDir, IndexFile);
        if (!File.Exists(index))
            throw new InvalidOperationException($"Mobile app {IndexFile} not found after extraction at {index}.");
        return mobileDir;
    }
}
