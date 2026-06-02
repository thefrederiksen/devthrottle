using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Records the version actually placed on disk for each component (component id -> version), so the
/// update planner has a trustworthy installed version even for assets that carry no readable file
/// version stamp (e.g. PyInstaller tool exes). Written by the install/update apply path; read by
/// <see cref="InstalledStateReader"/>. Persisted at <see cref="InstallLayout.InstalledManifestPath"/>.
/// </summary>
public sealed class InstalledManifest
{
    private readonly Dictionary<string, string> _versions;

    private InstalledManifest(Dictionary<string, string> versions) => _versions = versions;

    /// <summary>An empty manifest (no components recorded).</summary>
    public static InstalledManifest Empty() => new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Load the manifest for a layout; an absent or unreadable file yields an empty manifest.</summary>
    public static InstalledManifest Load(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var path = layout.InstalledManifestPath;
        if (!File.Exists(path)) return Empty();
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return map is null ? Empty() : new InstalledManifest(new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[InstalledManifest] load failed ({path}): {ex.Message}; treating as empty");
            return Empty();
        }
    }

    /// <summary>The recorded version for a component id, or null if not recorded.</summary>
    public string? Get(string componentId) =>
        _versions.TryGetValue(componentId, out var v) ? v : null;

    /// <summary>Record (or overwrite) the version placed for a component.</summary>
    public void Set(string componentId, string version)
    {
        if (string.IsNullOrWhiteSpace(componentId)) throw new ArgumentException("componentId required", nameof(componentId));
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("version required", nameof(version));
        _versions[componentId] = version;
    }

    /// <summary>Forget a component (used on uninstall).</summary>
    public bool Remove(string componentId) => _versions.Remove(componentId);

    /// <summary>The recorded entries (component id -> version).</summary>
    public IReadOnlyDictionary<string, string> Entries => _versions;

    /// <summary>Persist the manifest, creating the setup-state dir if needed.</summary>
    public void Save(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Directory.CreateDirectory(layout.SetupStateDir);
        var json = JsonSerializer.Serialize(_versions, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(layout.InstalledManifestPath, json);
    }
}
