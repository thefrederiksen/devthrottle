using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Terminal.Core;
using CcDirector.Terminal.Core.Rendering;

namespace CcDirector.Core.Sessions;

public enum SessionStatus
{
    Starting,
    Running,
    Exiting,
    Exited,
    Failed
}

/// <summary>
/// Which mobile view (if any) a phone is currently watching this session through. Set by the
/// active mobile tab via the Control API. The wingman keys its remark STYLE off this.
/// </summary>
public enum MobileViewMode
{
    /// <summary>No phone is watching remotely (desktop, or the phone navigated away).
    /// No proactive briefings; the wingman writes normal text remarks.</summary>
    Off,

    /// <summary>Phone is on the Session (text) tab. Proactive briefings on; normal text remarks.</summary>
    Text,

    /// <summary>Phone is on the Voice (in-car) tab. Proactive briefings on; the wingman writes
    /// spoken-friendly remarks for hands-free / driving use.</summary>
    Voice
}

/// <summary>
/// Status of terminal-based verification (matching terminal content to .jsonl files).
/// </summary>
public enum TerminalVerificationStatus
{
    /// <summary>Waiting - no match found yet.</summary>
    Waiting,
    /// <summary>Potential match found but not yet confirmed (< 50 lines).</summary>
    Potential,
    /// <summary>Matched - terminal content confirmed (50+ lines).</summary>
    Matched,
    /// <summary>Failed - could not find a matching .jsonl file after 50+ lines.</summary>
    Failed
}

