using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Browsers;

/// <summary>The Chromium browsers we can launch a specific profile in.</summary>
public enum BrowserKind
{
    Chrome,
    Edge
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
    /// Standard install locations for each supported browser, in priority order. The first exe
    /// that exists wins. User-Data directory is the standard per-user Chromium location.
    /// </summary>
    private static IReadOnlyList<(BrowserKind Kind, string DisplayName, string[] ExeCandidates, string UserDataDir)> Candidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
        };
    }

    /// <summary>
    /// Returns the Chromium browsers found at their standard install locations, in a stable order
    /// (Chrome, then Edge). A browser is "installed" when one of its candidate exe paths exists.
    /// </summary>
    public static IReadOnlyList<BrowserInfo> DetectBrowsers()
    {
        FileLog.Write("[BrowserLauncher] DetectBrowsers");

        var found = new List<BrowserInfo>();
        foreach (var (kind, displayName, exeCandidates, userDataDir) in Candidates())
        {
            var exe = exeCandidates.FirstOrDefault(File.Exists);
            if (exe is not null)
                found.Add(new BrowserInfo(kind, displayName, exe, userDataDir));
        }

        FileLog.Write($"[BrowserLauncher] DetectBrowsers: found={found.Count}");
        return found;
    }

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
