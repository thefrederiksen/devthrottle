using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Instances;

/// <summary>
/// The cross-instance registry of named Director instances. One JSON file at the SHARED
/// machine root - <c>{SharedRoot}\config\director\named-instances.json</c> - so that every
/// instance process and the launcher can enumerate all instances without starting one.
///
/// This is deliberately at the shared root (via <see cref="InstanceContext.SharedRoot"/>),
/// NOT under a per-instance data home, and NOT via <see cref="Storage.CcStorage"/> (whose
/// base is redirected to the per-instance home once <c>CC_DIRECTOR_ROOT</c> is set).
///
/// Not to be confused with the pre-existing runtime registration directory
/// <c>config\director\instances\{directorId}.json</c> (Director↔Gateway liveness), which is
/// a different concept at a different path.
/// </summary>
public static class NamedInstanceRegistry
{
    private const int SchemaVersion = 1;

    private static readonly object WriteLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>The registry file path at the shared machine root.</summary>
    public static string FilePath =>
        Path.Combine(InstanceContext.SharedRoot, "config", "director", "named-instances.json");

    /// <summary>
    /// All instances, guaranteed to include the default (seeded from the hostname on first
    /// read). Ordered default-first, then by creation time.
    /// </summary>
    public static IReadOnlyList<NamedInstance> List()
    {
        lock (WriteLock)
        {
            var instances = LoadRaw();
            if (!instances.Any(i => i.IsDefault))
            {
                instances.Insert(0, BuildDefault());
                SaveRaw(instances);
            }
            return instances
                .OrderByDescending(i => i.IsDefault)
                .ThenBy(i => i.CreatedAt, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>The instance with the given slug, or null if unknown.</summary>
    public static NamedInstance? Get(string slug)
    {
        var normalized = InstanceContext.Normalize(slug);
        return List().FirstOrDefault(i =>
            string.Equals(i.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ensure the default instance exists (seeded with the given hostname as its display
    /// name). Safe to call before FileLog.Start. Idempotent.
    /// </summary>
    public static void EnsureDefault(string hostname)
    {
        lock (WriteLock)
        {
            var instances = LoadRaw();
            if (instances.Any(i => i.IsDefault))
                return;
            instances.Insert(0, BuildDefault(hostname));
            SaveRaw(instances);
        }
    }

    /// <summary>
    /// Create a new named instance: derive a unique slug from the display name, mint an id,
    /// persist to the registry, and scaffold the instance's data home with a
    /// <c>config\config.json</c> carrying the gateway block. Returns the instance.
    /// </summary>
    public static NamedInstance Create(string displayName, string gatewayUrl, string gatewayToken)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.", nameof(displayName));

        FileLog.Write($"[NamedInstanceRegistry] Create: displayName=\"{displayName}\", gatewayUrl=\"{gatewayUrl}\"");
        lock (WriteLock)
        {
            var instances = LoadRaw();
            if (!instances.Any(i => i.IsDefault))
                instances.Insert(0, BuildDefault());

            var slug = UniqueSlug(Slugify(displayName), instances);
            var instance = new NamedInstance
            {
                Id = Guid.NewGuid().ToString(),
                Name = slug,
                DisplayName = displayName.Trim(),
                GatewayUrl = gatewayUrl?.Trim() ?? "",
                CreatedAt = DateTime.UtcNow.ToString("o"),
            };
            instances.Add(instance);
            SaveRaw(instances);

            ScaffoldInstanceHome(instance, gatewayToken?.Trim() ?? "");
            FileLog.Write($"[NamedInstanceRegistry] Created instance id={instance.Id}, slug={slug}");
            return instance;
        }
    }

    /// <summary>
    /// Rename an instance's DISPLAY NAME only. The slug, id, home and gateway are
    /// untouched - nothing that identifies the instance moves.
    /// </summary>
    public static NamedInstance Rename(string slug, string newDisplayName)
    {
        if (string.IsNullOrWhiteSpace(newDisplayName))
            throw new ArgumentException("Display name is required.", nameof(newDisplayName));

        var normalized = InstanceContext.Normalize(slug);
        FileLog.Write($"[NamedInstanceRegistry] Rename: slug={normalized}, newDisplayName=\"{newDisplayName}\"");
        lock (WriteLock)
        {
            var instances = LoadRaw();
            var target = instances.FirstOrDefault(i =>
                string.Equals(i.Name, normalized, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No instance with slug '{normalized}'.");
            target.DisplayName = newDisplayName.Trim();
            SaveRaw(instances);
            FileLog.Write($"[NamedInstanceRegistry] Renamed slug={normalized} -> \"{target.DisplayName}\"");
            return target;
        }
    }

    /// <summary>Preview the slug that would be derived from a display name (no uniqueness suffix).</summary>
    public static string PreviewSlug(string displayName) => Slugify(displayName ?? "");

    // -- internals --

    private static NamedInstance BuildDefault(string? hostname = null)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = InstanceContext.DefaultSlug,
            DisplayName = string.IsNullOrWhiteSpace(hostname) ? Environment.MachineName : hostname.Trim(),
            GatewayUrl = "",
            CreatedAt = DateTime.UtcNow.ToString("o"),
        };

    private static List<NamedInstance> LoadRaw()
    {
        var path = FilePath;
        if (!File.Exists(path))
            return new List<NamedInstance>();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new List<NamedInstance>();

        // No-fallback rule: a malformed registry THROWS rather than being silently reset.
        var doc = JsonSerializer.Deserialize<RegistryFile>(text, JsonOptions)
            ?? throw new InvalidOperationException($"named-instances.json parsed to null: {path}");
        return doc.Instances ?? new List<NamedInstance>();
    }

    private static void SaveRaw(List<NamedInstance> instances)
    {
        var path = FilePath;
        var dir = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"named-instances.json has no directory: {path}");
        Directory.CreateDirectory(dir);

        var doc = new RegistryFile { SchemaVersion = SchemaVersion, Instances = instances };
        var json = JsonSerializer.Serialize(doc, JsonOptions);
        var temp = Path.Combine(dir, $".named-instances.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Write the instance's own <c>config\config.json</c> with the gateway block, directly
    /// (not via CcDirectorConfigService, whose target is the CURRENT process's home).
    /// </summary>
    private static void ScaffoldInstanceHome(NamedInstance instance, string gatewayToken)
    {
        var home = Path.Combine(InstanceContext.SharedRoot, "instances", instance.Name);
        var configDir = Path.Combine(home, "config");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "config.json");
        if (File.Exists(configPath))
            return; // never clobber an existing instance config

        var config = new Dictionary<string, object?>
        {
            ["gateway"] = new Dictionary<string, object?>
            {
                ["url"] = instance.GatewayUrl,
                ["token"] = gatewayToken,
            },
        };
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var temp = Path.Combine(configDir, $".config.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, json);
        File.Move(temp, configPath, overwrite: true);
    }

    private static string UniqueSlug(string baseSlug, List<NamedInstance> instances)
    {
        var existing = instances.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Never let a user-created instance take the reserved default slug.
        if (string.Equals(baseSlug, InstanceContext.DefaultSlug, StringComparison.OrdinalIgnoreCase))
            baseSlug = baseSlug + "-1";
        if (!existing.Contains(baseSlug))
            return baseSlug;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseSlug}-{n}";
            if (!existing.Contains(candidate))
                return candidate;
        }
    }

    private static string Slugify(string displayName)
    {
        var sb = new StringBuilder();
        foreach (var ch in displayName.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_' or '.') sb.Append('-');
        }
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? "instance" : slug;
    }

    private sealed class RegistryFile
    {
        public int SchemaVersion { get; set; }
        public List<NamedInstance>? Instances { get; set; }
    }
}
