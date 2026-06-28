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
        _sessionManager.OnSessionRemoved += OnSessionRemoved;

        // Wire existing sessions (restored from persistence on Director boot).
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s, isNew: false);
    }

    private void OnSessionCreated(Session session) => WireSession(session, isNew: true);

    // Issue #815: a removed session can be the controller of one or more sub-agents. Repaint those
    // children so they drop the Supporting color and revert to normal now that nobody drives them.
    private void OnSessionRemoved(Session session) => RecomputeControlledChildren(session.Id);

    /// <summary>
    /// Resolve a session's colour through <see cref="ColorFor"/>, first looking up whether its
    /// controlling session (issue #815) is still alive and its display name. For an uncontrolled
    /// session this is just the plain activity/Wingman colour.
    /// </summary>
    private (string color, string reason) ComputeColor(Session session, bool isNew)
    {
        var (alive, name) = ResolveController(session);
        return ColorFor(session, isNew, alive, name);
    }

    /// <summary>
    /// Look up this session's controlling session (issue #815). Returns whether it is still alive
    /// (exists in the manager AND has not exited) and its display name. A session with no controller,
    /// or whose controller is gone, returns <c>(false, null)</c> so it paints normally.
    /// </summary>
    private (bool alive, string? name) ResolveController(Session session)
    {
        if (session.ControllerSessionId is not Guid controllerId)
            return (false, null);

        var controller = _sessionManager.GetSession(controllerId);
        if (controller is null || controller.ActivityState == ActivityState.Exited)
            return (false, null);

        return (true, controller.CustomName);
    }

    /// <summary>
    /// Repaint every controlled sub-agent (issue #815) whose controller is the given session id.
    /// Called when that controller exits or is removed: the children's own activity has not changed,
    /// so this is what flips them off the Supporting color back to their normal colour. Both callers
    /// mean "this controller is definitively gone", so the children are repainted with the controller
    /// treated as not alive - we do NOT re-resolve it (on the removal path it may still linger in the
    /// roster for an instant, which would wrongly read as alive and keep the Supporting color).
    /// </summary>
    private void RecomputeControlledChildren(Guid controllerId)
    {
        foreach (var child in _sessionManager.ListSessions())
        {
            if (child.ControllerSessionId != controllerId) continue;
            try
            {
                var (c, r) = ColorFor(child, isNew: false, controllerAlive: false);
                child.SetStatusColor(c, r);
                FileLog.Write($"[SessionStatusWingman] controller {controllerId} gone -> repaint sub-agent {child.Id} => {c} ({r})");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionStatusWingman] RecomputeControlledChildren failed for {child.Id}: {ex.Message}");
            }
        }
    }

    private void WireSession(Session session, bool isNew)
    {
        if (_activityHandlers.ContainsKey(session.Id)) return;

        // Initialize colour from the current activity state.
        var (color, reason) = ComputeColor(session, isNew);
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
                var (c, r) = ComputeColor(session, isNew: false);
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

                // Issue #815: when THIS session exits, any sub-agents it was controlling lose their
                // controller and must drop the recessive Supporting color, reverting to normal. Their
                // own activity has not changed, so nothing else would repaint them - do it here.
                if (newState == ActivityState.Exited)
                    RecomputeControlledChildren(session.Id);
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
                var (c, r) = ComputeColor(session, isNew: false);
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

        // BriefingState flips around the wingman's read of a finished turn (issue #192;
        // since #187 the writer would be a gateway push-down - the Director itself no
        // longer briefs). Recompute the colour on every flip so the dot moves into Yellow
        // while the wingman reads and settles to the verdict colour when the brief lands.
        // Like the explain overlay, the activity state has NOT changed during this
        // window, so we do not write to StateChangeLog.
        Action<BriefingState> briefingHandler = state =>
        {
            try
            {
                var (c, r) = ComputeColor(session, isNew: false);
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
                var (c, r) = ComputeColor(session, isNew: false);
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
    /// The voice-mode "yellow until audio ready" color rule (issue #553). A VOICE-MODE session that
    /// is waiting for the user (WaitingForInput / WaitingForPerm) must NOT show red "needs you" while
    /// its spoken audio is still being prepared: it stays YELLOW ("preparing voice / not ready yet")
    /// while the wingman is still generating (<paramref name="voiceGenerating"/>) OR there is no
    /// playable audio yet (<c>!</c><paramref name="voiceAudioReady"/>), and only turns RED once
    /// playable audio exists. Non-voice sessions, and voice sessions that are not at a waiting
    /// turn-end, are returned with their <paramref name="baseColor"/> unchanged.
    ///
    /// The actual play-readiness signals (HasVoice / IsGenerating) live in the Gateway's
    /// WingmanVoiceService, surfaced to clients on SessionDto.VoiceAudioReady / VoiceGenerating; the
    /// Gateway's SessionOrdering.EffectiveColor and the /m client's effColor apply this same rule on
    /// those fields. This pure helper keeps the rule defined and unit-tested once, next to the rest of
    /// the color mapping.
    /// </summary>
    internal static (string color, string reason) VoiceColorFor(
        bool voiceMode, ActivityState state, bool voiceGenerating, bool voiceAudioReady,
        (string color, string reason) baseColor)
    {
        var atTurnEnd = state is ActivityState.WaitingForInput or ActivityState.WaitingForPerm;
        if (voiceMode && atTurnEnd && string.Equals(baseColor.color, StatusColor.Red, StringComparison.OrdinalIgnoreCase)
            && (voiceGenerating || !voiceAudioReady))
            return (StatusColor.Yellow, "preparing voice");
        return baseColor;
    }

    /// <summary>
    /// Resolve the dot colour for a session, layering the controlled-sub-agent "Supporting"
    /// overlay (issue #815) on top of the activity/Wingman colour from <see cref="ResolveActivityColor"/>.
    ///
    /// Precedence: red "needs you" &gt; Supporting &gt; every other activity colour. A controlled
    /// sub-agent recedes to slate while ANOTHER session drives it, so the operator is not nagged -
    /// but only while that controller is still alive (<paramref name="controllerAlive"/>), and never
    /// at the cost of hiding a genuinely blocked sub-agent: if the underlying colour is red the red
    /// wins and breaks through. When the controller is gone the session paints normally again.
    /// </summary>
    /// <param name="controllerAlive">Whether this session's controlling session still exists and
    /// has not exited. The caller resolves this (see <see cref="ResolveController"/>); pure unit
    /// tests pass it explicitly. Defaults false so an uncontrolled session is unaffected.</param>
    /// <param name="controllerName">Display name of the controlling session, for the tooltip reason
    /// ("supporting &lt;name&gt;"). Null falls back to "another session".</param>
    internal static (string color, string reason) ColorFor(Session session, bool isNew,
        bool controllerAlive = false, string? controllerName = null)
    {
        var resolved = ResolveActivityColor(session, isNew);

        // Supporting overlay (issue #815): recede a controlled sub-agent to slate while its
        // controller drives it, EXCEPT red "needs you" breaks through so a blocked sub-agent still
        // surfaces. Honored only while the controller is alive; otherwise the session paints normally.
        if (session.IsControlled && controllerAlive
            && !string.Equals(resolved.color, StatusColor.Red, StringComparison.OrdinalIgnoreCase))
        {
            var who = string.IsNullOrWhiteSpace(controllerName) ? "another session" : controllerName.Trim();
            return (StatusColor.Supporting, $"supporting {who}");
        }

        return resolved;
    }

    /// <summary>
    /// Resolve the dot colour for a session given its current ActivityState plus the
    /// Wingman overlays (BriefingState, IsExplaining, IsBackgroundRunning). Yellow is
    /// emitted only when the session is parked at a turn-end (WaitingForInput /
    /// WaitingForPerm) and a wingman read is in flight - either the turn-brief pipeline
    /// (BriefingState=Briefing, all sessions) or the legacy auto-explain (IsExplaining,
    /// WingmanEnabled only). Otherwise the colour is the plain activity-state mapping above.
    /// This is the colour BEFORE the controlled-sub-agent overlay in <see cref="ColorFor"/>.
    /// </summary>
    internal static (string color, string reason) ResolveActivityColor(Session session, bool isNew)
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

        // Green "ready": a brand-new session that has not yet taken a turn is simply sitting at
        // its input prompt, available for the user. It does NOT need the user, so it must not
        // start red. IsBrandNew stays true until the first prompt is submitted (it then flips to
        // Working/blue), so this window covers the whole startup-to-first-turn span. The wingman
        // will reuse green for its own "ready" verdicts later; for now it is set only on startup.
        if (session.IsBrandNew && atTurnEnd)
            return (StatusColor.Green, "ready");

        return baseColor;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessionManager.OnSessionCreated -= OnSessionCreated;
        _sessionManager.OnSessionRemoved -= OnSessionRemoved;
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
