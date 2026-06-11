using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Persisted collapsed/expanded state of the desktop session sidebar.
/// Persisted in config.json as "sidebar_collapsed": true | false.
/// </summary>
public static class SidebarConfig
{
    private static bool _collapsed;
    private static bool _loaded;

    /// <summary>Whether the session sidebar is collapsed to the slim strip.</summary>
    public static bool Collapsed
    {
        get
        {
            if (!_loaded) Load();
            return _collapsed;
        }
    }

    /// <summary>
    /// Drop the cached state so the next <see cref="Collapsed"/> read re-loads from
    /// config.json. Test-only: lets tests exercise the Load path despite the static cache.
    /// </summary>
    internal static void ResetForTests() => _loaded = false;

    /// <summary>Set the sidebar collapsed state and persist to config.json.</summary>
    public static void SetCollapsed(bool collapsed)
    {
        FileLog.Write($"[SidebarConfig] SetCollapsed: {collapsed}");
        _collapsed = collapsed;
        _loaded = true;
        Save();
    }

    private static void Load()
    {
        _loaded = true;
        _collapsed = false;

        var configPath = CcStorage.ConfigJson();
        if (!File.Exists(configPath)) return;

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sidebar_collapsed", out var prop)
                && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _collapsed = prop.GetBoolean();
            }
            FileLog.Write($"[SidebarConfig] Load: sidebar_collapsed={_collapsed}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SidebarConfig] Load FAILED: {ex.Message}");
        }
    }

    private static void Save()
    {
        var configPath = CcStorage.ConfigJson();
        try
        {
            JsonNode? root;
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                root = JsonNode.Parse(json);
            }
            else
            {
                var configDir = Path.GetDirectoryName(configPath);
                if (configDir is null)
                    throw new InvalidOperationException($"Cannot determine directory for config path: {configPath}");
                Directory.CreateDirectory(configDir);
                root = new JsonObject();
            }

            if (root is JsonObject obj)
            {
                obj["sidebar_collapsed"] = _collapsed;
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(configPath, root.ToJsonString(options));
                FileLog.Write($"[SidebarConfig] Save: wrote sidebar_collapsed={_collapsed} to {configPath}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SidebarConfig] Save FAILED: {ex.Message}");
        }
    }
}
