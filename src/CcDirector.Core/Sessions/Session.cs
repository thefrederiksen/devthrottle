using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Memory;
using CcDirector.Core.Pipes;
using CcDirector.Core.Utilities;
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
/// Represents a single Claude session. Delegates process management to an ISessionBackend.
/// Session handles metadata, activity state, and routing - backend handles process I/O.
/// </summary>
public sealed class Session : IDisposable
{
    /// <summary>Minimum length of first prompt required for verification (avoid verifying too early).</summary>
    public const int MinVerificationLength = 50;

    private readonly ISessionBackend _backend;
    private readonly TurnAccumulator _turnAccumulator = new();
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

    /// <summary>Process ID from the backend.</summary>
    public int ProcessId => _backend.ProcessId;

    /// <summary>Claude's cognitive activity state, driven by hook events.</summary>
    public ActivityState ActivityState { get; private set; } = ActivityState.Starting;

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
    /// waiting on, or null when nothing is pending. Set by
    /// <see cref="HandlePipeEvent"/> on the corresponding hook events and
    /// cleared automatically when the activity state moves out of
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

    /// <summary>Fires when a turn completes (Stop event received after UserPromptSubmit).</summary>
    public event Action<Session, TurnData>? OnTurnCompleted;

    /// <summary>
    /// When true, this Director derives ActivityState and turn boundaries from the
    /// terminal stream (<c>TerminalStateDetector</c>) instead of Claude Code hooks.
    /// Set once at startup. In this mode <see cref="HandlePipeEvent"/> is inert for
    /// state and turns - hooks, if any still arrive, are ignored - and the detector is
    /// the single authority. Reversible via the CC_DIRECTOR_TERMINAL_STATE env var so
    /// we can fall back to the hook path without a rebuild.
    /// </summary>
    public static bool TerminalDrivenState { get; set; }

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

    /// <summary>Latest proactively-generated wingman briefing, or null if none yet.</summary>
    public string? CachedExplainText { get; private set; }

    /// <summary>When <see cref="CachedExplainText"/> was last generated (UTC).</summary>
    public DateTime? CachedExplainAt { get; private set; }

    /// <summary>Model that produced the cached briefing (e.g. "opus").</summary>
    public string? CachedExplainModel { get; private set; }

