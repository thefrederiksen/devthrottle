using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Manages all active sessions. Creates, tracks, and kills sessions.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, Guid> _claudeSessionMap = new();
    private readonly AgentOptions _options;
    private readonly Action<string>? _log;

    public AgentOptions Options => _options;

    /// <summary>
    /// Fired immediately after a session is added to the manager's internal dictionary,
    /// for EVERY session - whether created via the Avalonia UI, the web Control API,
    /// or restored from persistence at startup. Handlers must be idempotent: the
    /// Avalonia UI already skips sessions it has already wrapped, and any other
    /// subscriber should do the same.
    /// </summary>
    public event Action<Session>? OnSessionCreated;

    public SessionManager(AgentOptions options, Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
    }

    /// <summary>Invoke OnSessionCreated. Public so external endpoint mappers (web Control API)
    /// can announce sessions they created without going through CreateSession overloads.</summary>
    public void RaiseSessionCreated(Session session)
    {
        try { OnSessionCreated?.Invoke(session); }
        catch (Exception ex) { _log?.Invoke($"OnSessionCreated handler threw: {ex.Message}"); }
    }

    /// <summary>Create a new ConPty session that spawns claude.exe in the given repo path.</summary>
    public Session CreateSession(string repoPath, string? claudeArgs = null)
    {
        return CreateSession(repoPath, claudeArgs, SessionBackendType.ConPty, resumeSessionId: null);
    }

    /// <summary>Create a new session with the specified backend type.</summary>
    public Session CreateSession(string repoPath, string? claudeArgs, SessionBackendType backendType)
    {
        return CreateSession(repoPath, claudeArgs, backendType, resumeSessionId: null);
    }

    /// <summary>Create a session, optionally resuming a previous Claude session.
    /// This overload preserves the original Claude-Code-only behavior and is the entry
    /// point for legacy callers. New callers should prefer the IAgent overload.</summary>
    public Session CreateSession(string repoPath, string? claudeArgs, SessionBackendType backendType, string? resumeSessionId)
    {
        return CreateSession(repoPath, new ClaudeAgent(_options), claudeArgs, backendType, resumeSessionId);
    }

    /// <summary>
    /// Create a session driven by a specific <see cref="IAgent"/> (Claude Code, Pi, etc).
    /// Agents that don't support preassigned session IDs (Pi) skip Claude's session-linking
    /// step; Director still tracks the session via its own GUID and backend lifecycle.
    /// </summary>
    public Session CreateSession(string repoPath, IAgent agent, string? userArgs, SessionBackendType backendType, string? resumeSessionId)
    {
        if (agent is null)
            throw new ArgumentNullException(nameof(agent));
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();

        var studioMode = backendType == SessionBackendType.Studio;
        var launchSpec = agent.BuildLaunchSpec(userArgs, resumeSessionId, studioMode);
        var args = launchSpec.Arguments;
        var preassignedClaudeSessionId = launchSpec.PreassignedSessionId;

        if (!string.IsNullOrEmpty(resumeSessionId))
            _log?.Invoke($"Resuming {agent.Kind} session {resumeSessionId}");
        else if (!string.IsNullOrEmpty(preassignedClaudeSessionId))
            _log?.Invoke($"New {agent.Kind} session with preassigned id {preassignedClaudeSessionId}");
        else
            _log?.Invoke($"New {agent.Kind} session (no preassigned id)");

        if (studioMode)
            _log?.Invoke($"Studio mode args: {args}");

        ISessionBackend backend = backendType switch
        {
            // ConPty on Windows, UnixPty on macOS/Linux
            SessionBackendType.ConPty when RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                => new ConPtyBackend(_options.DefaultBufferSizeBytes),
            SessionBackendType.ConPty
                => new UnixPtyBackend(_options.DefaultBufferSizeBytes),
            SessionBackendType.Pipe => new PipeBackend(_options.DefaultBufferSizeBytes),
            SessionBackendType.Studio => new StudioBackend(),
            SessionBackendType.Embedded when RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                => throw new InvalidOperationException(
                    "Use CreateEmbeddedSession for embedded mode - requires WPF backend."),
            SessionBackendType.Embedded
                => throw new PlatformNotSupportedException(
                    "Embedded mode is only supported on Windows."),
            _ => throw new ArgumentOutOfRangeException(nameof(backendType))
        };

        var session = new Session(id, repoPath, repoPath, userArgs, backend, backendType)
        {
            AgentKind = agent.Kind
        };

        try
        {
            // Inject CC_SESSION_ID so skills (e.g. /handover) can look up the session name
            var envVars = new Dictionary<string, string>
            {
                ["CC_SESSION_ID"] = id.ToString()
            };

            // Get initial terminal dimensions (default 120x30)
            backend.Start(agent.ExecutablePath, args, repoPath, 120, 30, envVars);
            session.MarkRunning();

            _sessions[id] = session;
            RaiseSessionCreated(session);

            // Pre-populate ClaudeSessionId when we already know it:
            //   * resumeSessionId is always known (caller supplied it via --resume)
            //   * preassignedClaudeSessionId is set only by agents that opt into
            //     pre-assignment via SupportsPreassignedSessionId (currently none -
            //     claude 2.1.143+ broke that path, see ClaudeAgent docs).
            var knownClaudeId = resumeSessionId
                ?? (agent.SupportsPreassignedSessionId ? preassignedClaudeSessionId : null);
            if (!string.IsNullOrEmpty(knownClaudeId))
            {
                session.ClaudeSessionId = knownClaudeId;
                _claudeSessionMap[knownClaudeId] = id;
                session.MarkAsPreVerified();
            }

            var resumeInfo = !string.IsNullOrEmpty(resumeSessionId) ? $", Resume={resumeSessionId[..8]}..." : "";
            var sessionIdInfo = !string.IsNullOrEmpty(preassignedClaudeSessionId) ? $", ClaudeSessionId={preassignedClaudeSessionId[..8]}..." : "";
            _log?.Invoke($"Session {id} created for repo {repoPath} (Agent={agent.Kind}, PID {backend.ProcessId}, Backend={backendType}{resumeInfo}{sessionIdInfo}).");

            return session;
        }
        catch (Exception ex)
        {
            session.MarkFailed();
            _log?.Invoke($"Failed to create session for {repoPath}: {ex.Message}");
            session.Dispose();
            throw;
        }
    }

    /// <summary>Create a new pipe mode session for the given repo path.
    /// No process is spawned until the user sends a prompt.</summary>
    public Session CreatePipeModeSession(string repoPath, string? claudeArgs = null)
    {
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();
        string args = claudeArgs ?? _options.DefaultClaudeArgs ?? string.Empty;

        var backend = new PipeBackend(_options.DefaultBufferSizeBytes);
        backend.Start(_options.ClaudePath, args, repoPath, 120, 30);

        var session = new Session(id, repoPath, repoPath, claudeArgs, backend, SessionBackendType.Pipe);
        session.MarkRunning();

        _sessions[id] = session;
        RaiseSessionCreated(session);
        _log?.Invoke($"Pipe mode session {id} created for repo {repoPath}.");

        return session;
    }

    /// <summary>
    /// Create an embedded mode session. The WPF layer must provide the backend
    /// since EmbeddedBackend depends on WPF components.
    /// </summary>
    public Session CreateEmbeddedSession(string repoPath, string? claudeArgs, ISessionBackend embeddedBackend)
    {
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();

        var session = new Session(id, repoPath, repoPath, claudeArgs, embeddedBackend, SessionBackendType.Embedded);
        session.MarkRunning();

        _sessions[id] = session;
        RaiseSessionCreated(session);
        _log?.Invoke($"Embedded session {id} created for repo {repoPath}.");

        return session;
    }

    /// <summary>Get a session by ID.</summary>
    public Session? GetSession(Guid id) => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>List all sessions.</summary>
    public IReadOnlyCollection<Session> ListSessions() => _sessions.Values.ToList().AsReadOnly();

    /// <summary>Kill a session by ID.</summary>
    public async Task KillSessionAsync(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var session))
            throw new KeyNotFoundException($"Session {id} not found.");

        await session.KillAsync(_options.GracefulShutdownTimeoutSeconds * 1000);
    }

    /// <summary>Return PIDs of all tracked sessions that have live processes.</summary>
    public HashSet<int> GetTrackedProcessIds()
        => _sessions.Values
            .Where(s => s.ProcessId > 0)
            .Select(s => s.ProcessId)
            .ToHashSet();

    /// <summary>Scan for orphaned claude.exe processes on startup.</summary>
    public void ScanForOrphans()
    {
        try
        {
            var claudeProcesses = Process.GetProcessesByName("claude");
            if (claudeProcesses.Length > 0)
            {
                _log?.Invoke(
                    $"Found {claudeProcesses.Length} orphaned claude.exe process(es). " +
                    "Cannot re-attach ConPTY. Consider killing them manually if they are from a previous run.");

                foreach (var proc in claudeProcesses)
                {
                    _log?.Invoke($"  Orphan PID {proc.Id}, started {proc.StartTime}");
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Error scanning for orphaned claude.exe processes: {ex.Message}");
        }
    }

    /// <summary>Remove a session from tracking (dispose and clean up).</summary>
    public void RemoveSession(Guid id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            // Remove any Claude session mapping
            if (session.ClaudeSessionId != null)
                _claudeSessionMap.TryRemove(session.ClaudeSessionId, out _);

            session.Dispose();
            _log?.Invoke($"Session {id} removed.");
        }
    }

    /// <summary>Kill all sessions (used during graceful shutdown).</summary>
    public async Task KillAllSessionsAsync()
    {
        var tasks = _sessions.Values
            .Where(s => s.Status is SessionStatus.Running or SessionStatus.Starting)
            .Select(s => s.KillAsync(_options.GracefulShutdownTimeoutSeconds * 1000))
            .ToArray();

        if (tasks.Length > 0)
        {
            _log?.Invoke($"Killing {tasks.Length} active session(s)...");
            await Task.WhenAll(tasks);
        }
    }

    /// <summary>Fires when a Claude session is registered to a Director session.</summary>
    public event Action<Session, string>? OnClaudeSessionRegistered;

    /// <summary>Fires when a session's CustomName is changed via <see cref="RenameSession"/>.
    /// Subscribers (e.g. the Avalonia main window) should update their view models
    /// and persist state. Args: (session, newName).</summary>
    public event Action<Session, string?>? OnSessionRenamed;

    /// <summary>
    /// Set the user-defined display name for an existing session. Fires
    /// <see cref="OnSessionRenamed"/> so the host (Avalonia main window) can refresh
    /// the sidebar and persist state. Returns false if the session is not found.
    /// </summary>
    public bool RenameSession(Guid sessionId, string? newName)
    {
        FileLog.Write($"[SessionManager] RenameSession: id={sessionId}, name=\"{newName}\"");
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            FileLog.Write($"[SessionManager] RenameSession: session not found");
            return false;
        }
        var normalized = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        session.CustomName = normalized;
        try { OnSessionRenamed?.Invoke(session, normalized); }
        catch (Exception ex) { _log?.Invoke($"OnSessionRenamed handler threw: {ex.Message}"); }
        return true;
    }

    /// <summary>Register a Claude session_id -> Director session mapping.</summary>
    public void RegisterClaudeSession(string claudeSessionId, Guid directorSessionId)
    {
        // Check if this Claude session ID is already assigned to a different Director session
        if (_claudeSessionMap.TryGetValue(claudeSessionId, out var existingId) && existingId != directorSessionId)
        {
            _log?.Invoke($"WARNING: Claude session {claudeSessionId} is already registered to Director session {existingId}, ignoring registration for {directorSessionId}.");
            return;
        }

        _claudeSessionMap[claudeSessionId] = directorSessionId;
        if (_sessions.TryGetValue(directorSessionId, out var session))
        {
            session.ClaudeSessionId = claudeSessionId;
            // Refresh Claude metadata now that we have the session ID
            session.RefreshClaudeMetadata();
            // Verify the session file exists (may fail early if .jsonl not written yet)
            session.VerifyClaudeSession();
            // Mark terminal verification as matched — receiving a session ID from hooks
            // or marker search IS the verification. Don't gate on file check since
            // the .jsonl may not have content yet when hooks fire early.
            session.MarkAsPreVerified();
            // Notify listeners
            OnClaudeSessionRegistered?.Invoke(session, claudeSessionId);
        }
        _log?.Invoke($"Registered Claude session {claudeSessionId} -> Director session {directorSessionId}.");
    }

    /// <summary>Manually re-link a Director session to a different Claude session ID.</summary>
    public void RelinkClaudeSession(Guid directorSessionId, string newClaudeSessionId)
    {
        if (!_sessions.TryGetValue(directorSessionId, out var session))
        {
            _log?.Invoke($"RelinkClaudeSession: Director session {directorSessionId} not found.");
            return;
        }

        // Remove old mapping if present
        if (session.ClaudeSessionId != null)
        {
            _claudeSessionMap.TryRemove(session.ClaudeSessionId, out _);
            _log?.Invoke($"RelinkClaudeSession: Removed old mapping {session.ClaudeSessionId}.");
        }

        // Set new mapping
        session.ClaudeSessionId = newClaudeSessionId;
        _claudeSessionMap[newClaudeSessionId] = directorSessionId;

        // Refresh metadata and verify
        session.RefreshClaudeMetadata();
        session.VerifyClaudeSession();

        // If file verification passed, also mark terminal verification as matched
        if (session.VerificationStatus == Claude.SessionVerificationStatus.Verified)
        {
            session.MarkAsPreVerified();
        }

        // Notify listeners
        OnClaudeSessionRegistered?.Invoke(session, newClaudeSessionId);
        _log?.Invoke($"RelinkClaudeSession: Linked {directorSessionId} to Claude session {newClaudeSessionId}.");
    }

    /// <summary>Look up a Director session by its Claude session_id.</summary>
    public Session? GetSessionByClaudeId(string claudeSessionId)
    {
        if (_claudeSessionMap.TryGetValue(claudeSessionId, out var id))
            return GetSession(id);
        return null;
    }

    /// <summary>
    /// Find the Director session most likely to be the one whose Claude session id
    /// was just rotated by /clear or /compact. Used by EventRouter to relink the
    /// session to the NEW Claude session id when SessionStart(source=clear|compact)
    /// arrives with no existing mapping.
    ///
    /// Heuristic:
    ///   - RepoPath matches <paramref name="cwd"/> (case-insensitive, trim trailing slashes)
    ///   - Session is alive (Status is Running/Starting -- not Exited/Failed)
    ///   - Has a non-null ClaudeSessionId (so we know it WAS linked at some point)
    ///   - Prefer ActivityState == Exited (it just received SessionEnd)
    ///   - Tie-break: most recently created (proxy for "most recently touched")
    /// Returns null if no candidate matches.
    /// </summary>
    public Session? FindOrphanForReclaim(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return null;
        var normalizedCwd = cwd.TrimEnd('\\', '/');

        Session? best = null;
        foreach (var s in _sessions.Values)
        {
            if (s.Status is SessionStatus.Exited or SessionStatus.Failed) continue;
            if (s.ClaudeSessionId is null) continue;
            var repo = s.RepoPath?.TrimEnd('\\', '/');
            if (!string.Equals(repo, normalizedCwd, StringComparison.OrdinalIgnoreCase)) continue;

            if (best is null)
            {
                best = s;
                continue;
            }

            // Prefer the one whose ActivityState is Exited (it just got SessionEnd).
            var bestIsExited = best.ActivityState == ActivityState.Exited;
            var sIsExited = s.ActivityState == ActivityState.Exited;
            if (sIsExited && !bestIsExited) { best = s; continue; }
            if (!sIsExited && bestIsExited) continue;

            // Tie-break on most recently created.
            if (s.CreatedAt > best.CreatedAt) best = s;
        }
        return best;
    }

    /// <summary>
    /// Save state of sessions that can be resumed.
    /// Includes: running sessions, and ANY session with ClaudeSessionId (can resume with --resume).
    /// </summary>
    public void SaveCurrentState(SessionStateStore store)
    {
        LogSessionsForDebug("SaveCurrentState");
        var persisted = BuildPersistedSessions();
        store.Save(persisted);
        _log?.Invoke($"[SaveCurrentState] Saved {persisted.Count} session(s) to state store.");
    }

    /// <summary>
    /// Save state of sessions to the store (used when keeping sessions on exit).
    /// The getHwnd delegate maps session ID -> console HWND (as long), for Embedded mode only.
    /// Saves ALL sessions that can be resumed: running sessions and any session with ClaudeSessionId.
    /// </summary>
    public void SaveSessionState(SessionStateStore store, Func<Guid, long> getHwnd)
    {
        LogSessionsForDebug("SaveSessionState");
        var persisted = BuildPersistedSessions(getHwnd);
        store.Save(persisted);
        _log?.Invoke($"[SaveSessionState] Saved {persisted.Count} session(s) to state store.");
    }

    private void LogSessionsForDebug(string caller)
    {
        _log?.Invoke($"[{caller}] Total sessions in manager: {_sessions.Count}");
        foreach (var s in _sessions.Values)
            _log?.Invoke($"  Session {s.Id}: Status={s.Status}, ClaudeSessionId={s.ClaudeSessionId ?? "(null)"}, Repo={s.RepoPath}");
    }

    private List<PersistedSession> BuildPersistedSessions(Func<Guid, long>? getHwnd = null)
    {
        return _sessions.Values
            .Where(s => s.Status == SessionStatus.Running ||
                       !string.IsNullOrEmpty(s.ClaudeSessionId))
            .OrderBy(s => s.SortOrder)
            .Select(s => new PersistedSession
            {
                Id = s.Id,
                RepoPath = s.RepoPath,
                WorkingDirectory = s.WorkingDirectory,
                ClaudeArgs = s.ClaudeArgs,
                CustomName = s.CustomName,
                CustomColor = s.CustomColor,
                PendingPromptText = s.PendingPromptText,
                EmbeddedProcessId = s.ProcessId,
                ConsoleHwnd = getHwnd != null && s.BackendType == SessionBackendType.Embedded ? getHwnd(s.Id) : 0,
                ClaudeSessionId = s.ClaudeSessionId,
                ActivityState = s.ActivityState,
                CreatedAt = s.CreatedAt,
                SortOrder = s.SortOrder,
                ExpectedFirstPrompt = s.ExpectedFirstPrompt ?? s.VerifiedFirstPrompt,
                HistoryEntryId = s.HistoryEntryId,
                BackendType = s.BackendType,
                AgentKind = s.AgentKind,
                RawStartupText = s.RawStartupText,
                SelectedTabName = s.SelectedTabName,
                QueuedPrompts = s.PromptQueue.HasItems
                    ? s.PromptQueue.Items.Select(q => new PersistedPromptQueueItem
                    {
                        Id = q.Id,
                        Text = q.Text,
                        CreatedAt = q.CreatedAt
                    }).ToList()
                    : null,
            })
            .ToList();
    }

    /// <summary>Restore a single persisted embedded session into tracking.
    /// The WPF layer must provide the reattached backend.</summary>
    public Session RestoreEmbeddedSession(PersistedSession ps, ISessionBackend embeddedBackend)
    {
        var session = new Session(
            ps.Id, ps.RepoPath, ps.WorkingDirectory, ps.ClaudeArgs,
            embeddedBackend, ps.ClaudeSessionId, ps.ActivityState, ps.CreatedAt,
            ps.CustomName, ps.CustomColor, ps.PendingPromptText);

        session.AgentKind = ps.AgentKind;

        // Set expected first prompt BEFORE verification so it can be compared
        session.ExpectedFirstPrompt = ps.ExpectedFirstPrompt;
        session.HistoryEntryId = ps.HistoryEntryId;
        session.RawStartupText = ps.RawStartupText;

        // Restore queued prompts
        if (ps.QueuedPrompts is { Count: > 0 })
        {
            session.PromptQueue.LoadFrom(ps.QueuedPrompts.Select(q => new PromptQueueItem
            {
                Id = q.Id,
                Text = q.Text,
                CreatedAt = q.CreatedAt
            }));
            _log?.Invoke($"Restored {ps.QueuedPrompts.Count} queued prompt(s) for session {session.Id}.");
        }

        _sessions[session.Id] = session;
        RaiseSessionCreated(session);

        if (ps.ClaudeSessionId != null)
        {
            // Check for duplicate ClaudeSessionId - if another session already has this ID,
            // clear it from this session to force auto-registration of a new ID
            if (_claudeSessionMap.TryGetValue(ps.ClaudeSessionId, out var existingId))
            {
                _log?.Invoke($"WARNING: ClaudeSessionId {ps.ClaudeSessionId[..8]}... already used by session {existingId}, clearing from {session.Id}");
                session.ClaudeSessionId = null;
            }
            else
            {
                _claudeSessionMap[ps.ClaudeSessionId] = session.Id;
                // Verify session file exists AND content matches expected prompt
                session.VerifyClaudeSession();
                if (session.VerificationStatus == Claude.SessionVerificationStatus.ContentMismatch)
                {
                    _log?.Invoke($"WARNING: Session {session.Id} ClaudeSessionId {ps.ClaudeSessionId[..8]}... content mismatch - session file doesn't match expected prompt");
                }
            }
        }

        _log?.Invoke($"Restored session {session.Id} (PID {session.ProcessId}).");
        return session;
    }

    /// <summary>
    /// Load persisted sessions from the store. Returns a RestoreSessionsResult containing
    /// PersistedSession records for the WPF layer to restore, plus any load errors.
    /// Sessions with ClaudeSessionId can be resumed via --resume flag even if the original process is gone.
    /// </summary>
    public RestoreSessionsResult LoadPersistedSessions(SessionStateStore store)
    {
        var loadResult = store.Load();

        // If load failed, return immediately with error info
        if (!loadResult.Success)
        {
            _log?.Invoke($"CRITICAL: Failed to load sessions.json: {loadResult.ErrorMessage}");
            return new RestoreSessionsResult
            {
                Sessions = new List<PersistedSession>(),
                LoadSuccess = false,
                LoadErrorMessage = loadResult.ErrorMessage,
                FileExistedButFailed = loadResult.FileExistedButFailed
            };
        }

        var persisted = loadResult.Sessions;
        var valid = new List<PersistedSession>();

        // Track seen ClaudeSessionIds to detect duplicates in persisted data
        var seenClaudeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ps in persisted)
        {
            // Sessions with ClaudeSessionId can be resumed with --resume flag,
            // even if the original process is gone (ConPty crash recovery)
            if (!string.IsNullOrEmpty(ps.ClaudeSessionId))
            {
                // Check for duplicate ClaudeSessionIds - this indicates corrupt persisted data
                if (seenClaudeIds.Contains(ps.ClaudeSessionId))
                {
                    _log?.Invoke($"WARNING: Persisted session {ps.Id} has duplicate ClaudeSessionId {ps.ClaudeSessionId[..8]}..., clearing to force fresh start.");
                    ps.ClaudeSessionId = null;
                }
                else
                {
                    seenClaudeIds.Add(ps.ClaudeSessionId);
                    _log?.Invoke($"Persisted session {ps.Id} has ClaudeSessionId {ps.ClaudeSessionId[..8]}..., valid for resume.");
                }
                valid.Add(ps);
                continue;
            }

            // Sessions without ClaudeSessionId are still valid - they just won't use --resume
            // ConPTY will start a fresh Claude process for them
            _log?.Invoke($"Persisted session {ps.Id} has no ClaudeSessionId, will start fresh Claude process.");
            valid.Add(ps);
        }

        _log?.Invoke($"Found {valid.Count}/{persisted.Count} valid persisted session(s).");

        // Don't re-save here - let RestorePersistedSessions handle cleanup after restoration
        return new RestoreSessionsResult
        {
            Sessions = valid,
            LoadSuccess = true,
            LoadErrorMessage = null,
            FileExistedButFailed = false
        };
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
        _claudeSessionMap.Clear();
    }
}
