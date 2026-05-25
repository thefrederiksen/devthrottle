using System.Collections.Concurrent;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Per-Director wingman that is the SINGLE WRITER of <see cref="Session.StatusColor"/>.
///
/// Phase 3 of the SessionWingman goal: every live session has a meaningful color
/// from the moment it is created — no "unknown" dots on the directory. The wingman
/// listens for real-time session events and updates the color accordingly. The UI
/// renders exactly what the wingman writes; it never derives or guesses.
///
/// Two paths feed the color:
///
/// 1. Fast path (this class, synchronous, no LLM)
///    - OnSessionCreated         -> green  ("session created")
///    - OnActivityStateChanged   -> Working          -> blue   ("working")
///                                  Idle             -> green  ("idle, ready")
///                                  WaitingForInput  -> green  ("ready, awaiting next prompt")
///                                                      (Phase 4a: this state by itself
///                                                       is the agent sitting at its
///                                                       prompt - it is NOT a pending
///                                                       question. Red is promoted only
///                                                       when the wingman has positive
///                                                       evidence via the buffer scan or
///                                                       the slow-path turn summary.)
///                                  WaitingForPerm   -> red    ("waiting for permission")
///                                                      (always a user gate, no ambiguity)
///                                  Starting         -> green  ("starting")
///                                  Exited           -> (left to the API to hide; we
///                                                       set color to "unknown" + reason
///                                                       so debug tools still see a value)
///
/// 2. Slow path (turn summary, async; wired via <see cref="TurnSummaryCache"/> today)
///    - On OnTurnCompleted, when Haiku returns a TurnSummary, the wingman refines:
///      * needs_user = "question" | "error" | "permission" -> red    (+ detail)
///      * wingman warnings (rule violations etc.)       -> yellow (+ detail)
///      * needs_user = "idle" with git dirty               -> yellow ("idle, uncommitted")
///      * otherwise                                        -> green  ("clean turn")
///
///    The slow path is wired in by feeding finished summaries through
///    <see cref="ApplyTurnSummary"/>. The wingman does not generate summaries itself.
///
/// Race semantics: writes are unconditional, last-event-wins. The activity-state path
/// produces near-instant updates; the turn-summary path arrives ~10 s later. When both
/// fire close together, the final color reflects whatever was true at the moment of the
/// last write — which is the truthful answer.
/// </summary>
public sealed class SessionStatusWingman : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _activityHandlers = new();
    private readonly ConcurrentDictionary<Guid, PromptInjectionWatcher> _injectionWatchers = new();
    private readonly ConcurrentDictionary<Guid, OutputActivityWatcher> _outputWatchers = new();
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Debounce window between the last buffer write and running the prompt-input
    /// extraction. Long enough that Claude Code's Ink TUI has settled into a
    /// final frame; short enough that the user sees the suggestion almost
    /// immediately after Claude Code finishes drawing.
    /// </summary>
    internal static readonly TimeSpan PromptInjectionDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Debounce window for the output-activity watcher. Shorter than the
    /// prompt-injection debounce so the dot turns blue *before* the input-line
    /// mirror runs. A single TUI redraw burst is well under this window; a real
    /// streaming turn produces many writes per second so the watcher promotes
    /// to blue almost immediately and stays there.
    /// </summary>
    internal static readonly TimeSpan OutputActivityDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Minimum byte count within a debounce window before the output-activity
    /// watcher promotes to blue. Tunes out single-byte spinner ticks; a normal
    /// streamed token produces 10s-100s of bytes per write.
    /// </summary>
    internal const int OutputActivityMinBurstBytes = 32;

    public SessionStatusWingman(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>Begin watching sessions. Idempotent.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[SessionStatusWingman] Start");

        _sessionManager.OnSessionCreated += OnSessionCreated;

        // Wire existing sessions (restored from persistence on Director boot).
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s, isNew: false);
    }

    private void OnSessionCreated(Session session) => WireSession(session, isNew: true);

    private void WireSession(Session session, bool isNew)
    {
        if (_activityHandlers.ContainsKey(session.Id)) return;

        // Initialize color from current activity state. New sessions start green
        // (greenfield); restored sessions get their truthful current-state color.
        var (color, reason) = ColorFromActivityState(session.ActivityState, isNew);
        session.SetStatusColor(color, reason, source: SourceForState(session.ActivityState));
        FileLog.Write($"[SessionStatusWingman] init {session.Id} -> {color} ({reason})");

        Action<ActivityState, ActivityState> handler = (oldState, newState) =>
        {
            try
            {
                var (c, r) = ColorFromActivityState(newState, isNew: false);
                session.SetStatusColor(c, r, source: SourceForState(newState));
                FileLog.Write($"[SessionStatusWingman] {session.Id} activity {oldState}->{newState} => {c} ({r})");

                // Phase 4a: WaitingForInput is green by default; promote to red only
                // when the buffer shows an actual question marker. Cheap; gated on the
                // state transition so we don't scan every buffer tick.
                if (newState == ActivityState.WaitingForInput)
                {
                    PromotePendingQuestionIfBufferShowsOne(session);
                    // One-shot scan for an injected suggestion right at the transition,
                    // in addition to the debounced byte-stream watcher below — covers
                    // the case where Claude Code has already rendered the next-prompt
                    // suggestion by the time the activity-state hook fires.
                    if (_injectionWatchers.TryGetValue(session.Id, out var w))
                        w.RequestImmediateScan();
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionStatusWingman] handler failed for {session.Id}: {ex.Message}");
            }
        };
        _activityHandlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;

        // Subscribe to the byte stream so we re-scan Claude Code's input-prompt
        // line whenever the TUI redraws and then goes quiet. The watcher debounces
        // bursts; we only run extraction once writes settle.
        var buffer = session.Buffer;
        if (buffer is not null)
        {
            var watcher = new PromptInjectionWatcher(session, buffer);
            if (_injectionWatchers.TryAdd(session.Id, watcher))
                watcher.Start();
            else
                watcher.Dispose();

            // Phase 2 safety net: if Claude Code is streaming output, the session is
            // by definition working. This catches any path where the hook events
            // didn't deliver Working (relay backlog, /clear-orphan window before
            // relink lands, race, restored session). Never overrides red.
            var outputWatcher = new OutputActivityWatcher(session, buffer);
            if (_outputWatchers.TryAdd(session.Id, outputWatcher))
                outputWatcher.Start();
            else
                outputWatcher.Dispose();
        }
    }

    /// <summary>
    /// Fast-path mapping from real-time activity state to color.
    ///
    /// Phase 4a: WaitingForInput is GREEN by default. The activity-state code cannot
    /// distinguish "agent asked a question mid-turn" from "agent finished and is back
    /// at the prompt". Painting both red trained the user to ignore red. The wingman
    /// promotes to Red only when it has positive evidence of a pending question:
    /// either the slow-path turn summary (<see cref="ApplyTurnSummary"/>) or a buffer
    /// scan for known question markers (<see cref="PromotePendingQuestionIfBufferShowsOne"/>).
    /// </summary>
    internal static (string color, string reason) ColorFromActivityState(ActivityState state, bool isNew)
    {
        return state switch
        {
            ActivityState.Starting        => (StatusColor.Green, isNew ? "session created" : "starting"),
            ActivityState.Working         => (StatusColor.Blue,  "working"),
            ActivityState.Idle            => (StatusColor.Green, "idle, ready for next task"),
            ActivityState.WaitingForInput => (StatusColor.Green, "ready, awaiting next prompt"),
            ActivityState.WaitingForPerm  => (StatusColor.Red,   "waiting for permission"),
            ActivityState.Exited          => (StatusColor.Unknown, "exited"),
            _                             => (StatusColor.Unknown, "unknown activity state"),
        };
    }

    /// <summary>
    /// Colour-source for a fast-path activity-state write (issue #136 option C).
    /// WaitingForPerm is an authoritative on-screen gate the user must clear, so it
    /// is positive evidence -- a byte-burst guess or a re-evaluated mapping must not
    /// repaint over it. Every other state is the ordinary ActivityState baseline.
    /// </summary>
    internal static StatusColorSource SourceForState(ActivityState state)
        => state == ActivityState.WaitingForPerm
            ? StatusColorSource.PositiveEvidence
            : StatusColorSource.ActivityState;

    /// <summary>
    /// Buffer markers that indicate a Claude Code session is actively waiting for the
    /// user to answer a question (not just sitting at the idle prompt). Case-insensitive
    /// substring match against the tail of the terminal buffer.
    /// </summary>
    private static readonly string[] QuestionMarkers = new[]
    {
        // Imperative-style "may I" phrasings the agent uses when asking the user.
        "do you want to",
        "would you like",
        "want me to",
        "should i ",
        "should we ",
        "shall i ",
        "shall we ",
        "may i ",
        "ok to ",
        "okay to ",
        // Yes/No / confirm prompts (both Claude Code's own and shell tools').
        "[y/n]",
        "(y/n)",
        "(y/N)",
        "(Y/n)",
        "please confirm",
        "press enter to continue",
        "select an option",
        // Interrupted: user pressed Esc mid-turn; Claude Code parks on this footer
        // waiting for redirection. A genuine user gate (see issue #129).
        "what should claude do instead",
    };

    /// <summary>
    /// Scan the tail of the session's terminal buffer for an active question marker.
    /// Cheap (no regex, single ToLower + Contains over &lt;= 4 KB). Called by the
    /// activity-state handler whenever a session enters WaitingForInput so the
    /// wingman can promote to Red even before the slow-path turn summary arrives.
    /// </summary>
    internal void PromotePendingQuestionIfBufferShowsOne(Session session)
    {
        if (BufferShowsUserGate(session, out var marker))
        {
            session.SetStatusColor(StatusColor.Red, "pending question", source: StatusColorSource.PositiveEvidence);
            FileLog.Write($"[SessionStatusWingman] {session.Id} buffer marker '{marker}' -> red");
        }
    }

    /// <summary>
    /// Returns true when the tail of the session's terminal buffer shows POSITIVE,
    /// deterministic evidence that the agent is parked waiting on the user: a known
    /// question phrasing, a yes/no or numbered-choice confirmation, or the
    /// interrupted footer. The persistent mode footer ("bypass permissions on ...")
    /// is deliberately NOT in <see cref="QuestionMarkers"/>, so it never counts.
    /// Cheap: single ToLower + Contains over &lt;= 4 KB. Used both to promote to red
    /// directly and (issue #136) to corroborate an LLM turn-summary verdict before
    /// it is allowed to flip an idle session red.
    /// </summary>
    internal static bool BufferShowsUserGate(Session session, out string marker)
    {
        marker = "";
        try
        {
            var buf = session.Buffer;
            if (buf is null) return false;
            var bytes = buf.DumpAll();
            if (bytes is null || bytes.Length == 0) return false;
            const int TailBytes = 4096;
            var start = Math.Max(0, bytes.Length - TailBytes);
            var tail = System.Text.Encoding.UTF8.GetString(bytes, start, bytes.Length - start).ToLowerInvariant();
            foreach (var m in QuestionMarkers)
            {
                if (tail.Contains(m, StringComparison.OrdinalIgnoreCase))
                {
                    marker = m;
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionStatusWingman] BufferShowsUserGate failed for {session.Id}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Externally signal that a session has a pending user-facing question, with the
    /// supplied short detail. Used by anything that knows positively (e.g. the
    /// turn-summary slow path's <c>needs_user_short</c>). Idempotent.
    /// </summary>
    public void PromotePendingQuestion(Session session, string detail)
    {
        if (session is null) return;
        var reason = string.IsNullOrWhiteSpace(detail) ? "pending question" : detail.Trim();
        if (reason.Length > 500) reason = reason[..497] + "...";
        session.SetStatusColor(StatusColor.Red, reason, source: StatusColorSource.PositiveEvidence);
    }

    /// <summary>
    /// Slow-path: refine the color using a freshly-computed TurnSummary. Callers
    /// (today: <see cref="TurnSummaryCache"/>'s background completion handler) invoke
    /// this once Haiku returns. The wingman does not generate summaries itself.
    /// </summary>
    public void ApplyTurnSummary(Session session, Gateway.Contracts.TurnSummary summary, bool gitDirty = false, bool hasWarnings = false, long? expectedGeneration = null)
    {
        if (session is null || summary is null) return;

        // Issue #137 item 4: a turn summary is computed for the turn that ended at a
        // specific activity-state generation. If the state has moved on by the time
        // Haiku returns (~10s later) -- the user submitted again, an interrupt, a new
        // turn -- the summary is stale and must not write. This is the principled
        // generalization of the state-based guard below: the caller samples the
        // generation at turn-end and we drop the write if it no longer matches.
        if (expectedGeneration.HasValue && session.ActivityGeneration != expectedGeneration.Value)
        {
            FileLog.Write($"[SessionStatusWingman] ApplyTurnSummary {session.Id} skipped: stale generation (expected {expectedGeneration.Value}, now {session.ActivityGeneration})");
            return;
        }

        // Phase 4a: WaitingForPerm is always red and authoritative - the agent will not
        // proceed until the user grants permission. Don't let a stale turn summary
        // downgrade it.
        //
        // Phase 4g: same protection for Working. A turn summary describes the turn
        // that JUST ENDED. If the user has already submitted the next prompt
        // (session is now Working), the summary's needs_user=question is stale by
        // definition - the question has been answered. Don't let an in-flight blue
        // get repainted red by Haiku finishing 10s later. Reproduced as: user
        // submits answer at T, wingman goes blue, Haiku summary for prior turn
        // lands at T+10s carrying needs_user=question, banner flickers back to red.
        if (session.ActivityState is ActivityState.WaitingForPerm or ActivityState.Working)
        {
            FileLog.Write($"[SessionStatusWingman] ApplyTurnSummary {session.Id} skipped: stale, activity={session.ActivityState}");
            return;
        }

        var n = (summary.NeedsUser ?? "").Trim().ToLowerInvariant();

        // Issue #136: a turn summary must NOT, on its own, flip an idle session to
        // red. When the session is sitting at its prompt (WaitingForInput/Idle) and
        // the buffer shows no real on-screen user gate, an LLM verdict of
        // question/permission/error is uncorroborated -- it is the classifier
        // misreading the persistent mode footer ("bypass permissions on ...") or the
        // agent's conversational "say the word when you want it" as a pending gate.
        // That false red contradicted the idle state and flip-flopped against the
        // deterministic fast path. Downgrade it to idle; the fast path stays green.
        if (n is "question" or "error" or "permission"
            && session.ActivityState is ActivityState.WaitingForInput or ActivityState.Idle
            && !BufferShowsUserGate(session, out _))
        {
            FileLog.Write($"[SessionStatusWingman] ApplyTurnSummary {session.Id} needs_user={n} uncorroborated by buffer at {session.ActivityState} -> treating as idle (issue #136)");
            n = "idle";
        }

        if (n is "question" or "error" or "permission")
        {
            // Phase 4e: prefer NeedsUserShort (one crisp sentence) over NeedsUserDetail
            // (which can be a paragraph). Falls back through NeedsUserDetail -> the raw
            // category if neither is present.
            var reason =
                !string.IsNullOrWhiteSpace(summary.NeedsUserShort) ? summary.NeedsUserShort!.Trim() :
                !string.IsNullOrWhiteSpace(summary.NeedsUserDetail) ? summary.NeedsUserDetail!.Trim() :
                n;
            session.SetStatusColor(StatusColor.Red, reason, llm: true, source: StatusColorSource.PositiveEvidence);
            FileLog.Write($"[SessionStatusWingman] ApplyTurnSummary {session.Id} => red (needs_user={n}, reasonLen={reason.Length})");
            return;
        }
        if (hasWarnings)
        {
            session.SetStatusColor(StatusColor.Yellow, "wingman warning", llm: true);
            FileLog.Write($"[SessionStatusWingman] ApplyTurnSummary {session.Id} => yellow (warnings)");
            return;
        }
        if (n == "idle" && gitDirty)
        {
            session.SetStatusColor(StatusColor.Yellow, "idle, uncommitted changes", llm: true);
            FileLog.Write($"[SessionStatusWingman] ApplyTurnSummary {session.Id} => yellow (idle+dirty)");
            return;
        }

        // Clean turn -> back to green with the headline as the reason (truncated).
        var headline = string.IsNullOrWhiteSpace(summary.Headline) ? "clean turn" : summary.Headline!;
        if (headline.Length > 80) headline = headline[..77] + "...";
        session.SetStatusColor(StatusColor.Green, headline, llm: true);
        FileLog.Write($"[SessionStatusWingman] ApplyTurnSummary {session.Id} => green (needs_user={n}, headline=\"{headline}\")");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessionManager.OnSessionCreated -= OnSessionCreated;
        foreach (var s in _sessionManager.ListSessions())
        {
            if (_activityHandlers.TryRemove(s.Id, out var h))
                s.OnActivityStateChanged -= h;
        }
        foreach (var kv in _injectionWatchers)
            kv.Value.Dispose();
        _injectionWatchers.Clear();
        foreach (var kv in _outputWatchers)
            kv.Value.Dispose();
        _outputWatchers.Clear();
    }
}

/// <summary>
/// Watches a session's terminal buffer for text Claude Code has injected into
/// its own input-prompt line, and forwards detected text to
/// <see cref="Session.SetPendingPromptText"/> with source "wingman" so the
/// cc-director "Type a message..." textbox can mirror it.
///
/// Operation:
/// 1. Subscribe to <see cref="CircularTerminalBuffer.OnBytesWritten"/>.
/// 2. On each write, restart a 500ms debounce timer.
/// 3. When the timer fires (no new bytes for 500ms), run
///    <see cref="PromptInputLineExtractor.ExtractClaudeCodeInputLine"/>.
/// 4. If the extracted text is non-empty and differs from what we last pushed,
///    call <c>session.SetPendingPromptText(text, "wingman")</c>.
/// 5. The UI side decides whether to actually populate the visible textbox
///    (e.g. don't clobber what the user has already typed). This class is
///    intentionally ignorant of UI state.
///
/// State machine for <c>_lastPushedText</c>:
///  - null = nothing pushed yet for this session
///  - ""   = we observed an empty input box; resets the "already pushed" memory
///  - "X"  = we pushed "X"; don't push it again unless the extracted text changes
///
/// This means: if the user clears the cc-director textbox while Claude Code's
/// injection is still in the terminal, we will NOT re-inject — by the next
/// scan, <c>_lastPushedText</c> still equals "X" and we short-circuit. Only when
/// Claude Code itself changes its injection (or empties its prompt) does the
/// state reset.
/// </summary>
internal sealed class PromptInjectionWatcher : IDisposable
{
    private readonly Session _session;
    private readonly CircularTerminalBuffer _buffer;
    private readonly Action<byte[]> _onBytes;
    private readonly System.Threading.Timer _timer;
    private string? _lastPushedText;
    private int _disposed;

    public PromptInjectionWatcher(Session session, CircularTerminalBuffer buffer)
    {
        _session = session;
        _buffer = buffer;
        _onBytes = _ => Bump();
        _timer = new System.Threading.Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _buffer.OnBytesWritten += _onBytes;
        FileLog.Write($"[PromptInjectionWatcher] start session={_session.Id}");
    }

    /// <summary>
    /// Force a scan at the next debounce window, without waiting for new bytes.
    /// Used on activity-state transitions where the relevant content may already
    /// be in the buffer.
    /// </summary>
    public void RequestImmediateScan() => Bump();

    private void Bump()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _timer.Change(SessionStatusWingman.PromptInjectionDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* race with Dispose; ignore */ }
    }

    private void OnTimerTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var bytes = _buffer.DumpAll();
            var extracted = PromptInputLineExtractor.ExtractClaudeCodeInputLine(bytes);

            if (extracted is null)
            {
                // No Claude Code TUI frame detectable. Don't disturb whatever's in
                // the textbox; just reset our "already pushed" memory so the next
                // detected injection (after a frame change) is treated as new.
                _lastPushedText = null;
                return;
            }

            if (extracted.Length == 0)
            {
                // Claude Code's input box is empty. Reset memory so we'll push
                // again if it later fills with the same suggestion.
                _lastPushedText = null;
                return;
            }

            if (string.Equals(extracted, _lastPushedText, StringComparison.Ordinal))
                return; // already pushed this exact text — don't re-fire

            FileLog.Write($"[PromptInjectionWatcher] session={_session.Id} push len={extracted.Length} text=\"{Truncate(extracted, 80)}\"");
            _session.SetPendingPromptText(extracted, "wingman");
            _lastPushedText = extracted;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PromptInjectionWatcher] tick failed session={_session.Id}: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _buffer.OnBytesWritten -= _onBytes;
        _timer.Dispose();
    }
}

