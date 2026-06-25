using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Manages named-session definition files as individual JSON files in the named-sessions directory.
/// Each named session is stored as <c>{slug}.named-session.json</c>. Mirrors
/// <see cref="WorkspaceStore"/> so the on-disk story is consistent across the two features.
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
    /// Save a named-session definition. Overwrites if a file with the same slug exists, so a saved
    /// name is never duplicated on disk.
    /// </summary>
    public bool Save(NamedSessionDefinition session)
    {
        if (session is null)
            throw new ArgumentNullException(nameof(session));

        var slug = ToSlug(session.Name);
        FileLog.Write($"[NamedSessionStore] Save: name={session.Name}, slug={slug}, repo={session.RepoPath}, agent={session.AgentId}");

        try
        {
            EnsureDirectory();

            var filePath = GetFilePath(slug);
            var json = JsonSerializer.Serialize(session, JsonOptions);
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
    /// Load all named-session definitions, sorted by name. Skips corrupt files with a warning log.
    /// </summary>
    public List<NamedSessionDefinition> LoadAll()
    {
        FileLog.Write($"[NamedSessionStore] LoadAll: scanning {FolderPath}");

        if (!Directory.Exists(FolderPath))
        {
            FileLog.Write("[NamedSessionStore] LoadAll: folder does not exist, returning empty list");
            return new List<NamedSessionDefinition>();
        }

        var sessions = new List<NamedSessionDefinition>();
        var files = Directory.GetFiles(FolderPath, "*.named-session.json");

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var session = JsonSerializer.Deserialize<NamedSessionDefinition>(json, JsonOptions);
                if (session != null)
                    sessions.Add(session);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[NamedSessionStore] LoadAll: skipping corrupt file {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        sessions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        FileLog.Write($"[NamedSessionStore] LoadAll: loaded {sessions.Count} named sessions");
        return sessions;
    }

    /// <summary>Load a single named session by slug. Returns null if not found or corrupt.</summary>
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

    /// <summary>Delete a named session by slug. Returns true if the file was deleted.</summary>
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

    /// <summary>Check whether a named session with the given slug exists on disk.</summary>
    public bool Exists(string slug)
    {
        return File.Exists(GetFilePath(slug));
    }

    /// <summary>Convert a name to a filesystem-safe slug (lowercase, hyphens).</summary>
    public static string ToSlug(string name)
    {
        var slug = name.Trim().ToLowerInvariant();
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
