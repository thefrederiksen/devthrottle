using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CcDirector.Core.AgentPlugins;
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
    /// Base URL of this Director's Control API (e.g. "http://127.0.0.1:7880"), injected into
    /// every spawned session as CC_DIRECTOR_API so agents can call the REST API on their own
    /// Director (look themselves up, list handovers, etc). Set by ControlApiHost.StartAsync
    /// once Kestrel has bound; sessions spawned before the Control API starts won't have it.
    /// </summary>
    public string? ControlApiBaseUrl { get; set; }

    /// <summary>
    /// This Director's stable id, injected into spawned sessions as CC_DIRECTOR_ID.
    /// Set by ControlApiHost.StartAsync alongside <see cref="ControlApiBaseUrl"/>.
    /// </summary>
    public string? DirectorId { get; set; }

    /// <summary>
    /// Fired immediately after a session is added to the manager's internal dictionary,
    /// for EVERY session - whether created via the Avalonia UI, the web Control API,
    /// or restored from persistence at startup. Handlers must be idempotent: the
    /// Avalonia UI already skips sessions it has already wrapped, and any other
    /// subscriber should do the same.
    /// </summary>
    public event Action<Session>? OnSessionCreated;

    /// <summary>
    /// Fired immediately BEFORE a session is disposed and removed from tracking
    /// (via <see cref="RemoveSession"/>). Subscribers that wired per-session
    /// resources in response to <see cref="OnSessionCreated"/> -- timers, buffer
    /// event handlers, caches -- MUST tear them down here. Firing before disposal
    /// is critical: it lets a subscriber stop a background timer that touches the
    /// session's <see cref="CircularTerminalBuffer"/> before that buffer is
    /// disposed, which otherwise faults on a timer thread and crashes the process.
    /// Handlers must be idempotent and must not throw.
    /// </summary>
    public event Action<Session>? OnSessionRemoved;

    public SessionManager(AgentOptions options, Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
    }

    /// <summary>Invoke OnSessionCreated. Public so external endpoint mappers (web Control API)
    /// can announce sessions they created without going through CreateSession overloads.</summary>
    public void RaiseSessionCreated(Session session)
    {
        // Every creation route (UI, web Control API, restore) funnels through here, so this
        // is the one place to wire process-exit reaping for ALL sessions. Reaping removes a
        // cleanly-exited session so it does not linger as a dead row with no process behind it
        // (the "two sessions in the desktop, one with no claude" symptom). One-shot + idempotent
        // downstream, so a duplicate announce of the same session does no harm.
        WireSessionReaper(session);

        try { OnSessionCreated?.Invoke(session); }
        catch (Exception ex) { _log?.Invoke($"OnSessionCreated handler threw: {ex.Message}"); }
    }

    /// <summary>
    /// Grace period between a clean process exit and reaping the session, so the final
    /// terminal output flushes, the clean exit is briefly visible, and any explicit
    /// DELETE racing the same exit settles first.
    /// </summary>
    internal int CleanExitReapDelayMs { get; set; } = 3000;

    /// <summary>
    /// Whether a process exit should reap (auto-remove) the session. Reap only local
    /// interactive agent sessions that exited cleanly (code 0): a non-zero/abnormal exit
    /// is left in place so the user sees it died and crash recovery (#212) can act, and
    /// remote/embedded backends are never auto-removed on completion. Pure + static so it
    /// is unit-testable without spawning a process.
    /// </summary>
    public static bool ShouldReapOnExit(SessionBackendType backendType, int exitCode)
    {
        if (exitCode != 0) return false;
        // ConPty is the local interactive PTY session on every OS (the Unix PTY backend
        // is still tracked under the ConPty enum value); Pipe is the per-prompt local
        // process. Remote (GitHubActions), Studio and Embedded are never auto-reaped.
        return backendType is SessionBackendType.ConPty or SessionBackendType.Pipe;
    }

    /// <summary>Subscribe a session's one-shot exit signal to the reaper.</summary>
    private void WireSessionReaper(Session session)
    {
        session.OnExited += exitCode => OnSessionProcessExited(session, exitCode);
    }

    /// <summary>
    /// React to a session's process exiting on its own (not via an explicit DELETE/close):
    /// reap cleanly-exited local sessions after a short grace delay; keep everything else.
    /// </summary>
    private void OnSessionProcessExited(Session session, int exitCode)
    {
        if (!ShouldReapOnExit(session.BackendType, exitCode))
        {
            _log?.Invoke($"Session {session.Id} exited (code={exitCode}, backend={session.BackendType}); keeping row for recovery.");
            return;
        }

        _log?.Invoke($"Session {session.Id} exited cleanly; reaping in {CleanExitReapDelayMs}ms.");
        _ = ReapAfterDelayAsync(session.Id);
    }

    private async Task ReapAfterDelayAsync(Guid id)
    {
        if (CleanExitReapDelayMs > 0)
            await Task.Delay(CleanExitReapDelayMs).ConfigureAwait(false);

        // Re-check under the live dictionary: an explicit DELETE may have already removed it,
        // or a restart may have replaced it with a live process. Only reap a session that is
        // still tracked and still exited.
        if (_sessions.TryGetValue(id, out var session) && session.Status == SessionStatus.Exited)
        {
            _log?.Invoke($"Reaping cleanly-exited session {id}.");
            RemoveSession(id);
        }
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
        return CreateSession(repoPath, AgentPluginRegistry.CreateAgent(AgentKind.ClaudeCode, _options), claudeArgs, backendType, resumeSessionId);
    }

    /// <summary>
    /// Create a session by resolving the requested built-in CLI plugin and asking it for the
    /// launch strategy. This is the plugin-backed path new callers should use.
    /// </summary>
    public Session CreateSession(string repoPath, AgentKind agentKind, string? userArgs, SessionBackendType backendType, string? resumeSessionId, SessionType sessionType = SessionType.Developer, Guid? groupId = null, string? groupRole = null, string? groupName = null)
    {
        return CreateSession(
            repoPath,
            AgentPluginRegistry.CreateAgent(agentKind, _options),
            userArgs,
            backendType,
            resumeSessionId,
            sessionType,
            groupId,
            groupRole,
            groupName);
    }

    /// <summary>
    /// Create a session driven by a specific <see cref="IAgent"/> (Claude Code, Pi, etc).
    /// Agents that don't support preassigned session IDs (Pi) skip Claude's session-linking
    /// step; Director still tracks the session via its own GUID and backend lifecycle.
    /// </summary>
    /// <param name="sessionType">The session's declared purpose (issue #211), stamped once
    /// here and immutable afterwards. Drives the UI badge and the wingman mission clause;
    /// playbook text (<see cref="SessionTypePlaybooks.For"/>) is no longer auto-seeded.</param>
    /// <param name="groupId">Group identity (issue #225) when this session is a group member;
    /// null for a solo session.</param>
    /// <param name="groupRole">The member's descriptive role within its group (issue #225).</param>
    /// <param name="groupName">The group's display name (issue #225), for the desktop header.</param>
    public Session CreateSession(string repoPath, IAgent agent, string? userArgs, SessionBackendType backendType, string? resumeSessionId, SessionType sessionType = SessionType.Developer, Guid? groupId = null, string? groupRole = null, string? groupName = null)
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
            AgentKind = agent.Kind,
            SessionType = sessionType,
            GroupId = groupId,
            GroupRole = groupRole,
            GroupName = groupName,
            // The EFFECTIVE launch line (userArgs merged with the configured agent defaults) is the
            // authoritative source of the launched --model value for the context gauge (issue #803).
            // `userArgs`/ClaudeArgs is null when the model comes from the default, not a per-session
            // override, so persist the merged `args` from BuildLaunchSpec here.
            EffectiveLaunchArgs = args,
        };

        try
        {
            // Inject CC_SESSION_ID so skills (e.g. /handover) can look up the session name,
            // plus the Control API endpoint so a session can find itself over REST:
            //   GET $CC_DIRECTOR_API/sessions/$CC_SESSION_ID
            var envVars = new Dictionary<string, string>
            {
                ["CC_SESSION_ID"] = id.ToString()
            };
            if (!string.IsNullOrEmpty(ControlApiBaseUrl))
                envVars["CC_DIRECTOR_API"] = ControlApiBaseUrl;
            if (!string.IsNullOrEmpty(DirectorId))
                envVars["CC_DIRECTOR_ID"] = DirectorId;

            // Issue #705: make session-to-session messaging discoverable to the agent. This is a
            // one-line reminder, NOT a credential - the tools reach the fleet through CC_DIRECTOR_API
            // above (the Director relays to the Gateway), so the fleet token never enters the session.
            envVars["CC_FLEET_TOOLS"] =
                "cc-devthrottle actions --json (list DevThrottle actions); cc-devthrottle session list; cc-devthrottle session whoami; cc-devthrottle session rename \"name\"; cc-devthrottle message send <id|all> \"message\"; cc-devthrottle message ask <id> \"question\"; cc-devthrottle schedule list; cc-devthrottle setup status";

            // Cursor authenticates via CURSOR_API_KEY (issue #517, assumption A5). Inject the
            // configured key into the session environment so cursor-agent picks it up. The key
            // value is never logged.
            if (agent.Kind == AgentKind.Cursor)
            {
                var cursorKey = _options.ResolveCursorApiKey();
                if (!string.IsNullOrEmpty(cursorKey))
                {
                    envVars["CURSOR_API_KEY"] = cursorKey;
                    _log?.Invoke("Injected CURSOR_API_KEY into the Cursor session environment.");
                }
            }

            // GitHub Copilot authenticates via a GitHub token (issue #625). Inject the configured
            // token (or the resolved COPILOT_GITHUB_TOKEN > GH_TOKEN > GITHUB_TOKEN env value) as
            // COPILOT_GITHUB_TOKEN so copilot starts without an interactive /login. When none is
            // configured Director injects nothing and the user logs in inside the tab. Never logged.
            if (agent.Kind == AgentKind.Copilot)
            {
                var copilotToken = _options.ResolveCopilotToken();
                if (!string.IsNullOrEmpty(copilotToken))
                {
                    envVars["COPILOT_GITHUB_TOKEN"] = copilotToken;
                    _log?.Invoke("Injected COPILOT_GITHUB_TOKEN into the GitHub Copilot session environment.");
                }
            }

            // For Claude, install the session-pointer hooks and pass them via --settings so the
            // Director learns the current Claude session id + transcript path across /clear and
            // auto-compaction (Claude mints a new id + transcript file on each). --settings MERGES
            // with the user's own hooks - it never replaces them - and the hook files read
            // CC_SESSION_ID / CC_DIRECTOR_API from the environment injected above.
            if (agent.Kind == AgentKind.ClaudeCode)
            {
                var hookSettings = CcDirector.Core.Claude.ClaudeHookInstaller.EnsureInstalled();
                if (!string.IsNullOrEmpty(hookSettings))
                {
                    args = $"{args} --settings \"{hookSettings}\"".Trim();
                    _log?.Invoke("Installed Claude session-pointer hooks (--settings).");
                }
            }

            // For Codex, merge the fleet-preamble SessionStart hook into ~/.codex/hooks.json and
            // append --dangerously-bypass-hook-trust so it runs without a per-user trust prompt.
            // Codex re-fires SessionStart on /clear and /compact, so the preamble re-injects itself;
            // the hook reads CC_SESSION_ID / CC_DIRECTOR_API from the environment injected above.
            if (agent.Kind == AgentKind.Codex)
            {
                if (CcDirector.Core.Codex.CodexHookInstaller.EnsureInstalled())
                {
                    args = $"{args} {CcDirector.Core.Codex.CodexHookInstaller.BypassTrustFlag}".Trim();
                    _log?.Invoke("Installed Codex fleet-preamble SessionStart hook (--dangerously-bypass-hook-trust).");
                }
            }

            // For Pi, write the fleet preamble to a per-session file and pass it via
            // --append-system-prompt. Pi keeps the launch system prompt across /new and /compact, so
            // the preamble persists without a re-injection hook. The hook reads nothing - the Director
            // builds the preamble locally from the session's known identity.
            if (agent.Kind == AgentKind.Pi)
            {
                var piName = string.IsNullOrWhiteSpace(session.CustomName)
                    ? Path.GetFileName(repoPath.TrimEnd('\\', '/'))
                    : session.CustomName;
                var preambleFile = CcDirector.Core.Pi.PiPreambleWriter.WriteForSession(
                    id.ToString(), piName, Environment.MachineName, repoPath);
                args = $"{args} --append-system-prompt \"{preambleFile}\"".Trim();
                _log?.Invoke("Wrote Pi fleet preamble and passed it via --append-system-prompt.");
            }

            // Resolve the agent command to a concrete executable path before spawning.
            // CreateProcess only appends ".exe" to a bare command name, so a CLI installed
            // as a ".cmd" shim (e.g. npm-installed "opencode.cmd") would never be found from
            // the bare name "opencode". Resolving against PATH+PATHEXT yields the full
            // "...\opencode.cmd" path. CreateProcess still cannot execute a batch shim
            // directly, so CommandLineLauncher wraps .cmd/.bat through cmd.exe. If the command
            // cannot be resolved at all we keep the original so the launch fails loudly.
            var resolvedExe = ExecutableResolver.Resolve(agent.ExecutablePath) ?? agent.ExecutablePath;
            if (!string.Equals(resolvedExe, agent.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                _log?.Invoke($"Resolved agent command '{agent.ExecutablePath}' to '{resolvedExe}'");

            var (launchExe, launchArgs) = CommandLineLauncher.Build(resolvedExe, args);
            if (!string.Equals(launchExe, resolvedExe, StringComparison.OrdinalIgnoreCase))
                _log?.Invoke($"Launching '{resolvedExe}' via shell: {launchExe} {launchArgs}");

            // Get initial terminal dimensions (default 120x30)
            backend.Start(launchExe, launchArgs, repoPath, 120, 30, envVars);
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
    /// Create a GitHub Actions remote session. No local process is spawned: the
    /// session is a handle to a GitHub issue/PR thread driven by @claude comments,
    /// with the work running on a GitHub-hosted runner. The backend's authoritative
    /// activity-state sink is wired to the session so run status (queued/in_progress/
    /// completed) drives the Working/WaitingForInput badge directly - the
    /// <c>TerminalStateDetector</c> silence heuristic is skipped for remote sessions.
    /// </summary>
    /// <param name="config">Repo, branch, trigger mode, and initial prompt.</param>
    /// <param name="client">
    /// GitHub REST client. Pass null to build a real <see cref="GitHubRestClient"/>
    /// using the token from credentials.env (read at point of use). Tests pass a stub.
    /// </param>
    public Session CreateGitHubActionsSession(RemoteSessionConfig config, IGitHubClient? client = null)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        FileLog.Write($"[SessionManager] CreateGitHubActionsSession: {config.Slug} mode={config.TriggerMode}");

        var gh = client ?? new GitHubRestClient(GitHubCredentials.ReadToken());
        var backend = new GitHubActionsBackend(config, gh, _options.DefaultBufferSizeBytes);

        var id = Guid.NewGuid();
        // A remote thread has no local working directory; use the repo slug as a stable
        // human label in the RepoPath slot (the UI shows it; nothing on disk is touched).
        var label = config.Slug;
        var session = new Session(id, label, label, config.InitialPrompt, backend, SessionBackendType.GitHubActions)
        {
            AgentKind = Agents.AgentKind.ClaudeCode
        };

        // Authoritative activity wiring: the run status drives the badge.
        backend.ActivitySink = state => session.ApplyTerminalActivityState(state);

        try
        {
            backend.StartRemote();
            session.MarkRunning();

            _sessions[id] = session;
            RaiseSessionCreated(session);
            _log?.Invoke($"GitHub Actions session {id} created for {config.Slug}.");
            return session;
        }
        catch (Exception ex)
        {
            session.MarkFailed();
            _log?.Invoke($"Failed to create GitHub Actions session for {config.Slug}: {ex.Message}");
            session.Dispose();
            throw;
        }
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

            // Tell per-session subscribers to tear down BEFORE we dispose the
            // session (and its terminal buffer). A subscriber holding a background
            // timer that reads the buffer must stop it now; otherwise that timer
            // faults on a disposed buffer and crashes the process.
            try { OnSessionRemoved?.Invoke(session); }
            catch (Exception ex) { _log?.Invoke($"OnSessionRemoved handler threw: {ex.Message}"); }

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

    /// <summary>
    /// Fires after a session's Wingman context has been reset following a <c>/clear</c>.
    /// Subscribers that cache per-session Wingman state outside the <see cref="Session"/>
    /// (e.g. <c>TurnSummaryCache</c>) should drop their entries for this session so the
    /// Wingman stops narrating the pre-clear conversation. Arg: the affected session.
    /// </summary>
    public event Action<Session>? OnSessionContextReset;

    /// <summary>
    /// Reset everything that described the conversation before a <c>/clear</c> for a
    /// session: the Session's own Wingman context (status-event log + terminal replay
    /// buffer) and, via <see cref="OnSessionContextReset"/>, external caches keyed by
    /// the Director session id. No-op (logged) when the session is not found.
    /// </summary>
    public void ResetSessionContextAfterClear(Guid directorSessionId)
    {
        FileLog.Write($"[SessionManager] ResetSessionContextAfterClear: id={directorSessionId}");
        if (!_sessions.TryGetValue(directorSessionId, out var session))
        {
            FileLog.Write($"[SessionManager] ResetSessionContextAfterClear: session not found");
            return;
        }
        session.ClearWingmanContext();
        try { OnSessionContextReset?.Invoke(session); }
        catch (Exception ex) { _log?.Invoke($"OnSessionContextReset handler threw: {ex.Message}"); }
    }

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
    /// was just rotated by /clear or /compact, so it can be relinked to the NEW
    /// Claude session id when no existing mapping matches.
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
                SessionType = s.SessionType,
                GroupId = s.GroupId,
                GroupRole = s.GroupRole,
                GroupName = s.GroupName,
                RawStartupText = s.RawStartupText,
                SelectedTabName = s.SelectedTabName,
                WingmanEnabled = s.WingmanEnabled,
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

    /// <summary>
    /// Adopt an already-constructed session into tracking, wiring it exactly as the
    /// create/restore paths do (added to the roster, announced via
    /// <see cref="RaiseSessionCreated"/>, which also wires process-exit reaping).
    /// Internal seam for tests that need a session with a controllable backend in the
    /// live roster - production code uses the typed CreateSession/Restore overloads.
    /// </summary>
    internal void AdoptSession(Session session)
    {
        _sessions[session.Id] = session;
        RaiseSessionCreated(session);
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
        session.SessionType = ps.SessionType;
        session.GroupId = ps.GroupId;
        session.GroupRole = ps.GroupRole;
        session.GroupName = ps.GroupName;
        session.WingmanEnabled = ps.WingmanEnabled;
        // Restored sessions already have history, so the brand-new gate (which short-
        // circuits the Wingman's first turn-end briefing on fresh sessions) does not apply.
        session.IsBrandNew = false;

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
