using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// The reason a named-session preset cannot be launched (issue #508). <see cref="None"/> means the
/// preset is launchable; the other values are the two orphan cases the Manage dialog labels.
/// </summary>
public enum NamedSessionOrphanReason
{
    /// <summary>The preset is launchable - its repository folder exists and its agent is present.</summary>
    None,

    /// <summary>The preset's repository folder no longer exists on disk.</summary>
    RepositoryMissing,

    /// <summary>The preset's agent id no longer matches any configured agent entry.</summary>
    AgentRemoved,
}

/// <summary>
/// A named-session preset paired with whether it is launchable or an orphan (issue #508). The
/// Manage dialog renders the orphan ones greyed and launch-disabled; the New Session start flow
/// only ever shows the launchable ones.
/// </summary>
public sealed class NamedSessionStatus
{
    public NamedSessionStatus(NamedSessionDefinition preset, NamedSessionOrphanReason reason)
    {
        Preset = preset ?? throw new ArgumentNullException(nameof(preset));
        OrphanReason = reason;
    }

    /// <summary>The underlying saved preset.</summary>
    public NamedSessionDefinition Preset { get; }

    /// <summary>Why the preset cannot launch, or <see cref="NamedSessionOrphanReason.None"/>.</summary>
    public NamedSessionOrphanReason OrphanReason { get; }

    /// <summary>True when the preset is launchable (repository present and agent present).</summary>
    public bool IsLaunchable => OrphanReason == NamedSessionOrphanReason.None;
}