/// <summary>
/// Phase 2 safety-net watcher: if the session's terminal buffer is actively
/// receiving bytes from Claude Code AND the activity state is uncertain
/// (Starting / Working), promote <see cref="Session.StatusColor"/> to blue
/// ("streaming output"). The watcher exists to catch hook-event gaps: relay
/// backlog, the /clear-orphan window before the EventRouter relink lands,
/// race conditions, restored sessions wired up before their buffer has data.
///
/// Conservative by design:
///  - Debounced (250ms) so a single redraw doesn't fire.
///  - Requires <see cref="SessionStatusWingman.OutputActivityMinBurstBytes"/>
///    bytes accumulated within the window so spinner ticks don't promote.
///  - Defers to hook-reported activity state when that state is definitive:
///    <see cref="ActivityState.WaitingForPerm"/>, <see cref="ActivityState.WaitingForInput"/>,
///    <see cref="ActivityState.Idle"/>, <see cref="ActivityState.Exited"/>. In
///    those states Claude has told us it is not working; any bytes are cosmetic
///    (cursor blink, spinner ticks, status-line redraws like "Brewed for Xs"),
///    not real streamed output.
///  - Never overrides an existing <see cref="StatusColor.Red"/>. Red means the
///    wingman has positive evidence the user must act (pending question,
///    permission, warning). A byte burst is not evidence the user has answered.
///  - Never re-fires once the color is already blue (no-op on subsequent bursts).
/// </summary>
internal sealed class OutputActivityWatcher : IDisposable
{
    private readonly Session _session;
    private readonly CircularTerminalBuffer _buffer;
    private readonly Action<byte[]> _onBytes;
    private readonly System.Threading.Timer _timer;
    private int _pendingBytes;
    private int _disposed;

