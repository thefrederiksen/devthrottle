using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Manages all active sessions. Creates, tracks, and kills sessions.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, Guid> _claudeSessionMap = new();
    private readonly AgentOptions _options;

    /// <summary>
    /// Tracks which three-digit session numbers THIS Director currently shows (issue #820), so a
    /// number is not reused for two live sessions on this box and is freed when a session ends. Issue
    /// #1292: the AUTHORITY for the number is now the Gateway (see <see cref="FleetNumberSource"/>);
    /// this allocator only reserves whatever number a session ends up with (a Gateway hand-out, a
    /// persisted number, or a local offline pick) and hands out the OFFLINE fallback number when the
    /// Gateway cannot be reached.
    /// </summary>
    private readonly SessionNumberAllocator _numberAllocator = new();

    /// <summary>The Director-local session-number allocator (issue #820). Exposed for tests.</summary>
    internal SessionNumberAllocator NumberAllocator => _numberAllocator;

    /// <summary>
    /// Issue #1292: asks the Gateway for a session's fleet-unique three-digit number. Set by the host
    /// (ControlApiHost) to call the Gateway; returns the number, or null when the Gateway is disabled,
    /// unreachable, or its pool is exhausted - the Director then assigns a local offline number. Null
    /// (tests, a Director with no host wiring) skips the Gateway entirely and numbers offline.
    /// </summary>
    public Func<Guid, CancellationToken, Task<int?>>? FleetNumberSource { get; set; }

    /// <summary>
    /// Issue #1292: tells the Gateway a session's number is free again when the session ends. Set by
    /// the host to call the Gateway. Null (tests, no host) is a no-op.
    /// </summary>
    public Action<Guid>? FleetNumberRelease { get; set; }

    /// <summary>
    /// Issue #1292: true only while a Gateway is CONFIGURED (gateway.url set). When true, a new
    /// session's number is requested from the Gateway asynchronously (creation never blocks on the
    /// network - the number appears when the Gateway answers). When false - no Gateway configured, and
    /// in tests - the session is numbered locally and synchronously at creation, so it always has a
    /// number the instant it is created. The host keeps this in step with the gateway config.
    /// </summary>
    public bool FleetNumberingActive { get; set; }

    private readonly Action<string>? _log;

    public AgentOptions Options => _options;

    /// <summary>
    /// This Director's stable id, injected into spawned sessions as CC_DIRECTOR_ID.
    /// Set by ControlApiHost.StartAsync. Identity, not an address: the Remove-the-network-port
    /// mission deleted CC_DIRECTOR_API and CC_DIRECTOR_TOKEN with the listener they named, so a
    /// session is told WHICH Director it belongs to and reaches everything through the Gateway
    /// pair below.
    /// </summary>
    public string? DirectorId { get; set; }

    /// <summary>
    /// Remove-the-network-port mission, phase 1b: mints the credential a session's own agent presents to the
    /// GATEWAY, bound to that session's id, and registers it with the Gateway over the connection the
    /// Director already holds. Returns null when no Gateway is configured or no key could be minted. Set by
    /// ControlApiHost.StartAsync alongside <see cref="GatewayUrl"/> - the address and the credential are
    /// stamped together, because either alone is useless.
    ///
    /// The Gateway session key is random, registered by its hash, and revoked when the session is
    /// reaped - never a signature over a machine secret, because the Gateway holds no such secret
    /// and must never be given one.
    ///
    /// THERE IS NO FALLBACK BEHIND IT. A session either gets its own key or it gets nothing - it is never
    /// handed the Director's own Gateway key, which would give every agent process authority over the whole
    /// account on every machine. That is a strictly larger hole than the network port this mission removes.
    /// </summary>
    public Func<Guid, string?>? GatewaySessionCredentialSource { get; set; }

    /// <summary>
    /// End a Gateway session key that was minted for a session which then FAILED to launch.
    ///
    /// The key is minted while the environment is being built, which is before the process starts. If
    /// anything after that throws, the session is disposed and never enters the roster - so the reaper
    /// that normally revokes on removal never runs for it, and the key stayed registered on the Gateway,
    /// refreshed on every reseed, belonging to a session that never existed. Same class of leak as the
    /// worktree reservation released in the same catch, and found by the same inspection.
    /// </summary>
    public Action<Guid>? GatewaySessionCredentialRevoker { get; set; }

    /// <summary>
    /// Remove-the-network-port mission, phase 1b: the Gateway's base URL, injected into every spawned session
    /// as CC_GATEWAY_URL so an agent's command line knows where to present the key from
    /// <see cref="GatewaySessionCredentialSource"/>. Null/empty when no Gateway is configured, in which case
    /// neither variable is stamped - an address with no credential, or a credential with no address, is a
    /// half-configured session that fails in a confusing way rather than an obvious one.
    /// </summary>
    public string? GatewayUrl { get; set; }

    /// <summary>
    /// Issue #1357: returns the signed-in DevThrottle user (email + nickname) to name in a Pi session's
    /// launch-time preamble, or null when no one is signed in. Set by ControlApiHost.StartAsync to read
    /// the host's cached snapshot SYNCHRONOUSLY (no network) so session creation never blocks. Null
    /// (tests, no host wiring) omits the user-identity line, exactly as when nobody is signed in.
    /// </summary>
    public Func<Account.SignedInUser?>? SignedInUserAccessor { get; set; }

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

    /// <summary>
    /// How often the deletion reaper sweeps for sessions flagged via the Control API
    /// (POST /sessions/{id}/request-deletion) and removes the eligible ones. Matches the
    /// ~30s cadence of the other Director/Gateway timer sweeps. Read once when the timer is
    /// armed in the constructor; tests drive <see cref="ReapPendingDeletions"/> directly.
    /// </summary>
    public int DeletionReaperIntervalMs { get; set; } = 30_000;

    /// <summary>
    /// Grace window between a session being flagged for deletion and the reaper being allowed to
    /// remove it, so the flagging turn can flush its final output / completion notification first.
    /// </summary>
    public int DeletionGraceMs { get; set; } = 30_000;

    /// <summary>Periodic deletion reaper (issue: self-requested session teardown). Disposed in
    /// <see cref="Dispose"/>. Distinct from the event-driven clean-exit reaper
    /// (<see cref="WireSessionReaper"/>), which fires off a process exit rather than a flag.</summary>
    private readonly System.Threading.Timer _deletionReaper;

    /// <summary>Machine-local reservations: while a session is alive its working directory is
    /// reserved so the worktree reaper (in this or any Director slot on this machine) never removes
    /// it out from under the session. Injectable for tests; production uses the default machine path.</summary>
    private readonly Git.WorktreeReservationStore _reservations;

    /// <summary>
    /// The cc-worktrees pool a session is handed a worktree from when its repository has the setting
    /// turned on. Injectable for tests; production runs the installed command line tool.
    /// </summary>
    private readonly Git.IWorktreePool _worktreePool;

    /// <summary>
    /// The pooled-worktree setting for a repository. A test overrides it; production reads
    /// <see cref="Git.WorktreePoolSettings"/>, whose answer for a repository nobody configured is OFF.
    /// </summary>
    private readonly Func<string, Git.WorktreePoolSetting> _worktreePoolSetting;

    public SessionManager(
        AgentOptions options,
        Action<string>? log = null,
        Git.WorktreeReservationStore? reservations = null,
        Git.IWorktreePool? worktreePool = null,
        Func<string, Git.WorktreePoolSetting>? worktreePoolSetting = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
        _reservations = reservations ?? new Git.WorktreeReservationStore();
        _worktreePool = worktreePool ?? new Git.CcWorktreesPool();
        _worktreePoolSetting = worktreePoolSetting ?? Git.WorktreePoolSettings.For;
        _deletionReaper = new System.Threading.Timer(
            _ => ReapPendingDeletions(), null, DeletionReaperIntervalMs, DeletionReaperIntervalMs);
    }

    /// <summary>
    /// Whether launching a session also writes the fleet's skills onto this machine.
    ///
    /// OFF BY DEFAULT, AND THE DEFAULT IS THE POINT. Placement writes into the USER'S OWN home
    /// directory - it creates and removes entries under <c>~/.agents/skills</c> and <c>~/.claude/skills</c>.
    /// Anything that constructs a SessionManager and launches a session would do that to whoever is
    /// running it, which for a test suite means the developer's own machine. That is not hypothetical:
    /// a full Core test run reorganised a real machine's Claude Code skills, because several tests call
    /// CreateSession and CreateSession placed skills unconditionally. The app turns this on; nothing
    /// else does, so every test is hermetic without having to remember to be.
    /// </summary>
    public bool PlacesSkillsOnLaunch { get; init; }

    /// <summary>
    /// Where this Director keeps its sessions on disk (<c>sessions.json</c>). Set by the app; null in tests that do not
    /// persist. Written at once when a session's owner changes (<see cref="PersistOwnerChange"/>).
    /// </summary>
    public SessionStateStore? DurableStateStore { get; set; }

    /// <summary>
    /// This Director's crash journal - the roster a Director started after a crash reads back
    /// (<see cref="DirectorCrashJournal.DetectAndClaim"/>). Set by the app once the Director's id is known. Written at
    /// once when a session's owner changes (<see cref="PersistOwnerChange"/>).
    /// </summary>
    public DirectorCrashJournal? CrashJournal { get; set; }

    /// <summary>
    /// Change a session's owner (the Gateway's hand over, the Fleet Manager mission, step 8), only if it is still
    /// <paramref name="expectedOwner"/>, and write the change to disk before answering (<see cref="PersistOwnerChange"/>).
    /// Throws when the change cannot be written; the session then keeps its previous owner.
    /// </summary>
    public OwnerChangeResult ChangeOwner(Session session, Guid? expectedOwner, Guid? newOwner)
    {
        ArgumentNullException.ThrowIfNull(session);
        FileLog.Write($"[SessionManager] ChangeOwner: session={session.Id}, expected={expectedOwner?.ToString() ?? "(the user)"}, " +
                      $"owner={newOwner?.ToString() ?? "(the user)"}");
        var result = session.SetController(expectedOwner, newOwner, PersistOwnerChange);
        FileLog.Write($"[SessionManager] ChangeOwner: session={session.Id}, outcome={result.Outcome}");
        return result;
    }

    /// <summary>
    /// Write a session's changed owner to disk NOW, before the verb answers, rather than on the next routine save - a
    /// Director that stops right after a hand over must come back knowing who owns the session.
    ///
    /// THE DURABLE RECORD IS THE CRASH JOURNAL. A Director started after a stop reads its crash journal
    /// (<see cref="DirectorCrashJournal.DetectAndClaim"/>); the app clears <c>sessions.json</c> at start and never
    /// restores from it. So when this Director has a crash journal, the journal write must succeed - it throws, and the
    /// change is undone, when it does not - and a failed <c>sessions.json</c> write is logged but does not undo a change
    /// the journal already holds. A Director with no crash journal yet has only <c>sessions.json</c>, and then that
    /// write must succeed. A manager with neither (a test) writes nothing.
    /// </summary>
    internal void PersistOwnerChange(Session session)
    {
        var owner = session.ControllerSessionId?.ToString();
        FileLog.Write($"[SessionManager] PersistOwnerChange: session={session.Id}, owner={owner ?? "(the user)"}, " +
                      $"journal={(CrashJournal is null ? "none" : CrashJournal.FilePath)}, " +
                      $"store={(DurableStateStore is null ? "none" : DurableStateStore.FilePath)}");
        if (CrashJournal is { } journal && !journal.SetSessionOwner(session.Id.ToString(), owner))
            journal.Update(BuildCrashJournalRoster());
        if (DurableStateStore is { } store && !store.Save(BuildPersistedSessions()))
        {
            if (CrashJournal is null)
                throw new IOException($"the owner of session {session.Id} could not be written to {store.FilePath}");
            FileLog.Write($"[SessionManager] PersistOwnerChange: {store.FilePath} was NOT written for {session.Id}; " +
                          $"the change is durable in the crash journal {CrashJournal.FilePath}, which is what a restarted Director reads");
        }
    }

    /// <summary>The crash-journal roster of every tracked session, in rail order.</summary>
    public IReadOnlyList<DirectorCrashJournalSession> BuildCrashJournalRoster()
        => _sessions.Values.OrderBy(s => s.SortOrder).Select(ToCrashJournalSession).ToList();

    /// <summary>One session as its crash-journal row. The desktop's routine save builds its rows here too, so the two
    /// writers of the journal can never disagree about what a row carries.</summary>
    public static DirectorCrashJournalSession ToCrashJournalSession(Session s) => new()
    {
        SessionId = s.Id.ToString(),
        Name = s.CustomName,
        RepoPath = s.RepoPath,
        Agent = s.AgentKind.ToString(),
        ClaudeSessionId = s.ClaudeSessionId,
        CreatedAtUtc = s.CreatedAt,
        ControllerSessionId = s.ControllerSessionId?.ToString(),
    };

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

        // Every creation route funnels through here, so this is also the one place to RESERVE the
        // session's working directory (inspection): while this session is alive, the worktree reaper
        // - in this Director slot or another on this machine - must not remove the worktree it is in.
        // Only real local directories are reserved (a remote/label RepoPath matches no worktree).
        //
        // The reservation is owned by the SESSION PROCESS, not this Director (inspection round 6): a
        // session (or a detached child) can outlive a force-killed Director, and its worktree must stay
        // protected. Liveness therefore tracks the session's own process. If its start time cannot be
        // read the reservation stays Director-owned (the pre-launch reservation), which is still correct
        // while this Director is alive.
        if (!string.IsNullOrWhiteSpace(session.RepoPath) && Directory.Exists(session.RepoPath))
        {
            int spid = session.ProcessId;
            DateTime? sstart = null;
            if (spid > 0)
            {
                try { using var sp = System.Diagnostics.Process.GetProcessById(spid); sstart = sp.StartTime.ToUniversalTime(); }
                catch { sstart = null; }
            }
            if (spid > 0 && sstart is not null)
                _reservations.Reserve(session.RepoPath, session.Id.ToString(), spid, sstart);
            else
                _reservations.Reserve(session.RepoPath, session.Id.ToString());
        }

        // Issue #820: every creation route funnels through here, so this is the one place to ensure
        // the session carries a three-digit number BEFORE subscribers (the desktop UI, the web
        // Control API mapper) read it to build their views.
        AssignSessionNumber(session);

        try { OnSessionCreated?.Invoke(session); }
        catch (Exception ex) { _log?.Invoke($"OnSessionCreated handler threw: {ex.Message}"); }
    }

    /// <summary>
    /// Ensure the session carries a three-digit number (issue #820). A restore path pre-sets
    /// <see cref="Session.Number"/> from persistence; reserve that exact number when it is still
    /// free, otherwise allocate a fresh one. A brand-new session has no number yet, so allocate one.
    /// When the pool is exhausted the session is left without a number (logged) - creation never
    /// blocks on a cosmetic handle. Idempotent: a duplicate announce of an already-numbered session
    /// (whose number this allocator already holds) is a no-op.
    /// </summary>
    private void AssignSessionNumber(Session session)
    {
        if (session.Number is int existing && _numberAllocator.IsReserved(existing))
            return; // already numbered and registered by this Director - duplicate announce, no-op

        if (session.Number is int preferred)
        {
            // Restore path: keep the persisted number when it is still free on this Director. The
            // Gateway adopts it via the /sessions aggregation, so it stays fleet-unique across a restart.
            if (_numberAllocator.TryReserve(preferred))
            {
                _log?.Invoke($"[SessionManager] Reserved persisted session number {preferred} for {session.Id}.");
                return;
            }
            _log?.Invoke($"[SessionManager] Persisted session number {preferred} for {session.Id} is taken; allocating a fresh one.");
            session.SetNumber(null);
        }

        // No Gateway configured (tests, or a local-only Director): number offline immediately and
        // synchronously so the session always has a number the moment it is created.
        if (!FleetNumberingActive || FleetNumberSource is null)
        {
            AssignOfflineNumber(session);
            return;
        }

        // Issue #1292: ask the Gateway for a fleet-unique number. Done off the creation path so session
        // creation never blocks on the network - the number appears a moment later when the Gateway
        // answers (SetNumber raises OnNumberChanged so the rail shows it). On no answer (Gateway
        // disabled/unreachable/exhausted) assign a local offline number instead.
        var source = FleetNumberSource;
        _ = Task.Run(async () =>
        {
            int? fromGateway = null;
            try { fromGateway = await source(session.Id, CancellationToken.None); }
            catch (Exception ex) { _log?.Invoke($"[SessionManager] Gateway number request for {session.Id} failed: {ex.Message}"); }

            if (fromGateway is int gw && _numberAllocator.TryReserve(gw))
            {
                session.SetNumber(gw);
                _log?.Invoke($"[SessionManager] Gateway assigned fleet session number {gw} to {session.Id}.");
            }
            else
            {
                AssignOfflineNumber(session);
            }
        });
    }

    /// <summary>
    /// Assign a LOCAL offline number in the high band (issue #1292) - the best guess used when the
    /// Gateway did not hand one out. Uses <see cref="Session.SetNumber"/> so a listener (the rail)
    /// updates even when this runs after creation.
    /// </summary>
    private void AssignOfflineNumber(Session session)
    {
        var assigned = _numberAllocator.AllocateOffline();
        session.SetNumber(assigned);
        if (assigned is int n)
            _log?.Invoke($"[SessionManager] Assigned local offline session number {n} to {session.Id} (Gateway unavailable).");
        else
            _log?.Invoke($"[SessionManager] No session number available for {session.Id} (number pool exhausted).");
    }

    /// <summary>
    /// Assign a three-digit number to every tracked session that lacks one (issue #820 backfill).
    /// Run once at Director startup so already-active sessions that predate this feature - or were
    /// restored without a number - become numbered in a single pass, not only sessions created
    /// afterward. Returns the count of sessions newly numbered. Issue #1292: these get a local offline
    /// number (the high band); the Gateway adopts each one via the /sessions aggregation, so a restart
    /// backfill never collides with a coordinated low-band number.
    /// </summary>
    public int BackfillNumbers()
    {
        FileLog.Write("[SessionManager] BackfillNumbers: scanning active sessions for missing numbers");
        var assigned = 0;
        foreach (var session in _sessions.Values)
        {
            if (session.Number.HasValue)
                continue;

            var n = _numberAllocator.AllocateOffline();
            if (n is int num)
            {
                session.SetNumber(num);
                assigned++;
                FileLog.Write($"[SessionManager] BackfillNumbers: assigned {num} to {session.Id}");
            }
            else
            {
                FileLog.Write($"[SessionManager] BackfillNumbers: number pool exhausted, {session.Id} left without a number");
                break;
            }
        }
        FileLog.Write($"[SessionManager] BackfillNumbers: assigned {assigned} number(s)");
        return assigned;
    }

    /// <summary>
    /// Grace period between a clean process exit and reaping the session, so the final
    /// terminal output flushes, the clean exit is briefly visible, and any explicit
    /// DELETE racing the same exit settles first.
    /// </summary>
    public int CleanExitReapDelayMs { get; set; } = 3000;

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
        // Release the worktree reservation the moment the session's process exits (clean exit, crash,
        // or an explicit close that kills the process), so the worktree becomes reapable again.
        session.OnExited += _ => _reservations.Release(session.Id.ToString());
    }

    /// <summary>
    /// React to a session's process exiting on its own (not via an explicit DELETE/close):
    /// reap cleanly-exited local sessions after a short grace delay; keep everything else.
    /// </summary>
    private void OnSessionProcessExited(Session session, int exitCode)
    {
        // A crashed session (issue #959) is NEVER auto-removed: it stays in the roster in its Error
        // state so the user sees that work stopped. Only an intentional clean exit is reaped.
        if (session.Crashed || !ShouldReapOnExit(session.BackendType, exitCode))
        {
            _log?.Invoke($"Session {session.Id} exited (code={exitCode}, backend={session.BackendType}, crashed={session.Crashed}); keeping row.");
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

    /// <summary>
    /// Sweep for sessions flagged for deletion via the Control API and remove the eligible ones.
    /// A flagged session is reaped once its grace window (<see cref="DeletionGraceMs"/>) has elapsed
    /// AND it is not actively Working - option (a): we never cut off a final in-flight turn; a session
    /// that is still Working is left for the next sweep. Called on the reaper timer; also invoked
    /// directly by tests. Per-session failures are isolated so one bad row cannot stall the sweep.
    /// </summary>
    internal void ReapPendingDeletions()
    {
        var now = DateTime.UtcNow;
        foreach (var session in _sessions.Values)
        {
            try
            {
                if (session.DeletionRequestedAt is not DateTime requestedAt) continue;
                if ((now - requestedAt).TotalMilliseconds < DeletionGraceMs) continue;
                // Option (a): wait out a still-running final turn. Reap only when the session is
                // idle / parked / waiting / exited, never mid-Working.
                if (session.ActivityState == ActivityState.Working) continue;

                _log?.Invoke($"Reaping session {session.Id} flagged for deletion ({session.DeletionReason ?? "no reason"}).");
                _ = KillAndRemoveForDeletionAsync(session.Id);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Deletion reaper: error evaluating session {session.Id}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Kill (best-effort) and force-remove a session the reaper picked. Unlike the clean-exit reaper,
    /// this removes regardless of backend/exit code - the session explicitly asked to be gone. Re-checks
    /// the flag right before removal so a <see cref="Session.CancelDeletion"/> that lands mid-sweep
    /// spares it.
    /// </summary>
    private async Task KillAndRemoveForDeletionAsync(Guid id)
    {
        try
        {
            if (_sessions.TryGetValue(id, out var session)
                && session.Status is SessionStatus.Running or SessionStatus.Starting)
            {
                try { await KillSessionAsync(id).ConfigureAwait(false); }
                catch (Exception ex) { _log?.Invoke($"Deletion reaper: kill failed for {id}: {ex.Message}"); }
            }

            if (_sessions.TryGetValue(id, out var still) && still.DeletionRequestedAt.HasValue)
                RemoveSession(id);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Deletion reaper: remove failed for {id}: {ex.Message}");
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
    public Session CreateSession(string repoPath, AgentKind agentKind, string? userArgs, SessionBackendType backendType, string? resumeSessionId, Guid? groupId = null, string? groupRole = null, string? groupName = null, Func<Guid, string>? nameFactory = null, Guid? controllerSessionId = null, Action<Session>? beforeLaunch = null)
    {
        return CreateSession(
            repoPath,
            AgentPluginRegistry.CreateAgent(agentKind, _options),
            userArgs,
            backendType,
            resumeSessionId,
            groupId,
            groupRole,
            groupName,
            nameFactory,
            controllerSessionId,
            beforeLaunch);
    }

    /// <summary>
    /// Create a session driven by a specific <see cref="IAgent"/> (Claude Code, Pi, etc).
    /// Agents that don't support preassigned session IDs (Pi) skip Claude's session-linking
    /// step; Director still tracks the session via its own GUID and backend lifecycle.
    /// </summary>
    /// <param name="groupId">Group identity (issue #225) when this session is a group member;
    /// null for a solo session.</param>
    /// <param name="groupRole">The member's descriptive role within its group (issue #225).</param>
    /// <param name="groupName">The group's display name (issue #225), for the desktop header.</param>
    /// <param name="nameFactory">Optional name-at-birth composer (issue #800): when supplied it
    /// is invoked with the new session's id and its result becomes the session's
    /// <see cref="Session.CustomName"/>, so the session is named in the create call rather than
    /// only by a later rename. The id is passed in so the name can carry an id-derived
    /// disambiguator. Null leaves the session unnamed (legacy behavior).</param>
    /// <param name="controllerSessionId">The controlling session's id (issue #815) when this
    /// session is spawned as a controlled sub-agent; null for a normal session. Set ONLY here at
    /// birth and immutable afterwards. Drives the recessive "Supporting" status color.</param>
    /// <param name="beforeLaunch">Optional stamp hook (Workflows mission, phase 5b) invoked on the
    /// constructed session BEFORE any launch-time context is materialized and BEFORE the process
    /// starts. Anything a launch-time channel reads off the session - the Pi preamble file, the
    /// preamble a startup hook fetches the instant the agent boots - must be stamped here, not
    /// after create returns, or the earliest readers race the stamp and Pi misses it entirely.</param>
    /// <param name="reattachPooledWorktree">The pooled worktree this seat ALREADY holds, when it is
    /// being restored after a Director restart. When supplied, the session runs in that slot on that
    /// lease and cc-worktrees is not asked for a slot at all - the repository's setting is not even
    /// read, because the answer is already known. Null on every ordinary create.</param>
    public Session CreateSession(string repoPath, IAgent agent, string? userArgs, SessionBackendType backendType, string? resumeSessionId, Guid? groupId = null, string? groupRole = null, string? groupName = null, Func<Guid, string>? nameFactory = null, Guid? controllerSessionId = null, Action<Session>? beforeLaunch = null, Git.PooledWorktree? reattachPooledWorktree = null)
    {
        if (agent is null)
            throw new ArgumentNullException(nameof(agent));
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();

        // Issue #800: the name-at-birth is composed HERE, once, because the pooled worktree below is
        // taken in this session's name and the holder shown by `cc-worktrees list` should be the name
        // a person reads, not only an identifier. The factory takes nothing but the id, so composing it
        // a few lines earlier than it is assigned changes nothing else.
        var birthName = nameFactory?.Invoke(id);

        // THE POOLED WORKTREE, when this repository has the setting turned on. Off is the default and
        // off means today's behaviour exactly: no tool is run and the session works in the checkout it
        // was opened in.
        //
        // NO FALLBACK. A refusal - the pool is full, the remote cannot be reached, the tool is not
        // installed - ends the create here, in the tool's own words, and no session opens. Starting the
        // session in the shared checkout instead would be the precise behaviour the setting was turned
        // on to stop, and it would be silent.
        //
        // A RESTORE BRINGS ITS OWN SLOT AND NO NEW ONE IS TAKEN. When the caller hands back a slot
        // this seat already held - a workspace restored after a Director restart - the lease is
        // re-attached and cc-worktrees is not asked for anything. Asking would take a SECOND slot
        // while the first stayed in use under a holder that no longer exists, so a Director that
        // restarted a few times would fill its own pool with sessions nobody could give back.
        var pooled = reattachPooledWorktree is not null
            ? ReattachPooledWorktree(reattachPooledWorktree, id, birthName)
            : AcquirePooledWorktree(repoPath, id, birthName);
        var workingDirectory = pooled?.Path ?? repoPath;

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
            // ConPty on Windows, UnixPty on macOS/Linux - one selection, in PlatformSessionBackend.
            SessionBackendType.ConPty
                => PlatformSessionBackend.CreateDefault(_options.DefaultBufferSizeBytes),
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

        // A pooled session's RepoPath IS the slot: it is where the agent works, and every reader of
        // RepoPath - the git status monitor, the reaper's live-session match, the Source Control page -
        // means "where the session is". Where the slot CAME from is on session.PooledWorktree.
        var session = new Session(id, workingDirectory, workingDirectory, userArgs, backend, backendType)
        {
            AgentKind = agent.Kind,
            GroupId = groupId,
            GroupRole = groupRole,
            GroupName = groupName,
            ControllerSessionId = controllerSessionId,
            // The EFFECTIVE launch line (userArgs merged with the configured agent defaults) is the
            // authoritative source of the launched --model value for the context gauge (issue #803).
            // `userArgs`/ClaudeArgs is null when the model comes from the default, not a per-session
            // override, so persist the merged `args` from BuildLaunchSpec here.
            EffectiveLaunchArgs = args,
        };

        // Issue #800: name the session AT BIRTH. The factory is invoked with the new id so the
        // composed name can carry an id-derived disambiguator. This is what stops a session from
        // ever displaying as the bare repository folder name.
        if (birthName is not null)
            session.CustomName = birthName;

        // The lease travels with the session from birth: close is the only moment it is needed, and a
        // lease that was never recorded is a slot cc-worktrees will not take back.
        session.PooledWorktree = pooled;

        // Pre-launch stamps (Workflows mission, phase 5b): applied BEFORE the per-agent launch
        // channels below read the session (Pi's preamble file is written from it) and BEFORE the
        // process starts (a startup hook can fetch the preamble the instant the agent boots).
        beforeLaunch?.Invoke(session);

        try
        {
            // Inject CC_SESSION_ID so skills (e.g. /handover) can look up the session name, and
            // CC_DIRECTOR_ID so the session knows WHICH Director it belongs to (identity only).
            //
            // CC_DIRECTOR_API and CC_DIRECTOR_TOKEN are GONE (Remove-the-network-port mission,
            // phase 5). They named this Director's deleted loopback listener and the credential
            // for it; stamping them now would hand every agent a live-looking address for a door
            // nothing answers on - and would hide any straggling caller for as long as the
            // variables existed. A session's only door to the fleet is the CC_GATEWAY_URL /
            // CC_GATEWAY_SESSION_KEY pair below.
            var envVars = new Dictionary<string, string>
            {
                ["CC_SESSION_ID"] = id.ToString()
            };
            if (!string.IsNullOrEmpty(DirectorId))
                envVars["CC_DIRECTOR_ID"] = DirectorId;

            // Put the machine's installed cc-* tools first on the session's PATH.
            //
            // Machine PATH is shared state: any other install, an unfinished migration, or a test rig
            // that leaked an entry can put a different copy of cc-devthrottle in front of ours, and
            // then every agent in every session reports "cannot connect to DevThrottle" while this
            // Director is healthy and connected. That is not hypothetical - it is what happened on the
            // machine this was written for, and repairing the machine PATH afterwards could only ever
            // fix it for sessions started later.
            //
            // So the session is not asked to find us. It is TOLD. Nothing is removed from the user's
            // PATH here - only which copy of our own command line wins - and this leaves the machine
            // PATH itself untouched.
            //
            // It is the MACHINE's tools, not this Director's own copy: there is one installed copy per
            // machine and every Director uses it. Composing the path from this Director's folder is what
            // made each Director carry a copy of its own, drifting a release further behind the product
            // every time one of them was not reinstalled by hand.
            var ownToolBin = Storage.CcStorage.Bin();
            if (Directory.Exists(ownToolBin))
            {
                envVars["PATH"] = Setup.FleetToolPathRepair.PathWithOwnToolsFirst(
                    ownToolBin, Environment.GetEnvironmentVariable("PATH"));
            }

            // Remove-the-network-port mission, phase 1b: the Gateway's address and THIS SESSION'S OWN key
            // for it. Stamped as a PAIR or not at all - a session given one without the other cannot reach
            // the Gateway and would report a credential problem or an address problem depending on which
            // half arrived, when the real answer is simply "there is no Gateway here".
            //
            // Until now a session had no Gateway credential of any kind, and the only way to give it one
            // would have been the Director's own - authority over the entire account. This is the least
            // privilege replacement: bound to this session, limited to the agent routes, ended when the
            // session is.
            if (!string.IsNullOrEmpty(GatewayUrl))
            {
                var gatewayCredential = GatewaySessionCredentialSource?.Invoke(id);
                if (!string.IsNullOrEmpty(gatewayCredential))
                {
                    envVars["CC_GATEWAY_URL"] = GatewayUrl;
                    envVars["CC_GATEWAY_SESSION_KEY"] = gatewayCredential;
                }
            }

            // Issue #705: make session-to-session messaging discoverable to the agent. This is a
            // one-line reminder, NOT a credential - the tools reach the fleet through the Gateway
            // with the session key above, so the fleet token never enters the session.
            //
            // It is ALSO injected text, and it is OURS: nothing in the product reads this variable, so
            // its only reader is the agent. That makes it prose we put in front of an agent without
            // being asked, exactly like the preamble - and it reaches every agent, including the ones
            // with no preamble at all.
            //
            // So it follows the same choice. A user running their own text has declined our words, and
            // continuing to whisper our command list through the environment would make the Settings
            // tab a liar: they could delete the fleet commands from their text and we would put them
            // back through a channel the tab never mentions.
            if (new InjectedTextStore().ActiveSource() == InjectedTextSource.Ours)
            {
                envVars["CC_FLEET_TOOLS"] =
                    "cc-devthrottle actions --json (list DevThrottle actions); cc-devthrottle session list; cc-devthrottle session whoami; cc-devthrottle session rename \"name\"; cc-devthrottle message inbox (read messages waiting for you); cc-devthrottle message send <id|all> \"message\" (rare, queued; only the session that started you or sessions you started); cc-devthrottle message reply <id> \"answer\"; cc-devthrottle schedule list; cc-devthrottle setup status";
            }

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

            if (agent.Kind == AgentKind.OpenCode)
            {
                envVars["NO_UPDATE_NOTIFIER"] = "1";
                envVars["OPENCODE_DISABLE_AUTOUPDATE"] = "1";
                envVars["OPENCODE_DISABLE_UPDATE_CHECK"] = "1";
                envVars["OPENCODE_DISABLE_AUTO_UPDATE"] = "1";
            }

            // Remove-the-network-port mission, phase 3: the two files a SessionStart hook uses instead
            // of calling this Director over HTTP. Both are named EXPLICITLY rather than computed by the
            // hook script, because working out the storage root - per platform and per named instance -
            // in two shell dialects would be a second copy of CcStorage that could drift from it in
            // silence.
            //
            // The preamble is WRITTEN HERE, before the process starts, because a startup hook fires
            // within moments of it. It is not written ONLY here: the Director maintains it for the
            // session's whole life (SessionPreambleMaintainer), because the hook fires again on every
            // resume, clear and compact, and the text renders from stores the user edits meanwhile.
            //
            // Only the two agent families that have a SessionStart hook get these. Pi has its own
            // launch-time system-prompt file, and an agent with no hook would be handed a path nothing
            // reads.
            if (agent.Kind is AgentKind.ClaudeCode or AgentKind.Codex)
            {
                try
                {
                    var preamblePath = SessionPreambleFile.WriteFor(
                        session, Environment.MachineName, SignedInUserAccessor?.Invoke());
                    envVars[SessionHookFiles.PreambleFileEnvVar] = preamblePath;
                }
                catch (Exception ex)
                {
                    // A preamble that cannot be written must never stop a session starting. The
                    // variable is then not stamped at all, so the hook prints nothing rather than
                    // reading a path that does not resolve.
                    _log?.Invoke($"The fleet preamble file could not be written for this session: {ex.Message}");
                    FileLog.Write($"[SessionManager] preamble file write FAILED (session still launching): {ex}");
                }
            }

            // The pointer drop box, for Claude alone - it is the only agent that mints a new session id
            // and transcript on /clear and compaction, so it is the only one with a pointer to report.
            // The directory is created HERE as well as by the watcher: the hook writes into it and
            // swallows every error, so a missing directory would cost transcript tracking silently.
            //
            // The path carries the session's own drop token, and handing it over HERE - in the
            // process environment, visible only to this session - is what makes the path a
            // capability. The watcher refuses any drop whose name does not carry the token, so a
            // sibling process that merely knows this session's id cannot retarget its pointer.
            if (agent.Kind == AgentKind.ClaudeCode)
            {
                try
                {
                    var pointerPath = SessionHookFiles.PointerPathFor(id, session.PointerDropToken);
                    Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
                    envVars[SessionHookFiles.PointerFileEnvVar] = pointerPath;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"The session-pointer drop box could not be prepared: {ex.Message}");
                    FileLog.Write($"[SessionManager] pointer drop box FAILED (session still launching): {ex}");
                }
            }

            // For Claude, install the session-pointer hooks and pass them via --settings so the
            // Director learns the current Claude session id + transcript path across /clear and
            // auto-compaction (Claude mints a new id + transcript file on each). --settings MERGES
            // with the user's own hooks - it never replaces them - and the hook files read the two
            // file paths stamped above from the environment.
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
            // the hook reads the preamble file path stamped above from the environment.
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
                // Issue #800: route the display-name fallback through the single composer so a
                // session never identifies itself by the bare folder name.
                var piName = SessionName.DisplayName(
                    session.CustomName,
                    SessionName.FolderName(repoPath),
                    SessionName.Disambiguator(id));
                // Issue #1357: name the signed-in user in Pi's preamble too, read synchronously from the
                // host's cached snapshot (no network) so launch never blocks; null omits the line.
                var signedInUser = SignedInUserAccessor?.Invoke();
                // Workflows mission (phase 5b): a seated Pi session's preamble file carries the seat
                // paragraph. The seat was stamped by beforeLaunch ABOVE, which is the whole reason
                // that hook runs before this block - Pi's file is immutable after launch.
                var piSeatParagraph = WorkflowSeatParagraph.Build(
                    session.WorkflowRunId, session.WorkflowId, session.WorkflowVersion, session.ExplicitRole);
                var preambleFile = CcDirector.Core.Pi.PiPreambleWriter.WriteForSession(
                    id.ToString(), piName, Environment.MachineName, workingDirectory, signedInUser, piSeatParagraph);
                args = $"{args} --append-system-prompt \"{preambleFile}\"".Trim();
                _log?.Invoke("Wrote Pi fleet preamble and passed it via --append-system-prompt.");
            }

            // Put the fleet's skills where THIS agent looks for them, so it discovers them through its
            // own skills machinery rather than needing a DevThrottle command. Local and synchronous -
            // it reconciles against what the last Gateway refresh materialized and never touches the
            // network, so it cannot slow or block a launch.
            //
            // The catch is deliberate and is the only thing standing between a skill problem and a
            // session that will not start. A launch must never fail because a capability that is
            // ADDITIONAL to the session could not be installed, so the failure is recorded and the
            // session starts with whatever skills it already had.
            //
            // A PLACEMENT THAT FELL SHORT IS REPORTED, NOT SWALLOWED. The point of a central library is
            // that a skill published on the Gateway is one the agent can actually read; when that stops
            // being true nothing throws, nothing turns red, and the only symptom is an agent working
            // from instructions nobody meant it to have. So the outcome is inspected and said out loud
            // in the session's own log, which is the one place the person starting the session looks.
            if (PlacesSkillsOnLaunch)
            {
                try
                {
                    var placement = CcDirector.Core.Skills.SkillDirectoryInstaller.InstallFor(agent.Kind);
                    if (!placement.IsComplete && !placement.NothingExpected)
                        _log?.Invoke(placement.Describe());
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Skills were not installed for this session: {ex.Message}");
                    FileLog.Write($"[SessionManager] skill install FAILED (session still launching): {ex}");
                }
            }

            // Resolve the agent command to a concrete executable path before spawning.
            // CreateProcess only appends ".exe" to a bare command name, so a CLI installed
            // as a ".cmd" shim (e.g. npm-installed "opencode.cmd") would never be found from
            // the bare name "opencode". Resolving against PATH+PATHEXT yields the full
            // "...\opencode.cmd" path. CreateProcess still cannot execute a batch shim
            // directly, so CommandLineLauncher wraps .cmd/.bat through cmd.exe.
            //
            // A command that resolves to NOTHING is refused here, by name. It used to be passed
            // through unchanged "so the launch fails loudly", and it did not fail loudly - it failed
            // cryptically: devthrottle_internal issue #1050 handed CreateProcess a bare "claude" that
            // was on no PATH and got back "CreateProcess failed." with no error code, no path, and
            // nothing for the person to act on. The sentence below names the agent, the command that
            // was tried, and what to do, and it travels all the way out as the caller's error.
            var resolvedExe = ExecutableResolver.Resolve(agent.ExecutablePath);
            if (resolvedExe is null)
            {
                var display = ToolDetectionService.DisplayName(agent.Kind);
                throw new InvalidOperationException(
                    $"{display} could not be started: the command \"{agent.ExecutablePath}\" is not a file on this " +
                    "machine and was not found on this Director's PATH. Set this agent's executable path in " +
                    $"Settings, Agents - or install {display} - and start the session again.");
            }
            if (!string.Equals(resolvedExe, agent.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                _log?.Invoke($"Resolved agent command '{agent.ExecutablePath}' to '{resolvedExe}'");

            var (launchExe, launchArgs) = CommandLineLauncher.Build(resolvedExe, args);
            if (!string.Equals(launchExe, resolvedExe, StringComparison.OrdinalIgnoreCase))
                _log?.Invoke($"Launching '{resolvedExe}' via shell: {launchExe} {launchArgs}");

            // The whole launch spec, in the log the person reads when a session will not start, and in
            // the line immediately before the attempt. Issue #1050 was diagnosed from the outside for
            // an hour without this: the working RawCli path printed its executable and the failing
            // Claude path printed none, so the one difference that mattered was the one thing invisible.
            // Variable NAMES only - the injected set carries agent credentials.
            _log?.Invoke($"Launching {agent.Kind}: exe={launchExe}, args={(string.IsNullOrEmpty(launchArgs) ? "(none)" : launchArgs)}, " +
                         $"workingDir={workingDirectory}, injectedEnv={string.Join(",", envVars.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}");
            session.LaunchExecutable = launchExe;

            // Reserve the worktree BEFORE the process starts (inspection round 5). The reserve-write is
            // serialized against the reaper's remove by a machine-wide lock, so a reaper can never
            // observe this running process without also observing its reservation - closing the
            // launch-versus-reap race. RaiseSessionCreated re-reserves (idempotent) for the other
            // creation routes (restore, web Control API) that do not pass through this launch path.
            // A pooled slot is NOT reserved here - cc-worktrees owns it and holds the lease, and the
            // reaper refuses to touch a pool slot on that evidence alone (WorktreeReservationStore.Reserve
            // skips one too, so this holds however a session reaches that call).
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                _reservations.Reserve(workingDirectory, id.ToString());

            // Get initial terminal dimensions (default 120x30)
            backend.Start(launchExe, launchArgs, workingDirectory, 120, 30, envVars);
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
            _log?.Invoke($"Session {id} created for repo {workingDirectory} (Agent={agent.Kind}, PID {backend.ProcessId}, Backend={backendType}{resumeInfo}{sessionIdInfo}).");

            return session;
        }
        catch (Exception ex)
        {
            session.MarkFailed();
            // Issue #1019, defect 3: a create that fails must SAY SO somewhere a person can find it later.
            // _log is optional - anything that constructs a SessionManager without one (the Control API's
            // own host among them) failed completely silently, which is why the original report could not
            // tell a failed spawn from a UI glitch after the fact. FileLog always lands in the Director log.
            FileLog.Write($"[SessionManager] CreateSession FAILED: session={id}, repo={repoPath}, agent={agent.Kind}, backend={backendType}: {ex.Message}");
            _log?.Invoke($"Failed to create session for {repoPath}: {ex.Message}");

            // Release the worktree reservation this create took out. It is reserved just BEFORE
            // backend.Start, but the matching release is wired in WireSessionReaper, which runs only from
            // RaiseSessionCreated - i.e. only once the session is already in the roster. A throw between
            // those two points therefore left a PERMANENT reservation held by a session id that no longer
            // exists anywhere, silently blocking the worktree reaper from ever cleaning that directory up.
            // Release is idempotent and keyed by session id, so this is safe whether or not we got as far
            // as reserving.
            _reservations.Release(id.ToString());

            // Give the pooled worktree back. This create took a slot out of the pool and is now not
            // going to use it; without this the slot would stay in-use, held by a session that never
            // existed, until somebody found it in `cc-worktrees list` and reclaimed it by hand. The
            // return runs the tool's full landed-work check like any other, so a slot with something in
            // it comes back HELD rather than being reset - which is right: an agent that got far enough
            // to write something wrote it.
            if (pooled is not null)
            {
                // Never let this throw over the exception that brought us here - that one is what the
                // caller needs to see and what says why the session did not start.
                try
                {
                    var given = _worktreePool.Return(pooled);
                    if (!given.Freed)
                        _log?.Invoke($"The pooled worktree {given.Path} was not returned and is held: {given.HeldReason}");
                }
                catch (Exception returnEx)
                {
                    FileLog.Write($"[SessionManager] returning the pooled worktree {pooled.Path} after a failed create FAILED: {returnEx.Message}");
                    _log?.Invoke($"The pooled worktree {pooled.Path} could not be returned: {returnEx.Message}");
                }
            }

            // End the Gateway key if one was minted before the throw. Idempotent and keyed by session id,
            // so it is safe whether or not we got as far as minting one.
            GatewaySessionCredentialRevoker?.Invoke(id);

            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The pooled worktree a new session in <paramref name="repoPath"/> runs in, or null when that
    /// repository's pooled-worktree setting is off - which is the default, and which means no tool is
    /// run and nothing about the create changes.
    ///
    /// A refusal from cc-worktrees throws, in the tool's own words, so the create ends and no session
    /// opens. THERE IS NO FALLBACK to the shared checkout: the setting is on precisely because sessions
    /// in this repository are not to run there, and a silent fallback would break that without saying so.
    /// </summary>
    /// <summary>
    /// Take back a slot this seat already holds, after a Director restart. NO TOOL IS RUN.
    ///
    /// There is nothing to ask cc-worktrees for: the slot is still in use in its pool, the lease is
    /// still the one it will accept, and there is no second lease to be had for a slot in use - a
    /// <c>lease</c> call on one is refused, by design. So the only thing that can put this session
    /// back in charge of its own slot is the lease it was given at birth, which is why it is
    /// persisted.
    ///
    /// Nothing is forced here, and nothing may ever be: this method never reclaims, never releases,
    /// never resets and never destroys. A slot whose holder is gone for good stays exactly as it is
    /// until a person decides otherwise with the tool's own <c>lease --reclaim-held</c> or
    /// <c>release</c>, which are the commands that say out loud what is being given up.
    /// </summary>
    private Git.PooledWorktree ReattachPooledWorktree(Git.PooledWorktree pooled, Guid id, string? birthName)
    {
        var holder = string.IsNullOrWhiteSpace(birthName) ? id.ToString() : $"{birthName.Trim()} ({id})";
        FileLog.Write($"[SessionManager] ReattachPooledWorktree: session={id}, slot={pooled.Slot}, " +
                      $"path={pooled.Path}, repo={pooled.Repo}, holder={holder}; no tool was run and no new slot was taken");
        _log?.Invoke($"This session takes back the pooled worktree {pooled.Path} (slot {pooled.Slot} of {pooled.Repo}) it already held.");
        return pooled;
    }

    private Git.PooledWorktree? AcquirePooledWorktree(string repoPath, Guid id, string? birthName)
    {
        Git.WorktreePoolSetting setting;
        try
        {
            setting = _worktreePoolSetting(repoPath);
        }
        catch (Exception ex)
        {
            // The setting could not be read. OFF is the only safe reading of "I do not know", and it is
            // also the default, so this session behaves exactly as it did before the setting existed.
            FileLog.Write($"[SessionManager] AcquirePooledWorktree: could not read the setting for {repoPath}: {ex.Message}; treating it as off");
            return null;
        }

        if (!setting.Enabled)
            return null;

        // Who holds the slot, as `cc-worktrees list` will show it: the name the session is born with
        // when it has one, and its identifier either way so one slot maps to exactly one session.
        var holder = string.IsNullOrWhiteSpace(birthName) ? id.ToString() : $"{birthName.Trim()} ({id})";

        FileLog.Write($"[SessionManager] AcquirePooledWorktree: repo={repoPath}, poolSize={setting.PoolSize}, holder={holder}");
        try
        {
            var pooled = _worktreePool.Get(repoPath, holder, setting.PoolSize);
            _log?.Invoke($"This session runs in the pooled worktree {pooled.Path} (slot {pooled.Slot} of {repoPath}).");
            return pooled;
        }
        catch (Git.CcWorktreesRefusedException ex)
        {
            // The tool's own sentence, unchanged, plus its own "what to run next" lines. Nothing here
            // paraphrases it: it is the only account of what happened, and it is written for a person.
            var help = ex.Help.Count == 0 ? "" : " Try: " + string.Join("; ", ex.Help);
            var message = $"No session was opened. {repoPath} is set to run its sessions in a pooled worktree, "
                + $"and cc-worktrees refused: {ex.Message}.{help}";
            FileLog.Write($"[SessionManager] AcquirePooledWorktree REFUSED: code={ex.Code}, exit={ex.ExitCode}, message={ex.Message}");
            _log?.Invoke(message);
            throw new InvalidOperationException(message, ex);
        }
    }

    /// <summary>
    /// Give a closing session's pooled worktree back, and say whether cc-worktrees took it.
    ///
    /// Returns null when the session had no pooled worktree (every session, by default). Otherwise it
    /// returns the tool's answer: freed, or held with the tool's own reason. NOTHING IS FORCED HERE -
    /// there is no retry with a stronger flag and no destroy. A held slot keeps whatever is in it.
    /// </summary>
    private Git.PooledWorktreeReturn? ReturnPooledWorktree(Session session)
    {
        if (session.PooledWorktree is not { } pooled)
            return null;

        FileLog.Write($"[SessionManager] ReturnPooledWorktree: session={session.Id}, slot={pooled.Slot}, path={pooled.Path}");
        try
        {
            var answer = _worktreePool.Return(pooled);
            if (answer.Freed)
            {
                session.PooledWorktreeHeldReason = null;
                _log?.Invoke($"The pooled worktree {answer.Path} was returned to the pool.");
            }
            else
            {
                session.PooledWorktreeHeldReason = answer.HeldReason;
                _log?.Invoke($"The pooled worktree {answer.Path} was NOT returned and is held: {answer.HeldReason}");
            }
            return answer;
        }
        catch (Exception ex)
        {
            // The tool could not be asked at all. That is not "the slot came back" - it is not knowing,
            // and the slot stays in-use in the pool with this session named as its holder. Recording it
            // as held is the answer that loses nothing.
            var reason = $"cc-worktrees could not be asked to take {pooled.Path} back: {ex.Message}";
            FileLog.Write($"[SessionManager] ReturnPooledWorktree FAILED: session={session.Id}: {ex}");
            _log?.Invoke(reason);
            session.PooledWorktreeHeldReason = reason;
            return new Git.PooledWorktreeReturn(pooled.Slot, pooled.Path, Freed: false, HeldReason: reason);
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
    /// using the github-token entry from the cc-secrets store (read at point of use). Tests pass a stub.
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

    // ResolveLocalRole WAS HERE, AND IS DELETED - DO NOT BRING IT BACK (gap 1).
    //
    // It resolved a session's role from THIS Director's local roster, mirroring the Gateway's fleet
    // resolver. Its single caller was MainWindow.RecomputeGroupPositions, which stamped the rail's role
    // glyph from it; that caller is gone, because the glyph now derives from Session.GatewayResolvedRole
    // like the colour always did.
    //
    // It is deleted rather than left for a future caller because it was WRONG BY CONSTRUCTION, not merely
    // unused. "Is this session's controller still alive?" cannot be answered from one Director - the
    // controller is frequently a session on another machine, and this method scored those sessions
    // Standalone with total confidence. That is why the Gateway resolves the role across the whole fleet
    // and stamps it down (Session.GatewayResolvedRole, set-resolved-role verb); a local mirror of a
    // fleet-wide question is a second answer that disagrees, which is this mission's entire defect class.
    // Session.cs already carries the standing instruction not to assign the stamp from this resolver.
    //
    // Nothing needs it now: no caller, no reflective use, and the compiler is the proof.

    /// <summary>
    /// Kill a session by ID. <paramref name="gracefulTimeoutMsOverride"/> (issue: faster STOP) lets the
    /// FLEET stop path escalate to force sooner than the local desktop window: a positive value is the
    /// graceful-wait in milliseconds; null (the default, used by every local caller) keeps the standard
    /// <see cref="AgentOptions.GracefulShutdownTimeoutSeconds"/> window, byte-identical to before.
    /// </summary>
    public async Task KillSessionAsync(Guid id, int? gracefulTimeoutMsOverride = null)
    {
        if (!_sessions.TryGetValue(id, out var session))
            throw new KeyNotFoundException($"Session {id} not found.");

        var timeoutMs = gracefulTimeoutMsOverride is int o && o > 0
            ? o
            : _options.GracefulShutdownTimeoutSeconds * 1000;
        await session.KillAsync(timeoutMs);
    }

    /// <summary>
    /// The graceful-shutdown window (milliseconds) the FLEET/remote stop path uses before force-killing.
    /// Resolves <see cref="AgentOptions.FleetKillGraceMs"/>; a null or non-positive config disables the fast
    /// path, falling back to the standard <see cref="AgentOptions.GracefulShutdownTimeoutSeconds"/> window
    /// (so a disabled fast path is byte-identical to before). The LOCAL desktop kill never reads this.
    /// </summary>
    public int FleetKillGraceMs =>
        _options.FleetKillGraceMs is int ms && ms > 0 ? ms : _options.GracefulShutdownTimeoutSeconds * 1000;

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

    /// <summary>
    /// Remove a session from tracking (dispose and clean up). Returns true when the row was removed.
    ///
    /// IT CAN NOW DECLINE, and there is exactly one reason: the session was running in a pooled
    /// worktree and cc-worktrees would not take that worktree back. The slot keeps whatever is in it -
    /// an unpushed commit, an uncommitted edit - and the row stays with the tool's reason on it
    /// (<see cref="Session.PooledWorktreeHeldReason"/>) so the person who closed the session can see
    /// what is being kept and act on it. A SECOND close removes the row anyway: by then the reason has
    /// been shown and asking again is the person saying so. The slot stays held in the pool either way -
    /// nothing here forces, retries with a stronger flag, or destroys anything - and
    /// <c>cc-worktrees list</c> is where it is answered for afterwards.
    ///
    /// For every session with no pooled worktree, which is every session by default, this is exactly
    /// what it always was and it always returns true.
    /// </summary>
    public bool RemoveSession(Guid id)
    {
        // The return runs BEFORE the row is taken out of the roster, so a row that is kept was never
        // missing from it, and so the session is still there to carry the reason.
        if (_sessions.TryGetValue(id, out var closing) && closing.PooledWorktree is not null)
        {
            var alreadyToldOnce = closing.PooledWorktreeHeldReason is not null;
            var answer = ReturnPooledWorktree(closing);
            if (answer is { Freed: false } && !alreadyToldOnce)
            {
                _log?.Invoke($"Session {id} was not removed: its pooled worktree is held ({answer.HeldReason}). Close it again to remove the row anyway; the worktree stays held either way.");
                FileLog.Write($"[SessionManager] RemoveSession: session={id} row KEPT, pooled worktree held: {answer.HeldReason}");
                return false;
            }
        }

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

            // Issue #820: return the session's three-digit number to the LOCAL pool so it is no longer
            // reported as in use and a later session can reuse it. Issue #1292: also tell the Gateway
            // (the fleet authority) to free it so it can be reused across the fleet.
            if (session.Number is int number)
                _numberAllocator.Release(number);
            FleetNumberRelease?.Invoke(id);

            session.Dispose();
            _log?.Invoke($"Session {id} removed.");
            return true;
        }

        return false;
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
        // Automatic session roles (chunk 3): an explicit rename is a human/self name - it wins and must
        // never be re-auto-named, so clear the auto-named marker.
        session.IsAutoNamed = false;
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
                Number = s.Number,
                PendingPromptText = s.PendingPromptText,
                PendingPromptSpokenSpans = s.PendingPromptSpokenSpans.Count == 0
                    ? null
                    : s.PendingPromptSpokenSpans.Select(span => new PersistedSpokenSpan { Start = span.Start, Length = span.Length }).ToList(),
                EmbeddedProcessId = s.ProcessId,
                ConsoleHwnd = getHwnd != null && s.BackendType == SessionBackendType.Embedded ? getHwnd(s.Id) : 0,
                ClaudeSessionId = s.ClaudeSessionId,
                ActivityState = s.ActivityState,
                // Defect 22: a snooze must survive a Director restart.
                CreatedAt = s.CreatedAt,
                SortOrder = s.SortOrder,
                ExpectedFirstPrompt = s.ExpectedFirstPrompt ?? s.VerifiedFirstPrompt,
                HistoryEntryId = s.HistoryEntryId,
                BackendType = s.BackendType,
                AgentKind = s.AgentKind,
                GroupId = s.GroupId,
                GroupRole = s.GroupRole,
                GroupName = s.GroupName,
                ControllerSessionId = s.ControllerSessionId,
                ExplicitRole = s.ExplicitRole,
                // Birth facts (issue #982): unrecoverable once lost, so they ride every snapshot.
                OriginKind = s.OriginKind,
                OriginSurface = s.OriginSurface,
                ParentSessionId = s.ParentSessionId,
                IsAutoNamed = s.IsAutoNamed,
                MissionId = s.MissionId,
                MissionName = s.MissionName,
                WorkflowRunId = s.WorkflowRunId,
                WorkflowId = s.WorkflowId,
                WorkflowVersion = s.WorkflowVersion,
                RawStartupText = s.RawStartupText,
                SelectedTabName = s.SelectedTabName,
                WingmanEnabled = s.WingmanEnabled,
                // The pooled worktree, WITH ITS LEASE. Without this a Director restart loses the only
                // evidence cc-worktrees accepts for giving the slot back, and the slot stays in use
                // under a holder that no longer exists.
                PooledWorktree = s.PooledWorktree is { } pooled
                    ? new PersistedPooledWorktree
                    {
                        Repo = pooled.Repo,
                        Slot = pooled.Slot,
                        Path = pooled.Path,
                        Lease = pooled.Lease,
                    }
                    : null,
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

    /// <summary>
    /// The pooled worktree a persisted record names, or null when it names none or names one only
    /// partly. A partial record is DROPPED and logged: the missing value is always the one that
    /// matters, and a slot without its lease is a slot this Director cannot give back.
    /// </summary>
    private Git.PooledWorktree? ToPooledWorktree(PersistedPooledWorktree? persisted, Guid sessionId)
    {
        if (persisted is null)
            return null;

        if (string.IsNullOrWhiteSpace(persisted.Repo) || string.IsNullOrWhiteSpace(persisted.Slot)
            || string.IsNullOrWhiteSpace(persisted.Path) || string.IsNullOrWhiteSpace(persisted.Lease))
        {
            FileLog.Write($"[SessionManager] ToPooledWorktree: session={sessionId} carries an incomplete " +
                          $"pooled worktree (repo=\"{persisted.Repo}\", slot=\"{persisted.Slot}\", " +
                          $"path=\"{persisted.Path}\", lease present={!string.IsNullOrWhiteSpace(persisted.Lease)}); " +
                          "it is not attached, so the slot stays in use in the pool and cc-worktrees is the way out");
            return null;
        }

        FileLog.Write($"[SessionManager] ToPooledWorktree: session={sessionId} re-attached to slot={persisted.Slot} at {persisted.Path}");
        return new Git.PooledWorktree(persisted.Repo, persisted.Slot, persisted.Path, persisted.Lease);
    }

    /// <summary>Restore a single persisted embedded session into tracking.
    /// The WPF layer must provide the reattached backend.</summary>
    public Session RestoreEmbeddedSession(PersistedSession ps, ISessionBackend embeddedBackend)
    {
        var session = new Session(
            ps.Id, ps.RepoPath, ps.WorkingDirectory, ps.ClaudeArgs,
            embeddedBackend, ps.ClaudeSessionId, ps.ActivityState, ps.CreatedAt,
            ps.CustomName, ps.CustomColor, ps.PendingPromptText);

        // The dictated ranges of the pending text (ruling R20), after the text that was set in the constructor.
        if (ps.PendingPromptSpokenSpans is { Count: > 0 } spans)
            session.PendingPromptSpokenSpans = spans.Select(span => new SpokenTurnRule.SpokenSpan(span.Start, span.Length)).ToList();
        session.AgentKind = ps.AgentKind;
        session.GroupId = ps.GroupId;
        session.GroupRole = ps.GroupRole;
        session.GroupName = ps.GroupName;
        session.ControllerSessionId = ps.ControllerSessionId;
        session.ExplicitRole = ps.ExplicitRole;
        // Birth facts (issue #982). Composed, not assigned: a snapshot written before these fields
        // existed carries nulls, and the composer turns those into the honest "unknown" rather than
        // leaving the session claiming an origin it never had.
        session.StampOrigin(SessionOrigin.Compose(ps.OriginKind, ps.OriginSurface, ps.ParentSessionId));
        session.IsAutoNamed = ps.IsAutoNamed;
        session.MissionId = ps.MissionId;
        session.MissionName = ps.MissionName;
        session.WorkflowRunId = ps.WorkflowRunId;
        session.WorkflowId = ps.WorkflowId;
        session.WorkflowVersion = ps.WorkflowVersion;
        session.WingmanEnabled = ps.WingmanEnabled;
        // No hold is restored here, because this Director never owned one. The Gateway holds the state and
        // persists it (SnoozeRegistry, an atomic write-through on every mutation), and it pushes the hold
        // back down to this session's display mirror as soon as this Director reconnects and it folds the
        // roster. Restoring a hold from local disk would be a second copy of the fact, racing the Gateway's
        // - which is what defect 22 was.
        //
        // This is strictly better than what it replaces: a hold now survives a Director restart even if
        // this Director never comes back at all, which is the whole reason the state moved.
        // Issue #820: carry the persisted three-digit number in BEFORE RaiseSessionCreated so
        // AssignSessionNumber reserves this exact number (keeping it across a restart) when it is
        // still free, or backfills a fresh one when this session had none / it collides.
        session.Number = ps.Number;

        // THE POOLED WORKTREE IS RE-ATTACHED, NOT RE-TAKEN. This session already holds a slot; the
        // lease it was given at birth is still the one cc-worktrees will accept, and the pool still
        // has the slot in use. Taking a NEW slot here would leave the old one in use under a holder
        // that no longer exists AND consume a second slot, so a few restarts would fill the pool.
        //
        // An incomplete record is dropped rather than half-attached: a slot named without its lease
        // is one this Director cannot give back, and carrying it would make the session look properly
        // restored while close silently did nothing.
        session.PooledWorktree = ToPooledWorktree(ps.PooledWorktree, ps.Id);

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
        _deletionReaper.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
        _claudeSessionMap.Clear();
    }
}
