using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Diagnostics;

/// <summary>
/// Gathers the "what am I running / what's installed" diagnostics shown in the About boxes across
/// every surface (Director desktop dialog, Cockpit page, Gateway tray window) so each one reports
/// the same facts the same way. Pure reads - no side effects.
/// </summary>
public static class AboutInfo
{
    /// <summary>The product name shown everywhere.</summary>
    public const string ProductName = "CC Director";

    /// <summary>Version display form, e.g. "v0.6.15 (1a2b3c4)".</summary>
    public static string Version => AppVersion.Display;

    /// <summary>Full informational version as stamped, e.g. "0.6.15+sha".</summary>
    public static string VersionFull => AppVersion.Full;

    /// <summary>The per-user install root (<c>%LOCALAPPDATA%\cc-director</c>).</summary>
    public static string InstallRoot => CcStorage.Root();

    /// <summary>Build date of the running exe (its file write time), or null when it can't be read.</summary>
    public static DateTime? BuildDate()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : File.GetLastWriteTime(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Component id -> installed version, read from the setup manifest at
    /// <c>%LOCALAPPDATA%\cc-director\config\setup\installed.json</c>. Empty when the file is absent
    /// (e.g. running from a dev build) or unreadable. Read directly (no dependency on the installer
    /// engine) so every UI surface can call it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> InstalledComponents()
    {
        var path = Path.Combine(CcStorage.Root(), "config", "setup", "installed.json");
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, string>();
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return map ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// The common label/value rows every About surface shows. Surface-specific rows (this Director's
    /// Control API URL, the Cockpit front-door URL, gateway ports) are appended by each surface.
    /// </summary>
    public static List<(string Label, string Value)> SharedRows()
    {
        var rows = new List<(string, string)>
        {
            ("Product", ProductName),
            ("Version", Version),
        };

        if (BuildDate() is { } built)
            rows.Add(("Build date", built.ToString("yyyy-MM-dd HH:mm:ss")));

        rows.Add(("Machine", Environment.MachineName));
        rows.Add(("User", Environment.UserName));
        rows.Add(("Install root", InstallRoot));

        var gw = GatewayConfig.Load();
        rows.Add(("Gateway", gw.IsEnabled ? gw.Url : "(standalone - no gateway configured)"));

        var installed = InstalledComponents();
        if (installed.Count > 0)
        {
            foreach (var kv in installed.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                rows.Add(($"Installed: {kv.Key}", kv.Value));
        }
        else
        {
            rows.Add(("Installed components", "(no installed.json - running from a dev build?)"));
        }

        return rows;
    }
}