/// <summary>
/// Manages named-session preset files as individual JSON files in the named-sessions directory
/// (issue #508). Each preset is stored as {slug}.named-session.json. Mirrors
/// <see cref="WorkspaceStore"/> (create / list / delete / slug / exists) and adds orphan resolution
/// so the UI can grey out presets whose repository folder or agent id no longer exists.
/// </summary>
public class NamedSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FolderPath { get; }

    public NamedSessionStore(string? folderPath = null)
    {
        FolderPath = folderPath ?? CcStorage.NamedSessions();
    }

    /// <summary>
    /// Save a named-session preset. Overwrites if a file with the same slug exists.
    /// </summary>
    public bool Save(NamedSessionDefinition preset)
    {
        if (preset is null) throw new ArgumentNullException(nameof(preset));

        var slug = ToSlug(preset.Name);
        FileLog.Write($"[NamedSessionStore] Save: name={preset.Name}, slug={slug}, agentId={preset.AgentId}");

        try
        {
            EnsureDirectory();

            var filePath = GetFilePath(slug);
            var json = JsonSerializer.Serialize(preset, JsonOptions);
            File.WriteAllText(filePath, json);

            FileLog.Write($"[NamedSessionStore] Save: written to {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NamedSessionStore] Save FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Load all named-session presets, sorted by name. Skips corrupt files with a warning log.
    /// </summary>
    public List<NamedSessionDefinition> LoadAll()
    {
        FileLog.Write($"[NamedSessionStore] LoadAll: scanning {FolderPath}");

        if (!Directory.Exists(FolderPath))
        {
            FileLog.Write("[NamedSessionStore] LoadAll: folder does not exist, returning empty list");
            return new List<NamedSessionDefinition>();
        }

        var presets = new List<NamedSessionDefinition>();
        var files = Directory.GetFiles(FolderPath, "*.named-session.json");

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var preset = JsonSerializer.Deserialize<NamedSessionDefinition>(json, JsonOptions);
                if (preset != null)
                    presets.Add(preset);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[NamedSessionStore] LoadAll: skipping corrupt file {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        presets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        FileLog.Write($"[NamedSessionStore] LoadAll: loaded {presets.Count} presets");
        return presets;
    }

    /// <summary>
    /// Load all presets and classify each as launchable or orphaned (issue #508), given the set of
    /// configured agent ids. A preset is an orphan when its repository folder is missing or its
    /// agent id is not in <paramref name="knownAgentIds"/>. Repository is checked first, so a preset
    /// that is broken both ways reports the repository as the reason.
    /// </summary>
    public List<NamedSessionStatus> LoadAllWithStatus(IEnumerable<string> knownAgentIds)
    {
        if (knownAgentIds is null) throw new ArgumentNullException(nameof(knownAgentIds));

        var agentIds = new HashSet<string>(knownAgentIds, StringComparer.Ordinal);
        var statuses = LoadAll().Select(p => new NamedSessionStatus(p, ResolveOrphanReason(p, agentIds))).ToList();

        FileLog.Write($"[NamedSessionStore] LoadAllWithStatus: {statuses.Count} presets, {statuses.Count(s => !s.IsLaunchable)} orphaned");
        return statuses;
    }

    /// <summary>
    /// Classify one preset as launchable or orphaned. Repository-missing is checked before
    /// agent-removed so a doubly-broken preset reports the repository reason.
    /// </summary>
    public static NamedSessionOrphanReason ResolveOrphanReason(NamedSessionDefinition preset, ISet<string> knownAgentIds)
    {
        if (preset is null) throw new ArgumentNullException(nameof(preset));
        if (knownAgentIds is null) throw new ArgumentNullException(nameof(knownAgentIds));

        if (string.IsNullOrWhiteSpace(preset.RepositoryPath) || !Directory.Exists(preset.RepositoryPath))
            return NamedSessionOrphanReason.RepositoryMissing;

        if (!knownAgentIds.Contains(preset.AgentId))
            return NamedSessionOrphanReason.AgentRemoved;

        return NamedSessionOrphanReason.None;
    }

    /// <summary>
    /// Load a single preset by slug. Returns null if not found or corrupt.
    /// </summary>
    public NamedSessionDefinition? Load(string slug)
    {
        FileLog.Write($"[NamedSessionStore] Load: slug={slug}");

        var filePath = GetFilePath(slug);
        if (!File.Exists(filePath))
        {
            FileLog.Write($"[NamedSessionStore] Load: file not found for slug={slug}");
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<NamedSessionDefinition>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NamedSessionStore] Load FAILED for {slug}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Delete a preset by slug. Returns true if the file was deleted.
    /// </summary>
    public bool Delete(string slug)
    {
        FileLog.Write($"[NamedSessionStore] Delete: slug={slug}");

        var filePath = GetFilePath(slug);
        if (!File.Exists(filePath))
        {
            FileLog.Write($"[NamedSessionStore] Delete: file not found for slug={slug}");
            return false;
        }

        try
        {
            File.Delete(filePath);
            FileLog.Write($"[NamedSessionStore] Delete: deleted {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NamedSessionStore] Delete FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>Check if a preset with the given slug exists.</summary>
    public bool Exists(string slug)
    {
        return File.Exists(GetFilePath(slug));
    }

    /// <summary>
    /// Convert a preset name to a filesystem-safe slug (lowercase, hyphens). Same rules as
    /// <see cref="WorkspaceStore.ToSlug"/> for consistency, falling back to "named-session" when the
    /// name has no slug-able characters.
    /// </summary>
    public static string ToSlug(string name)
    {
        var slug = (name ?? string.Empty).Trim().ToLowerInvariant();
        slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
        slug = Regex.Replace(slug, @"[\s]+", "-");
        slug = Regex.Replace(slug, @"-+", "-");
        slug = slug.Trim('-');

        if (string.IsNullOrEmpty(slug))
            slug = "named-session";

        return slug;
    }

    private string GetFilePath(string slug) => Path.Combine(FolderPath, $"{slug}.named-session.json");

    private void EnsureDirectory()
    {
        if (!Directory.Exists(FolderPath))
        {
            Directory.CreateDirectory(FolderPath);
            FileLog.Write($"[NamedSessionStore] Created directory {FolderPath}");
        }
    }
}
