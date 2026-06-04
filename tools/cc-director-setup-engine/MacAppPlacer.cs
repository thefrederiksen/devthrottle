using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of placing the macOS Director .app.</summary>
public sealed record MacAppResult(bool Success, string Message, string? Version);

/// <summary>
/// Places the Director on macOS. The generic UpdateRunner places single-file exes and skips archives,
/// so the Director (shipped as cc-director-mac-arm64.zip containing "CC Director.app") needs this
/// dedicated step - the analog of CockpitPackage on Windows. It downloads + SHA-256 verifies the zip,
/// extracts the .app with ditto (preserving the bundle's symlinks + exec bits), swaps it into
/// ~/Applications, strips the Gatekeeper quarantine, and marks the launcher executable. Mirrors
/// UpdateInstaller.SwapMac so a fresh install and an auto-update converge on the same on-disk result.
/// </summary>
public static class MacAppPlacer
{
    public const string DirectorAsset = "cc-director-mac-arm64.zip";
    private const string AppName = "CC Director.app";

    [SupportedOSPlatform("macos")]
    public static async Task<MacAppResult> PlaceAsync(
        InstallLayout layout, ResolvedRelease release, ReleaseSource source,
        Action<string>? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);
        void Log(string m) => (log ?? (_ => { }))(m);

        var asset = release.Manifest.TryGetAsset(DirectorAsset);
        if (asset is null) return new MacAppResult(false, $"release is missing {DirectorAsset}.", null);

        string? zip = null, stage = null;
        try
        {
            Log($"downloading {DirectorAsset}");
            zip = await source.DownloadAssetAsync(DirectorAsset, release.DownloadUrls, ct);
            if (!Hashing.Sha256Matches(zip, asset.Sha256))
                return new MacAppResult(false, $"{DirectorAsset} SHA-256 mismatch; download rejected.", null);

            stage = Path.Combine(Path.GetTempPath(), $"cc-director-app-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stage);
            // ditto -x -k extracts a PKZip while preserving the .app's symlinks and permissions.
            var (exExit, exOut) = ProcessRunner.Run("/usr/bin/ditto", $"-x -k \"{zip}\" \"{stage}\"");
            if (exExit != 0) return new MacAppResult(false, $"extracting {DirectorAsset} failed: {Trim(exOut)}", null);

            var stagedApp = Path.Combine(stage, AppName);
            if (!Directory.Exists(stagedApp))
                return new MacAppResult(false, $"{AppName} not found inside {DirectorAsset}.", null);

            var target = layout.PathFor(ComponentRegistry.Director); // ~/Applications/CC Director.app
            Directory.CreateDirectory(layout.MacAppsDir);

            Log($"installing {AppName} to {layout.MacAppsDir}");
            // Build beside, then this is a fresh place: remove any existing app, then ditto in.
            ProcessRunner.Run("/bin/rm", $"-rf \"{target}\"");
            var (cpExit, cpOut) = ProcessRunner.Run("/usr/bin/ditto", $"\"{stagedApp}\" \"{target}\"");
            if (cpExit != 0) return new MacAppResult(false, $"installing {AppName} failed: {Trim(cpOut)}", null);

            // Post-place: de-quarantine + ensure the launcher binary is executable (mirrors SwapMac).
            ProcessRunner.Run("/usr/bin/xattr", $"-dr com.apple.quarantine \"{target}\"");
            ProcessRunner.Run("/bin/chmod", $"+x \"{Path.Combine(target, "Contents", "MacOS", "cc-director")}\"");

            // Record the Director version for the updater.
            var im = InstalledManifest.Load(layout);
            im.Set(ComponentRegistry.Director.Id, asset.Version);
            im.Save(layout);

            Log($"CC Director {asset.Version} installed to {target}");
            return new MacAppResult(true, $"CC Director {asset.Version} installed to {target}.", asset.Version);
        }
        finally
        {
            try { if (zip is not null && File.Exists(zip)) File.Delete(zip); } catch { /* best-effort */ }
            try { if (stage is not null && Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;
}
