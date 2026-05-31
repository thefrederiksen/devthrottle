using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Memory;
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

    public Guid Id { get; }
    public string RepoPath { get; }
    public string WorkingDirectory { get; }
    public SessionStatus Status { get; internal set; }
    public DateTimeOffset CreatedAt { get; }
    public string? ClaudeArgs { get; }
    public int? ExitCode { get; internal set; }

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
    /// while the startup splash is painting, so the badge stays red ("needs you") from the moment
    /// the row appears until the user submits their first prompt.
    /// </summary>
    public ActivityState ActivityState { get; private set; } = ActivityState.WaitingForInput;

    /// <summary>The session_id reported by Claude hooks, used for routing.</summary>
    public string? ClaudeSessionId { get; internal set; }

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
        OnPendingPromptTextChanged?.Invoke(value, source ?? "user");
    }

    /// <summary>Name of the last selected tab (e.g. "Terminal", "Agent", "SourceControl"). Persisted across switches and restarts.</summary>
    public string? SelectedTabName { get; set; }

    /// <summary>Queue of prompts the user wants to send later. Persisted across switches and restarts.</summary>
    public PromptQueue PromptQueue { get; } = new();

    /// <summary>Order in the session list, used to restore UI order after restart.</summary>
    public int SortOrder { get; set; }

    /// <summary>Fires when ActivityState changes. Args: (oldState, newState).</summary>
    public event Action<ActivityState, ActivityState>? OnActivityStateChanged;

    // ---------- Mobile mode + proactive wingman explain (remote experience) ----------

    /// <summary>
    /// Which mobile view a phone is currently watching this session through, set by the active
    /// mobile tab via the Control API (Session tab -> Text, Voice tab -> Voice; Off when no phone
    /// is watching). Single source of truth for the mobile experience; <see cref="MobileMode"/>
    /// and <see cref="VoiceMode"/> are derived from it. Off by default. Not persisted: it tracks
    /// what a remote viewer is looking at right now, not durable session state.
    /// </summary>
    public MobileViewMode ViewMode { get; set; } = MobileViewMode.Off;

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
    /// True when the user has explicitly parked this session in the FIFO voice queue:
    /// "I do not want to deal with this one right now." A user override that is
    /// ORTHOGONAL to <see cref="ActivityState"/> and <see cref="StatusColor"/> - the
    /// terminal-state detector keeps reporting the true underlying state, and this flag
    /// sits on top of it so the FIFO conductor can skip held sessions without the
    /// detector ever clobbering the intent. Cleared when the user takes it off hold.
    /// Runtime-only (not persisted across a Director restart): it tracks what the user
    /// is currently choosing to defer, not durable session state. Off by default.
    /// </summary>
    public bool OnHold
    {
        get => _onHold;
        set
        {
            if (_onHold == value) return;
            _onHold = value;
            OnHoldChanged?.Invoke(value);
        }
    }
    private bool _onHold;

    /// <summary>Fires when <see cref="OnHold"/> changes. Arg: new value. The desktop
    /// session list subscribes so the color strip can repaint dark blue (held) without
    /// the wingman touching <see cref="StatusColor"/>; OnHold sits on top of it.</summary>
    public event Action<bool>? OnHoldChanged;

    /// <summary>
    /// Whether this session participates in the Wingman experience: the auto-explain
    /// briefing on turn-end, the Voice/Wingman tabs, and the Yellow "Wingman is reading"
    /// state. OFF by default for every new session (the Wingman is not reliable enough
    /// yet to opt every session in). When OFF the session behaves like a plain terminal:
    /// ProactiveExplainService skips it, the dot goes straight Blue->Red, and the clients
    /// hide the Voice + Wingman tabs. Opt in per session via the context menu / new-session
    /// dialog. Durable per session (persisted via <see cref="PersistedSession.WingmanEnabled"/>).
    /// </summary>
    public bool WingmanEnabled { get; set; } = false;

    private bool _isExplaining;

    /// <summary>
    /// True while <c>ProactiveExplainService</c> has a Wingman briefing in flight for
    /// this session. Set just before the call, cleared in <c>finally</c>. Transient
    /// (in-memory only). The Yellow status is keyed off this flag together with
    /// <see cref="WingmanEnabled"/>; <see cref="OnIsExplainingChanged"/> notifies the
    /// SessionStatusWingman so it can repaint the dot.
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

    private bool _isBackgroundRunning;
    private string _backgroundReason = "running in background";

    /// <summary>
    /// True when the Wingman has read the screen and determined this session is parked
    /// waiting on its OWN background task (a long build, "N shell still running") rather
    /// than on the user. A Wingman-owned overlay ORTHOGONAL to <see cref="ActivityState"/>,
    /// exactly like <see cref="IsExplaining"/>: the <c>TerminalStateDetector</c> still reports
    /// the true underlying <see cref="ActivityState.WaitingForInput"/> (the dumb 10s silence
    /// timer cannot tell a background-wait apart from "your turn"), and this flag sits on top
    /// so <c>SessionStatusWingman</c> can paint the badge Purple ("running in background")
    /// instead of Red ("needs you"). Set by <c>ProactiveExplainService</c> from the explain
    /// verdict via <see cref="SetBackgroundRunning"/>; auto-cleared the moment real output
    /// resumes (the session transitions off WaitingForInput in <see cref="SetActivityState"/>).
    /// Transient (in-memory only); it tracks a live read of the screen, not durable state.
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

    /// <summary>Fires when <see cref="IsBackgroundRunning"/> changes. Arg: new value. The
    /// SessionStatusWingman subscribes so it can repaint the badge Purple/Red.</summary>
    public event Action<bool>? OnIsBackgroundRunningChanged;

    /// <summary>
    /// Set (or clear) the Wingman's "parked on a background task" verdict for this session.
    /// Sole caller is <c>ProactiveExplainService</c> after an explain briefing. Pass a short
    /// reason when <paramref name="running"/> is true (used as the badge tooltip); clearing
    /// resets the reason to the default. The flag only affects the badge while the session is
    /// parked at a turn-end (see <c>SessionStatusWingman.ColorFor</c>).
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
    public void SetCachedExplainStructured(string? headline, string? whatHappened, string? longDescription, string? whatClaudeWants, string? say)
    {
        CachedExplainHeadline = string.IsNullOrWhiteSpace(headline) ? null : headline.Trim();
        CachedExplainWhatHappened = string.IsNullOrWhiteSpace(whatHappened) ? null : whatHappened.Trim();
        CachedExplainLongDescription = string.IsNullOrWhiteSpace(longDescription) ? null : longDescription.Trim();
        CachedExplainWhatClaudeWants = string.IsNullOrWhiteSpace(whatClaudeWants) ? null : whatClaudeWants.Trim();
        CachedExplainSay = string.IsNullOrWhiteSpace(say) ? null : say.Trim();
    }

    /// <summary>
    /// Aggregate at-a-glance color for this session. Owned by the
    /// SessionStatusWingman on the Director; the rest of the system reads it but never
    /// writes it. Defaults to "blue" ("working/starting") at construction, which the
    /// wingman immediately confirms. The detector only ever drives blue (working) and
    /// red (needs you); "unknown" is used for an exited session.
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
    /// Sole writer of <see cref="StatusColor"/>. Called by the
    /// SessionStatusWingman. No other code path may set the color — that's
    /// how we keep the UI a faithful mirror of the wingman's verdict.
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
        _htmlParserFeed = data =>
        {
            // Raw "the terminal moved" timestamp -- every byte, no cosmetic filtering.
            // The Wingman tab reads this to show how long ago output last appeared.
            Volatile.Write(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
            lock (_htmlParserLock)
            {
                _htmlParser?.Parse(data);
            }
        };
        buffer.OnBytesWritten += _htmlParserFeed;
        FileLog.Write($"[Session] InitializeHtmlParser: sessionId={Id}, grid={HtmlGridCols}x{HtmlGridRows}, maxScrollback={HtmlMaxScrollback}");
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
        lock (_htmlParserLock)
        {
            var rows = new string[HtmlGridRows];
            var sb = new System.Text.StringBuilder(HtmlGridCols);
            for (int r = 0; r < HtmlGridRows; r++)
            {
                sb.Clear();
                for (int c = 0; c < HtmlGridCols; c++)
                {
                    var ch = _htmlCells[c, r].Character;
                    sb.Append(ch == '\0' ? ' ' : ch);
                }
                rows[r] = sb.ToString().TrimEnd();
            }
            var (col, row) = _htmlParser.GetCursorPosition();
            return (rows, row, col);
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

    /// <summary>Send raw bytes to the backend.</summary>
    public void SendInput(byte[] data)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        FileLog.Write($"[Session] SendInput: session={Id}, bytes={data.Length}, firstByte=0x{(data.Length > 0 ? data[0].ToString("X2") : "00")}");
        _backend.Write(data);
        // Only promote to Working when the write contains an actual submission
        // (CR or LF). A bare keystroke is the user composing at the prompt --
        // Claude Code hasn't received a turn yet. Treating every byte as Working
        // flickered the sidebar dot blue on every character typed.
        if (ContainsSubmit(data))
        {
            IsBrandNew = false;
            SetActivityState(ActivityState.Working);
        }
    }

    private static bool ContainsSubmit(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
            if (data[i] == 0x0D || data[i] == 0x0A) return true;
        return false;
    }

    /// <summary>Send text + Enter to the backend.</summary>
    public async Task SendTextAsync(string text)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;

        FileLog.Write($"[Session] SendTextAsync: session={Id}, text=\"{(text.Length > 60 ? text[..60] + "..." : text)}\", len={text.Length}");
        await _backend.SendTextAsync(text);
        IsBrandNew = false;
        SetActivityState(ActivityState.Working);
    }

    /// <summary>Send text followed by Enter (sync wrapper).</summary>
    public void SendText(string text)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        // Fire and forget for sync API
        _ = SendTextAsync(text);
    }

    /// <summary>Send just an Enter keystroke to the backend.</summary>
    public async Task SendEnterAsync()
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        await _backend.SendEnterAsync();
    }

    private void SetActivityState(ActivityState newState)
    {
        var old = ActivityState;
        if (old == newState) return;
        ActivityState = newState;
        // A real activity change ends any Wingman "running in the background" overlay: once the
        // terminal produces output again (Working), or the session otherwise leaves the parked
        // turn-end it was judged at, the background-wait verdict is stale. The next turn-end
        // briefing re-evaluates from scratch. Only WaitingForInput/WaitingForPerm preserve it.
        if (newState is not (ActivityState.WaitingForInput or ActivityState.WaitingForPerm))
            IsBackgroundRunning = false;
        // A real state change opens a new "generation". This releases any sticky
        // positive-evidence color from the previous generation (issue #136 option C):
        // e.g. a red pending-question survives cosmetic repaints while the session is
        // idle, but the moment the user answers (-> Working) the badge is free to go
        // blue again.
        Interlocked.Increment(ref _activityGeneration);
        // Log the transition (blue<->red) to the in-memory ring the Wingman tab renders.
        RecordStateChange(old, newState);
        OnActivityStateChanged?.Invoke(old, newState);
    }

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
        _backend.Resize(cols, rows);
        CurrentCols = cols;
        CurrentRows = rows;
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
        ExitCode = exitCode;
        Status = SessionStatus.Exited;
        // Process exit is an authoritative, transport-independent signal - drive the
        // state directly so it works in both terminal-driven and hook modes.
        SetActivityState(ActivityState.Exited);
    }

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
        _backend.Dispose();
    }
}