/// <summary>
/// Result of verifying a session by matching terminal content to .jsonl files.
/// </summary>
public sealed class TerminalVerificationResult
{
    public bool IsMatched { get; init; }
    public bool IsPotential { get; init; }
    public string? MatchedSessionId { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// One styled run within a snapshot screen row: a stretch of characters sharing the same
/// colours and weight. <see cref="Fg"/>/<see cref="Bg"/> are "#RRGGBB" strings, or null when
/// the cell carried no explicit colour (the consumer renders that with its default brush).
/// Produced by <see cref="Session.SnapshotScreenColoredRows"/> so the captured terminal can be
/// reproduced in colour, not flattened to monochrome text.
/// </summary>
public sealed record ScreenSegment(string Text, string? Fg, string? Bg, bool Bold);

/// <summary>
/// Represents a single Claude session. Delegates process management to an ISessionBackend.
/// Session handles metadata, activity state, and routing - backend handles process I/O.
/// </summary>
public sealed class Session : IDisposable
{
    /// <summary>Minimum length of first prompt required for verification (avoid verifying too early).</summary>
    public const int MinVerificationLength = 50;

    private readonly ISessionBackend _backend;
    private bool _disposed;

    // ===== HTML-view terminal emulator =====
    // The Avalonia terminal owns its own AnsiParser bound to the live window
    // size. The HTML "Raw terminal" tab needs an independent emulator with a
    // fixed grid (browser-side resize must not perturb ConPty width). We feed
    // it from the buffer's OnBytesWritten event, gated by _htmlParserLock so
    // request threads can take snapshots concurrently.
    private const int HtmlGridCols = 220;
    private const int HtmlGridRows = 40;
    private const int HtmlMaxScrollback = 5000;
    private readonly object _htmlParserLock = new();
    private TerminalCell[,]? _htmlCells;
    private List<TerminalCell[]>? _htmlScrollback;
    private AnsiParser? _htmlParser;
    private Action<byte[]>? _htmlParserFeed;

    // ===== Live-attach terminal emulator (the WebSocket stream's attach snapshot) =====
    // A SECOND authoritative parser, fed the same bytes from byte 0 but sized to track the REAL
    // PTY geometry (unlike the fixed-grid _htmlParser above). A freshly-attaching browser client
    // cannot rebuild a long session's screen from a mid-stream byte slice (relative cursor moves and
    // scrolls land on a baseline the slice never established, so an incrementally-repainting agent
    // like Codex reconstructs torn). This parser always holds the correct current screen, so the
    // stream endpoint serializes it into a self-contained "prime" frame on attach - the reattach
    // strategy tmux/mosh use. Kept separate from _htmlParser so its geometry tracking cannot perturb
    // the Cockpit "Raw terminal" tab or the Wingman screen detection that read _htmlParser. Fed and
    // read under the same _htmlParserLock so both parsers and the snapshot are always consistent.
    private const int StreamMaxScrollback = 5000;
    private TerminalCell[,]? _streamCells;
    private List<TerminalCell[]>? _streamScrollback;
    private AnsiParser? _streamParser;
    private int _streamGridCols;
    private int _streamGridRows;
    // Total bytes the live-attach parser has consumed. Captured with the snapshot (under the same
    // lock) so the stream endpoint resumes live output at EXACTLY the byte the snapshot reflects -
    // no gap (missing bytes) and no overlap (double-applied bytes). Equals the buffer's absolute
    // position because the parser is subscribed from session start, before any output.
    private long _streamBytesReflected;

    public SessionBackendType BackendType { get; }

    /// <summary>True when this session is a GitHub Actions remote session.</summary>
    public bool IsRemote => BackendType == SessionBackendType.GitHubActions;

    /// <summary>Remote repo slug ("owner/repo") for GitHub Actions sessions, else null.</summary>
    public string? RemoteRepo => (_backend as GitHubActionsBackend)?.RepoSlug;

    /// <summary>Web URL of the issue/PR thread for GitHub Actions sessions, else null.</summary>
    public string? RemoteThreadUrl => (_backend as GitHubActionsBackend)?.ThreadUrl;

    /// <summary>Web URL of the most recent workflow run for GitHub Actions sessions, else null.</summary>
    public string? RemoteRunUrl => (_backend as GitHubActionsBackend)?.CurrentRunUrl;

    /// <summary>Last observed run status for GitHub Actions sessions, else null.</summary>
    public string? RemoteRunStatus => (_backend as GitHubActionsBackend)?.RunStatus;

    /// <summary>Which agent CLI this session is running (Claude Code, Pi, etc).
    /// Defaults to ClaudeCode for sessions created via legacy code paths.</summary>
    public AgentKind AgentKind { get; internal set; } = AgentKind.ClaudeCode;

    /// <summary>
    /// The executable this session was actually launched with - the resolved path handed to
    /// CreateProcess, after the agent entry's recorded path and any batch-shim wrapping have been
    /// applied. Null for sessions that spawn no local process (embedded test backends, remote
    /// backends).
    ///
    /// Recorded because it was not (devthrottle_internal issue #1050): a clean machine launched a
    /// bare "claude" instead of the absolute path its own wizard had just recorded, and neither the
    /// log nor the session said which executable had been tried, so the failure was
    /// indistinguishable from a broken pseudo-console. This is the launch fact a test can pin and an
    /// operator can read.
    /// </summary>
    public string? LaunchExecutable { get; internal set; }

    /// <summary>
    /// The unguessable half of this session's pointer-drop file name, minted once per session and
    /// never persisted. The Director stamps the full drop path (id dot token) into the session's
    /// environment, and <see cref="SessionPointerWatcher"/> refuses any drop whose name does not
    /// carry this exact value - so writing a drop for a session requires having been HANDED that
    /// session's path, not merely being able to spell its id. This is the session-bound limit the
    /// deleted claude-hook route's credential gave, rebuilt for the drop box.
    /// </summary>
    public string PointerDropToken { get; } = SessionHookFiles.NewDropToken();

    /// <summary>If this session was created as part of a group (issue #225), the shared
    /// group identity its members travel by; null for a solo session. Members of the same
    /// group sort adjacently and drag as one unit. Stamped at creation, immutable.</summary>
    public Guid? GroupId { get; internal set; }

    /// <summary>The session's role within its group (issue #225), e.g. "Submitter",
    /// "Implementer", "QA" - a descriptive label. Null for a solo session.</summary>
    public string? GroupRole { get; internal set; }

    /// <summary>The group's display name (issue #225), e.g. "Product" - shown in the desktop
    /// group header. Same for every member of a group; null for a solo session.</summary>
    public string? GroupName { get; internal set; }

    /// <summary>When this session was spawned to be controlled by ANOTHER session (issue #815) -
    /// a "Supporting" sub-agent - the id of the controlling session; null for a normal session.
    /// Set ONLY at birth and immutable afterwards (stamped by the create/restore paths, like
    /// <see cref="GroupId"/>). Drives the recessive "Supporting" status color, which is honored
    /// only while the controlling session still exists; a red "needs you" still breaks through.</summary>
    public Guid? ControllerSessionId { get; internal set; }

    /// <summary>True when this session is a controlled sub-agent (issue #815) - it carries a
    /// <see cref="ControllerSessionId"/>. Whether the recessive "Supporting" color is actually
    /// painted also depends on the controller still being alive; see the SessionStatusWingman.</summary>
    public bool IsControlled => ControllerSessionId.HasValue;

    /// <summary>
    /// WHO asked for this session to exist (devthrottle_internal issue #982) - one of the
    /// <see cref="SessionOriginKinds"/> values. A BIRTH FACT: stamped by the create path and never
    /// changed, so it keeps describing the create call however the session later behaves. Persisted, so
    /// a Director restart does not turn a known origin into an unknown one.
    ///
    /// Defaults to <see cref="SessionOriginKinds.Unknown"/> rather than to <c>human</c>. The number this
    /// field exists to produce is the share of sessions AGENTS start; defaulting the unstated case to
    /// either real value would bias exactly that number, in a way no later reader could detect.
    /// </summary>
    public string OriginKind { get; internal set; } = SessionOriginKinds.Unknown;

    /// <summary>WHERE the create call came from (issue #982) - one of the
    /// <see cref="SessionOriginSurfaces"/> values. A birth fact beside <see cref="OriginKind"/>, on the
    /// same terms: stamped once, persisted, unknown when unstated.</summary>
    public string OriginSurface { get; internal set; } = SessionOriginSurfaces.Unknown;

    /// <summary>
    /// The session that asked for this one (issue #982), or null when nothing did. This is the LINEAGE
    /// edge: with it a roster of twenty-two sessions resolves into the handful of operations it actually
    /// is, and delegation depth and runaway-parent detection become answerable at all.
    ///
    /// DISTINCT FROM <see cref="ControllerSessionId"/>, which is a live supervision relationship that
    /// changes how the session is PAINTED (a controlled sub-agent recedes to slate while its controller
    /// lives). This one is a historical fact about who made the create call and affects no display at
    /// all. They coincide on the common CLI spawn and diverge whenever a session spawns a deliberate
    /// peer (<c>--standalone</c>): that peer has no controller and must stay human-facing, but an agent
    /// still started it, and that is precisely the thing #982 exists to count.
    /// </summary>
    public Guid? ParentSessionId { get; internal set; }

    /// <summary>The three birth facts read back together. Stamped through
    /// <see cref="StampOrigin"/>.</summary>
    public SessionOrigin Origin => new(OriginKind, OriginSurface, ParentSessionId);

    /// <summary>
    /// Stamp the birth facts (issue #982). Called once by the create path BEFORE launch, and by the
    /// restore path carrying the persisted values back. Composed through
    /// <see cref="SessionOrigin.Compose"/>, so an unknown token lands as unknown rather than as a
    /// plausible-looking lie, and a parent id never survives on a non-agent origin.
    /// </summary>
    public void StampOrigin(SessionOrigin origin)
    {
        var composed = SessionOrigin.Compose(origin.Kind, origin.Surface, origin.ParentSessionId);
        OriginKind = composed.Kind;
        OriginSurface = composed.Surface;
        ParentSessionId = composed.ParentSessionId;
        FileLog.Write($"[Session] {Id} origin stamped: kind={OriginKind} surface={OriginSurface} parent={ParentSessionId?.ToString() ?? "(none)"}");
    }

    /// <summary>
    /// The sticky EXPLICIT role a human/session declared for this session (automatic session roles), or null
    /// for none. When set it WINS over the Gateway's auto-derivation of the role - the only way to be an
    /// Architect, which cannot be inferred from the spawn graph. Settable at birth (from the create request)
    /// and later via the set-role verb (<see cref="SetExplicitRole"/>); a null/blank value clears it. One of
    /// the SessionRoles values (validated by the caller). Persisted so it survives a Director restart.
    /// </summary>
    public string? ExplicitRole { get; internal set; }

    /// <summary>
    /// Defect 5: the RESOLVED role, as computed by the GATEWAY from the whole fleet and stamped down onto
    /// this Director over the tunnel. Null until a Gateway has said otherwise.
    ///
    /// THE DIRECTOR CARRIES THIS; IT NEVER COMPUTES IT. This is a Gateway-owned fact being cached so the
    /// desktop rail can fold the SAME role the phone and the Cockpit fold - nothing more. "Is this session's
    /// controller still alive?" is unanswerable from one Director (the controller may be a session on
    /// another machine), which is why the answer must arrive rather than be derived. Written ONLY by the
    /// <c>set-resolved-role</c> verb; read back out by <c>ControlEndpoints.Map</c> onto
    /// <c>SessionDto.SessionRole</c>.
    ///
    /// DO NOT assign this from <c>SessionManager.ResolveLocalRole</c>. That resolver sees only the local
    /// roster, so it is wrong for exactly the cross-machine case this field exists to serve, and wiring it
    /// in would make the Director decide a colour input - which law 2 forbids and which re-opens the whole
    /// defect class. It is deliberately NOT persisted: a restarted Director has no business remembering a
    /// fact it never owned, and the Gateway re-stamps within one push of reconnecting.
    /// (docs/new_architecture/sessions.html, defect 5.)
    /// </summary>
    public string? GatewayResolvedRole { get; private set; }

    /// <summary>
    /// The Gateway's answer to "is a supervisor alive right now?" for this session, cached on the same terms
    /// as <see cref="GatewayResolvedRole"/> and delivered by the same <c>set-resolved-role</c> verb; read
    /// back out by <c>ControlEndpoints.Map</c> onto <c>SessionDto.HasLiveSupervisor</c>.
    ///
    /// A Director cannot compute this - the supervising session is routinely on another machine - so like
    /// the role it is stored verbatim and never derived locally. Not persisted, for the same reason: a
    /// restarted Director has no business remembering a fact it never owned, and false is the safe value to
    /// come back with, because false means the session surfaces to the owner.
    /// </summary>
    public bool GatewayResolvedHasLiveSupervisor { get; private set; }

    /// <summary>
    /// Store the role the Gateway resolved for this session (defect 5). A null/blank value clears the stamp
    /// back to "no answer". This ONLY stores - it does not validate, adjust, or derive: the Gateway is the
    /// authority and this is the cache.
    /// </summary>
    public void SetGatewayResolvedRole(string? role, bool hasLiveSupervisor = false)
    {
        var normalized = string.IsNullOrWhiteSpace(role) ? null : role.Trim();
        var unchanged = string.Equals(GatewayResolvedRole, normalized, StringComparison.Ordinal)
                        && GatewayResolvedHasLiveSupervisor == hasLiveSupervisor;
        if (unchanged) return;
        FileLog.Write($"[Session] SetGatewayResolvedRole: session={Id}, role={normalized ?? "(cleared)"}, " +
                      $"liveSupervisor={hasLiveSupervisor}");
        GatewayResolvedRole = normalized;
        // BOTH FACTS ARE GUARDED BY ONE EQUALITY CHECK ABOVE. A supervisor dying does not change the seat,
        // so a role-only guard would swallow the change that matters most - the session would stay quiet on
        // the desktop with nobody holding it, which is the defect this whole rule exists to close.
        GatewayResolvedHasLiveSupervisor = hasLiveSupervisor;
        // Fires only on a real change (the equality guard above returns first otherwise), so the Gateway
        // re-stamping the same role every sweep does not churn the rail.
        try { OnGatewayResolvedRoleChanged?.Invoke(normalized); }
        catch (Exception ex) { FileLog.Write($"[Session] {Id} OnGatewayResolvedRoleChanged handler threw: {ex.Message}"); }
    }

    /// <summary>
    /// Raised when <see cref="GatewayResolvedRole"/> changes, so a view can re-read the fold. Carries the
    /// new value.
    ///
    /// WITHOUT THIS THE WHOLE DEFECT 5 DELIVERABLE DOES NOT WORK, and it shipped without it. The role
    /// reaches the Director and the fold reads it correctly - but the desktop rail is only told to re-read
    /// on activity, status, hold, dictation, number and pending-deletion changes. A role arriving is none
    /// of those, so a controlled Worker stayed visibly RED and counted in "needs you" until some unrelated
    /// event happened to fire, which is the exact disagreement this was built to end. The mapper tests
    /// passed throughout: they read the fold, and reading is not rendering.
    ///
    /// Same shape as <see cref="OnPendingDeletionChanged"/>, added one commit earlier in this same mission
    /// for the same reason - a new fact with no signal is invisible. Report what changed; decide nothing.
    /// </summary>
    public event Action<string?>? OnGatewayResolvedRoleChanged;

    // ===== Gateway-pushed DISPLAY STATE (the fold answer), cached so the desktop rail renders exactly what
    // the Gateway decided instead of re-folding from local facts it cannot see (dictation, transcription,
    // voice generation, the snooze clock). THE GATEWAY DECIDES; THE DIRECTOR CARRIES. Written only by the
    // set-display-state verb, read back out through ControlEndpoints.Map onto the SessionDto. Deliberately
    // NOT persisted - a restarted Director has no business remembering a fold it never owned, and the
    // Gateway re-stamps within one push of reconnecting (same rule as GatewayResolvedRole).
    // (docs/new_architecture/sessions.html - "the desktop must ask".) =====

    /// <summary>The Gateway's folded effective color (<see cref="SessionOrdering.EffectiveColor"/>), or null
    /// until a Gateway has stamped one. The rail renders this verbatim - it does not compute a colour.</summary>
    public string? GatewayEffectiveColor { get; private set; }

    /// <summary>The Gateway's folded human-readable state label ("Working" / "Needs you" / "Snoozed" / ...),
    /// or null until stamped. The rail renders this verbatim.</summary>
    public string? GatewayStateLabel { get; private set; }

    /// <summary>The Gateway's folded triage bucket ("needsYou" / "active" / "onHold"), or null until stamped.
    /// The rail's "needs you" count and ordering read this, never a local classification.</summary>
    public string? GatewayTriageBucket { get; private set; }

    /// <summary>The Gateway-owned instant this session entered red (<see cref="SessionDto.NeedsYouSince"/>),
    /// so the rail's "waiting 11m" matches every other surface. Null when not red.</summary>
    public DateTime? GatewayNeedsYouSince { get; private set; }

    /// <summary>The Gateway-owned armed-snooze deadline (<see cref="SessionDto.SnoozeUntil"/>), so the rail
    /// can show "Snoozed - wakes in 3h 48m". Null when there is no running snooze clock.</summary>
    public DateTime? GatewaySnoozeUntil { get; private set; }

    /// <summary>The Gateway's "this session JUST came back from an expired snooze" marker
    /// (<see cref="SessionDto.SnoozeExpired"/>), rendered as a distinct "Snooze ended" badge.</summary>
    public bool GatewaySnoozeExpired { get; private set; }

    /// <summary>The Gateway's row line (<see cref="SessionDto.InboxLine"/>): what waits in this session's fleet
    /// inbox - "2 messages waiting" - or null when nothing does. The rail renders this verbatim; it never counts
    /// messages itself.</summary>
    public string? GatewayInboxLine { get; private set; }

    /// <summary>
    /// Raised when any pushed display-state field changes, so the desktop rail re-reads the fold. Same shape
    /// and same reason as <see cref="OnGatewayResolvedRoleChanged"/>: a new fact with no signal is invisible -
    /// the rail is only told to re-read on activity/status/hold/dictation/number/role changes, and a fold
    /// answer arriving over the wire is none of those.
    /// </summary>
    public event Action? OnGatewayDisplayStateChanged;

    /// <summary>
    /// Store the display state the Gateway folded for this session. This ONLY caches values it was told - it
    /// does not compute, validate, or adjust the fold; the Gateway is the authority. Fires
    /// <see cref="OnGatewayDisplayStateChanged"/> only on a real change, so the Gateway re-stamping the same
    /// answer every sweep does not churn the rail. A null <paramref name="effectiveColor"/> clears the stamp
    /// back to "no answer" (the desktop then shows its neutral waiting-for-gateway placeholder).
    /// </summary>
    public void ApplyGatewayDisplayState(
        string? effectiveColor,
        string? stateLabel,
        string? triageBucket,
        DateTime? needsYouSince,
        DateTime? snoozeUntil,
        bool snoozeExpired,
        string? inboxLine = null)
    {
        var color = string.IsNullOrWhiteSpace(effectiveColor) ? null : effectiveColor.Trim();
        var label = string.IsNullOrWhiteSpace(stateLabel) ? null : stateLabel.Trim();
        var bucket = string.IsNullOrWhiteSpace(triageBucket) ? null : triageBucket.Trim();
        var inbox = string.IsNullOrWhiteSpace(inboxLine) ? null : inboxLine;

        var changed =
            !string.Equals(GatewayEffectiveColor, color, StringComparison.Ordinal)
            || !string.Equals(GatewayStateLabel, label, StringComparison.Ordinal)
            || !string.Equals(GatewayTriageBucket, bucket, StringComparison.Ordinal)
            || GatewayNeedsYouSince != needsYouSince
            || GatewaySnoozeUntil != snoozeUntil
            || GatewaySnoozeExpired != snoozeExpired
            || !string.Equals(GatewayInboxLine, inbox, StringComparison.Ordinal);

        if (!changed) return;

        GatewayEffectiveColor = color;
        GatewayStateLabel = label;
        GatewayTriageBucket = bucket;
        GatewayNeedsYouSince = needsYouSince;
        GatewaySnoozeUntil = snoozeUntil;
        GatewaySnoozeExpired = snoozeExpired;
        GatewayInboxLine = inbox;

        FileLog.Write($"[Session] ApplyGatewayDisplayState: session={Id}, color={color ?? "(cleared)"}, label={label ?? "(none)"}, bucket={bucket ?? "(none)"}, snoozeUntil={snoozeUntil?.ToString("O") ?? "(none)"}, snoozeExpired={snoozeExpired}, inboxLine={inbox ?? "(none)"}");
        try { OnGatewayDisplayStateChanged?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[Session] {Id} OnGatewayDisplayStateChanged handler threw: {ex.Message}"); }
    }

    /// <summary>Set (or clear, on a null/blank value) this session's sticky explicit role. The value is
    /// validated against the role set by the caller; this only stores it.</summary>
    public void SetExplicitRole(string? role)
    {
        ExplicitRole = string.IsNullOrWhiteSpace(role) ? null : role.Trim();
        FileLog.Write($"[Session] {Id} explicit role set to {ExplicitRole ?? "(none)"}");
        RaisePreambleInputsChanged(nameof(ExplicitRole));
    }

    /// <summary>
    /// Remove-the-network-port mission, phase 3: fires when something this session's fleet preamble
    /// RENDERS FROM has changed - its explicit role or its workflow seat. The Director maintains a
    /// hook-output file per session and rewrites it here, so the next SessionStart hook fire (a resume,
    /// a clear, a compact) delivers the current text rather than a launch-time snapshot.
    ///
    /// Only the per-session inputs are announced here. The three SHARED stores the preamble also reads -
    /// the user's injected text, the workflow index, the skill index - are Gateway-owned and refreshed
    /// on the Director's interval poll, which rewrites every live session's file at the end of each
    /// refresh. See <see cref="SessionPreambleMaintainer"/>.
    /// </summary>
    public event Action? OnPreambleInputsChanged;

    private void RaisePreambleInputsChanged(string what)
    {
        try { OnPreambleInputsChanged?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[Session] {Id} OnPreambleInputsChanged ({what}) handler threw: {ex.Message}"); }
    }

    /// <summary>
    /// The Mission this session is ATTACHED to (see
    /// docs/new_architecture/fleet.html), or null when it is attached to no
    /// Mission. A Mission is its own persisted record (<see cref="Mission"/>); this is the attachment link
    /// that binds a pod (Architect + Manager + Workers all attach to one Mission). Stamped at spawn (from
    /// the create request) or later via the attach verb (<see cref="AttachToMission"/>). Persisted so the
    /// attachment survives a Director restart.
    /// </summary>
    public Guid? MissionId { get; internal set; }

    /// <summary>
    /// The attached Mission's display name, CACHED here so a client can render the Mission without resolving
    /// <see cref="MissionId"/> against the Mission store. Set alongside <see cref="MissionId"/> at attach
    /// time; the Mission record remains the source of truth. Null when attached to no Mission. Persisted.
    /// </summary>
    public string? MissionName { get; internal set; }

    /// <summary>
    /// Attach this session to a Mission (or DETACH it when <paramref name="missionId"/> is null). Stamps
    /// <see cref="MissionId"/> and caches the resolved <see cref="MissionName"/>. The caller resolves the
    /// name from the Mission store; this only stores what it is given.
    /// </summary>
    public void AttachToMission(Guid? missionId, string? missionName)
    {
        MissionId = missionId;
        MissionName = missionId is null ? null : (string.IsNullOrWhiteSpace(missionName) ? null : missionName.Trim());
        FileLog.Write($"[Session] {Id} attached to mission {MissionId?.ToString() ?? "(none)"} (name={MissionName ?? "(none)"})");
    }

    /// <summary>
    /// The workflow RUN this session is seated on (Workflows mission, phase 5b), or null for an
    /// unseated session. Stamped at spawn from the create request after the GATEWAY validated the run
    /// (the source of truth); the Director never resolves a run itself. Persisted, like the mission
    /// attachment beside it.
    /// </summary>
    public Guid? WorkflowRunId { get; internal set; }

    /// <summary>The seated run's workflow id (e.g. "mission"), cached for rendering and for the
    /// seated preamble paragraph. Null when unseated. Persisted.</summary>
    public string? WorkflowId { get; internal set; }

    /// <summary>The seated run's PINNED workflow version. The seated preamble tells the agent to
    /// fetch its conduct at exactly this version - never a moving head. Null when unseated. Persisted.</summary>
    public int? WorkflowVersion { get; internal set; }

    /// <summary>Seat this session on a workflow run (all three values Gateway-resolved), or clear the
    /// seat when <paramref name="workflowRunId"/> is null. Stores only what it is given.</summary>
    public void SeatOnWorkflow(Guid? workflowRunId, string? workflowId, int? workflowVersion)
    {
        WorkflowRunId = workflowRunId;
        WorkflowId = workflowRunId is null ? null : (string.IsNullOrWhiteSpace(workflowId) ? null : workflowId.Trim());
        WorkflowVersion = workflowRunId is null ? null : workflowVersion;
        FileLog.Write($"[Session] {Id} seated on workflow run {WorkflowRunId?.ToString() ?? "(none)"} " +
                      $"({WorkflowId ?? "(none)"} v{WorkflowVersion?.ToString() ?? "-"})");
        RaisePreambleInputsChanged(nameof(WorkflowRunId));
    }

    public Guid Id { get; }

    /// <summary>
    /// The session's short, human-friendly three-digit number (100-999), or null when the session
    /// has no number (allocated before this feature ran, or the Director's number pool was exhausted
    /// at creation). Issue #820. Assigned by the <see cref="SessionManager"/> at creation (or
    /// re-applied from persistence on restore) and stable for the life of the session - a rename
    /// never changes it because the number is a separate field from the display name.
    ///
    /// Issue #1292: the number is now handed out by the Gateway (unique across the whole fleet), so
    /// for a brand-new session it may be assigned a moment AFTER creation - once the Gateway answers.
    /// Set through <see cref="SetNumber"/> so <see cref="OnNumberChanged"/> fires and the rail shows
    /// the number when it arrives.
    /// </summary>
    public int? Number { get; internal set; }

    /// <summary>Raised when <see cref="Number"/> changes, so a view (the desktop rail) can show the
    /// number when the Gateway assigns it after creation (issue #1292).</summary>
    public event Action? OnNumberChanged;

    /// <summary>Set <see cref="Number"/> and notify listeners. No-op when the value is unchanged.</summary>
    internal void SetNumber(int? number)
    {
        if (Number == number) return;
        Number = number;
        try { OnNumberChanged?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[Session] {Id} OnNumberChanged handler threw: {ex.Message}"); }
    }

    public string RepoPath { get; }
    public string WorkingDirectory { get; }
    public SessionStatus Status { get; internal set; }
    public DateTimeOffset CreatedAt { get; }
    public string? ClaudeArgs { get; }

    /// <summary>The EFFECTIVE launch command line the agent process was actually started with -
    /// the merged result of <see cref="ClaudeArgs"/> (per-session args) and the configured agent
    /// defaults (e.g. <c>AgentOptions.DefaultClaudeArgs</c>), as produced by the agent's
    /// BuildLaunchSpec. This is the authoritative source of the launched <c>--model</c> value
    /// (issue #803): <see cref="ClaudeArgs"/> is null whenever the model comes from the configured
    /// default rather than a per-session override, so the context gauge must read the effective
    /// args, not the raw per-session ones. Set by SessionManager at launch.</summary>
    public string? EffectiveLaunchArgs { get; internal set; }

    public int? ExitCode { get; internal set; }

    /// <summary>
    /// True when the agent process ended UNEXPECTEDLY - a crash (issue #959). Set on process exit
    /// when the exit was not a clean, intentional end (see <see cref="IsUnexpectedExit"/>). A crashed
    /// session is kept in the roster in an Error state rather than being auto-removed, so the user
    /// sees that work stopped instead of the session silently disappearing.
    /// </summary>
    public bool Crashed { get; private set; }

    /// <summary>
    /// The pure decision for "did this session crash?" A process exit is treated as a crash when it
    /// returns a non-zero exit code OR when the session dropped out while it was actively working
    /// (some crashes exit with code 0, so the exit code alone is not enough). A clean exit (code 0)
    /// from an idle/waiting session is an intentional end and is NOT a crash.
    /// </summary>
    public static bool IsUnexpectedExit(int exitCode, bool wasWorking) => exitCode != 0 || wasWorking;

    /// <summary>The terminal buffer from the backend. May be null for Embedded mode.</summary>
    public CircularTerminalBuffer? Buffer => _backend.Buffer;

    /// <summary>
    /// Current PTY column count. Initialized to the size the backend is started
    /// with (120) and updated by <see cref="Resize"/> when the desktop terminal
    /// pane drives a resize. The phone's xterm.js view reads this so it renders
    /// the grid at the true PTY width instead of guessing.
    /// </summary>
    public short CurrentCols { get; private set; } = 120;

    /// <summary>Current PTY row count. See <see cref="CurrentCols"/>.</summary>
    public short CurrentRows { get; private set; } = 30;

    /// <summary>Process ID from the backend.</summary>
    public int ProcessId => _backend.ProcessId;

    /// <summary>
    /// Claude's cognitive activity state, driven by hook events.
    /// Initial state is <see cref="ActivityState.WaitingForInput"/>: a freshly spawned session is
    /// literally sitting at Claude Code's input prompt with no turn in flight. This pairs with the
    /// IsBrandNew guard in <c>TerminalStateDetector</c>, which suppresses the byte->Working flip
    /// while the startup splash is painting, so the session stays parked at its prompt from the
    /// moment the row appears until the user submits their first prompt. While
    /// <see cref="IsBrandNew"/> holds, that parked state folds to green ("ready") rather than red
    /// ("needs you") - at the GATEWAY, in <c>SessionOrdering.ResolveActivity</c>, which reads
    /// <c>SessionDto.IsBrandNew</c> as a raw fact. This comment used to point at
    /// <c>SessionStatusWingman.ColorFor</c> as the source of truth; that method has not existed since
    /// phase 2.3, and the Director has not painted green since it either.
    /// </summary>
    public ActivityState ActivityState { get; private set; } = ActivityState.WaitingForInput;

    /// <summary>The session_id reported by Claude hooks, used for routing.</summary>
    public string? ClaudeSessionId { get; internal set; }

    /// <summary>
    /// When the Director last cleared this session's context through a driver that could not report the
    /// transcript the clear started (pi's <c>/new</c>: the new file appears only with the next message).
    /// Null when no such clear is outstanding. <see cref="Pi.PiSessionRebinder"/> consumes it at the next
    /// turn end and clears it once the session is relinked to the file created after it (issue #2670).
    /// </summary>
    public DateTime? ContextClearedUtc { get; private set; }

    /// <summary>The outstanding clear has been resolved to its transcript; nothing is pending.</summary>
    internal void ClearContextClearedStamp() => ContextClearedUtc = null;

    /// <summary>
    /// The absolute path to the current Claude transcript .jsonl, as reported by the Claude
    /// SessionStart hook. Authoritative across /clear and compaction (Claude mints a new id
    /// and file on each), where deriving the path from a stale <see cref="ClaudeSessionId"/>
    /// would be wrong. Null until the first hook fires.
    /// </summary>
    public string? ClaudeTranscriptPath { get; private set; }

    /// <summary>
    /// Update the live Claude session pointer from a SessionStart hook. Claude mints a NEW
    /// session id (and transcript file) on /clear and on auto-compaction; the hook reports the
    /// current id and transcript path so the Director keeps tracking the right transcript
    /// instead of the stale one it preassigned at launch.
    /// </summary>
    public void UpdateClaudeSessionPointer(string? claudeSessionId, string? transcriptPath, string? source)
    {
        // A session id must LOOK like one before it is allowed to replace a working one.
        //
        // This guard used to be `!IsNullOrWhiteSpace`, and that is the whole of how issue #2456 destroyed
        // three sessions. A drop arrived carrying the literal one-character id "x" - with no event, no
        // source and no transcript - and it was accepted over a verified GUID and persisted. The session
        // could no longer resolve its own transcript, so narration read no reply, recorded "nothing to
        // narrate", and returned before generating anything. The rail sat on "Preparing voice" forever and
        // the session never spoke again. Nothing failed loudly; nothing retried; no reason was recorded
        // anywhere. One of the three took the damage while running the v1.9.11 release gate.
        //
        // The writer in that case was a TEST inheriting the caller's environment, and that is fixed at its
        // own site - but fixing only the messenger would leave this door open to every other writer. Any
        // non-blank string could overwrite a known-good pointer, and the damage was silent and permanent.
        // This guard alone would have prevented all three cases with that test unchanged.
        //
        // REFUSING is strictly better than accepting here, and the asymmetry is the point: keeping a
        // slightly stale id costs one turn of narration, while taking a malformed one costs the session
        // its voice for good, with no error to notice. So a value that does not parse as a GUID is
        // refused and said out loud, and the previous good value stands.
        //
        // WHAT THIS DOES NOT DO, stated plainly because the shape of the check invites the wrong
        // assumption: it validates SHAPE, not identity. A well-formed GUID naming a transcript that
        // does not exist is still accepted, and would silence narration in exactly the same way. So
        // this closes the incident and the whole class of malformed-value writers; it does not close
        // the class of well-formed-but-wrong ones. Closing that needs the pointer checked against a
        // transcript that actually exists, which is a larger change and is deliberately not attempted
        // here.
        if (!string.IsNullOrWhiteSpace(claudeSessionId))
        {
            if (Guid.TryParse(claudeSessionId, out _))
            {
                ClaudeSessionId = claudeSessionId;
            }
            else
            {
                FileLog.Write(
                    $"[Session] UpdateClaudeSessionPointer REFUSED a malformed claude session id for sid={Id}: "
                    + $"'{claudeSessionId}' is not a GUID (source={source ?? "(none)"}). Keeping "
                    + $"'{ClaudeSessionId ?? "(none)"}'. A pointer drop carrying a non-GUID id is a bug in "
                    + "whatever wrote it - see issue #2456.");
                return;
            }
        }
        if (!string.IsNullOrWhiteSpace(transcriptPath))
            ClaudeTranscriptPath = transcriptPath;
        FileLog.Write($"[Session] UpdateClaudeSessionPointer: sid={Id} source={source ?? "(none)"} claudeId={claudeSessionId ?? "(none)"} transcript={transcriptPath ?? "(none)"}");
    }

    /// <summary>Cached metadata from Claude's sessions-index.json.</summary>
    public ClaudeSessionMetadata? ClaudeMetadata { get; private set; }

    /// <summary>Fires when ClaudeMetadata is refreshed.</summary>
    public event Action<ClaudeSessionMetadata?>? OnClaudeMetadataChanged;

    /// <summary>Status of session file verification (whether .jsonl exists and is readable).</summary>
    public SessionVerificationStatus VerificationStatus { get; private set; } = SessionVerificationStatus.NotLinked;

    /// <summary>The first prompt snippet from the verified .jsonl file.</summary>
    public string? VerifiedFirstPrompt { get; private set; }

    /// <summary>The expected first prompt to verify against (set from persisted state).</summary>
    public string? ExpectedFirstPrompt { get; set; }

    /// <summary>Fires when verification status changes.</summary>
    public event Action<SessionVerificationStatus>? OnVerificationStatusChanged;

    /// <summary>User-defined display name for this session. Null means use default (repo folder name).</summary>
    public string? CustomName { get; set; }

    /// <summary>
    /// True when <see cref="CustomName"/> was AUTO-composed at birth (no human/explicit name); false once a
    /// human or the session itself explicitly renamed it (automatic session roles, chunk 3). A self/human
    /// name always wins and must never be re-auto-named - this is the marker that gates any future
    /// auto-rename. Set at birth by the create path and cleared by an explicit
    /// <see cref="SessionManager.RenameSession"/>. Persisted so the auto-vs-explicit distinction survives a
    /// restart.
    /// </summary>
    public bool IsAutoNamed { get; set; }

    /// <summary>
    /// The structured question, plan, or permission ask the agent is currently
    /// waiting on, or null when nothing is pending. Cleared automatically when the
    /// activity state moves out of
    /// <see cref="ActivityState.WaitingForInput"/> / <see cref="ActivityState.WaitingForPerm"/>.
    /// Volatile state; not persisted across Director restarts.
    /// </summary>
    public PendingInteraction? PendingInteraction { get; private set; }

    /// <summary>User-chosen header color (hex string like "#2563EB"). Null means default dark header.</summary>
    public string? CustomColor { get; set; }

    /// <summary>Links this session to a SessionHistoryEntry for persistent workspace tracking.</summary>
    public Guid? HistoryEntryId { get; set; }

    /// <summary>Raw terminal output captured during Claude Code startup. Preserved for future parsing.</summary>
    public string? RawStartupText { get; set; }

    /// <summary>Terminal-based verification status (matching terminal to .jsonl).</summary>
    public TerminalVerificationStatus TerminalVerificationStatus { get; private set; } = TerminalVerificationStatus.Waiting;

    /// <summary>Fires when terminal verification status changes.</summary>
    public event Action<TerminalVerificationStatus>? OnTerminalVerificationStatusChanged;

    /// <summary>Number of confirmation attempts made (at 50+ lines). Allows retries up to a limit.</summary>
    private volatile int _confirmationAttempts;

    /// <summary>Max confirmation attempts before giving up permanently.</summary>
    private const int MaxConfirmationAttempts = 5;

    /// <summary>Guard to prevent concurrent verification runs.</summary>
    private int _verificationRunning;

    /// <summary>
    /// Mark this session as pre-verified (for restored sessions that already have a ClaudeSessionId).
    /// This skips terminal verification since the session was previously verified.
    /// </summary>
    public void MarkAsPreVerified()
    {
        if (!string.IsNullOrEmpty(ClaudeSessionId))
        {
            _confirmationAttempts = MaxConfirmationAttempts;
            SetTerminalVerificationStatus(TerminalVerificationStatus.Matched);
        }
    }

    /// <summary>JSONL history snapshots for rewind/fork support.</summary>
    public SessionHistory? History { get; private set; }

    /// <summary>
    /// Initialize the session history tracker once the ClaudeSessionId is known.
    /// Must be called after ClaudeSessionId is set.
    /// </summary>
    public void InitializeHistory()
    {
        if (History != null)
            return;

        if (string.IsNullOrEmpty(ClaudeSessionId))
        {
            FileLog.Write("[Session] InitializeHistory: no ClaudeSessionId, skipping");
            return;
        }

        FileLog.Write($"[Session] InitializeHistory: sessionId={ClaudeSessionId}");
        History = new SessionHistory(ClaudeSessionId, RepoPath);
    }

    /// <summary>Chat messages for the Simple Chat view.</summary>
    public SessionChatHistory ChatHistory { get; } = new();

    private string? _pendingPromptText;

    /// <summary>
    /// Prompt text the user was composing but hasn't sent yet. Persisted across
    /// switches and restarts. Two writers exist: the UI when the user types (via
    /// the property setter, source="user"), and the SessionStatusWingman when
    /// it detects Claude Code has injected a suggestion into its own input line
    /// (via <see cref="SetPendingPromptText"/>, source="wingman"). Subscribers
    /// to <see cref="OnPendingPromptTextChanged"/> can distinguish the two and
    /// decide whether to apply.
    /// </summary>
    public string? PendingPromptText
    {
        get => _pendingPromptText;
        set => SetPendingPromptText(value, "user");
    }

    /// <summary>
    /// Fires when <see cref="PendingPromptText"/> changes. Args: (newText, source).
    /// source is "user" for property-setter writes, or whatever string the caller
    /// passes to <see cref="SetPendingPromptText"/> — currently "wingman" for
    /// terminal-injection detection.
    /// </summary>
    public event Action<string?, string>? OnPendingPromptTextChanged;

    /// <summary>
    /// Set the pending prompt text with an explicit source tag. Idempotent: a
    /// write with the same value as the current one does not fire the event.
    /// </summary>
    public void SetPendingPromptText(string? value, string source)
    {
        if (_pendingPromptText == value) return;
        _pendingPromptText = value;
        // A different text is a different box: whatever was spoken in the old one is not in this one. The
        // desktop composer sets the spans right after the text when it saves a box that still holds a
        // dictation (ruling R20); any other writer - the wingman, a restore of an older snapshot - leaves none.
        _pendingPromptSpokenSpans = Array.Empty<SpokenTurnRule.SpokenSpan>();
        OnPendingPromptTextChanged?.Invoke(value, source ?? "user");
    }

    private IReadOnlyList<SpokenTurnRule.SpokenSpan> _pendingPromptSpokenSpans = Array.Empty<SpokenTurnRule.SpokenSpan>();

    /// <summary>
    /// Which characters of <see cref="PendingPromptText"/> came from a microphone (ruling R20): the compose
    /// box's provenance, saved with the text when the user switches away and put back when they return, and
    /// persisted across restarts beside the text. Without it a dictation inserted, switched away from and
    /// sent later counted as typed. Set AFTER the text, because setting the text clears it; a span outside
    /// the text is refused.
    /// </summary>
    public IReadOnlyList<SpokenTurnRule.SpokenSpan> PendingPromptSpokenSpans
    {
        get => _pendingPromptSpokenSpans;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var length = (_pendingPromptText ?? "").Length;
            foreach (var span in value)
                if (span.Start < 0 || span.Length <= 0 || span.End > length)
                    throw new ArgumentException(
                        $"A spoken span {span.Start}+{span.Length} lies outside the pending prompt text of {length} characters " +
                        $"on session {Id}. The spans are set after the text they describe, never before.", nameof(value));
            _pendingPromptSpokenSpans = value.OrderBy(s => s.Start).ToArray();
        }
    }

    /// <summary>Name of the last selected tab (e.g. "Terminal", "Agent", "SourceControl"). Persisted across switches and restarts.</summary>
    public string? SelectedTabName { get; set; }

    /// <summary>Queue of prompts the user wants to send later. Persisted across switches and restarts.</summary>
    public PromptQueue PromptQueue { get; } = new();

    /// <summary>Order in the session list, used to restore UI order after restart.</summary>
    public int SortOrder { get; set; }

    /// <summary>Fires when ActivityState changes. Args: (oldState, newState).</summary>
    public event Action<ActivityState, ActivityState>? OnActivityStateChanged;

    /// <summary>
    /// Fires exactly once when the underlying process exits, carrying the exit code.
    /// Lets the <see cref="SessionManager"/> reap a session whose agent process died on
    /// its own (clean exit) so it does not linger as a dead "Exited" row with no process
    /// behind it. Abnormal exits are deliberately left in place for crash recovery (#212).
    /// </summary>
    public event Action<int>? OnExited;

    // ---------- Wingman turn briefing (TURN_BRIEFING.md; orthogonal to ActivityState) ----------

    /// <summary>
    /// The wingman briefing-pipeline state for the CURRENT turn. Orthogonal to
    /// <see cref="ActivityState"/> (a session can ask AND keep working). Since issue #187
    /// deleted the Director-side pipeline, NOTHING on the Director writes this anymore -
    /// it stays None and the GATEWAY stamps the real value onto the aggregated session
    /// view. Kept because the local status wingman still reacts to it (and a future
    /// gateway push-down could write it).
    /// </summary>
    public BriefingState BriefingState { get; private set; } = BriefingState.None;

    /// <summary>Fires when <see cref="BriefingState"/> changes.</summary>
    public event Action<BriefingState>? OnBriefingStateChanged;

    internal void SetBriefingState(BriefingState state)
    {
        if (_disposed || BriefingState == state) return;
        BriefingState = state;
        OnBriefingStateChanged?.Invoke(state);
    }

    /// <summary>
    /// The latest stored brief's railLine (the &lt;=8-word needs-you one-liner). Since
    /// issue #187 deleted the Director-side pipeline this stays null on the Director;
    /// the GATEWAY stamps the real value onto the aggregated session view.
    /// </summary>
    public string? LatestBriefRailLine { get; internal set; }

    // ---------- Mobile mode + proactive wingman explain (remote experience) ----------

    /// <summary>
    /// Which mobile view a phone is currently watching this session through, set by the active
    /// mobile tab via the Control API (Session tab -> Text, Voice tab -> Voice; Off when no phone
    /// is watching). Single source of truth for the mobile experience; <see cref="MobileMode"/>
    /// and <see cref="VoiceMode"/> are derived from it. Off by default. Not persisted: it tracks
    /// what a remote viewer is looking at right now, not durable session state.
    /// </summary>
    public MobileViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (_viewMode == value) return;
            var old = _viewMode;
            _viewMode = value;
            OnViewModeChanged?.Invoke(old, value);
        }
    }
    private MobileViewMode _viewMode = MobileViewMode.Off;

    /// <summary>
    /// Raised when <see cref="ViewMode"/> changes (old, new). The desktop session view-model
    /// listens so the in-voice-mode ear indicator (issue #554) appears and clears live as a
    /// phone enters and leaves the Voice tab, without waiting for a list rebuild.
    /// </summary>
    public event Action<MobileViewMode, MobileViewMode>? OnViewModeChanged;

    /// <summary>
    /// True when a phone is actively watching this session in either mobile mode (Text or Voice).
    /// Gates the proactive wingman "explain" briefing regeneration (see <see cref="CachedExplainText"/>)
    /// so the Opus cost stays off sessions nobody is watching remotely. Derived from <see cref="ViewMode"/>.
    /// </summary>
    public bool MobileMode => ViewMode != MobileViewMode.Off;

    /// <summary>
    /// True when the active mobile view is the Voice (in-car) tab. The wingman keys its remark
    /// STYLE off this -- spoken-friendly remarks while it holds -- but that read lives in the
    /// wingman, not here. Derived from <see cref="ViewMode"/>.
    /// </summary>
    public bool VoiceMode => ViewMode == MobileViewMode.Voice;

    /// <summary>
    /// A DISPLAY MIRROR of the hold the GATEWAY has decided for this session, so the local desktop rail
    /// can render it. Not state this session owns, and not a state machine: the only writer is
    /// <see cref="ApplyGatewayHold"/>, which the Gateway calls down the tunnel.
    ///
    /// This session does not decide hold and does not persist it. The Gateway holds the state
    /// (SnoozeRegistry), owns the clock, and makes every ruling about both; this Director contributes two
    /// facts and no opinions - <see cref="ActivityState"/> and <see cref="LastOwnerTurnAtUtc"/>.
    ///
    /// It is a mirror rather than a read of the Gateway roster only because the desktop rail folds from
    /// this in-process Session. Every other surface reads the truth from the fold.
    /// </summary>
    public HoldState HoldState
    {
        get => _holdState;
        private set
        {
            if (_holdState == value) return;
            var wasOnHold = OnHold;
            _holdState = value;
            // OnHoldChanged is the "is it parked?" signal (rail strip, Cockpit SNOOZED tag), so it fires only
            // when that answer actually flips - None <-> DeferredHold does not park anything.
            if (OnHold != wasOnHold) OnHoldChanged?.Invoke(OnHold);
            // HoldStateChanged fires on EVERY transition, including None <-> DeferredHold, which leaves
            // OnHold untouched. That edge matters to the Gateway: it is what tells it a deferred snooze
            // has LANDED, which is when the snooze clock starts (defect 20). Until 14 July 2026 this
            // comment claimed clients rendered a distinct "Working, snoozing when done" label off this
            // edge - they could not, because the state was byte-identical to None on the wire (defect
            // 12). It now crosses on SessionDto.HoldState; rendering a distinct badge is client work
            // that has not been done yet, so do not claim it here again until it has.
            HoldStateChanged?.Invoke(value);
        }
    }
    private HoldState _holdState;

    /// <summary>
    /// True when the session is parked right now, per the Gateway's last ruling. Derived from
    /// <see cref="HoldState"/>; a DeferredHold is NOT parked yet - it was asked for while the agent was
    /// working and lands when the work stops, which the GATEWAY decides.
    /// </summary>
    public bool OnHold => _holdState == HoldState.Held;

    /// <summary>True when the agent is producing output right now. No longer an input to any hold
    /// decision - it is REPORTED upward (ActivityState on the wire) and the Gateway rules on it.
    /// Starting counts as working - a session that has not settled yet has a turn ahead of it.</summary>
    private bool IsWorking => ActivityState is ActivityState.Working or ActivityState.Starting;

    /// <summary>Fires when <see cref="OnHold"/> changes. Arg: new value. The desktop
    /// session list subscribes so the color strip can repaint (held) without
    /// the wingman touching <see cref="StatusColor"/>; OnHold sits on top of it.</summary>
    public event Action<bool>? OnHoldChanged;

    /// <summary>Fires on EVERY <see cref="HoldState"/> transition, including ones that leave
    /// <see cref="OnHold"/> unchanged (None &lt;-&gt; DeferredHold). The Control API subscribes so a hold
    /// change is pushed to the Gateway the instant it happens, exactly as an activity change already is -
    /// without it, a hold toggle is invisible to every other screen until the next 10-second heartbeat.</summary>
    public event Action<HoldState>? HoldStateChanged;


    /// <summary>
    /// Write down what the GATEWAY has decided this session's hold is, so the desktop can render it.
    ///
    /// THIS IS A DUMB SETTER AND MUST STAY ONE. It contains no rule, reads no other state, and decides
    /// nothing. The Gateway owns hold: it holds the state, it holds the clock, and it makes every ruling
    /// about both. This field is a display mirror the Gateway writes down the tunnel, for the one reader
    /// that cannot go and ask - the local desktop rail, which folds from this in-process Session rather
    /// than from the Gateway roster.
    ///
    /// It used to be <c>RequestHold</c>, which decided defer-versus-immediate by reading
    /// <see cref="IsWorking"/>, and it lived here because the Director owned hold. It should never have:
    /// a hold is a statement of the OWNER'S INTENT ("do not bother me with this for twelve hours"), and
    /// intent has nothing to do with a pseudo-terminal. The Director's world is bytes and processes. Two
    /// owners of one idea drift, and every defect this machine ever had - 12, 20, 21, 22, and every hold
    /// that died within minutes on 15 July 2026 - was that drift.
    ///
    /// If you are about to add an <c>if</c> to this method, you are putting the bug back.
    /// </summary>
    public void ApplyGatewayHold(HoldState decidedByGateway)
    {
        HoldState = decidedByGateway;
    }


    /// <summary>
    /// Whether this session participates in the Wingman experience: the auto-explain
    /// briefing on turn-end, the Voice/Wingman tabs, and the Yellow "Wingman is reading"
    /// state. OFF by default for every new session (the Wingman is not reliable enough
    /// yet to opt every session in). When OFF the session behaves like a plain terminal:
    /// ProactiveExplainService skips it, the dot goes straight Blue->Red, and the clients
    /// hide the Voice + Wingman tabs. Opt in per session via the context menu / new-session
    /// dialog. Durable per session (persisted via <see cref="PersistedSession.WingmanEnabled"/>).
    /// </summary>
    /// <remarks>
    /// A FOLD INPUT, therefore change-notifying. It is not an overlay flag - it is the GATE on one of
    /// them: SessionOrdering.ResolveActivity yields yellow only when WingmanEnabled AND IsAutoExplaining.
    /// So turning the Wingman off on an auto-explaining session flips the correct answer from yellow
    /// "Wingman reading" to red "Needs you" WITHOUT any overlay flag changing. (It also gated a purple
    /// "Background" arm, which the Wingman-on-every-turn mission deleted.)
    ///
    /// It was a bare auto-property, so nothing could hear that. The overlays it gates all raise; the gate
    /// did not, and it was the last unwired fold input after three review passes fixed the obvious ones.
    /// A gate is easier to miss than a flag precisely because it is not the thing being rendered - which is
    /// why the rule is mechanical: if the fold reads it, it announces itself. Found by review of pull
    /// request 1598.
    /// </remarks>
    public bool WingmanEnabled
    {
        get => _wingmanEnabled;
        set
        {
            if (_wingmanEnabled == value) return;
            _wingmanEnabled = value;
            FileLog.Write($"[Session] WingmanEnabled: session={Id}, enabled={value}");
            try { OnWingmanEnabledChanged?.Invoke(value); }
            catch (Exception ex) { FileLog.Write($"[Session] {Id} OnWingmanEnabledChanged handler threw: {ex.Message}"); }
        }
    }
    private bool _wingmanEnabled;

    /// <summary>Raised when <see cref="WingmanEnabled"/> changes, so a view re-reads the fold it gates.</summary>
    public event Action<bool>? OnWingmanEnabledChanged;

    /// <summary>
    /// Scheduled-run auto-dismiss (issue #1200): true when this session is an AUTOMATED run (a cron seed)
    /// that should close itself when it finishes with nothing needing a human. Only sessions with this set
    /// are ever auto-closed; a human-started session leaves it false and is never touched. Set once at birth
    /// from <see cref="Gateway.Contracts.NewSessionRequest.AutoDismiss"/>. See <see cref="DismissVerdict"/>.
    /// </summary>
    public bool AutoDismiss { get; set; } = false;

    /// <summary>
    /// Scheduled-run auto-dismiss (issue #1200): the agent's explicit end-of-run verdict, parsed from the
    /// finished turn's final message (a <c>CC-DISMISS</c> block, see <see cref="Wingman.DismissVerdictSignal"/>).
    /// <c>"done"</c> = nothing needs the human (safe to auto-close); <c>"needs-human"</c> = keep it open.
    /// Null until a verdict is seen - the conservative default that guarantees a session is never auto-closed
    /// without an explicit <c>done</c>. Set on the Director via <see cref="SetDismissVerdict"/>; flows to the
    /// Gateway on <see cref="Gateway.Contracts.SessionDto.DismissVerdict"/>.
    /// </summary>
    public string? DismissVerdict { get; private set; }

    /// <summary>
    /// Record the agent's parsed end-of-run verdict for auto-dismiss (issue #1200). Idempotent per value:
    /// only logs on a change. A null/blank argument clears it (a fresh turn that produced no verdict). This
    /// is the only writer of <see cref="DismissVerdict"/>.
    /// </summary>
    public void SetDismissVerdict(string? verdict)
    {
        var normalized = string.IsNullOrWhiteSpace(verdict) ? null : verdict.Trim().ToLowerInvariant();
        if (string.Equals(normalized, DismissVerdict, StringComparison.Ordinal))
            return;
        DismissVerdict = normalized;
        FileLog.Write($"[Session] SetDismissVerdict: session={Id} verdict={normalized ?? "(cleared)"}");
    }

    /// <summary>
    /// The model this session's agent is CURRENTLY using (issue #1637), as reported by the driver's
    /// <see cref="Drivers.IAgentDriver.ReadCurrentModel"/> from the tool's own records - e.g.
    /// <c>claude-fable-5</c>, <c>gpt-5.5</c>, <c>grok-4.5</c>. Refreshed at every turn-end by
    /// <see cref="SessionRecordsWatcher"/>, so a mid-session model switch is reflected. Null
    /// until the first read succeeds (no turn yet, or an agent without the ModelReport capability).
    /// Set via <see cref="SetCurrentModel"/>; flows to the Gateway on
    /// <see cref="Gateway.Contracts.SessionDto.CurrentModel"/> for the model-usage statistics.
    /// </summary>
    public string? CurrentModel { get; private set; }

    /// <summary>
    /// Raised when <see cref="CurrentModel"/> changes, so the desktop rail repaints the model badge on a
    /// mid-session model switch (issue internal#1340). Without it the badge would be right only by luck:
    /// the model is re-read at turn-end, and the rail is told to re-read on activity/hold/role/number
    /// changes - none of which a <c>/model</c> switch inside a working session has to raise. That is the
    /// same shape as the role-stamp defect: a displayed fact with no invalidation path shows its old value
    /// until something unrelated happens to repaint the row, which reads as deliberate and is worse than
    /// blank. Same contract as <see cref="OnNumberChanged"/>: report what changed, decide nothing about
    /// how it looks.
    /// </summary>
    public event Action? OnCurrentModelChanged;

    /// <summary>
    /// Record the driver-reported current model (issue #1637). Idempotent per value: only logs on a
    /// change. A null/blank argument is IGNORED rather than clearing: a read that cannot determine
    /// the model (torn file, agent restarting) is a missed read, not evidence the session lost its
    /// model - the last known model stands. This is the only writer of <see cref="CurrentModel"/>.
    /// </summary>
    public void SetCurrentModel(string? model)
    {
        var normalized = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (normalized is null || string.Equals(normalized, CurrentModel, StringComparison.Ordinal))
            return;
        CurrentModel = normalized;
        FileLog.Write($"[Session] SetCurrentModel: session={Id} model={normalized}");
        try { OnCurrentModelChanged?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[Session] {Id} OnCurrentModelChanged handler threw: {ex.Message}"); }
    }

    /// <summary>
    /// This session's cumulative token spend (issue #1637), read from the tool's own records at turn-end
    /// by <see cref="SessionRecordsWatcher"/> and flowed to the Gateway on
    /// <see cref="Gateway.Contracts.SessionDto.TokenTotals"/> for the token-usage statistics. Null until
    /// the first read succeeds (no turn yet, or an agent without the TokenUsage capability). Set via
    /// <see cref="SetTokenTotals"/>.
    /// </summary>
    public Gateway.Contracts.TokenTotalsDto? TokenTotals { get; private set; }

    /// <summary>
    /// Record the driver-reported cumulative token spend (issue #1637). A null argument is IGNORED rather
    /// than clearing: a read that could not be taken (torn records, agent restarting, a driver without the
    /// capability) is a missed read, not evidence the spend went away - the last known totals stand, the
    /// same discipline as <see cref="SetCurrentModel"/>. This is the only writer of
    /// <see cref="TokenTotals"/>.
    /// </summary>
    public void SetTokenTotals(Gateway.Contracts.TokenTotalsDto? totals)
    {
        if (totals is null) return;
        TokenTotals = totals;
        FileLog.Write($"[Session] SetTokenTotals: session={Id} in={totals.InputTokens} out={totals.OutputTokens} " +
                      $"cacheRead={totals.CacheReadTokens} cacheCreate={totals.CacheCreationTokens} ctx={totals.ContextTokens}");
    }

    /// <summary>
    /// The <see cref="WingmanEnabled"/> value captured just before voice mode was enabled
    /// for this session, so the prior state can be restored when voice mode ends. Null when
    /// voice mode is not active (the value is transient and is not persisted). Voice mode
    /// requires the Wingman for reply summarization, so it force-enables WingmanEnabled on
    /// entry regardless of the user's normal setting; this field records what to restore.
    /// </summary>
    public bool? PreVoiceWingmanEnabled { get; set; } = null;

    private bool _isExplaining;

    /// <summary>
    /// True while <c>ProactiveExplainService</c> has a Wingman briefing in flight for
    /// this session. Set just before the call, cleared in <c>finally</c>. Transient
    /// (in-memory only).
    ///
    /// This is a RAW FACT. It is reported on <c>SessionDto.IsAutoExplaining</c> and the GATEWAY folds the
    /// orange from it (with <see cref="WingmanEnabled"/>, at a turn end) - the Director does not paint it.
    /// This comment used to say <see cref="OnIsExplainingChanged"/> "notifies the SessionStatusWingman so
    /// it can repaint the dot". That was false: SessionStatusWingman subscribes to the activity state and
    /// NOTHING else, and this event had ZERO subscribers of any kind. It was a comment describing a
    /// listener that did not exist - which is how defect 14 stayed invisible, because the event looked
    /// wired. It is now subscribed by <c>ControlApiHost.WireDoorbellPush</c>, which pushes the changed fact
    /// up the stream so the Gateway can fold it promptly instead of up to ten seconds late.
    /// </summary>
    public bool IsExplaining
    {
        get => _isExplaining;
        set
        {
            if (_isExplaining == value) return;
            _isExplaining = value;
            OnIsExplainingChanged?.Invoke(value);
        }
    }

    /// <summary>Fires when <see cref="IsExplaining"/> changes. Arg: new value.</summary>
    public event Action<bool>? OnIsExplainingChanged;

    private bool _isTranscribing;

    /// <summary>
    /// True while a dictated utterance is being transcribed and submitted into this session in the
    /// background: the desktop Speak dialog released the screen the instant Send was pressed, and the
    /// recorded audio is being transcribed off the UI thread (see the desktop background dictation
    /// send). A user-driven overlay ORTHOGONAL to <see cref="ActivityState"/>, exactly like
    /// <see cref="IsExplaining"/>: the underlying activity state is still reported truthfully, and this
    /// flag rides on top. Set true when Send is pressed and cleared when the background
    /// transcribe-and-submit finishes or fails. Transient (in-memory only).
    ///
    /// This is a RAW FACT. It is reported on <c>SessionDto.IsTranscribing</c> and the GATEWAY folds the
    /// orange from it; <c>SessionStatusWingman</c> has not painted it since Phase 2.3, whatever this
    /// comment used to claim ("paints the badge Orange", "notifies the SessionStatusWingman so it can
    /// repaint the dot"). Nor did the wingman subscribe: this event's only subscriber was a desktop UI
    /// handler, which pushes nothing - so the fact sat here until some unrelated change happened to push a
    /// delta. That was defect 14. <c>ControlApiHost.WireDoorbellPush</c> now pushes on this event.
    /// (Note the orange does NOT lock the session either - #1308 removed that; see the DTO.)
    /// </summary>
    public bool IsTranscribing
    {
        get => _isTranscribing;
        set
        {
            if (_isTranscribing == value) return;
            _isTranscribing = value;
            OnIsTranscribingChanged?.Invoke(value);
        }
    }

    /// <summary>Fires when <see cref="IsTranscribing"/> changes. Arg: new value. Subscribed by
    /// <c>ControlApiHost.WireDoorbellPush</c> (which pushes the fact up so the Gateway can fold the orange
    /// promptly - defect 14) and by the desktop's own UI. NOT by the SessionStatusWingman, which this
    /// comment used to name and which has never subscribed to it.</summary>
    public event Action<bool>? OnIsTranscribingChanged;

    private bool _isBackgroundRunning;
    private string _backgroundReason = "running in background";
    private int? _uncommittedCount;

    /// <summary>
    /// True when the Wingman has read the screen and determined this session is parked
    /// waiting on its OWN background task (a long build, "N shell still running") rather
    /// than on the user. A Wingman-owned overlay ORTHOGONAL to <see cref="ActivityState"/>,
    /// exactly like <see cref="IsExplaining"/>: the <c>TerminalStateDetector</c> still reports
    /// the true underlying <see cref="ActivityState.WaitingForInput"/> (the dumb 10s silence
    /// timer cannot tell a background-wait apart from "your turn"), and this flag rides on top.
    /// Set by <c>ProactiveExplainService</c> from the explain verdict via
    /// <see cref="SetBackgroundRunning"/>; auto-cleared the moment real output resumes (the session
    /// transitions off WaitingForInput in <see cref="SetActivityState"/>). Transient (in-memory only);
    /// it tracks a live read of the screen, not durable state.
    ///
    /// This is a RAW FACT. It is reported on <c>SessionDto.IsBackgroundRunning</c>, and NO COLOUR READS IT: the
    /// Gateway's background purple was deleted by the Wingman-on-every-turn mission, so purple has one producer,
    /// the calm verdict arm. <c>SessionStatusWingman</c> does not "paint the badge Purple" and has not since
    /// Phase 2.3 - it emits blue, red and unknown only. <see cref="OnIsBackgroundRunningChanged"/> had ZERO
    /// subscribers anywhere until defect 14 wired the push.
    /// </summary>
    public bool IsBackgroundRunning
    {
        get => _isBackgroundRunning;
        private set
        {
            if (_isBackgroundRunning == value) return;
            _isBackgroundRunning = value;
            OnIsBackgroundRunningChanged?.Invoke(value);
        }
    }

    /// <summary>Short reason for the Purple background state, shown as the badge tooltip,
    /// e.g. "running in background". Set alongside <see cref="IsBackgroundRunning"/>.</summary>
    public string BackgroundReason => _backgroundReason;

    /// <summary>Fires when <see cref="IsBackgroundRunning"/> changes. Arg: new value. Subscribed by
    /// <c>ControlApiHost.WireDoorbellPush</c>, which pushes the fact up so the Gateway can fold the purple
    /// promptly (defect 14). NOT by the SessionStatusWingman, which this comment used to name and which has
    /// never subscribed to it - before defect 14 this event had no subscribers at all.</summary>
    public event Action<bool>? OnIsBackgroundRunningChanged;

    /// <summary>
    /// How many files are changed in this session's working tree (staged plus unstaged plus untracked),
    /// or NULL when nobody has been able to tell yet. Written only by <c>SessionGitStatusMonitor</c>,
    /// which polls <c>GitStatusProvider</c> on the Director; reported on <c>SessionDto.UncommittedCount</c>
    /// so the desktop rail, the Cockpit roster and the phone all read ONE number instead of each polling
    /// git for themselves.
    ///
    /// NULL IS A REAL ANSWER AND IS NEVER RENDERED AS ZERO (issue 516). A git probe can fail - a missing
    /// git executable, a permissions problem, a repository that is mid-rebase - and reporting 0 there would
    /// erase the difference between "this tree is clean" and "we could not tell", which every reader
    /// downstream would show as a clean tree. The monitor therefore leaves the LAST KNOWN value in place on
    /// a failed probe and only ever publishes a count a probe actually produced; null means no probe has
    /// ever succeeded for this session.
    /// </summary>
    public int? UncommittedCount
    {
        get => _uncommittedCount;
        set
        {
            if (_uncommittedCount == value) return;
            _uncommittedCount = value;
            OnUncommittedCountChanged?.Invoke(value);
        }
    }

    /// <summary>Fires when <see cref="UncommittedCount"/> changes. Arg: the new count (null when unknown).
    /// Subscribed by <c>ControlApiHost.WireDoorbellPush</c>, which pushes the session up the stream so the
    /// Cockpit badge moves when the count moves rather than waiting for the next ten-second re-push, and by
    /// the desktop rail, which re-renders the badge in place.</summary>
    public event Action<int?>? OnUncommittedCountChanged;

    private int _turnCount;
    private DateTime? _waitingSince;
    private double _cumulativeIdleSeconds;
    private int _waitingStretchCount;

    /// <summary>Clock for the supervision facts below. A test seam; production never sets it.</summary>
    internal Func<DateTime> SupervisionClock { private get; set; } = static () => DateTime.UtcNow;

    /// <summary>
    /// How many turns the agent has completed in this run of the session: one flip of
    /// <see cref="ActivityState"/> to <see cref="ActivityState.WaitingForInput"/> equals one turn -
    /// the same rule <c>TurnReviewLogger</c> writes its records by. Kept as a running count at the
    /// flip so no reader ever re-parses a transcript, and so it works for every agent kind (the
    /// on-demand <c>ComputeTurnCount</c> is a full JSONL re-parse and Claude-only).
    /// Reported on <c>SessionDto.TurnCount</c>.
    /// </summary>
    public int TurnCount => _turnCount;

    /// <summary>
    /// UTC moment this session last entered a waiting-on-the-user state (WaitingForInput or
    /// WaitingForPerm), or null while it is not waiting. An absolute anchor, not a duration:
    /// readers derive the ticking "sitting on you for X" clock from it. Survives the
    /// WaitingForPerm-to-WaitingForInput transition - one uninterrupted wait, one anchor.
    /// </summary>
    public DateTime? WaitingSince => _waitingSince;

    /// <summary>
    /// Total seconds this session has spent waiting on the user, summed over CLOSED waiting
    /// stretches. The stretch currently open (<see cref="WaitingSince"/> non-null) is NOT yet
    /// included - a reader adds it for the live total. This is the honest idle clock the cards
    /// show; it is NOT the byte-silence <c>IdleSeconds</c> in the mapper, which resets on any
    /// terminal output.
    /// </summary>
    public double CumulativeIdleSeconds => _cumulativeIdleSeconds;

    /// <summary>
    /// How many times this session has STARTED waiting on the user (devthrottle_internal issue #982) -
    /// one per entry into a waiting state, counted at the same flip that opens the stretch
    /// <see cref="CumulativeIdleSeconds"/> later closes. The two are a matched pair: seconds waited is
    /// the total, this is the number of times, and a fleet where the brain is doing its job should move
    /// one without the other.
    ///
    /// The COUNT of interruptions, which the total seconds cannot stand in for: one session that needed
    /// you once for an hour and one that needed you twelve times for five minutes read identically on
    /// the clock and are completely different to live with. The brain's whole job is deciding when to
    /// interrupt, so this is the denominator it gets measured on.
    ///
    /// Counts the DIRECTOR's waiting state, which is close to but not identical with the Gateway's red
    /// verdict: a wait that the wingman is still narrating, or one the owner has snoozed, is counted
    /// here and is not red yet. That is the honest thing a Director can measure - it cannot see the
    /// Gateway's overlays - and it is the raw fact a red-event count would have to be built from
    /// anyway. Reported on <c>SessionDto.WaitingStretchCount</c>.
    ///
    /// The stretch that is currently OPEN is already counted (it started), unlike the seconds, which
    /// are only added when it closes. That is deliberate: a session waiting on you right now has
    /// interrupted you, whatever happens next.
    /// </summary>
    public int WaitingStretchCount => _waitingStretchCount;

    /// <summary>
    /// Supervision bookkeeping (internal#625 Phase 1): the turn counter and the waiting clock.
    /// Runs INSIDE <see cref="SetActivityState"/> before <see cref="OnActivityStateChanged"/> fires,
    /// so the delta push wired to that event always carries the numbers this very flip produced -
    /// a subscriber could run after the push and ship stale values. Reporting only, no rulings.
    /// </summary>
    private void RecordSupervisionFacts(ActivityState old, ActivityState @new)
    {
        var wasWaiting = old is ActivityState.WaitingForInput or ActivityState.WaitingForPerm;
        var isWaiting = @new is ActivityState.WaitingForInput or ActivityState.WaitingForPerm;

        if (@new == ActivityState.WaitingForInput)
            _turnCount++;

        if (!wasWaiting && isWaiting)
        {
            _waitingSince = SupervisionClock();
            // The interruption count (issue #982), incremented at the SAME flip that opens the stretch
            // the idle clock later closes. Counting it here rather than at the close is what makes an
            // open wait count: a session sitting on you right now has interrupted you already, and a
            // count that only moved on release would report zero for the sessions still waiting - the
            // exact ones the question is about.
            _waitingStretchCount++;
        }
        else if (wasWaiting && !isWaiting)
        {
            if (_waitingSince is { } since)
                _cumulativeIdleSeconds += Math.Max(0, (SupervisionClock() - since).TotalSeconds);
            _waitingSince = null;
        }
    }

    /// <summary>
    /// Set (or clear) the Wingman's "parked on a background task" verdict for this session.
    /// Sole caller is <c>ProactiveExplainService</c> after an explain briefing. Pass a short
    /// reason when <paramref name="running"/> is true (used as the badge tooltip); clearing
    /// resets the reason to the default. The flag only affects the colour while the session is parked at a
    /// turn-end - a rule the GATEWAY applies, in <c>SessionOrdering.ResolveActivity</c>, from the raw fact
    /// on the wire. (This comment used to cite <c>SessionStatusWingman.ColorFor</c>, a method that has not
    /// existed since phase 2.3.) Under the law the turn-end gate is what matters: a session that is WORKING
    /// is blue, and no background verdict can outrank that.
    /// </summary>
    public void SetBackgroundRunning(bool running, string? reason = null)
    {
        // string.IsNullOrWhiteSpace is annotated [NotNullWhen(false)], so in the else branch
        // the compiler already knows reason is non-null -- no null-forgiving operator needed.
        if (running)
            _backgroundReason = string.IsNullOrWhiteSpace(reason) ? "running in background" : reason.Trim();
        else
            _backgroundReason = "running in background";
        IsBackgroundRunning = running;
    }

    /// <summary>
    /// True until the user submits real input for the first time. New sessions boot with
    /// this flag set so the ProactiveExplainService can skip the first turn-end briefing
    /// (there is nothing yet to explain) and the Wingman tab can show a canned greeting
    /// instead. Cleared on the first <see cref="SendInput"/> with a submit byte or the
    /// first <see cref="SendTextAsync"/>. Restored sessions start with this <c>false</c>
    /// because they already have history.
    /// </summary>
    public bool IsBrandNew { get; set; } = true;

    /// <summary>Latest proactively-generated wingman briefing, or null if none yet.</summary>
    public string? CachedExplainText { get; private set; }

    /// <summary>When <see cref="CachedExplainText"/> was last generated (UTC).</summary>
    public DateTime? CachedExplainAt { get; private set; }

    /// <summary>Model that produced the cached briefing (e.g. "opus").</summary>
    public string? CachedExplainModel { get; private set; }

    /// <summary>Tap-to-answer options from the latest briefing (may be empty).</summary>
    public IReadOnlyList<string> CachedQuickReplies { get; private set; } = System.Array.Empty<string>();

    /// <summary>One-line headline from the latest briefing for the session card / list view.</summary>
    public string? CachedExplainHeadline { get; private set; }

    /// <summary>Latest briefing's on-screen "what's happened" QUICK line (one short sentence, scan-friendly).</summary>
    public string? CachedExplainWhatHappened { get; private set; }

    /// <summary>Latest briefing's on-screen "what's happened" LONGER detail (1-2 short paragraphs, may contain a markdown table).</summary>
    public string? CachedExplainLongDescription { get; private set; }

    /// <summary>Latest briefing's on-screen "what Claude wants" section (verbatim agent question when state is red).</summary>
    public string? CachedExplainWhatClaudeWants { get; private set; }

    /// <summary>Latest briefing's trust anchor: the agent's decisive line copied verbatim from the
    /// terminal (server-side validated, empty when unverified or nothing pending). Rendered as
    /// Claude's own words above <see cref="CachedExplainWhatClaudeWants"/> so a drifting summary
    /// is visible against it - mirrors the turn brief's verbatim evidence.</summary>
    public string? CachedExplainClaudeVerbatim { get; private set; }

    /// <summary>Latest briefing's spoken-version field, used by the phone's voice mode on demand. No markdown.</summary>
    public string? CachedExplainSay { get; private set; }

    /// <summary>
    /// Store a freshly-generated proactive explain briefing. Only replaces the cache when
    /// <paramref name="text"/> is non-empty, so a failed or timed-out regeneration preserves
    /// the last good briefing instead of blanking the phone screen on a huge-context turn.
    /// </summary>
    public void SetCachedExplain(string? text, string? model, IReadOnlyList<string>? quickReplies = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        CachedExplainText = text;
        CachedExplainModel = model;
        CachedQuickReplies = quickReplies ?? System.Array.Empty<string>();
        CachedExplainAt = DateTime.UtcNow;
        OnCachedExplainChanged?.Invoke();
    }

    /// <summary>Fires after <see cref="SetCachedExplain"/> stores a new briefing. The
    /// Wingman tab subscribes so it can re-render whenever the proactive explain pipeline
    /// has produced a fresh result. Re-renders read the structured fields off the session
    /// directly; the event carries no payload.</summary>
    public event Action? OnCachedExplainChanged;

    /// <summary>
    /// Store the structured fields from a freshly-generated explain briefing alongside the
    /// joined text. Fields are independent of <see cref="SetCachedExplain"/> so the caller
    /// can update them in one shot from <see cref="WingmanAskResult"/>.
    /// </summary>
    public void SetCachedExplainStructured(string? headline, string? whatHappened, string? longDescription, string? whatClaudeWants, string? say, string? claudeVerbatim = null)
    {
        CachedExplainHeadline = string.IsNullOrWhiteSpace(headline) ? null : headline.Trim();
        CachedExplainWhatHappened = string.IsNullOrWhiteSpace(whatHappened) ? null : whatHappened.Trim();
        CachedExplainLongDescription = string.IsNullOrWhiteSpace(longDescription) ? null : longDescription.Trim();
        CachedExplainWhatClaudeWants = string.IsNullOrWhiteSpace(whatClaudeWants) ? null : whatClaudeWants.Trim();
        CachedExplainClaudeVerbatim = string.IsNullOrWhiteSpace(claudeVerbatim) ? null : claudeVerbatim.Trim();
        CachedExplainSay = string.IsNullOrWhiteSpace(say) ? null : say.Trim();
    }

    /// <summary>
    /// Aggregate at-a-glance color for this session. Owned by the
    /// SessionStatusWingman on the Director; the rest of the system reads it but never
    /// writes it. Defaults to "blue" ("working/starting") at construction, which the
    /// wingman immediately confirms. The detector only ever drives blue (working) and
    /// red (needs you); "unknown" is used for an exited session.
    ///
    /// GAP 3 - SCOPED 15 JULY 2026, AND IT DOES NOT CLOSE. THIS FIELD STAYS. Here is the census, so the
    /// next reader does not have to redo it and does not delete this on the strength of a document.
    ///
    /// The gap said "the Director's cooked colour is not deleted; it survives only because things still
    /// read it", the implication being that once the last reader went, so would this. The census says
    /// otherwise, and the distinction that matters is between A CLIENT DECIDING A COLOUR (which law 2
    /// forbids) and THE WINGMAN REMEMBERING WHAT IT DECIDED (which is just a component's own record).
    ///
    /// The readers that KEEP THIS FIELD ALIVE are all the second kind, and they are the reason it stays.
    /// They are not, however, the only readers - the <c>?statusColor=</c> filter below is neither kind,
    /// and an earlier draft of this census claimed "every surviving reader" was wingman-memory, which was
    /// false. It is listed where it belongs rather than inside this list, because a census that quietly
    /// widens one category to swallow an awkward member is not a census.
    ///
    /// THE READERS THAT JUSTIFY KEEPING IT:
    ///
    ///  - WingmanContextBuilder -> WingmanAskContext.CurrentColor: the wingman's LLM prompt. Telling the
    ///    wingman what it last concluded is not a client picking a colour; it is the wingman's own memory.
    ///  - SessionReadExecutor -> WingmanViewDto.CurrentColor: the wingman's event view - "what did you say
    ///    and why". Its subject IS the wingman's decision, so reading the decision is the feature.
    ///  - TurnReviewLogger -> TurnReviewLog.StatusColor -> TurnReviewDialog: a HISTORICAL record of what the
    ///    wingman decided at a past turn. The dialog renders the stored row, not this live field - a log of
    ///    a decision, which is the opposite of a decision.
    ///  - SessionLogWriter: subscribes OnStatusColorChanged to write a log line.
    ///  - SetStatusColor itself: reads the old value for its precedence guard.
    ///
    /// Deleting the field would therefore delete the wingman's record of its own reasoning to make a
    /// sentence in a document true. That is this mission's own failure mode wearing a cleanup costume.
    ///
    /// THE FOLD READS THIS FOR NOTHING - verified, not assumed: SessionOrdering mentions StatusColor only
    /// in comments and tombstones, and folds from the raw ActivityState instead.
    ///
    /// NO LIVE CLIENT RENDERS IT. The rail, the Cockpit and the /m progressive web app all fold, and the
    /// FIFO queue window stopped reading it when gap 2 closed. Read that sentence as narrowly as it is
    /// written - the qualifier is load-bearing:
    ///
    ///  - THE RETIRED MAUI CLIENT STILL RENDERS AND FILTERS ON IT, and an earlier draft of this census
    ///    said "no client renders it" full stop, which is false in a repository that contains that code.
    ///    phone/CcDirectorClient: TalkPage.xaml.cs (DotFor(s.StatusColor)), ExesPage.xaml.cs (same), and
    ///    Voice/SessionFilter.cs, which decides "needs attention" from StatusColor == "red" - a client
    ///    both rendering AND triaging on the Director's cooked colour, which is exactly what law 2 forbids.
    ///    It does NOT count as a live reader and it is NOT a defect to fix: that project is the
    ///    discontinued native Android app (net10.0-android, UseMaui), it is NOT in cc-director.sln so it
    ///    neither builds nor tests with us, and it was retired in favour of the /m web app - native-only
    ///    bugs there are closed, not fixed. Found by independent inspection, which called it a live defect
    ///    because nothing in the code says it is dead. If that app is ever revived, this is a real defect
    ///    on its first day.
    ///  - THE GATEWAY'S <c>?statusColor=</c> QUERY FILTER SELECTS ON IT, server-side: GatewayEndpoints
    ///    compares s.StatusColor and `continue`s, so the cooked colour decides which sessions a caller
    ///    SEES. The same earlier draft filed this under "carrying a fact is not deciding from it", which
    ///    was wrong twice over - it is not carrying, and selecting IS deciding. It is the Director's colour
    ///    choosing a caller's roster.
    ///  - The Exes payload does carry it (ExesEndpoints, beside effectiveColor/stateLabel), and that one
    ///    genuinely is carrying: the live page renders the fold.
    ///
    /// ONE PRESENTATION READER IS LEFT, AND IT IS NOT THIS FIELD - it is the wire copy, SessionDto
    /// .StatusColor, at LoopbackCarModeFleet.ToInfo: <c>StateLabel ?? (EffectiveColor ?? StatusColor)</c>,
    /// which Car Mode SPEAKS. It is a fallback chain that ends at the Director's cooked colour, so on paper
    /// a client still renders a Director decision. It appears unreachable - SessionOrdering.StateLabel
    /// returns a non-empty literal on every arm, and the Gateway stamps it for every session in the fleet
    /// pass - but "appears unreachable" is not a proof, and the one hole (a blank DictationStatus returns
    /// blank) is real. NOT changed here: what Car Mode says when the fold's label is blank is a question
    /// about Car Mode's spoken output, not about this field, and it is raised with the Architect rather
    /// than guessed at.
    /// </summary>
    public string StatusColor { get; private set; } = "blue";

    /// <summary>
    /// Short human-readable reason for the current StatusColor, e.g.
    /// "session created", "working", "waiting for input", "clean turn". Shown
    /// as the dot tooltip in the Gateway directory view. Set together with
    /// <see cref="StatusColor"/> via <see cref="SetStatusColor"/>.
    /// </summary>
    public string LastStatusReason { get; private set; } = "session created";

    /// <summary>
    /// The verbatim text of the most recent prompt the user submitted to this session,
    /// or null if none has been seen. Its only source was the Claude Code
    /// <c>UserPromptSubmit</c> hook, which has been removed (terminal-driven detection
    /// does not parse user prompts), so it is currently always null. Kept because
    /// <c>GET /sessions/{sid}/wingman</c> surfaces it; a terminal-derived source can
    /// repopulate it later.
    /// </summary>
    public string? LastUserPrompt { get; private set; }

    /// <summary>UTC time <see cref="LastUserPrompt"/> was captured, or null if none.</summary>
    public DateTime? LastUserPromptAt { get; private set; }

    /// <summary>
    /// UTC time this session was flagged for deletion via the Control API
    /// (POST /sessions/{id}/request-deletion), or null if it is not flagged. When set, the
    /// Director's deletion reaper removes the session on its next sweep once the grace window
    /// has elapsed AND the session is not actively Working. Set/cleared via
    /// <see cref="MarkForDeletion"/> / <see cref="CancelDeletion"/>. The common case is a session
    /// flagging ITSELF: an unattended run that has nothing left for the user asks to be reaped and
    /// then finishes its turn normally - the reaper does the removal asynchronously, so the calling
    /// process is never yanked out from under an in-flight request. In-memory only (not persisted):
    /// a session is reaped within ~a minute, so it effectively never survives to a Director restart.
    /// </summary>
    public DateTime? DeletionRequestedAt { get; private set; }

    /// <summary>Short human reason captured when <see cref="DeletionRequestedAt"/> was set
    /// (e.g. "jobs-auto: nothing to report"), surfaced in the roster tooltip. Null when not flagged.</summary>
    public string? DeletionReason { get; private set; }

    /// <summary>True when this session has been flagged for deletion and is awaiting the reaper.</summary>
    public bool PendingDeletion => DeletionRequestedAt.HasValue;

    /// <summary>
    /// Raised when <see cref="PendingDeletion"/> changes, so a view (the desktop rail) can show or
    /// clear the "winding down" badge. Carries the new value.
    ///
    /// This exists because the fact must travel as a FACT (defect 23). Flagging for deletion used to
    /// notify the rail only as a side effect of <c>MarkForDeletion</c> writing a colour - the Director
    /// deciding a colour, which law 2 forbids. Deleting that write removed the notification with it, so
    /// the badge needs its own signal. Same shape as <see cref="OnNumberChanged"/> and
    /// <see cref="OnHoldChanged"/>: report what changed, decide nothing about how it looks.
    /// </summary>
    public event Action<bool>? OnPendingDeletionChanged;

    /// <summary>Fires when StatusColor changes. Args: (oldColor, newColor, reason).</summary>
    public event Action<string, string, string>? OnStatusColorChanged;

    /// <summary>
    /// One decision the SessionStatusWingman wrote onto this session: what color it
    /// chose, what reason, when, and which path produced it ("activity" | "turn-summary"
    /// | "promote" | "init" | "buffer-marker").
    /// </summary>
    public sealed record WingmanEvent(
        DateTime At,
        string OldColor,
        string NewColor,
        string Reason,
        bool Llm = false);

    private const int WingmanEventLogCapacity = 50;
    private readonly LinkedList<WingmanEvent> _wingmanEvents = new();
    private readonly object _wingmanEventsLock = new();

    /// <summary>
    /// One actuation the Wingman performed on this session's terminal (structured-intent
    /// path): the action kind ("type" | "send_keys" | "submit"), a short detail of what
    /// was sent, and the Wingman's stated reason. Distinct from <see cref="WingmanEvent"/>
    /// (a colour change) - this is a WRITE the Wingman made, and the audit trail for it.
    /// </summary>
    public sealed record WingmanActionRecord(
        DateTime At,
        string Action,
        string Detail,
        string Reason);

    private const int WingmanActionLogCapacity = 50;
    private readonly LinkedList<WingmanActionRecord> _wingmanActions = new();
    private readonly object _wingmanActionsLock = new();

    /// <summary>
    /// One activity-state transition for this session: when it happened and the state it
    /// moved from -&gt; to. The detector's only rule produces Working (bytes) and
    /// WaitingForInput (silence), so in practice this is the blue&lt;-&gt;red history the
    /// Wingman tab renders. Distinct from <see cref="WingmanEvent"/>, which records the
    /// resulting colour change rather than the underlying state.
    /// </summary>
    public sealed record StateChange(
        DateTime At,
        ActivityState From,
        ActivityState To);

    private const int StateChangeLogCapacity = 100;
    private readonly LinkedList<StateChange> _stateChanges = new();
    private readonly object _stateChangesLock = new();

    /// <summary>
    /// UTC time the terminal buffer last received ANY bytes (raw "characters moved",
    /// before the detector's cosmetic-vs-content filtering). The Wingman tab shows
    /// "how long ago the terminal moved" off this; a large value next to a "working"
    /// badge is the tell that the quiet gate has stalled. Updated on every buffer write.
    /// </summary>
    public DateTime LastOutputAtUtc => new(Volatile.Read(ref _lastOutputTicks), DateTimeKind.Utc);
    private long _lastOutputTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// UTC time the screen BODY last changed, as judged by the <c>TerminalStateDetector</c>'s
    /// content rule. For agents whose idle terminal never goes byte-silent (Grok), this is the
    /// honest idle clock: <see cref="LastOutputAtUtc"/> keeps moving forever as the animated
    /// footer repaints, but this only advances when the conversation body actually changes, so
    /// "time since the last body change" is a true measure of how long the agent has been idle.
    /// The Control API surfaces idle seconds off this for continuous-idle agents. Defaults to the
    /// creation time; only the detector advances it, via <see cref="StampBodyActivity"/>.
    /// </summary>
    public DateTime LastBodyActivityAtUtc => new(Volatile.Read(ref _lastBodyActivityTicks), DateTimeKind.Utc);
    private long _lastBodyActivityTicks = DateTime.UtcNow.Ticks;

    /// <summary>Record that the screen body changed now. Called by the detector's content rule;
    /// independent of whether the detector is driving state, so the idle clock is correct even in
    /// observe-only mode.</summary>
    internal void StampBodyActivity() => Volatile.Write(ref _lastBodyActivityTicks, DateTime.UtcNow.Ticks);

    /// <summary>
    /// UTC instant until which terminal byte activity must NOT be counted as agent work by
    /// the <c>TerminalStateDetector</c>. Set whenever the Director itself issues a PTY resize
    /// (on attaching/switching to a session, force-refresh, or a layout change): a resize is a
    /// SIGWINCH-equivalent that makes Claude Code repaint its whole screen, emitting a burst of
    /// real bytes that are OUR doing, not the agent producing output. Without this guard the
    /// detector flips an idle session to "Working" the instant you switch to it. The window is
    /// short (well under the detector's quiet threshold), so a genuine work-start that happens
    /// to land inside it is only delayed until the next byte after the window. Read by the
    /// detector; written via <see cref="SuppressActivityFor"/>.
    /// </summary>
    public DateTime SuppressActivityUntilUtc => new(Volatile.Read(ref _suppressActivityUntilTicks), DateTimeKind.Utc);
    private long _suppressActivityUntilTicks = DateTime.MinValue.Ticks;

    /// <summary>
    /// Mark the next <paramref name="window"/> of terminal byte activity as a Director-induced
    /// repaint that the <c>TerminalStateDetector</c> must ignore. Called right before a PTY
    /// resize. Always extends (never shortens) the current suppression window.
    /// </summary>
    public void SuppressActivityFor(TimeSpan window)
    {
        if (_disposed) return;
        var until = DateTime.UtcNow.Add(window).Ticks;
        long current;
        do
        {
            current = Volatile.Read(ref _suppressActivityUntilTicks);
            if (until <= current) return;
        }
        while (Interlocked.CompareExchange(ref _suppressActivityUntilTicks, until, current) != current);
    }

    /// <summary>
    /// Most recent activity-state transitions for this session, newest first. Ring-buffered
    /// at <see cref="StateChangeLogCapacity"/>. Populated by <see cref="RecordStateChange"/>
    /// (from <see cref="SetActivityState"/>) and rendered live by the Wingman tab.
    /// </summary>
    public IReadOnlyList<StateChange> RecentStateChanges
    {
        get
        {
            lock (_stateChangesLock)
                return _stateChanges.ToList();
        }
    }

    /// <summary>Fires when a new state transition is recorded, so the Wingman tab can
    /// refresh without polling. No args; the listener re-reads
    /// <see cref="RecentStateChanges"/>.</summary>
    public event Action? OnStateChangeRecorded;

    /// <summary>
    /// Record an activity-state transition into the in-memory ring (for the live Wingman
    /// tab) and notify listeners. Durable persistence is the caller's concern (see
    /// <c>StateChangeLog</c>), keeping this type free of file I/O.
    /// </summary>
    private void RecordStateChange(ActivityState from, ActivityState to)
    {
        lock (_stateChangesLock)
        {
            _stateChanges.AddFirst(new StateChange(DateTime.UtcNow, from, to));
            while (_stateChanges.Count > StateChangeLogCapacity)
                _stateChanges.RemoveLast();
        }
        OnStateChangeRecorded?.Invoke();
    }

    /// <summary>
    /// Monotonic counter bumped on every <see cref="ActivityState"/> change. Used by
    /// <see cref="SetStatusColor"/> to scope colour-source precedence to the current
    /// state "generation" (issue #136 option C).
    /// </summary>
    private long _activityGeneration;

    /// <summary>
    /// Current activity-state generation (see <see cref="_activityGeneration"/>). An
    /// async colour writer (e.g. the ~10s turn-summary) can sample this when its turn
    /// ends and pass it back so its write is dropped if the state has since moved on.
    /// </summary>
    public long ActivityGeneration => Interlocked.Read(ref _activityGeneration);

    /// <summary>The source of the last accepted colour write, and the generation it
    /// was accepted in. Together they make a positive-evidence verdict sticky within
    /// its generation so a lower-confidence write cannot repaint over it.</summary>
    private StatusColorSource _lastColorSource = StatusColorSource.ActivityState;
    private long _lastColorGeneration;

    /// <summary>
    /// Most recent wingman decisions for this session, newest first. Ring-buffered
    /// at <c>WingmanEventLogCapacity</c>. Surfaced via <c>GET /sessions/{sid}/wingman</c>
    /// so the UI can show WHY a dot is the color it is.
    /// </summary>
    /// <summary>
    /// Most recent Wingman actuations on this session, newest first. Ring-buffered at
    /// <c>WingmanActionLogCapacity</c>. Written only by <c>WingmanActionExecutor</c> via
    /// <see cref="RecordWingmanAction"/>; surfaced via <c>GET /sessions/{sid}/wingman</c>.
    /// </summary>
    public IReadOnlyList<WingmanActionRecord> RecentWingmanActions
    {
        get
        {
            lock (_wingmanActionsLock)
                return _wingmanActions.ToList();
        }
    }

    /// <summary>UTC time of the last Wingman injection, or null if none. With
    /// <see cref="LastActedScreenHash"/> this is the executor's idempotency/cooldown guard.</summary>
    public DateTime? LastWingmanInjectionAt { get; private set; }

    /// <summary>Screen hash the Wingman last acted on, or null. Used to suppress a repeat
    /// action on an unchanged screen.</summary>
    public string? LastActedScreenHash { get; private set; }

    /// <summary>Record that the Wingman just injected against the given screen hash. Sole
    /// writer is <c>WingmanActionExecutor</c>, called once per performed action.</summary>
    public void MarkWingmanInjection(string screenHash)
    {
        LastWingmanInjectionAt = DateTime.UtcNow;
        LastActedScreenHash = screenHash;
    }

    /// <summary>Append a performed actuation to the audit ring.</summary>
    public void RecordWingmanAction(WingmanActionRecord rec)
    {
        lock (_wingmanActionsLock)
        {
            _wingmanActions.AddFirst(rec);
            while (_wingmanActions.Count > WingmanActionLogCapacity)
                _wingmanActions.RemoveLast();
        }
    }

    public IReadOnlyList<WingmanEvent> RecentWingmanEvents
    {
        get
        {
            lock (_wingmanEventsLock)
                return _wingmanEvents.ToList();
        }
    }

    /// <summary>
    /// Writes <see cref="StatusColor"/>. THIS IS NOT THE SOLE WRITER, and it never was - this comment used
    /// to say "Sole writer... Called by the SessionStatusWingman. No other code path may set the color",
    /// which was false at the time it was written.
    ///
    /// Verified 14 July 2026, the THREE production callers:
    ///   1. <c>SessionStatusWingman</c> - the activity-state mapping (the one this comment described).
    ///   2. This file's crash arm - <c>SetStatusColor(Error, ...)</c> when the process dies unexpectedly.
    ///   3. <c>TransientErrorAutoResume</c> - a sticky PositiveEvidence red when auto-resume gives up.
    /// (A fourth, <c>MarkForDeletion</c>'s <c>SetStatusColor(Unknown, ...)</c>, was deleted by defect 23.)
    ///
    /// Why the lie mattered: 2 and 3 are BOTH higher-precedence writes than 1 - the source rule below makes
    /// a PositiveEvidence verdict sticky for a whole activity generation - so a reader who believed there
    /// was one writer would conclude the colour always follows the activity state, and be wrong exactly
    /// when it matters. If you are about to write "sole writer" here again, count the callers first.
    /// </summary>
    public void SetStatusColor(string color, string reason, bool llm = false,
        StatusColorSource source = StatusColorSource.ActivityState)
    {
        if (string.IsNullOrEmpty(color)) return;

        // Source precedence (issue #136 option C). Within one activity-state
        // generation, a positive-evidence verdict (a real on-screen question /
        // permission gate / corroborated needs-user) is sticky: a lower-confidence
        // write -- the activity-state mapping or a byte-stream guess -- cannot
        // repaint over it. This is what stops the badge flip-flopping. A genuine
        // state change bumps the generation (SetActivityState) and releases it.
        var gen = Interlocked.Read(ref _activityGeneration);
        if (gen == _lastColorGeneration
            && _lastColorSource == StatusColorSource.PositiveEvidence
            && source != StatusColorSource.PositiveEvidence)
        {
            FileLog.Write($"[Session] SetStatusColor dropped (lower precedence than sticky positive-evidence): color={color}, source={source}, gen={gen}");
            return;
        }

        var old = StatusColor;
        var newReason = reason ?? "";
        // Record precedence even when colour+reason are unchanged, so a repeated
        // positive-evidence verdict keeps (or re-establishes) its stickiness.
        _lastColorSource = source;
        _lastColorGeneration = gen;
        if (old == color && LastStatusReason == newReason) return;
        StatusColor = color;
        LastStatusReason = newReason;

        var evt = new WingmanEvent(DateTime.UtcNow, old, color, newReason, llm);
        lock (_wingmanEventsLock)
        {
            _wingmanEvents.AddFirst(evt);
            while (_wingmanEvents.Count > WingmanEventLogCapacity)
                _wingmanEvents.RemoveLast();
        }

        OnStatusColorChanged?.Invoke(old, color, LastStatusReason);
    }

    /// <summary>
    /// Flag this session for deletion. Idempotent: re-flagging refreshes the reason but keeps the
    /// ORIGINAL request time, so the grace window is always measured from the first request. The actual
    /// removal is done asynchronously by the Director's deletion reaper - this call never touches the
    /// process, so a session can safely flag ITSELF and still finish its turn.
    ///
    /// This RECORDS A FACT (<see cref="PendingDeletion"/> / <see cref="DeletionReason"/>) AND DECIDES
    /// NOTHING. Pending deletion is a BADGE, never a colour (owner's ruling, 14 July 2026, defect 23):
    /// it says nothing about what the agent is DOING, and a flagged session may still be working - the
    /// reaper explicitly waits out a running final turn (<c>SessionManager.ReapPendingDeletions</c>).
    /// Under the law a working session is BLUE, with a badge beside the dot.
    ///
    /// This used to call <c>SetStatusColor(StatusColor.Unknown, ...)</c> with
    /// <see cref="StatusColorSource.PositiveEvidence"/> - the Director deciding a colour, which law 2
    /// forbids. (An earlier version of this comment went further and said "nothing that paints reads
    /// anyway (the Gateway is the single fold and reads the Director's cooked StatusColor for NOTHING)".
    /// That is FALSE and it is struck: the Gateway gates its voice-yellow briefing stamp on
    /// <c>StatusColor == "red"</c>, and the desktop's needs-you count reads it directly. The
    /// FOLD reads it for nothing, which is a much narrower claim. Deleting this ONE write was right because
    /// the Director must not decide a colour - not because the colour is unread.) Because that write was
    /// positive-evidence it was
    /// also STICKY: within one activity generation it blocked the wingman's activity mapping from
    /// repainting the row, so a flagged session that was working could not show blue until a genuine
    /// state change bumped the generation. Do not restore it. The fact crosses the wire on
    /// <c>SessionDto.PendingDeletion</c>; the Gateway folds the colour, and clients render the badge.
    /// </summary>
    public void MarkForDeletion(string? reason)
    {
        if (_disposed) return;
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var wasPending = PendingDeletion;
        DeletionReason = trimmed;
        DeletionRequestedAt ??= DateTime.UtcNow;
        FileLog.Write($"[Session] MarkForDeletion: session={Id} reason={trimmed ?? "(none)"}");
        // Fires only on a real transition: re-flagging refreshes the reason and is not a change.
        if (!wasPending)
        {
            try { OnPendingDeletionChanged?.Invoke(true); }
            catch (Exception ex) { FileLog.Write($"[Session] {Id} OnPendingDeletionChanged handler threw: {ex.Message}"); }
        }
    }

    /// <summary>Clear a pending-deletion flag (an operator cancelled the reap during the grace
    /// window). No-op when the session was not flagged.</summary>
    public void CancelDeletion()
    {
        if (DeletionRequestedAt is null) return;
        DeletionRequestedAt = null;
        DeletionReason = null;
        FileLog.Write($"[Session] CancelDeletion: session={Id}");
        try { OnPendingDeletionChanged?.Invoke(false); }
        catch (Exception ex) { FileLog.Write($"[Session] {Id} OnPendingDeletionChanged handler threw: {ex.Message}"); }
    }

    /// <summary>
    /// Drop the per-session Wingman context that describes the conversation BEFORE
    /// a <c>/clear</c>: the status-event log and the terminal replay buffer. Claude
    /// Code rotates its session id on <c>/clear</c> (new, empty JSONL transcript), so
    /// without this the Wingman keeps narrating the pre-clear conversation. NOT called
    /// for <c>/compact</c>, which keeps the conversation going. The turn-summary cache
    /// lives outside the Session and is cleared by its owner via
    /// <see cref="SessionManager.OnSessionContextReset"/>.
    /// </summary>
    public void ClearWingmanContext()
    {
        FileLog.Write($"[Session] ClearWingmanContext: session={Id}");
        lock (_wingmanEventsLock)
            _wingmanEvents.Clear();
        Buffer?.Clear();
    }

    // ---- Wingman goal management ----
    // The session's stated objective plus the Wingman's latest verdict on whether
    // the session is still working toward it. Goal-tracking is dormant until a goal
    // is set. Observational only: the verdict is surfaced, never auto-acted on.
    private readonly object _goalLock = new();
    private string? _wingmanGoal;
    private DateTime? _wingmanGoalSetAt;
    private string _wingmanGoalState = Gateway.Contracts.GoalStates.Unknown;
    private string _wingmanGoalReason = "";
    private DateTime? _wingmanGoalEvaluatedAt;

    /// <summary>The session's stated goal, or null if none set.</summary>
    public string? WingmanGoal { get { lock (_goalLock) return _wingmanGoal; } }

    /// <summary>UTC time the goal was last set, or null if none.</summary>
    public DateTime? WingmanGoalSetAt { get { lock (_goalLock) return _wingmanGoalSetAt; } }

    /// <summary>Latest goal verdict: on_track | drifting | complete | unknown.</summary>
    public string WingmanGoalState { get { lock (_goalLock) return _wingmanGoalState; } }

    /// <summary>Short plain-language reason for <see cref="WingmanGoalState"/>.</summary>
    public string WingmanGoalReason { get { lock (_goalLock) return _wingmanGoalReason; } }

    /// <summary>UTC time the goal was last assessed, or null if never.</summary>
    public DateTime? WingmanGoalEvaluatedAt { get { lock (_goalLock) return _wingmanGoalEvaluatedAt; } }

    /// <summary>
    /// Set (or clear) the session goal. Setting a new goal resets the verdict to
    /// "unknown" so a stale on_track/drifting/complete does not linger. Pass null
    /// or empty to clear the goal and stop goal-tracking.
    /// </summary>
    public void SetWingmanGoal(string? goal)
    {
        lock (_goalLock)
        {
            _wingmanGoal = string.IsNullOrWhiteSpace(goal) ? null : goal.Trim();
            _wingmanGoalSetAt = _wingmanGoal is null ? null : DateTime.UtcNow;
            _wingmanGoalState = Gateway.Contracts.GoalStates.Unknown;
            _wingmanGoalReason = "";
            _wingmanGoalEvaluatedAt = null;
        }
    }

    /// <summary>
    /// Record the Wingman's latest goal verdict. Ignored if no goal is set or the
    /// state is not one of the four valid values (we never store a fabricated verdict).
    /// </summary>
    public void SetWingmanGoalAssessment(string state, string reason, DateTime evaluatedAt)
    {
        if (!Gateway.Contracts.GoalStates.IsValid(state)) return;
        lock (_goalLock)
        {
            if (_wingmanGoal is null) return;
            _wingmanGoalState = state;
            _wingmanGoalReason = reason ?? "";
            _wingmanGoalEvaluatedAt = evaluatedAt;
        }
    }

    /// <summary>Access to the underlying backend for mode-specific operations.</summary>
    public ISessionBackend Backend => _backend;

    /// <summary>
    /// Create a new session with the specified backend.
    /// </summary>
    internal Session(
        Guid id,
        string repoPath,
        string workingDirectory,
        string? claudeArgs,
        ISessionBackend backend,
        SessionBackendType backendType,
        DateTimeOffset? createdAt = null)
    {
        Id = id;
        RepoPath = repoPath;
        WorkingDirectory = workingDirectory;
        ClaudeArgs = claudeArgs;
        _backend = backend;
        BackendType = backendType;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        Status = SessionStatus.Starting;

        // Subscribe to backend events
        _backend.ProcessExited += OnBackendProcessExited;
        _backend.StatusChanged += OnBackendStatusChanged;
        InitializeHtmlParser();
    }

    /// <summary>
    /// Create a session for restoring a persisted embedded session.
    /// </summary>
    internal Session(
        Guid id,
        string repoPath,
        string workingDirectory,
        string? claudeArgs,
        ISessionBackend backend,
        string? claudeSessionId,
        ActivityState activityState,
        DateTimeOffset createdAt,
        string? customName,
        string? customColor,
        string? pendingPromptText = null)
    {
        Id = id;
        RepoPath = repoPath;
        WorkingDirectory = workingDirectory;
        ClaudeArgs = claudeArgs;
        _backend = backend;
        BackendType = SessionBackendType.Embedded;
        ClaudeSessionId = claudeSessionId;
        ActivityState = activityState;
        CreatedAt = createdAt;
        CustomName = customName;
        CustomColor = customColor;
        PendingPromptText = pendingPromptText;
        Status = SessionStatus.Running;

        _backend.ProcessExited += OnBackendProcessExited;
        _backend.StatusChanged += OnBackendStatusChanged;
        InitializeHtmlParser();

        // Initialize history for restored sessions that already have a ClaudeSessionId
        InitializeHistory();
    }

    private void InitializeHtmlParser()
    {
        var buffer = _backend.Buffer;
        if (buffer is null)
        {
            FileLog.Write($"[Session] InitializeHtmlParser: sessionId={Id}, backend has no buffer (Embedded?), skipping");
            return;
        }

        _htmlCells = new TerminalCell[HtmlGridCols, HtmlGridRows];
        _htmlScrollback = new List<TerminalCell[]>();
        _htmlParser = new AnsiParser(_htmlCells, HtmlGridCols, HtmlGridRows, _htmlScrollback, HtmlMaxScrollback);

        // The live-attach parser tracks the real PTY size so its screen matches what a browser
        // xterm at the same geometry shows. It starts at the current PTY dimensions and is resized
        // in Resize() as the PTY changes.
        _streamGridCols = Math.Max(1, (int)CurrentCols);
        _streamGridRows = Math.Max(1, (int)CurrentRows);
        _streamCells = new TerminalCell[_streamGridCols, _streamGridRows];
        _streamScrollback = new List<TerminalCell[]>();
        _streamParser = new AnsiParser(_streamCells, _streamGridCols, _streamGridRows, _streamScrollback, StreamMaxScrollback);

        _htmlParserFeed = data =>
        {
            // Raw "the terminal moved" timestamp -- every byte, no cosmetic filtering.
            // The Wingman tab reads this to show how long ago output last appeared.
            Volatile.Write(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
            lock (_htmlParserLock)
            {
                _htmlParser?.Parse(data);
                _streamParser?.Parse(data);
                _streamBytesReflected += data.Length;
            }
        };
        buffer.OnBytesWritten += _htmlParserFeed;
        FileLog.Write($"[Session] InitializeHtmlParser: sessionId={Id}, grid={HtmlGridCols}x{HtmlGridRows}, streamGrid={_streamGridCols}x{_streamGridRows}, maxScrollback={HtmlMaxScrollback}");
    }

    /// <summary>
    /// Render the current terminal grid + scrollback as styled HTML, suitable
    /// for the "Raw terminal" tab in the HTML session view. Returns an empty
    /// string when the session has no backend buffer (Embedded mode).
    /// </summary>
    public string GetHtmlSnapshot()
    {
        if (_htmlParser is null || _htmlCells is null || _htmlScrollback is null)
            return string.Empty;

        lock (_htmlParserLock)
        {
            return AnsiToHtmlConverter.ConvertToHtml(_htmlScrollback, _htmlCells, HtmlGridCols, HtmlGridRows);
        }
    }

    /// <summary>
    /// Render scrollback and visible grid as two separate HTML strings, so the
    /// web client can render them into distinct DOM regions (scrollback above,
    /// sticky live grid at the viewport bottom). See
    /// <see cref="AnsiToHtmlConverter.ConvertToHtmlSplit"/> for the rationale.
    /// Returns ("", "", 0) when the session has no backend buffer.
    /// </summary>
    public (string ScrollbackHtml, string GridHtml, int ScrollbackCount) GetHtmlSnapshotSplit()
    {
        if (_htmlParser is null || _htmlCells is null || _htmlScrollback is null)
            return ("", "", 0);

        lock (_htmlParserLock)
        {
            var (sb, grid) = AnsiToHtmlConverter.ConvertToHtmlSplit(
                _htmlScrollback, _htmlCells, HtmlGridCols, HtmlGridRows);
            return (sb, grid, _htmlScrollback.Count);
        }
    }

    /// <summary>
    /// True when an earlier send by the product gave up without proving this composer clear, so the next send
    /// would press Escape before typing (issue #2818). Read, never taken - see <see cref="Drivers.ComposerRetention.MayHoldText"/>.
    /// Always false for a session without a terminal backend that submits through the shared path.
    /// </summary>
    public bool ProductMayHaveLeftComposerText =>
        BackendType is SessionBackendType.ConPty && Drivers.ComposerRetention.MayHoldText(_backend);

    /// <summary>
    /// Type and submit the fleet doorbell line (the Message Load mission) through
    /// <see cref="Drivers.TerminalSubmit.DoorbellSubmitAsync"/>: one Enter, no nudges, no Escape. Agent-origin -
    /// it never stamps an owner turn. Only a VERIFIED submit is recorded as a submitted turn and turns the
    /// session Working; anything else leaves the session's state alone and the caller reads the composer.
    /// </summary>
    public async Task<Drivers.DoorbellSubmitOutcome> SubmitDoorbellLineAsync(
        string line, Func<bool> mayTypeNow, Func<bool> composerShowsLine, Func<bool> turnStarted)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed || BackendType is not SessionBackendType.ConPty)
            return Drivers.DoorbellSubmitOutcome.NotTyped;
        FileLog.Write($"[Session] SubmitDoorbellLineAsync: session={Id}, driver={Driver.Kind}, len={line.Length}");
        // The origin is reported from the moment Enter is pressed, before the submit is verified, so a Working
        // push in between does not read as unexplained work (which would end an armed snooze).
        var priorOrigin = WorkingOrigin;
        var outcome = await Drivers.TerminalSubmit.DoorbellSubmitAsync(
            _backend, line, Driver.Kind.ToString(), mayTypeNow, composerShowsLine, turnStarted,
            beforeEnter: () => WorkingOrigin = Gateway.Contracts.WorkingOrigins.Agent);
        FileLog.Write($"[Session] SubmitDoorbellLineAsync: session={Id}, outcome={outcome}");
        if (outcome != Drivers.DoorbellSubmitOutcome.Verified && ActivityState is not (ActivityState.Working or ActivityState.Starting))
            WorkingOrigin = priorOrigin;
        if (outcome == Drivers.DoorbellSubmitOutcome.Verified)
        {
            IsBrandNew = false;
            StampSubmission(SendSource.Agent, null, SubmissionEvidence.OfText(
                SubmissionProvenance.Typed(SubmissionRoutes.FleetMessage, SubmissionIdentityKinds.Framework), line));
            SetActivityState(ActivityState.Working);
        }
        return outcome;
    }

    /// <summary>
    /// Press Backspace <paramref name="count"/> times, one key at a time. Used ONLY to take back the doorbell's
    /// own line when its submit was not verified and the composer holds exactly that line - the text is the
    /// product's, so removing it cannot touch the owner's words.
    /// </summary>
    public async Task EraseComposerCharactersAsync(int count)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed || BackendType is not SessionBackendType.ConPty)
            return;
        FileLog.Write($"[Session] EraseComposerCharactersAsync: session={Id}, count={count}");
        for (var i = 0; i < count; i++)
        {
            _backend.Write(BackspaceKey);
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }

    private static readonly byte[] BackspaceKey = [0x7F];

    /// <summary>
    /// Snapshot the CURRENT visible terminal grid (not scrollback) as plain-text rows,
    /// trailing-trimmed, top to bottom. Unlike the raw byte buffer this is the RESOLVED
    /// on-screen state, so a spinner cell or a churning status line shows only its
    /// current value and old frames do not linger concatenated. The
    /// <c>TerminalStateDetector</c> uses this to tell a working spinner ("esc to
    /// interrupt" on screen) apart from an idle status-line repaint, which the raw
    /// byte stream cannot. Returns an empty array when there is no grid (Embedded mode).
    /// </summary>
    public string[] SnapshotScreenRows() => SnapshotScreenRowsWithCursor().Rows;

    /// <summary>
    /// True when the agent currently has the terminal in the alternate screen buffer
    /// (full screen mode, the <c>ESC[?1049h</c> sequence). While this is true the local
    /// scrollback is intentionally empty and terminal-based history capture does not work,
    /// so a consumer should classify the session as full screen and rely on a transcript
    /// provider (or screen reconstruction) instead of the scrollback. Reflects the live
    /// parser state at the moment of the call; it flips as the agent enters and leaves the
    /// alternate screen. False for Embedded sessions that have no server-side parser.
    /// </summary>
    public bool IsAlternateScreen
    {
        get
        {
            lock (_htmlParserLock)
                return _htmlParser?.IsAlternateScreen ?? false;
        }
    }

    /// <summary>
    /// True when the terminal application has requested bracketed paste mode (DEC private mode
    /// ?2004). This is read-only parser state; submit strategies can use it as a gate before
    /// sending bracketed paste delimiters.
    /// </summary>
    public bool BracketedPasteEnabled
    {
        get
        {
            lock (_htmlParserLock)
                return _htmlParser?.BracketedPasteEnabled ?? false;
        }
    }

    /// <summary>
    /// Like <see cref="SnapshotScreenRows"/> but also returns the live cursor cell
    /// (0-based grid row/col). The grid text and the cursor are captured under the
    /// same lock so they describe the same frame. This lets callers tell text the
    /// user (or Claude Code) actually authored in the input box apart from a dim
    /// history/autocomplete suggestion: the suggestion always lives to the RIGHT of
    /// the cursor. Returns CursorRow/CursorCol of -1 when there is no grid (Embedded mode).
    /// </summary>
    public (string[] Rows, int CursorRow, int CursorCol) SnapshotScreenRowsWithCursor()
    {
        if (_htmlCells is null || _htmlParser is null)
            return (System.Array.Empty<string>(), -1, -1);
        // Read the parser's ACTIVE grid, not our held _htmlCells array. On the alternate
        // screen (Grok, and now Claude Code) the parser draws into an internal buffer that
        // _htmlCells no longer points at, so iterating _htmlCells here would return the
        // frozen pre-alternate-screen content. SnapshotActiveRows reflects what is on screen.
        lock (_htmlParserLock)
            return _htmlParser.SnapshotActiveRows();
    }

    /// <summary>
    /// The resolved live screen grid, the live cursor cell, the cursor VISIBILITY, and the alternate-screen
    /// flag captured from ONE coherent read (issue #1777). Reading these as separate locked reads lets a buffer
    /// switch land between them and report main-screen rows with a mismatched flag. A caller that classifies a
    /// waiting screen needs the flags to describe the SAME frame as the rows, so this takes them all under a
    /// single lock. Returns empty rows and (-1,-1) with the flags false when there is no grid (an Embedded
    /// session with no server-side parser).
    /// </summary>
    public (string[] Rows, int CursorRow, int CursorCol, bool CursorVisible, bool IsAlternateScreen) SnapshotLiveScreen()
    {
        if (_htmlCells is null || _htmlParser is null)
            return (System.Array.Empty<string>(), -1, -1, false, false);
        lock (_htmlParserLock)
        {
            var (rows, cursorRow, cursorCol) = _htmlParser.SnapshotActiveRows();
            // Cursor VISIBILITY is captured in the SAME locked frame as the rows/cursor/alt-screen: it is the
            // discriminator between a text composer (cursor visible) and a drawn Ink menu (cursor hidden, a
            // stale cursor cell), so it must describe the same frame the rows do (issue #1777).
            return (rows, cursorRow, cursorCol, _htmlParser.IsCursorVisible, _htmlParser.IsAlternateScreen);
        }
    }

    /// <summary>
    /// Snapshot the CURRENT visible terminal grid as rows of styled <see cref="ScreenSegment"/>
    /// runs, preserving the foreground/background colours and bold weight that
    /// <see cref="SnapshotScreenRows"/> throws away. Adjacent cells sharing a style are coalesced
    /// into one segment (matching how <c>AnsiToHtmlConverter</c> builds spans), trailing blank
    /// cells per row and trailing blank rows are trimmed. Returns an empty list when there is no
    /// grid (Embedded mode). Used by the turn-review log so a captured screen can be replayed in
    /// colour for a human reviewer.
    /// </summary>
    public List<IReadOnlyList<ScreenSegment>> SnapshotScreenColoredRows()
    {
        var result = new List<IReadOnlyList<ScreenSegment>>();
        if (_htmlCells is null || _htmlParser is null)
            return result;

        lock (_htmlParserLock)
        {
            for (int r = 0; r < HtmlGridRows; r++)
            {
                // Trim trailing blanks: the last column with a glyph or an explicit background.
                int lastCol = -1;
                for (int c = HtmlGridCols - 1; c >= 0; c--)
                {
                    var cell = _htmlCells[c, r];
                    if ((cell.Character != '\0' && cell.Character != ' ') || cell.Background != default)
                    {
                        lastCol = c;
                        break;
                    }
                }

                var row = new List<ScreenSegment>();
                if (lastCol >= 0)
                {
                    var sb = new System.Text.StringBuilder(HtmlGridCols);
                    string? curFg = null, curBg = null;
                    bool curBold = false, started = false;

                    for (int c = 0; c <= lastCol; c++)
                    {
                        var cell = _htmlCells[c, r];
                        var ch = cell.Character == '\0' ? ' ' : cell.Character;
                        // The parser paints uncoloured text with its default foreground
                        // (TerminalColor.LightGray); treat that - and an untouched cell - as
                        // "no explicit colour" so the viewer renders it with its own default.
                        var fg = cell.Foreground == default || cell.Foreground == TerminalColor.LightGray
                            ? null
                            : cell.Foreground.ToString();
                        var bg = cell.Background == default ? null : cell.Background.ToString();

                        if (!started)
                        {
                            curFg = fg; curBg = bg; curBold = cell.Bold; started = true;
                        }
                        else if (fg != curFg || bg != curBg || cell.Bold != curBold)
                        {
                            row.Add(new ScreenSegment(sb.ToString(), curFg, curBg, curBold));
                            sb.Clear();
                            curFg = fg; curBg = bg; curBold = cell.Bold;
                        }

                        sb.Append(ch);
                    }

                    if (sb.Length > 0)
                        row.Add(new ScreenSegment(sb.ToString(), curFg, curBg, curBold));
                }

                result.Add(row);
            }

            while (result.Count > 0 && result[^1].Count == 0)
                result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    /// <summary>
    /// The DevThrottle Stats per-session input tally (submitted turns + character volume by modality and
    /// surface). Recorded at THIS choke point so desktop-local input is counted too; read by the Director
    /// mapper for flow-up and by the Director stats store for persistence. Always present; empty until the
    /// first counted input.
    /// </summary>
    public SessionInputStats InputStats { get; } = new();

    /// <summary>Send raw bytes to the backend. <paramref name="origin"/> tags this input for the
    /// DevThrottle Stats tally. A bare keystroke is the user COMPOSING and is not a turn; the write that
    /// carries the Enter IS the submitted turn and is counted as one, with the character volume of the
    /// whole line. Null origin = framework-internal, not counted.</summary>
    /// <summary>Printable characters typed at the terminal since the last submission, accumulated so
    /// the turn counted on Enter carries the size of the whole line rather than of the final keystroke.
    /// Reset at each submission. See <see cref="StampSubmission"/>.</summary>
    private int _pendingOriginChars;

    public void SendInput(byte[] data, InputOrigin? origin, SubmissionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        FileLog.Write($"[Session] SendInput: session={Id}, bytes={data.Length}, firstByte=0x{(data.Length > 0 ? data[0].ToString("X2") : "00")}");
        _backend.Write(data);
        // Accumulate only. The tally is written at the submission below, by the one method that also
        // stamps the submission event, so the two can never disagree about how many turns there were
        // (see StampSubmission). Characters composed and never submitted are not counted, exactly as
        // text typed into the composer and never sent is not counted.
        if (origin is not null)
            _pendingOriginChars += CountPrintable(data);
        // Only promote to Working when the write contains an actual submission
        // (CR or LF). A bare keystroke is the user composing at the prompt --
        // Claude Code hasn't received a turn yet. Treating every byte as Working
        // flickered the sidebar dot blue on every character typed.
        if (ContainsSubmit(data))
        {
            IsBrandNew = false;
            // A submission supersedes a hold only when the OWNER made it. SendInput carries no SendSource,
            // so the origin IS the whole signal here: non-null means a person typed this (the desktop
            // terminal tags every keystroke DesktopTyped). A null origin reaches here from the Gateway
            // prompt path when an AGENT sends text with AppendEnter=false, and that must not un-snooze a
            // session the owner parked. See IsOwnerDriven for the same rule on the text path.
            if (origin is not null)
                StampOwnerTurn();
            // The origin is noted at the submission (issue #1551) and the turn is counted with it.
            // Terminal typing arrives here one keystroke at a time, so the prompt TEXT cannot be
            // reconstructed from these bytes - backspace, arrow-key edits, history recall, paste and
            // the agent's own autocomplete all mutate the line invisibly, and replaying keystrokes
            // would silently record prompts the user never sent. Only the provenance and the size are
            // recorded here; the text is read back from the agent's own transcript by the conversation
            // ingest and joined to this event by timestamp.
            StampSubmission(source: null, origin, SubmissionEvidence.OfKeystrokes(provenance, _pendingOriginChars));
            _pendingOriginChars = 0;
            SetActivityState(ActivityState.Working);
        }
    }

    private static bool ContainsSubmit(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
            if (data[i] == 0x0D || data[i] == 0x0A) return true;
        return false;
    }

    /// <summary>
    /// Count the printable bytes in a raw keystroke buffer for the DevThrottle Stats character tally:
    /// every byte at or above 0x20 except DEL (0x7F). Control bytes (Enter, arrows' leading ESC, Backspace)
    /// do not count. This is an honest approximation of typed character volume - an escape sequence's
    /// trailing letters (e.g. "[A" from an arrow key) count as a couple of characters of navigation
    /// activity, which the secondary character metric tolerates; the headline metric is TURNS, not chars.
    /// </summary>
    private static int CountPrintable(byte[] data)
    {
        int n = 0;
        for (int i = 0; i < data.Length; i++)
            if (data[i] >= 0x20 && data[i] != 0x7F) n++;
        return n;
    }

    /// <summary>
    /// Host-injected predicate answering "is this session locked because a dictation is inbound to
    /// it?" (issue #1181, Task 3b). The Director host wires this to <see cref="DictationLockReader"/>
    /// at startup; left null (never locked) in any process that does not run dictation delivery, and
    /// in unit tests. A single process-wide hook keeps the lock check on ONE fail-closed checkpoint
    /// (<see cref="SendTextAsync(string, SendSource)"/>) without threading a dependency through every
    /// session-creation path. Tests that set it must reset it to null in teardown.
    /// </summary>
    public static Func<Guid, bool>? DictationLockCheck { get; set; }

    /// <summary>
    /// Host-injected BULK form of <see cref="DictationLockCheck"/>: every session id currently holding an
    /// inbound dictation, read in one pass (issue #1111). Wired by the Director host alongside the
    /// single-session hook; left null (nothing locked) elsewhere and in unit tests, exactly like its
    /// sibling. Exists because the roster asks this question of EVERY session on a one-second timer, and
    /// the single-session hook re-reads the whole marker store per session - work that grows with the
    /// session count to answer a question with one store-wide answer per tick.
    /// Tests that set it must reset it to null in teardown.
    /// </summary>
    public static Func<IReadOnlySet<string>>? DictationLockedIdsCheck { get; set; }

    /// <summary>
    /// The set of session ids with an inbound dictation, for a caller about to ask on behalf of many
    /// sessions at once. Empty when no host wired <see cref="DictationLockedIdsCheck"/> - the same
    /// "nothing is locked" answer <see cref="IsDictationLocked"/> gives in that case, so the bulk and
    /// single-session paths agree on an unwired host instead of disagreeing.
    /// </summary>
    public static IReadOnlySet<string> DictationLockedIds()
        => DictationLockedIdsCheck?.Invoke() ?? EmptyLockedIds;

    private static readonly IReadOnlySet<string> EmptyLockedIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a dictation is inbound to THIS session, so a human send into it is refused (issue
    /// #1181, Task 3b). A pure projection of the durable PENDING delivery marker via the host-injected
    /// <see cref="DictationLockCheck"/>; false when no host wired the check. This is the single
    /// predicate both the in-process throw (in <see cref="SendTextAsync(string, SendSource)"/>) and the
    /// control-API executor's explicit pre-check read, so the two paths cannot disagree.
    /// </summary>
    public bool IsDictationLocked => DictationLockCheck?.Invoke(Id) == true;

    private bool _isReceivingDictation;

    /// <summary>
    /// Cached, change-notifying view of <see cref="IsDictationLocked"/> for the UI (issue #1181, Task 3b).
    /// The roster reads THIS (a cheap bool) to paint the "receiving a dictation" orange, instead of doing a
    /// disk read on every render. The host re-evaluates it on a timer via <see cref="RefreshReceivingDictation"/>;
    /// this is the desktop presentation of the state (Task 4 will additionally surface it from the Gateway for
    /// the phone and cockpit).
    /// </summary>
    public bool IsReceivingDictation => _isReceivingDictation;

    /// <summary>Raised (with the new value) when <see cref="IsReceivingDictation"/> flips.</summary>
    public event Action<bool>? OnReceivingDictationChanged;

    /// <summary>
    /// Recompute <see cref="IsReceivingDictation"/> from the durable dictation marker and raise
    /// <see cref="OnReceivingDictationChanged"/> when it changes. Called on the host's roster-refresh tick
    /// so the disk read happens once per tick, not once per render.
    /// </summary>
    public void RefreshReceivingDictation()
        => ApplyReceivingDictation(IsDictationLocked);

    /// <summary>
    /// The same refresh for a caller updating MANY sessions in one tick: it has already read the whole
    /// marker store once (<see cref="DictationLockedIds"/>), so this asks that set instead of going back
    /// to disk for this one session (issue #1111). Same result as <see cref="RefreshReceivingDictation()"/>
    /// - both compare against the PENDING markers - but the disk read happens once per TICK rather than
    /// once per tick per SESSION, so the cost of the roster's one-second refresh stops scaling with how
    /// many sessions are open.
    /// </summary>
    public void RefreshReceivingDictation(IReadOnlySet<string> lockedSessionIds)
        => ApplyReceivingDictation(lockedSessionIds.Contains(Id.ToString()));

    private void ApplyReceivingDictation(bool now)
    {
        if (now == _isReceivingDictation) return;
        _isReceivingDictation = now;
        FileLog.Write($"[Session] IsReceivingDictation -> {now}: session={Id}");
        OnReceivingDictationChanged?.Invoke(now);
    }

    /// <summary>
    /// Send text + Enter through the shared terminal submit protocol. ConPTY sessions use one
    /// echo-verified implementation for every agent and route, with bracketed paste for large or
    /// multi-line blocks when the TUI has requested mode 2004; non-PTY transports keep their
    /// backend-specific whole-turn semantics.
    ///
    /// <paramref name="source"/> names who is sending: a human (<see cref="SendSource.UserInput"/>,
    /// the default), an arriving dictation (<see cref="SendSource.Delivery"/>), another agent across
    /// the fleet (<see cref="SendSource.Agent"/>), or the framework itself
    /// (<see cref="SendSource.Framework"/>). It is diagnostic only - sends are never refused
    /// by source. The old dictation-lock rejection here was removed deliberately: this is a
    /// single-operator tool, and a collision between the operator's own phone dictation and their
    /// own typed send is theirs to make, not the Director's to police.
    /// </summary>
    /// <summary>
    /// Did the OWNER drive this send - a person typing or speaking - as opposed to an agent or the
    /// framework? This is the ONLY thing allowed to lift a hold, because a hold is the owner's statement
    /// "do not bother me with this session", and only the owner can withdraw it.
    ///
    /// Two independent signals, either of which suffices:
    ///  * A non-null <see cref="InputOrigin"/>. Its contract is exactly this question: "a null origin at a
    ///    choke point means the caller is framework-internal ... it carries no human keystrokes and no
    ///    spoken words". It is the intent axis, which is what we need - the desktop dictation path sends
    ///    the owner's OWN transcribed voice tagged <see cref="SendSource.Framework"/> (the transport is
    ///    framework, the actor is the human), and only the origin tells those apart.
    ///  * <see cref="SendSource.UserInput"/> / <see cref="SendSource.Delivery"/> - a human typing, or the
    ///    human's own dictation landing. Kept as a second signal because UserInput is the enum's
    ///    fail-closed default: an untagged call site reads as a human, which errs toward LIFTING a hold
    ///    the owner can re-apply, rather than stranding a session the owner is actively typing into.
    ///
    /// A new framework or agent call site MUST tag its source (and pass no origin), or it will silently
    /// eat holds. Every call site was audited when this rule landed.
    /// </summary>
    private static bool IsOwnerDriven(SendSource source, InputOrigin? origin)
        => origin is not null || source is SendSource.UserInput or SendSource.Delivery;

    /// <summary>
    /// When the OWNER last drove a turn here - a person typing or speaking - or null if they never have.
    ///
    /// This is a FACT THIS SESSION REPORTS, not a decision it makes. It is the second and last thing the
    /// Director contributes to hold (the first being <see cref="ActivityState"/>): the Gateway owns hold
    /// and decides what to do about it, but only the Director can see who drove a turn, because desktop
    /// typing never leaves this machine and the origin is only known at the input choke points.
    ///
    /// The Gateway clears a hold when this moves past the moment the hold was asked for - the owner came
    /// back, so they are not being bothered by a session they are sitting in front of. That ruling lives
    /// on the Gateway. This session neither knows nor cares that it is held.
    /// </summary>
    public DateTime? LastOwnerTurnAtUtc { get; private set; }

    /// <summary>
    /// WHO STARTED THE CURRENT WORK: <see cref="Gateway.Contracts.WorkingOrigins.Owner"/> when the last submission
    /// since this session last settled was the owner's, <see cref="Gateway.Contracts.WorkingOrigins.Agent"/> when
    /// it was an agent or product send (the fleet doorbell among them), and null when no submission explains the
    /// work. Set at the submission choke point, cleared when the session settles or exits.
    ///
    /// A FACT THIS SESSION REPORTS, like <see cref="LastOwnerTurnAtUtc"/>. The Gateway reads it to keep an armed
    /// snooze through agent-origin work (the Message Load mission, ruling 15); this session does not know it is
    /// snoozed. A settle clears it, so a detector flicker in the middle of a doorbell turn makes the next working
    /// edge unexplained - and an unexplained edge ends a snooze, which errs toward the owner's rule.
    /// </summary>
    public string? WorkingOrigin { get; private set; }

    /// <summary>The origin a submission gives the work: the same test as <see cref="IsOwnerDriven"/>; on the
    /// raw-byte path (no source) only a human origin is the owner.</summary>
    private static string OriginFor(SendSource? source, InputOrigin? origin) =>
        origin is not null || source is SendSource.UserInput or SendSource.Delivery
            ? Gateway.Contracts.WorkingOrigins.Owner
            : Gateway.Contracts.WorkingOrigins.Agent;

    /// <summary>Record that the owner just drove a turn. Idempotent by nature - it is a timestamp.</summary>
    private void StampOwnerTurn()
    {
        LastOwnerTurnAtUtc = DateTime.UtcNow;
        FileLog.Write($"[Session] Owner drove a turn: session={Id}, atUtc={LastOwnerTurnAtUtc:O}");
    }

    /// <summary>
    /// When ANY successful submission last entered this session - typed Enter, Cockpit, voice, a queue
    /// drain, an agent-to-agent prompt - or null if none has. A FACT, like <see cref="LastOwnerTurnAtUtc"/>,
    /// but answering a different question: that one says whether the OWNER drove a turn (it gates holds);
    /// this one says whether A TURN EXISTS AT ALL, whoever drove it. The activity shadow classifier
    /// (docs/PLAN-trustworthy-working-start-2026-07-24.md) reads it to tell a submission-explained Working
    /// start from one explained only by terminal output. Do not conflate the two.
    /// </summary>
    public DateTime? LastSubmissionAtUtc { get; private set; }

    /// <summary>
    /// Fires when a submission enters this session, at the same choke points that set
    /// <see cref="LastSubmissionAtUtc"/>. Args: the send source (null on the raw-byte path -
    /// <see cref="SendInput"/> carries no <see cref="SendSource"/>) and the input origin (null when no
    /// human surface tagged it). The activity producer subscribes to record turn-submitted evidence.
    /// </summary>
    public event Action<SendSource?, InputOrigin?, SubmissionEvidence>? OnTurnSubmitted;

    /// <summary>
    /// Stamp the submission fact, COUNT THE TURN, note its origin, and notify observers. An observer's
    /// fault must never break the submission that already happened, so the fan-out is guarded like every
    /// other session event.
    ///
    /// ONE CHOKE POINT, ONE WRITE. The submission event and the DevThrottle Stats turn tally are the same
    /// fact, so they are written HERE, together, and nowhere else. They used to be written eight lines
    /// apart by each caller, and <see cref="SendInput"/> wrote only one of the two: over the owner's week
    /// of 2026-W35 that left 594 typed turns - 77 per cent of his typing - out of the ring's denominator
    /// and moved his published spoken share by 28.3 points, while the submission ledger written in the
    /// same method had them all. A caller can no longer record a submission without recording its turn,
    /// because it has no way to do one without the other.
    ///
    /// <paramref name="characters"/> is the character volume of THIS submission: the length of the text on
    /// the text path, and the printable keystrokes accumulated since the last submission on the raw-byte
    /// path. Zero is legitimate - a line recalled from history is submitted without any new printable
    /// keystroke - and it still counts as one turn. The turn tally and the submission event agree on the
    /// COUNT unconditionally; only the character volume depends on the size.
    /// </summary>
    private void StampSubmission(SendSource? source, InputOrigin? origin, SubmissionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var characters = (int)Math.Min(evidence.ContentLength, int.MaxValue);
        LastSubmissionAtUtc = DateTime.UtcNow;
        WorkingOrigin = OriginFor(source, origin);
        // A human origin is a human turn, on its own (modality, surface) bucket.
        if (origin is InputOrigin o)
        {
            InputStats.RecordTurn(o, characters);
            RecordOrigin(o, characters);
        }
        // Issue #1636: one agent prompting another IS a real turn - the sending agent decided to send it -
        // so it is counted, but never into the human buckets above. Framework text (handover, queue drain,
        // pre-prompt) carries nobody's decision and is not counted at all, which is the remaining case.
        else if (source == SendSource.Agent)
        {
            InputStats.RecordAgentTurn(characters);
        }
        // EACH OBSERVER ON ITS OWN (final inspection finding F-06). A multicast delegate invoked once stops at
        // the first subscriber that throws, and a single try/catch around it hid that loss: a subscriber
        // registered ahead of the activity producer could keep the submission ledger from ever hearing about
        // a turn the tally had already counted - the exact split the earlier fix for InputStats.Changed was
        // meant to close, one subscriber further along. So the invocation list is walked and every subscriber
        // is called and guarded on its own; a fault in one is logged and the rest still run.
        var observers = OnTurnSubmitted;
        if (observers is null) return;
        foreach (var observer in observers.GetInvocationList())
        {
            try { ((Action<SendSource?, InputOrigin?, SubmissionEvidence>)observer)(source, origin, evidence); }
            catch (Exception ex) { FileLog.Write($"[Session] OnTurnSubmitted handler failed: session={Id}, handler={observer.Method.DeclaringType?.Name}.{observer.Method.Name}, {ex.Message}"); }
        }
    }

    /// <summary>
    /// Raised when this session's prompt-delivery alarm turns on (a send was lost) or off (a later send
    /// landed) - issue internal#811. The desktop rail subscribes so its red "NOT DELIVERED" badge appears
    /// and clears without waiting for some unrelated repaint; the Gateway learns the same fact from the
    /// session row on the next push.
    /// </summary>
    public event Action? OnPromptDeliveryChanged;

    private void RaisePromptDeliveryChanged()
    {
        try { OnPromptDeliveryChanged?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[Session] OnPromptDeliveryChanged handler failed: session={Id}, {ex.Message}"); }
    }

    /// <param name="provenance">What the door this text came through knew at entry (source logging): required,
    /// so no door can send text without saying which door it is.</param>
    public async Task SendTextAsync(string text, SubmissionProvenance provenance, SendSource source = SendSource.UserInput, InputOrigin? origin = null)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;

        FileLog.Write($"[Session] SendTextAsync: session={Id}, source={source}, driver={Driver.Kind}, text=\"{(text.Length > 60 ? text[..60] + "..." : text)}\", len={text.Length}");
        // THE delivery boundary (issue internal#811). Everything below this try either delivered the
        // user's words or threw; there is no third outcome, and no other place in the Director knows both
        // "which session" and "did it go". A throw here used to travel up as an error string on whichever
        // caller happened to be listening and a line in a log file nobody reads - which is how two spoken
        // prompts were lost on 2026-07-15 and went unnoticed for two days. Now the loss is counted against
        // the session and rides its row to every screen.
        //
        // ON THE "TRY-CATCH AT ENTRY POINTS ONLY" RULE (CLAUDE.md #4): this catch does not HANDLE
        // anything. It records a fact and rethrows the same exception, untouched, so every caller's error
        // path - the 502 the dictation endpoint returns, the phone's retry, the error the composer shows -
        // behaves exactly as it did before. The rule exists to stop a catch swallowing a failure and
        // continuing in a degraded state; this one exists to stop a failure being swallowed by SILENCE.
        // A test pins the rethrow so it cannot quietly become a handler.
        // THE ORIGIN IS KNOWN BEFORE THE TURN STARTS, so it is reported before the turn starts. The submit below
        // can take seconds to verify, and the terminal detector may push Working in the meantime; a push that
        // said "no origin" then would end an armed snooze this send must not end (the Message Load mission,
        // ruling 15). A send that fails puts the previous value back.
        var priorOrigin = WorkingOrigin;
        WorkingOrigin = OriginFor(source, origin);
        try
        {
            if (BackendType is SessionBackendType.ConPty)
            {
                await Drivers.TerminalSubmit.SharedSubmitAsync(
                    _backend,
                    text,
                    Driver.Kind.ToString(),
                    BracketedPasteEnabled,
                    requireEcho: Driver.Kind != Agents.AgentKind.Copilot,
                    screenSnapshot: SnapshotScreenRows,
                    sessionId: Id);
            }
            else
            {
                await _backend.SendTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            WorkingOrigin = priorOrigin;
            PromptDeliveryFailures.RecordFailedDelivery(Id, source.ToString(), ex.Message, text?.Length ?? 0);
            RaisePromptDeliveryChanged();
            throw;
        }

        // Only a send that CLEARED a live alarm repaints anything - the common case is a session that has
        // never lost a prompt, and it must not repaint the rail on every turn.
        if (PromptDeliveryFailures.RecordDeliverySucceeded(Id))
            RaisePromptDeliveryChanged();
        IsBrandNew = false;
        // A submitted turn supersedes a hold ONLY when the OWNER submitted it (issue #470 refined). Not
        // every send is the owner coming back: a fleet message from another agent (SendSource.Agent) and
        // framework plumbing (handover text, a queue drain, a pre-prompt) also land here, and clearing the
        // hold for those un-snoozed a session the owner had explicitly parked. That is what made a 12-hour
        // hold die 90 seconds later when another agent messaged it. See IsOwnerDriven.
        if (IsOwnerDriven(source, origin))
            StampOwnerTurn();
        // A SendTextAsync is exactly one submitted turn. StampSubmission stamps the submission event AND
        // counts the turn, in that one place, so this path and the raw-byte path cannot drift apart.
        StampSubmission(source, origin, SubmissionEvidence.OfText(provenance, text ?? ""));
        SetActivityState(ActivityState.Working);
    }

    /// <summary>
    /// Note WHERE this submission came from (issue #1551). Carries no text: the conversation ingest
    /// reads the text back from the agent's own transcript at the end of the turn and joins this event
    /// to it by nearest timestamp, then pushes the joined record to the Gateway. This choke point is
    /// the only place the origin is ever known - the Gateway never sees desktop-local input at all.
    ///
    /// Held in memory, not written anywhere: the Director keeps no log of its own.
    ///
    /// Gated on the same non-null origin as the stats tally, so framework-internal sends (handover
    /// text, queue drain, framing) stay out for the same reason they stay out of the counts: they carry
    /// no human keystrokes and no spoken words.
    /// </summary>
    private void RecordOrigin(InputOrigin origin, int charCount)
    {
        if (charCount <= 0) return;
        InputOriginBuffer.Record(Id.ToString(), new InputOriginEvent(
            DateTime.UtcNow, origin.ModalityToken, origin.SurfaceToken, charCount));
    }

    /// <summary>Send text followed by Enter (sync wrapper). See
    /// <see cref="SendTextAsync(string, SubmissionProvenance, SendSource, InputOrigin?)"/> for what <paramref name="source"/> and
    /// <paramref name="origin"/> mean.</summary>
    public void SendText(string text, SubmissionProvenance provenance, SendSource source = SendSource.UserInput, InputOrigin? origin = null)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        // Fire and forget for sync API
        _ = SendTextAsync(text, provenance, source, origin);
    }

    /// <summary>Send just an Enter keystroke to the backend.</summary>
    public async Task SendEnterAsync()
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        await _backend.SendEnterAsync();
    }

    // ===== Agent driver verbs =====
    // The per-CLI interaction protocol (docs/plans/agent-driver.md): tool-specific
    // keystrokes live in one driver class per CLI, resolved from this session's
    // AgentKind. Claude gets ClaudeDriver, pi gets PiDriver, unverified tools get
    // GenericDriver (the pre-driver bytes, minimal declared capabilities). UIs and
    // endpoints read Driver.Capabilities to know which verbs this session supports.

    /// <summary>
    /// Test seam: a driver supplied directly instead of resolved from <see cref="AgentKind"/>. Null in
    /// every real session - drivers are resolved from the kind, never assigned - and settable only from
    /// this assembly's tests, so the verbs below can be exercised against a stub driver without a live
    /// agent process and without reading the developer's own transcript store.
    /// </summary>
    internal Drivers.IAgentDriver? DriverOverride { get; set; }

    /// <summary>The interaction driver for this session's CLI. Stateless singleton.</summary>
    public Drivers.IAgentDriver Driver => DriverOverride
        ?? (AgentPlugins.AgentPluginRegistry.Contains(AgentKind)
            ? AgentPlugins.AgentPluginRegistry.Get(AgentKind).Driver
            : Drivers.AgentDrivers.For(AgentKind));

    /// <summary>Soft-stop the current turn (Esc for Claude and pi).</summary>
    public async Task CancelTurnAsync()
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        FileLog.Write($"[Session] CancelTurnAsync: session={Id}, driver={Driver.Kind}");
        await Driver.CancelAsync(_backend);
    }

    /// <summary>Hard interrupt (Ctrl+C where the tool supports it; pi does NOT -
    /// its driver throws because Ctrl+C twice quits pi).</summary>
    public async Task InterruptAsync()
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        FileLog.Write($"[Session] InterruptAsync: session={Id}, driver={Driver.Kind}");
        await Driver.InterruptAsync(_backend);
    }

    /// <summary>Open the tool's in-terminal history picker (Claude's double-Esc).</summary>
    public async Task ShowHistoryAsync()
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        FileLog.Write($"[Session] ShowHistoryAsync: session={Id}, driver={Driver.Kind}");
        await Driver.ShowHistoryAsync(_backend);
    }