    public OutputActivityWatcher(Session session, CircularTerminalBuffer buffer)
    {
        _session = session;
        _buffer = buffer;
        _onBytes = bytes => Bump(bytes?.Length ?? 0);
        _timer = new System.Threading.Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _buffer.OnBytesWritten += _onBytes;
        FileLog.Write($"[OutputActivityWatcher] start session={_session.Id}");
    }

    private void Bump(int byteCount)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Add(ref _pendingBytes, byteCount);
        try { _timer.Change(SessionStatusWingman.OutputActivityDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* race with Dispose; ignore */ }
    }

    private void OnTimerTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var burstBytes = Interlocked.Exchange(ref _pendingBytes, 0);
            if (burstBytes < SessionStatusWingman.OutputActivityMinBurstBytes)
                return; // too small -- spinner / cursor blink, not a real stream

            // Already blue: nothing to do. The activity-state path or a prior
            // tick has correctly identified that the session is working.
            if (string.Equals(_session.StatusColor, StatusColor.Blue, StringComparison.Ordinal))
                return;

            // Defer to hook-reported activity state when it is definitive.
            // WaitingForPerm: Claude is blocked on the user. WaitingForInput:
            // Claude has finished its turn (Stop hook fired). Idle: Claude is
            // between turns. Exited: process gone. In all four cases any bytes
            // are cosmetic (cursor blink, spinner, "Brewed for Xs" status
            // redraws) -- not real streamed output. Promoting to blue here
            // overwrites a Red question prompt set by the slow-path turn
            // summary, which produced the observed live bug.
            if (_session.ActivityState is ActivityState.WaitingForPerm
                                       or ActivityState.WaitingForInput
                                       or ActivityState.Idle
                                       or ActivityState.Exited)
                return;

            // Never override Red. Red is the wingman's verdict that the user
            // must act (pending question, permission, warning). A byte burst is
            // not evidence the user has answered -- only an explicit hook
            // (UserPromptSubmit, etc.) clears that.
            if (string.Equals(_session.StatusColor, StatusColor.Red, StringComparison.Ordinal))
                return;

            FileLog.Write($"[OutputActivityWatcher] session={_session.Id} burst={burstBytes}B -> blue (streaming output)");
            _session.SetStatusColor(StatusColor.Blue, "streaming output", source: StatusColorSource.Inferred);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OutputActivityWatcher] tick failed session={_session.Id}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _buffer.OnBytesWritten -= _onBytes;
        _timer.Dispose();
    }
}
