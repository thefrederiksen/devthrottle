using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Browsers;

/// <summary>
/// The Chromium browsers we can launch a specific profile in.
///
/// Chromium is the whole list, and that is a capability boundary rather than a preference: a browser
/// is drivable here only because it speaks the Chrome DevTools Protocol on a
/// <c>--remote-debugging-port</c>, which is how browser-harness attaches. Firefox (WebDriver BiDi) and
/// Safari (WebKit remote inspector) speak neither, so they cannot be added by listing them here - they
/// would need a second protocol backend.
/// </summary>
public enum BrowserKind
{
    Chrome,
    Edge,
    Brave,
    Opera
}

/// <summary>
/// An installed Chromium browser: how to launch it (<see cref="ExePath"/>) and where its
/// per-profile state lives (<see cref="UserDataDir"/>, which holds <c>Local State</c>).
/// </summary>
public sealed record BrowserInfo(
    BrowserKind Kind,
    string DisplayName,
    string ExePath,
    string UserDataDir);

/// <summary>
/// A single browser profile read from <c>Local State</c> <c>profile.info_cache</c>.
/// <see cref="FolderName"/> is the value passed to <c>--profile-directory</c> (e.g. "Profile 1"),
/// NOT the human-facing <see cref="DisplayName"/>.
/// </summary>
public sealed record BrowserProfile(
    string FolderName,
    string DisplayName,
    string? Account);

/// <summary>
/// Detects installed Chromium browsers, enumerates their profiles from each browser's
/// <c>Local State</c> file, and launches a URL or local file in a chosen browser+profile via the
/// documented <c>--profile-directory</c> command-line flag.
///
/// No-fallback rule (CLAUDE.md / CodingStyle): when a caller asks for a specific browser+profile
/// and the exe or the profile folder is missing, the launch THROWS with a message naming the
/// missing target. It never silently opens the system default - that would hide the problem.
/// Opening the system default is a separate, explicit call (<see cref="OpenSystemDefault"/>).
/// </summary>
public static class BrowserLauncher
{
    /// <summary>
    /// Standard install locations for each supported browser on THIS operating system, in priority
    /// order. The first exe that exists wins. User-Data directory is the standard per-user Chromium
    /// location for that platform.
    ///
    /// The desktop Director ships on Windows and macOS, and the two lay Chromium out completely
    /// differently - a Windows-shaped table asked on a Mac finds nothing and the product reports
    /// "neither Chrome nor Edge is installed" on a machine holding both. So the table is chosen by
    /// platform, never guessed at: each branch names only the locations that platform actually uses.
    /// </summary>
    private static IReadOnlyList<(BrowserKind Kind, string DisplayName, string[] ExeCandidates, string UserDataDir)> Candidates()
    {
        if (OperatingSystem.IsWindows()) return WindowsCandidates();
        if (OperatingSystem.IsMacOS()) return MacCandidates();

        // The desktop app is published for Windows and macOS only. Saying so is the honest answer;
        // inventing Linux paths we have never run against would be a guess dressed as support.
        FileLog.Write($"[BrowserLauncher] Candidates: no browser table for this platform ({Environment.OSVersion.Platform})");
        return Array.Empty<(BrowserKind, string, string[], string)>();
    }