    /// <summary>
    /// Reset the conversation context in place via the driver (/clear for Claude,
    /// /new for pi). For drivers with transcript access this also DISCOVERS the new
    /// agent-internal session id (the post-/clear transcript file) - the caller must
    /// then re-link via SessionManager.RelinkClaudeSession so the manager's id map and
    /// metadata follow; this closes the stale-relink gap found in the issue #172 spike.
    /// Returns the new id, or null when the tool has no readable transcripts.
    /// </summary>
    public async Task<string?> ClearContextAsync(CancellationToken ct = default)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed)
            throw new InvalidOperationException($"ClearContextAsync: session {Id} is not running (status={Status})");
        var driver = Driver;
        if (!driver.Capabilities.HasFlag(Drivers.DriverCapabilities.ClearContext))
            throw new NotSupportedException($"ClearContextAsync: the {driver.Kind} driver declares no context clear.");

        var oldId = ClaudeSessionId;
        FileLog.Write($"[Session] ClearContextAsync: session={Id}, driver={driver.Kind}, oldAgentSessionId={oldId ?? "(none)"}");
        var t0 = DateTime.UtcNow;
        await driver.ClearContextAsync(_backend);

        if (!driver.Capabilities.HasFlag(Drivers.DriverCapabilities.TranscriptRead) || oldId is null)
        {
            // The clear happened but the transcript it starts cannot be found here: pi, for one, writes
            // the new file only on the next message. Stamp the moment, so a watcher that runs at the
            // session's next turn end can find the file created after it (PiSessionRebinder, #2670).
            ContextClearedUtc = t0;
            FileLog.Write($"[Session] ClearContextAsync: no synchronous transcript tracking for {driver.Kind}; clear submitted and stamped at {t0:O}");
            return null;
        }

        // The post-clear transcript is the file that is both new (id differs) and
        // recent; old transcripts in the same working directory must not match.
        var deadline = t0.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = driver.ListTranscripts(WorkingDirectory)
                .FirstOrDefault(s => !string.IsNullOrEmpty(s.AgentSessionId)
                                     && s.AgentSessionId != oldId
                                     && s.LastWriteUtc >= t0.AddSeconds(-10));
            if (!string.IsNullOrEmpty(candidate.AgentSessionId))
            {
                FileLog.Write($"[Session] ClearContextAsync: new transcript {candidate.AgentSessionId} after {(DateTime.UtcNow - t0).TotalSeconds:F1}s");
                return candidate.AgentSessionId;
            }
            await Task.Delay(250, ct);
        }
        throw new InvalidOperationException(
            $"ClearContextAsync: no new transcript appeared within 60s (session={Id}, oldId={oldId})");
    }

    /// <summary>
    /// How long a compaction is allowed to take before the Director stops waiting for it. This is the
    /// INNER bound of the compact-and-continue call and must stay strictly shorter than the Gateway's
    /// outer wait for the verb (DirectorCommandRouter.LanguageModelCommandTimeout, 3 minutes), so the
    /// inner one always fires first and reports WHAT failed - "the tool never reported a finished
    /// compaction" - instead of the outer one masking it with "the Director did not answer".
    /// </summary>
    public static readonly TimeSpan CompactionWaitTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How often the completion mark is re-read while waiting. The tool writes it once; there is
    /// nothing to gain from a tighter loop over a file that only grows.</summary>
    private static readonly TimeSpan CompactionPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Compact the conversation in place and, when asked, CONTINUE it (issue #2150).
    ///
    /// A session whose context window is full cannot read anything sent to it - every prompt is swallowed
    /// and the tool prints its context-limit line again - so nothing but compaction gets it moving, and
    /// until now nothing but a person at that keyboard could compact it. This is that verb.
    ///
    /// Unlike <see cref="ClearContextAsync"/> there is NO transcript re-link: compaction continues under
    /// the same agent session id (verified against live claude transcripts), so re-linking would wait for
    /// a new transcript that is never coming.
    ///
    /// The continuation is timed on the tool's OWN completion signal, never on a delay: with
    /// <paramref name="continuePrompt"/> set, the follow-up is submitted only once the driver reports the
    /// compaction finished. A driver that cannot report completion refuses the continuation outright
    /// rather than firing a prompt into a composer that is still summarizing.
    /// </summary>
    /// <param name="continuePrompt">The text to send once compaction finishes, or null/blank to compact only.</param>
    /// <param name="ct">Cancels the wait promptly; the compaction itself is already submitted by then.</param>
    /// <param name="waitTimeout">Overrides <see cref="CompactionWaitTimeout"/>. Tests pass a short one so the
    /// give-up path can be proven in milliseconds instead of minutes; callers pass null.</param>
    /// <param name="pollInterval">Overrides <see cref="CompactionPollInterval"/>, same reason.</param>
    public async Task<CompactContextOutcome> CompactContextAsync(
        string? continuePrompt = null,
        CancellationToken ct = default,
        TimeSpan? waitTimeout = null,
        TimeSpan? pollInterval = null)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed)
            throw new InvalidOperationException($"CompactContextAsync: session {Id} is not running (status={Status})");

        var driver = Driver;
        if (!driver.Capabilities.HasFlag(Drivers.DriverCapabilities.CompactContext))
            throw new NotSupportedException($"CompactContextAsync: the {driver.Kind} driver declares no compaction.");

        var wantsContinue = !string.IsNullOrWhiteSpace(continuePrompt);
        var agentSessionId = ClaudeSessionId;
        var canObserve = driver.Capabilities.HasFlag(Drivers.DriverCapabilities.CompactCompletionReport)
                         && !string.IsNullOrEmpty(agentSessionId);

        if (wantsContinue && !driver.Capabilities.HasFlag(Drivers.DriverCapabilities.CompactCompletionReport))
            throw new NotSupportedException(
                $"CompactContextAsync: the {driver.Kind} driver cannot report when a compaction finished, " +
                "so a follow-up prompt cannot be timed. Compact without a continuation, then send the " +
                "prompt yourself once the session is idle.");
        if (wantsContinue && string.IsNullOrEmpty(agentSessionId))
            throw new InvalidOperationException(
                $"CompactContextAsync: session {Id} has no agent session id yet, so the compaction cannot be " +
                "watched and a follow-up prompt cannot be timed.");

        FileLog.Write($"[Session] CompactContextAsync: session={Id}, driver={driver.Kind}, " +
                      $"agentSessionId={agentSessionId ?? "(none)"}, continue={(wantsContinue ? "yes" : "no")}");

        var t0 = DateTime.UtcNow;
        await driver.CompactContextAsync(_backend);
        SetActivityState(ActivityState.Working);

        if (!canObserve)
        {
            FileLog.Write($"[Session] CompactContextAsync: {driver.Kind} reports no completion signal; " +
                          "compaction submitted, not watched");
            return new CompactContextOutcome(
                Submitted: true,
                CompactionObserved: false,
                WaitedSeconds: 0,
                Continued: false,
                Detail: $"Compaction submitted. {driver.Kind} cannot report when it finishes, so this was not watched.");
        }

        var allowed = waitTimeout ?? CompactionWaitTimeout;
        var poll = pollInterval ?? CompactionPollInterval;
        var deadline = t0 + allowed;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (driver.HasCompactedSince(agentSessionId!, WorkingDirectory, t0))
            {
                var waited = (DateTime.UtcNow - t0).TotalSeconds;
                FileLog.Write($"[Session] CompactContextAsync: compaction finished after {waited:F1}s (session={Id})");

                if (!wantsContinue)
                    return new CompactContextOutcome(true, true, waited, false,
                        $"Compacted in {waited:F0} seconds.");

                // Framework, not Agent or UserInput: this text is the product's own follow-up to a
                // compaction it ran. It must not clear the owner's hold and must not be counted as
                // anybody's turn.
                await SendTextAsync(continuePrompt!, SubmissionProvenance.FrameworkText(), SendSource.Framework);
                FileLog.Write($"[Session] CompactContextAsync: continuation submitted (session={Id})");
                return new CompactContextOutcome(true, true, waited, true,
                    $"Compacted in {waited:F0} seconds, then sent the follow-up.");
            }
            await Task.Delay(poll, ct);
        }

        throw new TimeoutException(
            $"CompactContextAsync: {driver.Kind} did not report a finished compaction within " +
            $"{allowed.TotalSeconds:F0} seconds (session={Id}). The compaction command was " +
            "submitted; no follow-up was sent.");
    }

    private void SetActivityState(ActivityState newState)
    {
        var old = ActivityState;
        if (old == newState) return;
        ActivityState = newState;
        // NO HOLD EDGES LIVE HERE, AND NONE MAY BE ADDED. Activity does not decide hold. This method used
        // to run the whole hold machine off these transitions - work lifts a hold, exit clears it, settle
        // lands a deferral - and every one of those was the Director ruling on something that is not its
        // business. A hold is the owner's intent; this class knows about bytes and processes.
        //
        // Those rulings now live on the Gateway, driven by the two facts this session REPORTS upward:
        // its ActivityState (assigned just above, pushed on the tunnel, read by SnoozeLandingObserver to
        // land a deferral when the work ends and to drop a hold when the session exits) and
        // LastOwnerTurnAtUtc (the owner came back). Reporting is this class's job. Deciding is not.
        //
        // A real activity change ends any Wingman "running in the background" overlay: once the
        // terminal produces output again (Working), or the session otherwise leaves the parked
        // turn-end it was judged at, the background-wait verdict is stale. The next turn-end
        // briefing re-evaluates from scratch. Only WaitingForInput/WaitingForPerm preserve it.
        if (newState is not (ActivityState.WaitingForInput or ActivityState.WaitingForPerm))
            IsBackgroundRunning = false;
        // The work that a submission explained is over once the session settles or exits; the next working
        // edge is explained only by the next submission.
        if (newState is not (ActivityState.Working or ActivityState.Starting))
            WorkingOrigin = null;
        // A real state change opens a new "generation". This releases any sticky
        // positive-evidence color from the previous generation (issue #136 option C):
        // e.g. a red pending-question survives cosmetic repaints while the session is
        // idle, but the moment the user answers (-> Working) the badge is free to go
        // blue again.
        Interlocked.Increment(ref _activityGeneration);
        // Log the transition (blue<->red) to the in-memory ring the Wingman tab renders.
        RecordStateChange(old, newState);
        // Turn counter and waiting clock - must run before the event so the delta push
        // that rides OnActivityStateChanged carries this flip's numbers, not the last one's.
        RecordSupervisionFacts(old, newState);
        OnActivityStateChanged?.Invoke(old, newState);
    }

    // The prompt queue does NOT auto-send, and never has. A "TryDrainQueue" used to hang here, gated on
    // a transition to ActivityState.Idle - a state NOTHING has ever assigned. It went in with the auto-drain
    // itself in May 2026 and shipped dead: the terminal detector emits WaitingForInput at a turn end,
    // deliberately ("we do not try to tell 'finished cleanly' apart from 'blocked on a question'"), and Idle
    // is not even the enum's zero value (Starting is), so it could not arrive by default or deserialization
    // either. The only assignments of Idle in the whole repository were, and are, in tests - which is why the
    // drain's own tests passed for fourteen months by calling ApplyTerminalActivityState(Idle) directly and
    // injecting a state production never emits.
    //
    // It is deleted rather than repaired because the queue that users actually have is the one the product
    // describes: PromptQueue is "prompts the user wants to send later... sent in any order", the desktop
    // offers only "Add to prompt queue", and there is an explicit send verb (queue-send). NOTHING
    // user-facing has ever promised auto-send. Switching it on now would make DevThrottle fire prompts into
    // sessions by itself for the first time ever, at the exact moment it cannot tell a finished turn from a
    // question it would be answering with unrelated text.
    //
    // Auto-send is a real feature and needs a real turn-end classifier that can separate "finished cleanly"
    // from "blocked on a question" - the thing the detector explicitly refuses to do. See issue #1564.

    /// <summary>
    /// Set ActivityState from the <c>TerminalStateDetector</c> in terminal-driven mode.
    /// The detector is the single authority for state in that mode; this is its writer.
    /// </summary>
    internal void ApplyTerminalActivityState(ActivityState newState) => SetActivityState(newState);

    /// <summary>
    /// Refresh Claude session metadata from sessions-index.json.
    /// Call this after ClaudeSessionId is set or periodically to update message counts.
    /// </summary>
    public void RefreshClaudeMetadata()
    {
        if (string.IsNullOrEmpty(ClaudeSessionId))
        {
            if (ClaudeMetadata != null)
            {
                ClaudeMetadata = null;
                OnClaudeMetadataChanged?.Invoke(null);
            }
            return;
        }

        var metadata = ClaudeSessionReader.ReadSessionMetadata(ClaudeSessionId, RepoPath);
        ClaudeMetadata = metadata;
        OnClaudeMetadataChanged?.Invoke(metadata);
    }

    /// <summary>
    /// Verify that the Claude session's .jsonl file exists and matches expected content.
    /// Updates VerificationStatus and VerifiedFirstPrompt.
    /// Uses ExpectedFirstPrompt if set, otherwise just verifies file existence.
    /// Requires at least MinVerificationLength characters to verify.
    /// </summary>
    public void VerifyClaudeSession()
    {
        FileLog.Write($"[Session] VerifyClaudeSession: session={Id}, claudeSessionId={ClaudeSessionId ?? "null"}");
        var oldStatus = VerificationStatus;

        // Can't verify without a session ID
        if (string.IsNullOrEmpty(ClaudeSessionId))
        {
            VerificationStatus = SessionVerificationStatus.NotLinked;
            VerifiedFirstPrompt = null;
            if (oldStatus != VerificationStatus)
                OnVerificationStatusChanged?.Invoke(VerificationStatus);
            return;
        }

        // Read the JSONL first prompt to check length
        var jsonlPath = ClaudeSessionReader.GetJsonlPath(ClaudeSessionId, RepoPath);
        var firstPrompt = ClaudeSessionReader.ReadFirstPromptFromJsonl(jsonlPath);

        // Need minimum content to verify (avoid verifying new sessions too early)
        if (string.IsNullOrEmpty(firstPrompt) || firstPrompt.Length < MinVerificationLength)
        {
            // File exists but not enough content yet - stay NotLinked (no badge)
            VerificationStatus = SessionVerificationStatus.NotLinked;
            VerifiedFirstPrompt = firstPrompt;
            if (oldStatus != VerificationStatus)
                OnVerificationStatusChanged?.Invoke(VerificationStatus);
            return;
        }

        // Now do full verification
        var result = ClaudeSessionReader.VerifySessionFile(ClaudeSessionId, RepoPath, ExpectedFirstPrompt);
        VerificationStatus = result.Status;
        VerifiedFirstPrompt = result.FirstPromptSnippet;

        // If verified and we didn't have an expected prompt yet, save the actual one
        if (result.Status == SessionVerificationStatus.Verified && string.IsNullOrEmpty(ExpectedFirstPrompt))
        {
            ExpectedFirstPrompt = result.FirstPromptSnippet;
        }

        if (oldStatus != result.Status)
        {
            OnVerificationStatusChanged?.Invoke(result.Status);
        }
    }

    /// <summary>
    /// Find the matching .jsonl file by comparing terminal content with user prompts.
    /// Starts matching immediately - shows "Potential" for early matches, "Matched" after 50+ lines.
    /// </summary>
    /// <param name="terminalText">Terminal content.</param>
    /// <param name="lineCount">Number of lines in terminal.</param>
    /// <returns>Verification result with matched session ID or error.</returns>
    public TerminalVerificationResult VerifyWithTerminalContent(string terminalText, int lineCount)
    {
        FileLog.Write($"[Session] VerifyWithTerminalContent: session={Id}, lineCount={lineCount}, textLen={terminalText.Length}");
        // Skip if already matched or exhausted all retry attempts
        if (TerminalVerificationStatus == TerminalVerificationStatus.Matched)
        {
            return new TerminalVerificationResult
            {
                IsMatched = true,
                MatchedSessionId = ClaudeSessionId
            };
        }
        if (_confirmationAttempts >= MaxConfirmationAttempts)
        {
            return new TerminalVerificationResult
            {
                IsMatched = false,
                MatchedSessionId = ClaudeSessionId
            };
        }

        // Prevent concurrent verification runs (called from background threads)
        if (Interlocked.CompareExchange(ref _verificationRunning, 1, 0) != 0)
            return new TerminalVerificationResult { IsMatched = false, ErrorMessage = "Verification already running" };

        try
        {
            return VerifyWithTerminalContentCore(terminalText, lineCount);
        }
        finally
        {
            Interlocked.Exchange(ref _verificationRunning, 0);
        }
    }

    private TerminalVerificationResult VerifyWithTerminalContentCore(string terminalText, int lineCount)
    {
        FileLog.Write($"[Session.Verify] START: lineCount={lineCount}, status={TerminalVerificationStatus}, attempts={_confirmationAttempts}, sessionId={Id}");

        bool isConfirmationRun = lineCount >= 50;
        if (isConfirmationRun)
            _confirmationAttempts++;

        var error = LoadJsonlFilesForVerification(isConfirmationRun, out var allFiles);
        if (error != null) return error;

        // Normalize terminal text once for whitespace-insensitive matching
        var normalizedTerminal = ClaudeSessionReader.NormalizeForMatching(terminalText);

        // Score all files and pick the best match
        var bestMatch = FindBestMatch(allFiles, terminalText, normalizedTerminal);

        if (bestMatch != null)
        {
            ClaudeSessionId = bestMatch.Value.SessionId;

            if (isConfirmationRun || bestMatch.Value.MatchCount >= 2)
            {
                SetTerminalVerificationStatus(TerminalVerificationStatus.Matched);
                ExpectedFirstPrompt = ClaudeSessionReader.ReadFirstPromptFromJsonl(bestMatch.Value.FilePath);
                VerifyClaudeSession();
                FileLog.Write($"[Session.Verify] MATCHED: {bestMatch.Value.SessionId} (matches={bestMatch.Value.MatchCount}, prompts={bestMatch.Value.TotalPrompts})");
                return new TerminalVerificationResult { IsMatched = true, MatchedSessionId = bestMatch.Value.SessionId };
            }

            SetTerminalVerificationStatus(TerminalVerificationStatus.Potential);
            FileLog.Write($"[Session.Verify] POTENTIAL: {bestMatch.Value.SessionId} (matches={bestMatch.Value.MatchCount}, prompts={bestMatch.Value.TotalPrompts})");
            return new TerminalVerificationResult { IsPotential = true, MatchedSessionId = bestMatch.Value.SessionId };
        }

        if (isConfirmationRun)
        {
            // Set Failed status immediately, but allow retries up to MaxConfirmationAttempts
            FileLog.Write($"[Session.Verify] NO MATCH FOUND - Setting status=Failed (attempt {_confirmationAttempts}/{MaxConfirmationAttempts}, {allFiles.Count} files checked)");
            SetTerminalVerificationStatus(TerminalVerificationStatus.Failed);
        }
        else
        {
            FileLog.Write($"[Session.Verify] No match found yet, NOT confirmation run - staying in status={TerminalVerificationStatus}");
        }
        return new TerminalVerificationResult { ErrorMessage = "No matching .jsonl file found" };
    }

    /// <summary>
    /// Load and validate .jsonl files for terminal verification.
    /// Returns null on success (files populated), or an error result on failure.
    /// </summary>
    private TerminalVerificationResult? LoadJsonlFilesForVerification(
        bool isConfirmationRun, out List<FileInfo> allFiles)
    {
        allFiles = new List<FileInfo>();

        var projectFolder = ClaudeSessionReader.GetProjectFolderPath(RepoPath);
        if (!Directory.Exists(projectFolder))
        {
            FileLog.Write($"[Session.Verify] Project folder not found: {projectFolder}");
            if (isConfirmationRun)
                SetTerminalVerificationStatus(TerminalVerificationStatus.Failed);
            return new TerminalVerificationResult { ErrorMessage = "Project folder not found" };
        }

        allFiles = Directory.GetFiles(projectFolder, "*.jsonl")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        FileLog.Write($"[Session.Verify] Found {allFiles.Count} .jsonl files in {projectFolder}");

        if (allFiles.Count == 0)
        {
            if (isConfirmationRun)
                SetTerminalVerificationStatus(TerminalVerificationStatus.Failed);
            else
                FileLog.Write($"[Session.Verify] No .jsonl files, NOT confirmation run - staying in current status");
            return new TerminalVerificationResult { ErrorMessage = "No .jsonl files found" };
        }

        return null;
    }

    private readonly record struct MatchResult(string SessionId, string FilePath, int MatchCount, int TotalPrompts);

    /// <summary>
    /// Score all JSONL files against terminal text and return the best match.
    /// Uses both exact and whitespace-normalized matching to handle word wrapping.
    /// Picks the file with the most prompt matches (not ratio), requiring at least 1 match.
    /// </summary>
    private MatchResult? FindBestMatch(
        IReadOnlyList<FileInfo> allFiles, string terminalText, string normalizedTerminal)
    {
        MatchResult? best = null;

        foreach (var file in allFiles)
        {
            var prompts = ClaudeSessionReader.ExtractUserPrompts(file.FullName);
            if (prompts.Count == 0) continue;

            int matchCount = 0;
            foreach (var prompt in prompts)
            {
                // Try exact match first (fast)
                if (terminalText.Contains(prompt, StringComparison.Ordinal))
                {
                    matchCount++;
                    continue;
                }

                // Try whitespace-normalized match (handles word wrapping)
                var normalizedPrompt = ClaudeSessionReader.NormalizeForMatching(prompt);
                if (normalizedPrompt.Length > 10 && normalizedTerminal.Contains(normalizedPrompt, StringComparison.Ordinal))
                {
                    matchCount++;
                }
            }

            var fileName = Path.GetFileNameWithoutExtension(file.Name);
            var shortName = fileName.Length > 8 ? fileName[..8] : fileName;
            FileLog.Write($"[Session.Verify] File={shortName}..., prompts={prompts.Count}, matched={matchCount}");

            if (matchCount > 0 && (best == null || matchCount > best.Value.MatchCount))
            {
                best = new MatchResult(fileName, file.FullName, matchCount, prompts.Count);
            }
        }

        return best;
    }

    private void SetTerminalVerificationStatus(TerminalVerificationStatus status)
    {
        if (TerminalVerificationStatus == status) return;
        TerminalVerificationStatus = status;
        OnTerminalVerificationStatusChanged?.Invoke(status);
    }

    /// <summary>Resize the terminal (only meaningful for ConPty backend).</summary>
    public void Resize(short cols, short rows)
    {
        if (_disposed) return;
        if (cols <= 0 || rows <= 0) return;
        // No-op on an unchanged size. A resize repaints the PTY, which the monitoring loop
        // observes; resizing to the same dimensions would be a pointless write that could
        // feed a repaint storm (the Wingman repaint-loop invariant). Guard against it so a
        // chatty Cockpit (window-drag events) can't hammer the PTY.
        if (cols == CurrentCols && rows == CurrentRows) return;
        // Re-size the live-attach parser to match the new PTY geometry BEFORE the PTY repaints, so
        // the agent's post-resize output is parsed at the correct width/height. Overlapping content
        // is copied so an agent that does not fully repaint on resize (Codex) keeps its screen; any
        // imperfection self-heals on the repaint the resize triggers. The fixed-grid _htmlParser is
        // intentionally left alone (its consumers depend on its stable geometry).
        ResizeStreamParser(cols, rows);
        _backend.Resize(cols, rows);
        CurrentCols = cols;
        CurrentRows = rows;
    }

    // Grow/shrink the live-attach parser's grid, copying the overlapping cells so existing content
    // survives the resize (mirrors the desktop TerminalControl's resize path). Under _htmlParserLock
    // because the buffer feed thread parses into this same parser.
    private void ResizeStreamParser(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        lock (_htmlParserLock)
        {
            if (_streamParser is null || _streamCells is null) return;
            if (cols == _streamGridCols && rows == _streamGridRows) return;
            var newCells = new TerminalCell[cols, rows];
            int copyC = Math.Min(_streamGridCols, cols);
            int copyR = Math.Min(_streamGridRows, rows);
            for (int r = 0; r < copyR; r++)
                for (int c = 0; c < copyC; c++)
                    newCells[c, r] = _streamCells[c, r];
            _streamParser.UpdateGrid(newCells, cols, rows);
            _streamCells = newCells;
            _streamGridCols = cols;
            _streamGridRows = rows;
        }
    }

    /// <summary>
    /// Build a self-contained ANSI "prime" frame that reconstructs the session's CURRENT terminal
    /// screen (scrollback + visible grid + cursor) when replayed into a fresh client terminal. This
    /// is what the WebSocket stream endpoint sends on attach instead of a mid-stream raw-byte replay,
    /// so a browser xterm rebuilds the exact screen regardless of how far back or how incrementally
    /// the agent drew it. Returns an empty array for sessions with no server-side parser (Embedded).
    /// </summary>
    public (byte[] Snapshot, long ReflectedCursor, int Cols, int Rows) GetTerminalSnapshot()
    {
        if (_streamParser is null) return (System.Array.Empty<byte>(), 0, CurrentCols, CurrentRows);
        lock (_htmlParserLock)
        {
            if (_streamParser is null || _streamScrollback is null)
                return (System.Array.Empty<byte>(), _streamBytesReflected, CurrentCols, CurrentRows);
            var cells = _streamParser.ActiveCells;
            int cols = cells.GetLength(0);
            int rows = cells.GetLength(1);
            var (cc, cr) = _streamParser.GetCursorPosition();
            var bytes = TerminalSnapshotSerializer.ToAnsi(
                _streamScrollback, cells, cols, rows, cc, cr,
                _streamParser.IsCursorVisible, _streamParser.IsAlternateScreen);
            return (bytes, _streamBytesReflected, cols, rows);
        }
    }

    /// <summary>Kill the session gracefully, then force if needed.</summary>
    public async Task KillAsync(int timeoutMs = 5000)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        Status = SessionStatus.Exiting;
        await _backend.GracefulShutdownAsync(timeoutMs);
    }

    /// <summary>Mark the session as running (called after backend.Start succeeds).</summary>
    internal void MarkRunning()
    {
        Status = SessionStatus.Running;
    }

    /// <summary>Mark the session as failed.</summary>
    internal void MarkFailed()
    {
        Status = SessionStatus.Failed;
    }

    private void OnBackendProcessExited(int exitCode)
    {
        FileLog.Write($"[Session] ProcessExited: session={Id}, exitCode={exitCode}, pid={ProcessId}, uptime={(DateTimeOffset.UtcNow - CreatedAt).TotalSeconds:F1}s");

        // Decide crash-vs-clean BEFORE we overwrite the activity state: a session that dropped out
        // while it was actively working is a crash even if its exit code is 0 (issue #959).
        var wasWorking = ActivityState == ActivityState.Working;
        var crashed = IsUnexpectedExit(exitCode, wasWorking);

        ExitCode = exitCode;
        Crashed = crashed;
        Status = crashed ? SessionStatus.Failed : SessionStatus.Exited;
        // Process exit is an authoritative, transport-independent signal - drive the
        // state directly so it works in both terminal-driven and hook modes.
        SetActivityState(ActivityState.Exited);

        // A crash keeps the row visible in an Error colour so the user sees work stopped rather than
        // the session silently disappearing. SetActivityState above bumped the colour generation, so
        // this authoritative crash colour is not dropped by the sticky positive-evidence guard.
        if (crashed)
        {
            var reason = exitCode == 0
                ? "crashed: the agent process ended unexpectedly while working"
                : $"crashed: the agent process exited with code {exitCode}";
            SetStatusColor(CcDirector.Core.Wingman.StatusColor.Error, reason);
        }

        // Announce the exit exactly once (a backend could theoretically raise its
        // ProcessExited more than once). This is an event-raise on a backend thread,
        // so guard it: a faulting subscriber must not kill the exit-monitor thread.
        if (!_exitNotified)
        {
            _exitNotified = true;
            try { OnExited?.Invoke(exitCode); }
            catch (Exception ex) { FileLog.Write($"[Session] OnExited handler threw: {ex.Message}"); }
        }
    }

    private bool _exitNotified;

    private void OnBackendStatusChanged(string status)
    {
        FileLog.Write($"[Session] BackendStatus: session={Id}, status={status}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _backend.ProcessExited -= OnBackendProcessExited;
        _backend.StatusChanged -= OnBackendStatusChanged;
        if (_htmlParserFeed is not null && _backend.Buffer is not null)
            _backend.Buffer.OnBytesWritten -= _htmlParserFeed;
        // Nothing can render this session's row any more, so its delivery tally has no reader left. The
        // fleet-wide recent ring keeps the history; only the per-session counters are dropped.
        PromptDeliveryFailures.Forget(Id);
        _backend.Dispose();
    }
}