    /// <summary>Tap-to-answer options from the latest briefing (may be empty).</summary>
    public IReadOnlyList<string> CachedQuickReplies { get; private set; } = System.Array.Empty<string>();

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
    }

    /// <summary>
    /// Aggregate at-a-glance color for this session. Owned by the
    /// SessionStatusWingman on the Director; the rest of the system reads it
    /// but never writes it. Defaults to "green" at construction ("greenfield").
    /// Values: "green" | "blue" | "yellow" | "red" | "unknown".
    /// </summary>
    public string StatusColor { get; private set; } = "green";

    /// <summary>
    /// Short human-readable reason for the current StatusColor, e.g.
    /// "session created", "working", "waiting for input", "clean turn". Shown
    /// as the dot tooltip in the Gateway directory view. Set together with
    /// <see cref="StatusColor"/> via <see cref="SetStatusColor"/>.
    /// </summary>
    public string LastStatusReason { get; private set; } = "session created";

    /// <summary>
    /// The verbatim text of the most recent prompt the user submitted to this
    /// session, or null if none has been seen. Captured from the Claude Code
    /// <c>UserPromptSubmit</c> hook in <see cref="HandlePipeEvent"/>, so it covers
    /// every entry point (desktop terminal, phone web page, voice) - not just
    /// prompts sent through the Control API. Surfaced via
    /// <c>GET /sessions/{sid}/wingman</c> so the Session page can show "what I just
    /// asked" while the turn is working.
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
        string Reason);

    private const int WingmanEventLogCapacity = 50;
    private readonly LinkedList<WingmanEvent> _wingmanEvents = new();
    private readonly object _wingmanEventsLock = new();

    /// <summary>
    /// Most recent wingman decisions for this session, newest first. Ring-buffered
    /// at <c>WingmanEventLogCapacity</c>. Surfaced via <c>GET /sessions/{sid}/wingman</c>
    /// so the UI can show WHY a dot is the color it is.
    /// </summary>
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
    public void SetStatusColor(string color, string reason)
    {
        if (string.IsNullOrEmpty(color)) return;
        var old = StatusColor;
        var newReason = reason ?? "";
        if (old == color && LastStatusReason == newReason) return;
        StatusColor = color;
        LastStatusReason = newReason;

        var evt = new WingmanEvent(DateTime.UtcNow, old, color, newReason);
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
            SetActivityState(ActivityState.Working);
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

    /// <summary>Process a hook event and transition activity state accordingly.</summary>
    public void HandlePipeEvent(PipeMessage msg)
    {
        // Terminal-driven mode: the TerminalStateDetector owns ActivityState and turn
        // boundaries from the terminal stream. Hooks are inert here - we neither change
        // state nor accumulate/raise turns from them.
        if (TerminalDrivenState) return;

        FileLog.Write($"[Session] HandlePipeEvent: session={Id}, event={msg.HookEventName}, tool={msg.ToolName ?? "n/a"}, currentState={ActivityState}");

        // Accumulate turn data for session summary
        if (msg.HookEventName == "UserPromptSubmit" && !string.IsNullOrEmpty(msg.Prompt))
        {
            LastUserPrompt = msg.Prompt;
            LastUserPromptAt = DateTime.UtcNow;
            var interrupted = _turnAccumulator.StartTurn(msg.Prompt);
            if (interrupted != null)
                OnTurnCompleted?.Invoke(this, interrupted);
        }
        else if (msg.HookEventName == "PreToolUse")
            _turnAccumulator.AddToolUse(msg);
        else if (msg.HookEventName == "Stop" && _turnAccumulator.IsActive)
        {
            var turnData = _turnAccumulator.FinishTurn();
            OnTurnCompleted?.Invoke(this, turnData);
        }

        var newState = msg.HookEventName switch
        {
            "Stop" => ActivityState.WaitingForInput,
            "UserPromptSubmit" => ActivityState.Working,
            "PreToolUse" when IsInteractiveTool(msg.ToolName) => ActivityState.WaitingForInput,
            "PreToolUse" => ActivityState.Working,
            "PostToolUse" => ActivityState.Working,
            "PostToolUseFailure" => ActivityState.Working,
            "PermissionRequest" => ActivityState.WaitingForPerm,
            "Notification" when msg.NotificationType == "permission_prompt" => ActivityState.WaitingForPerm,
            "Notification" => ActivityState.WaitingForInput,
            "SubagentStart" => ActivityState.Working,
            "SubagentStop" => ActivityState.Working,
            "TaskCompleted" => ActivityState.Working,
            "SessionStart" => ActivityState.Idle,
            // /clear and /compact rotate Claude's session id (SessionEnd-then-SessionStart
            // for a fresh conversation). They are NOT terminations -- hold the current
            // state and let EventRouter relink to the new id. Other reasons
            // (logout, prompt_input_exit, other) are real terminations.
            "SessionEnd" when msg.Reason is "clear" or "compact" => (ActivityState?)null,
            "SessionEnd" => ActivityState.Exited,
            "TeammateIdle" => (ActivityState?)null,
            "PreCompact" => (ActivityState?)null,
            _ => (ActivityState?)null
        };

        if (!newState.HasValue)
        {
            FileLog.Write($"[Session] HandlePipeEvent: no state change for event={msg.HookEventName}");
            return;
        }

        // Once we're waiting for user input (green), only explicit user actions
        // or session end can change the state. This prevents late subagent stops
        // from incorrectly turning the indicator blue.
        if (ActivityState == ActivityState.WaitingForInput)
        {
            var allowedFromWaiting = msg.HookEventName is "UserPromptSubmit" or "SessionEnd" or "PermissionRequest"
                || (msg.HookEventName == "Notification" && msg.NotificationType == "permission_prompt");
            if (!allowedFromWaiting)
            {
                FileLog.Write($"[Session] HandlePipeEvent: blocked {msg.HookEventName} while WaitingForInput");
                return;
            }
        }

        FileLog.Write($"[Session] HandlePipeEvent: session={Id}, {ActivityState}->{newState.Value}");
        SetActivityState(newState.Value);
    }

    private static bool IsInteractiveTool(string? toolName) =>
        toolName is "ExitPlanMode" or "AskUserQuestion" or "EnterPlanMode" or "EnterWorktree" or "ExitWorktree";

    private void SetActivityState(ActivityState newState)
    {
        var old = ActivityState;
        if (old == newState) return;
        ActivityState = newState;
        OnActivityStateChanged?.Invoke(old, newState);
    }

    /// <summary>
    /// Set ActivityState from the <c>TerminalStateDetector</c> in terminal-driven mode.
    /// The detector is the single authority for state in that mode; this is its writer.
    /// </summary>
    internal void ApplyTerminalActivityState(ActivityState newState) => SetActivityState(newState);

    /// <summary>
    /// Raise <see cref="OnTurnCompleted"/> from the terminal detector when it observes a
    /// turn end (terminal output went quiet). Carries a minimal <see cref="TurnData"/>:
    /// the turn summary is built from the terminal buffer, not from this payload.
    /// </summary>
    internal void NotifyTurnEndedFromTerminal()
        => OnTurnCompleted?.Invoke(this, new TurnData("", new List<string>(), new List<string>(), new List<string>(), DateTimeOffset.UtcNow));

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
        _backend.Resize(cols, rows);
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