    /// <summary>Exposed internally so the table's shape can be asserted from any host OS - a Windows
    /// test run is the only place the macOS table would otherwise never be looked at.</summary>
    internal static IReadOnlyList<(BrowserKind Kind, string DisplayName, string[] ExeCandidates, string UserDataDir)> WindowsCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return new (BrowserKind, string, string[], string)[]
        {
            (BrowserKind.Chrome, "Google Chrome",
                new[]
                {
                    Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
                },
                Path.Combine(localAppData, "Google", "Chrome", "User Data")),
            (BrowserKind.Edge, "Microsoft Edge",
                new[]
                {
                    Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                },
                Path.Combine(localAppData, "Microsoft", "Edge", "User Data")),
            (BrowserKind.Brave, "Brave",
                new[]
                {
                    Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                    Path.Combine(programFilesX86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                    Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                },
                Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data")),
            // Opera breaks the pattern twice, so neither line is a copy of the ones above. Its
            // installer defaults to a PER-USER install under Local\Programs (the all-users location is
            // the option, not the default), which is why that candidate is first. And its profile lives
            // in ROAMING AppData with no "User Data" level - a Chrome-shaped guess here would look
            // right and find nothing.
            (BrowserKind.Opera, "Opera",
                new[]
                {
                    Path.Combine(localAppData, "Programs", "Opera", "opera.exe"),
                    Path.Combine(programFiles, "Opera", "opera.exe"),
                    Path.Combine(programFilesX86, "Opera", "opera.exe"),
                },
                Path.Combine(roamingAppData, "Opera Software", "Opera Stable")),
        };
    }

    /// <summary>
    /// macOS: a browser is an application bundle, and the launchable binary is the one inside
    /// <c>Contents/MacOS</c> - the <c>.app</c> folder itself is not an executable, so it is the inner
    /// binary we must record and hand to Process.Start with the automation flags. System-wide
    /// <c>/Applications</c> comes first because that is where both browsers install by default; the
    /// per-user <c>~/Applications</c> is checked after, for a copy dragged there instead.
    ///
    /// The user-data directory is NOT the Windows shape. macOS Chromium keeps its profiles directly
    /// under Application Support with no "User Data" level, and Edge uses one flat "Microsoft Edge"
    /// folder rather than a Microsoft/Edge pair. <c>Local State</c> - the file
    /// <see cref="GetProfiles"/> reads - sits at the root of these directories exactly as it does on
    /// Windows.
    /// </summary>
    internal static IReadOnlyList<(BrowserKind Kind, string DisplayName, string[] ExeCandidates, string UserDataDir)> MacCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appSupport = Path.Combine(home, "Library", "Application Support");

        static string[] BundlePaths(string home, string bundleName, string binaryName) =>
            new[]
            {
                Path.Combine("/Applications", bundleName, "Contents", "MacOS", binaryName),
                Path.Combine(home, "Applications", bundleName, "Contents", "MacOS", binaryName),
            };

        return new (BrowserKind, string, string[], string)[]
        {
            (BrowserKind.Chrome, "Google Chrome",
                BundlePaths(home, "Google Chrome.app", "Google Chrome"),
                Path.Combine(appSupport, "Google", "Chrome")),
            (BrowserKind.Edge, "Microsoft Edge",
                BundlePaths(home, "Microsoft Edge.app", "Microsoft Edge"),
                Path.Combine(appSupport, "Microsoft Edge")),
            (BrowserKind.Brave, "Brave",
                BundlePaths(home, "Brave Browser.app", "Brave Browser"),
                Path.Combine(appSupport, "BraveSoftware", "Brave-Browser")),
            // Opera names its support folder by bundle id, not by product name, so this one is not
            // guessable from the pattern the other three follow.
            (BrowserKind.Opera, "Opera",
                BundlePaths(home, "Opera.app", "Opera"),
                Path.Combine(appSupport, "com.operasoftware.Opera")),
        };
    }

    /// <summary>
    /// Returns the Chromium browsers found at their standard install locations, in a stable order
    /// (Chrome, Edge, Brave, Opera - most-used first, which is also the order the pickers show and the
    /// first-run wizard prefers). A browser is "installed" when one of its candidate exe paths exists.
    /// The answer is cached for <see cref="DetectCacheLifetime"/>: the browsers rail asks for it every
    /// 30 seconds and installations rarely change while the Director runs. A path where a person has
    /// just installed or removed a browser and asked to look again calls <see cref="DetectBrowsers(bool)"/>
    /// with <c>forceProbe: true</c>.
    /// </summary>
    public static IReadOnlyList<BrowserInfo> DetectBrowsers() => DetectBrowsers(forceProbe: false);

    /// <summary>
    /// As <see cref="DetectBrowsers()"/>; with <paramref name="forceProbe"/> true the disk is read now and
    /// the cache replaced, so a browser installed or removed a moment ago is seen immediately.
    /// </summary>
    public static IReadOnlyList<BrowserInfo> DetectBrowsers(bool forceProbe)
    {
        if (!forceProbe && TryReadFreshCache(out var cached))
            return cached;

        lock (DetectLock)
        {
            if (!forceProbe && TryReadFreshCache(out cached))
                return cached;

            FileLog.Write($"[BrowserLauncher] DetectBrowsers: probing (forced={forceProbe})");

            var found = new List<BrowserInfo>();
            foreach (var (kind, displayName, exeCandidates, userDataDir) in Candidates())
            {
                var exe = exeCandidates.FirstOrDefault(ExeExists);
                if (exe is not null)
                    found.Add(new BrowserInfo(kind, displayName, exe, userDataDir));
            }

            FileLog.Write($"[BrowserLauncher] DetectBrowsers: found={found.Count}");
            IReadOnlyList<BrowserInfo> result = found.AsReadOnly();
            // The timestamp is published before the list, so a reader that sees the new list sees its time.
            Volatile.Write(ref _detectedAtTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref _detectedBrowsers, result);
            return result;
        }
    }

    private static bool TryReadFreshCache([NotNullWhen(true)] out IReadOnlyList<BrowserInfo>? cached)
    {
        cached = Volatile.Read(ref _detectedBrowsers);
        if (cached is null)
            return false;
        var detectedAt = Volatile.Read(ref _detectedAtTimestamp);
        if (Stopwatch.GetElapsedTime(detectedAt) >= DetectCacheLifetime)
        {
            cached = null;
            return false;
        }
        return true;
    }

    /// <summary>How long a detection answer is reused before the disk is read again.</summary>
    public static readonly TimeSpan DetectCacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The exists check the probe uses; tests swap it to simulate an install or a removal.</summary>
    internal static Func<string, bool> ExeExists = File.Exists;

