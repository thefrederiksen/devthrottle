using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of placing the macOS Director .app.</summary>
public sealed record MacAppResult(bool Success, string Message, string? Version);

/// <summary>
/// Places the Director on macOS. The generic UpdateRunner places single-file exes and skips archives,
/// so the Director (shipped as cc-director-mac-arm64.zip containing "Director.app") needs this
/// dedicated step - the analog of MobilePackage's side-car-zip handling. It downloads + SHA-256 verifies the zip,
/// extracts the .app with ditto (preserving the bundle's symlinks + exec bits), swaps it into the
/// Applications folder (<see cref="InstallLayout.MacAppsDir"/>, normally <c>/Applications</c>), strips the
/// Gatekeeper quarantine, marks the launcher executable, and tells LaunchServices the bundle is there so
/// its icon appears without a sign-out. Mirrors the auto-updater's bundle swap so a fresh install and an
/// auto-update converge on the same on-disk result.
/// </summary>
public static class MacAppPlacer
{
    public const string DirectorAsset = "cc-director-mac-arm64.zip";
    private const string AppName = "Director.app";

    /// <summary>The pre-rename bundle name (issue #1821 alias). A fresh place removes any copy of this
    /// so a host never ends up with both "CC Director.app" and "Director.app".</summary>
    private const string LegacyAppName = "CC Director.app";

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

            // /Applications on an administrator's Mac, ~/Applications when this user cannot write it.
            var target = layout.PathFor(ComponentRegistry.Director);
            Directory.CreateDirectory(layout.MacAppsDir);
            if (!string.Equals(layout.MacAppsDir, MacAppsFolder.SystemAppsDir, StringComparison.Ordinal))
            {
                Log($"{MacAppsFolder.SystemAppsDir} cannot be written by this user, so {AppName} goes in "
                    + $"{layout.MacAppsDir} instead; its icon is in the Applications folder of this user's home.");
            }

            // Collapse the pile-up (issue #1821 rename, and the move out of ~/Applications): before placing
            // the one canonical bundle, remove every stale copy an older install or a manual one could have
            // left - the legacy "CC Director.app", Finder's auto-suffixed duplicates ("CC Director 2.app",
            // "Director 2.app"), and the copy in whichever of the two Applications folders is not the target.
            // This is what makes reinstalling upgrade-in-place instead of stacking a new icon beside the old
            // ones, and it is what moves an existing Mac's icon into /Applications rather than leaving one in
            // each folder. Deletion is identity-gated (MacBundlePurger): only bundles carrying the Director's
            // own CFBundleIdentifier are removed, so developer slot wrappers and unrelated apps that happen
            // to share the name survive.
            MacBundlePurger.Purge([MacAppsFolder.SystemAppsDir, layout.MacUserAppsDir], keep: target, Log);

            Log($"installing {AppName} to {layout.MacAppsDir}");
            // Build beside, then this is a fresh place: remove any existing app, then ditto in.
            ProcessRunner.Run("/bin/rm", $"-rf \"{target}\"");
            var (cpExit, cpOut) = ProcessRunner.Run("/usr/bin/ditto", $"\"{stagedApp}\" \"{target}\"");
            if (cpExit != 0) return new MacAppResult(false, $"installing {AppName} failed: {Trim(cpOut)}", null);

            // Post-place: de-quarantine + ensure the launcher binary is executable (mirrors the updater's swap).
            ProcessRunner.Run("/usr/bin/xattr", $"-dr com.apple.quarantine \"{target}\"");
            ProcessRunner.Run("/bin/chmod", $"+x \"{Path.Combine(target, "Contents", "MacOS", "cc-director")}\"");

            RegisterWithLaunchServices(target, Log);

            // Record the Director version for the updater.
            var im = InstalledManifest.Load(layout);
            im.Set(ComponentRegistry.Director.Id, asset.Version);
            im.Save(layout);

            Log($"Director {asset.Version} installed to {target}");
            return new MacAppResult(true, $"Director {asset.Version} installed to {target}.", asset.Version);
        }
        finally
        {
            try { if (zip is not null && File.Exists(zip)) File.Delete(zip); } catch { /* best-effort */ }
            try { if (stage is not null && Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// The LaunchServices database tool. Not on the PATH, and this absolute path is the documented one
    /// on every macOS release the Director supports.
    /// </summary>
    internal const string LsRegisterPath =
        "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";

    /// <summary>
    /// Tell LaunchServices about the bundle that was just placed, so the icon is there NOW - in the
    /// Finder, in the Applications view, in Spotlight - instead of whenever macOS next happens to notice
    /// the folder changed. A freshly placed bundle is otherwise unregistered until something rescans, and
    /// "I installed it and there is no icon" is indistinguishable from an install that failed.
    ///
    /// Best effort by design: the bundle IS installed by the time this runs, and a database that has not
    /// caught up yet is not a failed install. A failure is logged and nothing else.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void RegisterWithLaunchServices(string target, Action<string> log)
    {
        if (!File.Exists(LsRegisterPath))
        {
            log($"not registering {target} with LaunchServices: {LsRegisterPath} is not on this Mac");
            return;
        }

        var (exit, output) = ProcessRunner.Run(LsRegisterPath, $"-f \"{target}\"");
        log(exit == 0
            ? $"registered {target} with LaunchServices, so its icon appears in the Applications folder now"
            : $"could not register {target} with LaunchServices (exit {exit}): {Trim(output)}; the icon appears "
              + "once macOS rescans the folder");
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;
}
