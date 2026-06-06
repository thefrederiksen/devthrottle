using System.Collections.Concurrent;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Per-Director wingman that is the SINGLE WRITER of <see cref="Session.StatusColor"/>.
///
/// The badge colour is a direct, mechanical mapping from the session's
/// <see cref="ActivityState"/> - there is NO other algorithm anywhere. ActivityState
/// itself is owned by the <see cref="TerminalStateDetector"/>, whose entire rule is:
/// bytes out of the ConPTY -> Working; <see cref="TerminalStateDetector.QuietThreshold"/>
/// of complete silence -> WaitingForInput. This class just turns that one state into a
/// colour and records the transition:
///
///   Working / Starting          -> blue  ("working")
///   WaitingForInput / Perm / Idle -> red ("needs you")
///   Exited                      -> gray  ("exited")
///
/// There is deliberately no buffer question-marker scan, no byte-burst heuristic, and no
/// turn-summary colour voting. Those were competing classifiers that disagreed with the
/// timer and flip-flopped the badge; they were removed so the colour has a single source.
///
/// It still wires a <see cref="PromptInjectionWatcher"/> per session, but that only
/// mirrors text Claude Code injects into its own input line back to the cc-director
/// textbox - it never touches the colour.
/// </summary>
public sealed class SessionStatusWingman : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _activityHandlers = new();
    private readonly ConcurrentDictionary<Guid, Action<bool>> _explainHandlers = new();
    private readonly ConcurrentDictionary<Guid, Action<bool>> _backgroundHandlers = new();
    private readonly ConcurrentDictionary<Guid, Action<BriefingState>> _briefingHandlers = new();
    private readonly ConcurrentDictionary<Guid, PromptInjectionWatcher> _injectionWatchers = new();
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Debounce window between the last buffer write and running the prompt-input
    /// extraction. Long enough that Claude Code's Ink TUI has settled into a
    /// final frame; short enough that the user sees the suggestion almost
    /// immediately after Claude Code finishes drawing.
    /// </summary>
    internal static readonly TimeSpan PromptInjectionDebounce = TimeSpan.FromMilliseconds(500);

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

        // Initialize colour from the current activity state.
        var (color, reason) = ColorFor(session, isNew);
        session.SetStatusColor(color, reason);
        FileLog.Write($"[SessionStatusWingman] init {session.Id} -> {color} ({reason})");

        // Brand-new session: seed a canned Wingman greeting so the Wingman tab has
        // content the moment the user opens it, with no Opus call. The
        // ProactiveExplainService skips the first turn-end briefing for IsBrandNew
        // sessions (nothing useful to summarize yet); this line is what the user reads
        // until they send their first prompt.
        if (isNew && session.IsBrandNew)
        {
            session.SetCachedExplain(
                "This is a brand new session. Nothing to explain yet -- the Wingman will pick up after your first turn.",
                "system");
        }

        Action<ActivityState, ActivityState> handler = (oldState, newState) =>
        {
            try
            {
                var (c, r) = ColorFor(session, isNew: false);
                session.SetStatusColor(c, r);
                FileLog.Write($"[SessionStatusWingman] {session.Id} activity {oldState}->{newState} => {c} ({r})");

                // Durable record of every state transition (blue<->red), so a session's
                // history survives a Director restart. The in-memory ring on the Session
                // (RecordStateChange, populated in SetActivityState) feeds the live tab.
                StateChangeLog.Append(session.Id, new StateChangeLog.Record(
                    DateTime.UtcNow.ToString("o"), oldState.ToString(), newState.ToString(), c));

                // Keep the prompt-text mirror responsive: when the session settles at its
                // input box, nudge the injection watcher to scan now rather than waiting
                // for the next byte. This is text mirroring only - not a colour decision.
                if (newState == ActivityState.WaitingForInput
                    && _injectionWatchers.TryGetValue(session.Id, out var w))
                    w.RequestImmediateScan();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionStatusWingman] handler failed for {session.Id}: {ex.Message}");
            }
        };
        _activityHandlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;

        // ProactiveExplainService toggles Session.IsExplaining around its briefing call.
        // When the flag flips we recompute the colour so the dot can move into Yellow
        // ("Wingman is reading") while the briefing is in flight and back to Red when
        // it finishes. The activity state has NOT changed during this window -- only the
        // Yellow overlay -- so we don't write to StateChangeLog here.
        Action<bool> explainHandler = isExplaining =>
        {
            try
            {
                var (c, r) = ColorFor(session, isNew: false);
                session.SetStatusColor(c, r);
                FileLog.Write($"[SessionStatusWingman] {session.Id} explaining={isExplaining} => {c} ({r})");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionStatusWingman] explain handler failed for {session.Id}: {ex.Message}");
            }
        };
        _explainHandlers[session.Id] = explainHandler;
        session.OnIsExplainingChanged += explainHandler;

        // TurnBriefOrchestrator drives BriefingState around its read of a finished turn
        // (issue #192). Recompute the colour on every flip so the dot moves into Yellow
        // while the wingman reads and settles to the verdict colour when the brief lands.
        // Like the explain overlay, the activity state has NOT changed during this
        // window, so we do not write to StateChangeLog.
        Action<BriefingState> briefingHandler = state =>
        {
            try
            {
                var (c, r) = ColorFor(session, isNew: false);
                session.SetStatusColor(c, r);
                FileLog.Write($"[SessionStatusWingman] {session.Id} briefingState={state} => {c} ({r})");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionStatusWingman] briefing handler failed for {session.Id}: {ex.Message}");
            }
        };
        _briefingHandlers[session.Id] = briefingHandler;
        session.OnBriefingStateChanged += briefingHandler;

        // ProactiveExplainService flips Session.IsBackgroundRunning from the explain verdict.
        // When it flips we recompute the colour so a session parked on its own background task
        // moves Red -> Purple ("running in background") and back to Red when the verdict clears.
        // Like the explain overlay, the activity state has NOT changed here, so we do not write
        // to StateChangeLog.
        Action<bool> backgroundHandler = isBackground =>
        {
            try
            {
                var (c, r) = ColorFor(session, isNew: false);
                session.SetStatusColor(c, r);
                FileLog.Write($"[SessionStatusWingman] {session.Id} backgroundRunning={isBackground} => {c} ({r})");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionStatusWingman] background handler failed for {session.Id}: {ex.Message}");
            }
        };
        _backgroundHandlers[session.Id] = backgroundHandler;
        session.OnIsBackgroundRunningChanged += backgroundHandler;

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
        }
    }

    /// <summary>
    /// The one and only state-to-colour mapping. Working (and the brief Starting state)
    /// are blue; every state that means "not producing output, your turn" is red; a gone
    /// process is gray. The <see cref="TerminalStateDetector"/> only ever emits Working
    /// and WaitingForInput, so in practice the badge is just blue or red.
    /// </summary>
    internal static (string color, string reason) ColorFromActivityState(ActivityState state, bool isNew)
    {
        return state switch
        {
            ActivityState.Starting        => (StatusColor.Blue, isNew ? "session created" : "starting"),
            ActivityState.Working         => (StatusColor.Blue, "working"),
            ActivityState.WaitingForInput => (StatusColor.Red,  "needs you"),
            ActivityState.WaitingForPerm  => (StatusColor.Red,  "needs you"),
            ActivityState.Idle            => (StatusColor.Red,  "needs you"),
            ActivityState.Exited          => (StatusColor.Unknown, "exited"),
            _                             => (StatusColor.Unknown, "unknown activity state"),
        };
    }

    /// <summary>
    /// Resolve the dot colour for a session given its current ActivityState plus the
    /// Wingman overlays (BriefingState, IsExplaining, IsBackgroundRunning). Yellow is
    /// emitted only when the session is parked at a turn-end (WaitingForInput /
    /// WaitingForPerm) and a wingman read is in flight - either the turn-brief pipeline
    /// (BriefingState=Briefing, all sessions) or the legacy auto-explain (IsExplaining,
    /// WingmanEnabled only). Otherwise the colour is the plain activity-state mapping above.
    /// </summary>
    internal static (string color, string reason) ColorFor(Session session, bool isNew)
    {
        var baseColor = ColorFromActivityState(session.ActivityState, isNew);

        var atTurnEnd = session.ActivityState is ActivityState.WaitingForInput
                                              or ActivityState.WaitingForPerm;

        // Yellow overlay #1: the turn-brief pipeline is reading the just-finished turn
        // (TURN_BRIEFING.md, issue #192). Until the brief lands we do not KNOW whether
        // the session needs you - red is the brief's verdict, not the detector's guess -
        // so the badge must not scream "needs you" yet. Unlike the legacy IsExplaining
        // overlay below, this does NOT require WingmanEnabled: the TurnBriefOrchestrator
        // briefs every session.
        if (session.BriefingState == BriefingState.Briefing && atTurnEnd)
            return (StatusColor.Yellow, "wingman is reading");

        // Yellow overlay #2 (legacy): the ProactiveExplainService auto-explain in flight.
        if (session.WingmanEnabled && session.IsExplaining && atTurnEnd)
            return (StatusColor.Yellow, "wingman is reading");

        // Purple overlay: the Wingman read the screen and determined the session is parked on
        // its OWN background task (a build, a running shell), not on the user. Only meaningful
        // at a turn-end where the badge would otherwise be Red "needs you". Released the moment
        // output resumes (the Session clears the flag when it transitions off WaitingForInput).
        // Checked after Yellow so the transient "wingman is reading" briefing still shows while
        // the verdict is being computed, then settles to Purple once the flag is set.
        if (session.WingmanEnabled && session.IsBackgroundRunning && atTurnEnd)
            return (StatusColor.Purple, session.BackgroundReason);

        return baseColor;
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
            if (_explainHandlers.TryRemove(s.Id, out var eh))
                s.OnIsExplainingChanged -= eh;
            if (_backgroundHandlers.TryRemove(s.Id, out var bh))
                s.OnIsBackgroundRunningChanged -= bh;
            if (_briefingHandlers.TryRemove(s.Id, out var brh))
                s.OnBriefingStateChanged -= brh;
        }
        foreach (var kv in _injectionWatchers)
            kv.Value.Dispose();
        _injectionWatchers.Clear();
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
/// 3. When the timer fires (no new bytes for 500ms), snapshot the resolved grid
///    plus the live cursor and run
///    <see cref="PromptInputLineExtractor.ExtractUserAuthoredInput"/>. The cursor
///    lets us reject a dim history/autocomplete suggestion (cursor parked at the
///    start of the box) instead of mirroring it as if the user had entered it.
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
            // Read the RESOLVED grid plus the live cursor, not the raw byte buffer:
            // the cursor tells a real entry (cursor at the end of the box text) apart
            // from a dim history/autocomplete suggestion (cursor parked at the start).
            var (rows, cursorRow, cursorCol) = _session.SnapshotScreenRowsWithCursor();
            var extracted = PromptInputLineExtractor.ExtractUserAuthoredInput(rows, cursorRow, cursorCol);

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