    private static readonly object DetectLock = new();
    private static IReadOnlyList<BrowserInfo>? _detectedBrowsers;
    private static long _detectedAtTimestamp;

    /// <summary>
    /// Reads <paramref name="browser"/>'s <c>Local State</c> and returns its profiles, sorted with
    /// account-bearing profiles first (then by display name). Re-read on every call so newly added
    /// profiles appear without restarting the app.
    /// </summary>
    public static IReadOnlyList<BrowserProfile> GetProfiles(BrowserInfo browser)
    {
        if (browser is null) throw new ArgumentNullException(nameof(browser));

        var localStatePath = Path.Combine(browser.UserDataDir, "Local State");
        FileLog.Write($"[BrowserLauncher] GetProfiles: browser={browser.DisplayName}, localState={localStatePath}");

        if (!File.Exists(localStatePath))
        {
            FileLog.Write($"[BrowserLauncher] GetProfiles: no Local State for {browser.DisplayName}");
            return Array.Empty<BrowserProfile>();
        }

        var json = File.ReadAllText(localStatePath);
        var profiles = ParseProfiles(json);
        FileLog.Write($"[BrowserLauncher] GetProfiles: browser={browser.DisplayName}, profiles={profiles.Count}");
        return profiles;
    }

    /// <summary>
    /// Parses a Chromium <c>Local State</c> document and returns the profiles from
    /// <c>profile.info_cache</c>, sorted account-bearing-first then by display name. Exposed
    /// internally so the parsing/sorting can be unit-tested without a real browser install.
    /// </summary>
    internal static IReadOnlyList<BrowserProfile> ParseProfiles(string localStateJson)
    {
        if (string.IsNullOrWhiteSpace(localStateJson))
            throw new ArgumentException("Local State JSON is empty", nameof(localStateJson));

        var root = JsonNode.Parse(localStateJson)
            ?? throw new InvalidOperationException("Local State parsed to null");

        var infoCache = root["profile"]?["info_cache"] as JsonObject;
        if (infoCache is null)
            return Array.Empty<BrowserProfile>();

        var profiles = new List<BrowserProfile>();
        foreach (var kvp in infoCache)
        {
            var folderName = kvp.Key;
            var entry = kvp.Value as JsonObject;
            if (entry is null)
                continue;

            var displayName = (string?)entry["name"];
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = folderName;

            var userName = (string?)entry["user_name"];
            var gaiaName = (string?)entry["gaia_name"];
            var account = !string.IsNullOrWhiteSpace(userName) ? userName
                : !string.IsNullOrWhiteSpace(gaiaName) ? gaiaName
                : null;

            profiles.Add(new BrowserProfile(folderName, displayName, account));
        }

        return profiles
            .OrderBy(p => p.Account is null ? 1 : 0)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Opens <paramref name="target"/> (a URL or a local file path) in the OS default browser via
    /// the shell. This is the current behavior, kept as an explicit choice the user can pick.
    /// </summary>
    public static void OpenSystemDefault(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Target is required", nameof(target));

        FileLog.Write($"[BrowserLauncher] OpenSystemDefault: target={target}");
        var startInfo = new ProcessStartInfo(target) { UseShellExecute = true };
        Process.Start(startInfo);
    }

    /// <summary>
    /// Opens <paramref name="target"/> (a URL or a local file path) in <paramref name="browser"/>
    /// signed in to the profile folder <paramref name="profileFolder"/> using the
    /// <c>--profile-directory</c> flag. Throws (no silent fallback) if the browser exe or the
    /// profile folder no longer exists.
    /// </summary>
    /// <returns>The started process.</returns>
    /// <exception cref="FileNotFoundException">The browser exe is missing.</exception>
    /// <exception cref="DirectoryNotFoundException">The profile folder is missing.</exception>
    public static Process OpenWithProfile(string target, BrowserInfo browser, string profileFolder)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Target is required", nameof(target));
        if (browser is null) throw new ArgumentNullException(nameof(browser));
        if (string.IsNullOrWhiteSpace(profileFolder))
            throw new ArgumentException("Profile folder is required", nameof(profileFolder));

        FileLog.Write($"[BrowserLauncher] OpenWithProfile: browser={browser.DisplayName}, profile={profileFolder}, target={target}");

        if (!File.Exists(browser.ExePath))
            throw new FileNotFoundException(
                $"{browser.DisplayName} is no longer installed at {browser.ExePath}.", browser.ExePath);

        var profilePath = Path.Combine(browser.UserDataDir, profileFolder);
        if (!Directory.Exists(profilePath))
            throw new DirectoryNotFoundException(
                $"{browser.DisplayName} profile folder \"{profileFolder}\" was not found at {profilePath}.");

        var startInfo = new ProcessStartInfo
        {
            FileName = browser.ExePath,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add($"--profile-directory={profileFolder}");
        startInfo.ArgumentList.Add(target);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Failed to start {browser.DisplayName} ({browser.ExePath}).");

        FileLog.Write($"[BrowserLauncher] OpenWithProfile: started pid={process.Id}");
        return process;
    }
}
