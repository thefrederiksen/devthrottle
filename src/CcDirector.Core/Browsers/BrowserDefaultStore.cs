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
///
/// Two levels of default are kept: the application-wide default at <c>browser.default</c> and a
/// per-repository default at <c>browser.repoDefaults[&lt;repoKey&gt;]</c>. A plain "Open in Browser"
/// resolves in the order repository default -&gt; application-wide default -&gt; operating-system
/// default (see <see cref="Resolve"/>).
/// </summary>
public static class BrowserDefaultStore
{
    /// <summary>
    /// Returns the remembered application-wide default, or null if the user has never chosen a
    /// browser+profile. A null result legitimately means "use the OS default" - it is not an error.
    /// </summary>
    public static BrowserDefault? Load()
    {
        var root = CcDirectorConfigService.ReadRaw();
        var value = ReadDefaultObject(root["browser"]?["default"]);
        FileLog.Write(value is null
            ? "[BrowserDefaultStore] Load: no application-wide default set"
            : $"[BrowserDefaultStore] Load: exe={value.ExePath}, profile={value.ProfileFolder}");
        return value;
    }

    /// <summary>Persists <paramref name="value"/> as the remembered application-wide default in config.json.</summary>
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
    /// Forgets the application-wide default, so <see cref="Resolve"/> falls through to the
    /// operating-system default. This is how the user takes back a default they set: picking
    /// "System default browser" in the picker and asking to remember it means "stop using a
    /// remembered browser", which without this would be unsayable once a default existed.
    /// </summary>
    public static void Clear()
    {
        FileLog.Write("[BrowserDefaultStore] Clear: forgetting the application-wide default");
        var patch = new JsonObject
        {
            ["browser"] = new JsonObject { ["default"] = null }
        };
        CcDirectorConfigService.MergePatch(patch);
    }

    /// <summary>
    /// Forgets <paramref name="repoPath"/>'s default, so <see cref="Resolve"/> falls through to the
    /// application-wide default (and then to the operating-system default). Other repositories'
    /// defaults are untouched.
    /// </summary>
    public static void ClearForRepo(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("Repository path is required", nameof(repoPath));

        var key = NormalizeRepoKey(repoPath);
        FileLog.Write($"[BrowserDefaultStore] ClearForRepo: repo={key}");
        var patch = new JsonObject
        {
            ["browser"] = new JsonObject
            {
                ["repoDefaults"] = new JsonObject { [key] = null }
            }
        };
        CcDirectorConfigService.MergePatch(patch);
    }

    /// <summary>
    /// Returns the browser+profile remembered for <paramref name="repoPath"/>, or null when that
    /// repository has never set one. Null is not an error - the caller should then fall back to the
    /// application-wide default (see <see cref="Resolve"/>).
    /// </summary>
    public static BrowserDefault? LoadForRepo(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            FileLog.Write("[BrowserDefaultStore] LoadForRepo: blank repo path, no repository default");
            return null;
        }

        var key = NormalizeRepoKey(repoPath);
        var root = CcDirectorConfigService.ReadRaw();
        if (root["browser"]?["repoDefaults"] is not JsonObject repoDefaults)
        {
            FileLog.Write($"[BrowserDefaultStore] LoadForRepo: no repoDefaults section, repo={key}");
            return null;
        }

        var value = ReadDefaultObject(repoDefaults[key]);
        FileLog.Write(value is null
            ? $"[BrowserDefaultStore] LoadForRepo: no default for repo={key}"
            : $"[BrowserDefaultStore] LoadForRepo: repo={key}, exe={value.ExePath}, profile={value.ProfileFolder}");
        return value;
    }

    /// <summary>
    /// Persists <paramref name="value"/> as the remembered default for <paramref name="repoPath"/>.
    /// Uses <see cref="CcDirectorConfigService.MergePatch"/>, so the application-wide default and any
    /// other repositories' defaults are left untouched.
    /// </summary>
    public static void SaveForRepo(string repoPath, BrowserDefault value)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("Repository path is required", nameof(repoPath));
        if (value is null) throw new ArgumentNullException(nameof(value));

        var key = NormalizeRepoKey(repoPath);
        FileLog.Write($"[BrowserDefaultStore] SaveForRepo: repo={key}, exe={value.ExePath}, profile={value.ProfileFolder}");
        var patch = new JsonObject
        {
            ["browser"] = new JsonObject
            {
                ["repoDefaults"] = new JsonObject
                {
                    [key] = new JsonObject
                    {
                        ["exePath"] = value.ExePath,
                        ["profileFolder"] = value.ProfileFolder,
                    }
                }
            }
        };
        CcDirectorConfigService.MergePatch(patch);
    }

    /// <summary>
    /// Resolves the effective "Open in Browser" default for a session in <paramref name="repoPath"/>,
    /// applying the order: the repository's own default first, then the application-wide default. A
    /// null result means neither is set, so the caller should open the operating-system default
    /// browser. <paramref name="repoPath"/> may be null (a link with no owning repository), in which
    /// case only the application-wide default is consulted.
    /// </summary>
    public static BrowserDefault? Resolve(string? repoPath)
    {
        FileLog.Write($"[BrowserDefaultStore] Resolve: repo={repoPath ?? "(none)"}");

        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            var repoDefault = LoadForRepo(repoPath);
            if (repoDefault is not null)
            {
                FileLog.Write("[BrowserDefaultStore] Resolve: using the repository default");
                return repoDefault;
            }
        }

        var global = Load();
        FileLog.Write(global is null
            ? "[BrowserDefaultStore] Resolve: nothing remembered, caller uses the OS default browser"
            : "[BrowserDefaultStore] Resolve: using the application-wide default");
        return global;
    }

    /// <summary>
    /// Reads an <c>{exePath, profileFolder}</c> object into a <see cref="BrowserDefault"/>, or null
    /// when the node is absent or either field is missing/blank.
    /// </summary>
    private static BrowserDefault? ReadDefaultObject(JsonNode? node)
    {
        if (node is not JsonObject def)
            return null;

        var exePath = (string?)def["exePath"];
        var profileFolder = (string?)def["profileFolder"];
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(profileFolder))
            return null;

        return new BrowserDefault(exePath, profileFolder);
    }

    /// <summary>
    /// Normalizes a repository path into a stable config.json key: separators unified to backslash and
    /// trailing separators trimmed, then lowercased on Windows (where paths are case-insensitive) so
    /// the same repository always maps to the same stored entry regardless of how the path was typed.
    /// </summary>
    private static string NormalizeRepoKey(string repoPath)
    {
        var normalized = repoPath.Trim().Replace('/', '\\').TrimEnd('\\');
        return OperatingSystem.IsWindows() ? normalized.ToLowerInvariant() : normalized;
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

        // The remembered browser is about to be started: read the disk now rather than trust a
        // cached probe, so a browser removed since then is refused instead of launched.
        var match = BrowserLauncher.DetectBrowsers(forceProbe: true)
            .FirstOrDefault(b => string.Equals(b.ExePath, exePath, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new FileNotFoundException(
                $"The remembered browser is no longer installed at {exePath}.", exePath);

        return match;
    }
}
