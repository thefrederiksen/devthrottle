using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Persists the list of console scripts the installed Python tools bundle is expected to provide. It is
/// written by <see cref="PythonToolsInstaller"/> only after a healthy install, and read by
/// <see cref="ToolUpdater"/> so the auto-updater can probe venv health offline - it needs to know which
/// scripts to look for without re-downloading and extracting the bundle just to read tools-manifest.json.
/// Stored next to installed.json under the setup-state dir.
/// </summary>
public static class PythonToolsState
{
    /// <summary>The sidecar file recording the bundle's expected console-script names.</summary>
    public static string ScriptsPath(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.SetupStateDir, "python-tools-scripts.json");
    }

    /// <summary>Record the bundle's console-script names (overwrites any prior list).</summary>
    public static void SaveScripts(InstallLayout layout, IReadOnlyList<string> scripts)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(scripts);
        Directory.CreateDirectory(layout.SetupStateDir);
        var json = JsonSerializer.Serialize(scripts, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ScriptsPath(layout), json);
    }

    /// <summary>
    /// The recorded console-script names, or an empty list when none were recorded yet (e.g. a machine that
    /// installed before this sidecar existed). An empty list means "health unknown" to the caller.
    /// </summary>
    public static IReadOnlyList<string> LoadScripts(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var path = ScriptsPath(layout);
        if (!File.Exists(path)) return Array.Empty<string>();
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return list is null ? Array.Empty<string>() : list;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[PythonToolsState] load failed ({path}): {ex.Message}; treating as empty");
            return Array.Empty<string>();
        }
    }
}
