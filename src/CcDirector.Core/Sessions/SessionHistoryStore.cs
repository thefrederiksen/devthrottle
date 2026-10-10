using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Manages persistent session history as individual JSON files in a folder.
/// Each CC Director workspace gets one file: {id}.json.
/// Survives app restarts, providing the data for the Resume Session list.
/// </summary>
public class SessionHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FolderPath { get; }

    public SessionHistoryStore(string? folderPath = null)
    {
        FolderPath = folderPath ?? Path.Combine(
            CcStorage.ToolConfig("director"),
            "sessions");
    }

    /// <summary>
    /// Save or update a history entry. Writes {id}.json to the folder.
    /// </summary>
    public bool Save(SessionHistoryEntry entry)
    {
        FileLog.Write($"[SessionHistoryStore] Save: id={entry.Id}, name={entry.CustomName}");

        try
        {
            EnsureDirectory();

            var filePath = GetFilePath(entry.Id);
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            File.WriteAllText(filePath, json);

            FileLog.Write($"[SessionHistoryStore] Save: written to {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistoryStore] Save FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Load a single entry by ID. Returns null if not found or corrupt.
    /// </summary>
    public SessionHistoryEntry? Load(Guid id)
    {
        var filePath = GetFilePath(id);
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<SessionHistoryEntry>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistoryStore] Load FAILED for {id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Load all entries from the folder, sorted by LastUsedAt descending (most recent first).
    /// Skips corrupt files with a warning log.
    /// </summary>
    public List<SessionHistoryEntry> LoadAll()
    {
        FileLog.Write($"[SessionHistoryStore] LoadAll: scanning {FolderPath}");

        if (!Directory.Exists(FolderPath))
        {
            FileLog.Write("[SessionHistoryStore] LoadAll: folder does not exist, returning empty list");
            return new List<SessionHistoryEntry>();
        }

        var entries = new List<SessionHistoryEntry>();
        var files = Directory.GetFiles(FolderPath, "*.json");

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var entry = JsonSerializer.Deserialize<SessionHistoryEntry>(json, JsonOptions);
                if (entry != null)
                    entries.Add(entry);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionHistoryStore] LoadAll FAILED: skipping corrupt file {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        entries.Sort((a, b) => b.LastUsedAt.CompareTo(a.LastUsedAt));

        FileLog.Write($"[SessionHistoryStore] LoadAll: loaded {entries.Count} entries");
        return entries;
    }

    /// <summary>
    /// Find an entry by ClaudeSessionId. Returns the most recently used match, or null.
    /// </summary>
    public SessionHistoryEntry? FindByClaudeSessionId(string claudeSessionId)
    {
        if (string.IsNullOrEmpty(claudeSessionId))
            return null;

        if (!Directory.Exists(FolderPath))
            return null;

        var files = Directory.GetFiles(FolderPath, "*.json");
        SessionHistoryEntry? best = null;

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var entry = JsonSerializer.Deserialize<SessionHistoryEntry>(json, JsonOptions);
                if (entry != null
                    && string.Equals(entry.ClaudeSessionId, claudeSessionId, StringComparison.Ordinal)
                    && (best == null || entry.LastUsedAt > best.LastUsedAt))
                {
                    best = entry;
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionHistoryStore] FindByClaudeSessionId FAILED: skipping corrupt file {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return best;
    }

    /// <summary>
    /// Delete a history entry by ID. Returns true if the file was deleted.
    /// </summary>
    public bool Delete(Guid id)
    {
        FileLog.Write($"[SessionHistoryStore] Delete: id={id}");

        var filePath = GetFilePath(id);
        if (!File.Exists(filePath))
        {
            FileLog.Write($"[SessionHistoryStore] Delete: file not found for {id}");
            return false;
        }

        try
        {
            File.Delete(filePath);
            FileLog.Write($"[SessionHistoryStore] Delete: deleted {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistoryStore] Delete FAILED: {ex.Message}");
            return false;
        }
    }

    private string GetFilePath(Guid id) => Path.Combine(FolderPath, $"{id:N}.json");

    private void EnsureDirectory()
    {
        if (!Directory.Exists(FolderPath))
        {
            Directory.CreateDirectory(FolderPath);
            FileLog.Write($"[SessionHistoryStore] Created directory {FolderPath}");
        }
    }
}
