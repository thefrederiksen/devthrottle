using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Browsers;

/// <summary>
/// The remembered "Open in Browser" default: which browser exe and profile folder a plain click
/// on the terminal's "Open in Browser" item should reopen. Stored in <c>config.json</c> under
/// <c>browser.default</c> so it survives an app restart.
/// </summary>
public sealed record BrowserDefault(string ExePath, string ProfileFolder);

/// <summary>
/// Reads and writes the remembered <see cref="BrowserDefault"/> in <c>config.json</c> (via
/// <see cref="CcDirectorConfigService"/>, so other config sections are preserved), and resolves a
/// stored exe path back to a live <see cref="BrowserInfo"/>.
/// </summary>
public static class BrowserDefaultStore
{
    /// <summary>
    /// Returns the remembered default, or null if the user has never chosen a browser+profile.
    /// A null result legitimately means "use the OS default" - it is not an error.
    /// </summary>
    public static BrowserDefault? Load()
    {
        var root = CcDirectorConfigService.ReadRaw();
        if (root["browser"]?["default"] is not JsonObject def)
            return null;

        var exePath = (string?)def["exePath"];
        var profileFolder = (string?)def["profileFolder"];
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(profileFolder))
            return null;

        return new BrowserDefault(exePath, profileFolder);
    }

    /// <summary>Persists <paramref name="value"/> as the remembered default in config.json.</summary>
    public static void Save(BrowserDefault value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));

        FileLog.Write($"[BrowserDefaultStore] Save: exe={value.ExePath}, profile={value.ProfileFolder}");
        var patch = new JsonObject
        {
            ["browser"] = new JsonObject
            {
                ["default"] = new JsonObject
                {
                    ["exePath"] = value.ExePath,
                    ["profileFolder"] = value.ProfileFolder,
                }
            }
        };
        CcDirectorConfigService.MergePatch(patch);
    }

    /// <summary>
    /// Resolves a stored exe path to a currently-installed <see cref="BrowserInfo"/>. Throws (no
    /// silent fallback) if that browser is no longer installed, so the caller can surface a clear
    /// error rather than opening some other browser.
    /// </summary>
    /// <exception cref="FileNotFoundException">No installed browser matches the stored exe path.</exception>
    public static BrowserInfo ResolveBrowser(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("Exe path is required", nameof(exePath));

        var match = BrowserLauncher.DetectBrowsers()
            .FirstOrDefault(b => string.Equals(b.ExePath, exePath, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new FileNotFoundException(
                $"The remembered browser is no longer installed at {exePath}.", exePath);

        return match;
    }
}
