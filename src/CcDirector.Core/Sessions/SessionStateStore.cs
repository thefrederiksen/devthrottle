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

    /// <summary>Which characters of <see cref="PendingPromptText"/> were dictated (ruling R20), as start and
    /// length pairs, so a dictation left in the box survives a restart as dictation. Null or empty when none.</summary>
    public List<PersistedSpokenSpan>? PendingPromptSpokenSpans { get; set; }

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

    /// <summary>Group identity (issue #225), null for solo sessions. Persisted so a group
    /// survives a restart intact - members restore adjacent and still tied.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Role within the group (issue #225), null for solo sessions.</summary>
    public string? GroupRole { get; set; }

    /// <summary>The group's display name (issue #225), null for solo sessions.</summary>
    public string? GroupName { get; set; }

    /// <summary>The controlling session's id (issue #815) when this session is a controlled
    /// sub-agent; null for a normal session. Persisted so the controlled relationship survives a
    /// restart - it is set only at birth and never changes.</summary>
    public Guid? ControllerSessionId { get; set; }

    /// <summary>The sticky EXPLICIT session role (automatic session roles), or null for none. Persisted so
    /// an Architect (or any explicitly-set role) survives a Director restart. One of the SessionRoles
    /// values.</summary>
    public string? ExplicitRole { get; set; }

    /// <summary>WHO asked for this session (issue #982) - one of the SessionOriginKinds values.
    /// Persisted because it is a birth fact that can never be recovered once lost: nothing about a
    /// running session says who asked for it. Sessions persisted before this field existed restore as
    /// null and are stamped "unknown", which is the truth about them.</summary>
    public string? OriginKind { get; set; }

    /// <summary>WHERE the create call came from (issue #982) - one of the SessionOriginSurfaces values.
    /// Persisted on the same terms as <see cref="OriginKind"/>.</summary>
    public string? OriginSurface { get; set; }

    /// <summary>The session that asked for this one (issue #982), or null. Persisted so the lineage tree
    /// survives a Director restart - an edge lost here is an operation that silently becomes N unrelated
    /// sessions.</summary>
    public Guid? ParentSessionId { get; set; }

    /// <summary>True when the name was auto-composed at birth (automatic session roles, chunk 3). Persisted
    /// so an explicit human/self rename is never re-auto-named after a restart.</summary>
    public bool IsAutoNamed { get; set; }

    /// <summary>The Mission this session is attached to, or null for none. Persisted so the attachment (the
    /// pod binding) survives a Director restart. Null for sessions persisted before this field existed.</summary>
    public Guid? MissionId { get; set; }

    /// <summary>The attached Mission's cached display name, or null for none. Persisted alongside
    /// <see cref="MissionId"/> so the name renders after a restart without re-resolving the Mission store.</summary>
    public string? MissionName { get; set; }

    /// <summary>The workflow run this session is seated on (Workflows mission, phase 5b), or null.
    /// Persisted with its cached workflow id + pinned version so the seat - and the seated preamble
    /// paragraph - survives a Director restart. Null for sessions persisted before the field existed.</summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>The seated run's cached workflow id, or null.</summary>
    public string? WorkflowId { get; set; }

    /// <summary>The seated run's pinned workflow version, or null.</summary>
    public int? WorkflowVersion { get; set; }

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
    /// false so sessions persisted before this field existed restore with Wingman OFF, matching
    /// the new-session default.
    /// </summary>
    public bool WingmanEnabled { get; set; } = false;

    /// <summary>
    /// The session's three-digit number (issue #820), or null when it had none. Persisted so a
    /// session keeps its number across a Director restart / session restore; on restore the
    /// SessionManager reserves this exact number when it is still free, otherwise allocates a fresh
    /// one. Null for sessions persisted before this field existed (they are backfilled on restore).
    /// </summary>
    public int? Number { get; set; }

    /// <summary>
    /// The pooled worktree this session is working in, or null for every session in a repository
    /// whose pooled-worktree setting is off - which is the default and almost every session.
    ///
    /// PERSISTED BECAUSE THE LEASE CANNOT BE RECOVERED. cc-worktrees will not hand out a second
    /// lease for a slot that is in use, and its listing does not report the lease of one, so a
    /// Director that restarts without this has no way to give the slot back: it stays in use under
    /// a holder that no longer exists, and a Director that restarts a few times fills its own pool.
    /// Nothing in the slot is ever lost by that - the tool's landed-work check still stands between
    /// it and any reset - but the only ways out are that tool's own <c>lease --reclaim-held</c> and
    /// <c>release</c>, which are a person's decisions, not a restart's.
    ///
    /// Null for sessions persisted before this field existed, which is the truth about them.
    /// </summary>
    public PersistedPooledWorktree? PooledWorktree { get; set; }
}

/// <summary>
/// A persisted session's pooled worktree: the four values <see cref="Git.PooledWorktree"/> holds.
///
/// A plain record of its own rather than the Core type, for the same reason every other persisted
/// shape here is its own: this is a FILE FORMAT, and a serialization shape that follows a production
/// type changes whenever that type does - here, silently, to a file that older and newer Directors
/// both read.
/// </summary>
public class PersistedPooledWorktree
{
    /// <summary>The repository whose pool the slot belongs to.</summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>The slot's name in its pool (<c>wt01</c>).</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>The slot's directory on disk, which is where the session runs.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The lease <c>cc-worktrees return</c> requires. The value this whole record exists for.</summary>
    public string Lease { get; set; } = string.Empty;
}

/// <summary>One dictated range of a persisted pending prompt (ruling R20).</summary>
public class PersistedSpokenSpan
{
    public int Start { get; set; }
    public int Length { get; set; }
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
