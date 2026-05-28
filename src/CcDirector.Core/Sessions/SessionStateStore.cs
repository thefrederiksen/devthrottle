using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

public class PersistedSession
{
    public Guid Id { get; set; }
    public string RepoPath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string? ClaudeArgs { get; set; }
    public string? CustomName { get; set; }
    public string? CustomColor { get; set; }
    public string? PendingPromptText { get; set; }
    public int EmbeddedProcessId { get; set; }
    public long ConsoleHwnd { get; set; }
    public string? ClaudeSessionId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActivityState ActivityState { get; set; }

    /// <summary>Backend type used by this session (defaults to ConPty for backward compatibility).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SessionBackendType BackendType { get; set; } = SessionBackendType.ConPty;

    /// <summary>Which agent CLI this session was running. Defaults to ClaudeCode so
    /// sessions persisted before this field existed deserialize correctly.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentKind AgentKind { get; set; } = AgentKind.ClaudeCode;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Order in the session list, used to restore UI order after restart.</summary>
    public int SortOrder { get; set; }

    /// <summary>The first prompt text from the Claude session, used to verify session identity on restore.</summary>
    public string? ExpectedFirstPrompt { get; set; }

    /// <summary>Links this session to a SessionHistoryEntry for persistent workspace tracking.</summary>
    public Guid? HistoryEntryId { get; set; }

    /// <summary>Raw terminal output captured during Claude Code startup.</summary>
    public string? RawStartupText { get; set; }

    /// <summary>Queued prompts for this session. Null = no queued prompts (backward compatible).</summary>
    public List<PersistedPromptQueueItem>? QueuedPrompts { get; set; }

    /// <summary>Name of the last selected tab (e.g. "Terminal", "Agent", "SourceControl"). Null = default (Terminal).</summary>
    public string? SelectedTabName { get; set; }

    /// <summary>
    /// Whether the Wingman experience is enabled for this session (auto-explain briefing on
    /// turn-end, Voice/Wingman tabs visible, Yellow "Wingman is reading" state). Default is
    /// true so sessions persisted before this field existed restore with Wingman ON, matching
    /// the new-session default.
    /// </summary>
    public bool WingmanEnabled { get; set; } = true;
}

public class PersistedPromptQueueItem
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Result of loading persisted sessions from disk.</summary>
public class LoadSessionsResult
{
    public List<PersistedSession> Sessions { get; init; } = new();
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>True if sessions.json existed but could not be read.</summary>
    public bool FileExistedButFailed { get; init; }
}

/// <summary>Result of restoring persisted sessions, including load errors and restore failures.</summary>
public class RestoreSessionsResult
{
    public List<PersistedSession> Sessions { get; init; } = new();

    /// <summary>True if sessions.json was loaded successfully.</summary>
    public bool LoadSuccess { get; init; }

    /// <summary>Error message if sessions.json could not be loaded.</summary>
    public string? LoadErrorMessage { get; init; }

    /// <summary>True if sessions.json existed but could not be read (corrupted, locked, etc).</summary>
    public bool FileExistedButFailed { get; init; }
}

public class SessionStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FilePath { get; }
    public string BackupFilePath => FilePath + ".bak";

    public SessionStateStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            CcStorage.ToolConfig("director"),
            "sessions.json");
        FileLog.Write($"[SessionStateStore] Initialized: FilePath={FilePath}");
    }

    /// <summary>
    /// Save sessions to disk. Returns true on success, false on failure.
    /// </summary>
    public bool Save(IEnumerable<PersistedSession> sessions)
    {
        FileLog.Write($"[SessionStateStore] Save: saving sessions to {FilePath}");

        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(dir))
                throw new InvalidOperationException($"Cannot determine directory from path: {FilePath}");

            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                FileLog.Write($"[SessionStateStore] Save: created directory {dir}");
            }

            var sessionList = sessions.ToList();
            var json = JsonSerializer.Serialize(sessionList, JsonOptions);
            File.WriteAllText(FilePath, json);

            FileLog.Write($"[SessionStateStore] Save: saved {sessionList.Count} session(s)");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionStateStore] Save FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Load sessions from disk. Returns a result containing sessions and any error information.
    /// </summary>
    public LoadSessionsResult Load()
    {
        FileLog.Write($"[SessionStateStore] Load: loading from {FilePath}");

        if (!File.Exists(FilePath))
        {
            FileLog.Write("[SessionStateStore] Load: file does not exist, returning empty list");
            return new LoadSessionsResult
            {
                Sessions = new List<PersistedSession>(),
                Success = true,
                FileExistedButFailed = false
            };
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var sessions = JsonSerializer.Deserialize<List<PersistedSession>>(json, JsonOptions)
                ?? new List<PersistedSession>();

            FileLog.Write($"[SessionStateStore] Load: loaded {sessions.Count} session(s)");
            return new LoadSessionsResult
            {
                Sessions = sessions,
                Success = true,
                FileExistedButFailed = false
            };
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[SessionStateStore] Load FAILED (JSON parse error): {ex.Message}");
            return new LoadSessionsResult
            {
                Sessions = new List<PersistedSession>(),
                Success = false,
                ErrorMessage = $"sessions.json is corrupted: {ex.Message}",
                FileExistedButFailed = true
            };
        }
        catch (IOException ex)
        {
            FileLog.Write($"[SessionStateStore] Load FAILED (IO error): {ex.Message}");
            return new LoadSessionsResult
            {
                Sessions = new List<PersistedSession>(),
                Success = false,
                ErrorMessage = $"Cannot read sessions.json: {ex.Message}",
                FileExistedButFailed = true
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionStateStore] Load FAILED: {ex.GetType().Name}: {ex.Message}");
            return new LoadSessionsResult
            {
                Sessions = new List<PersistedSession>(),
                Success = false,
                ErrorMessage = $"Failed to load sessions: {ex.Message}",
                FileExistedButFailed = true
            };
        }
    }

    /// <summary>
    /// Create a backup of sessions.json before clearing.
    /// </summary>
    public void Backup()
    {
        if (!File.Exists(FilePath))
        {
            FileLog.Write("[SessionStateStore] Backup: nothing to backup, file does not exist");
            return;
        }

        try
        {
            File.Copy(FilePath, BackupFilePath, overwrite: true);
            FileLog.Write($"[SessionStateStore] Backup: created backup at {BackupFilePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionStateStore] Backup FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear the sessions file. Creates a backup first.
    /// </summary>
    public void Clear()
    {
        FileLog.Write("[SessionStateStore] Clear: clearing sessions file");

        // Always backup before clearing
        Backup();

        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
                FileLog.Write("[SessionStateStore] Clear: deleted sessions.json");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionStateStore] Clear FAILED: {ex.Message}");
        }
    }
}
