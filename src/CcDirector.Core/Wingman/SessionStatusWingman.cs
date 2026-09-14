using System.Collections.Concurrent;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Per-Director wingman that writes <see cref="Session.StatusColor"/> from the session's activity state.
///
/// IT IS NOT THE SINGLE WRITER, whatever this comment used to say (and <c>Session.SetStatusColor</c> still
/// says: "Sole writer... No other code path may set the color"). Verified 14 July 2026 - TWO other
/// production paths write it: <c>Session.cs</c>'s crash arm (<c>SetStatusColor(Error, ...)</c> when the
/// process dies) and <c>TransientErrorAutoResume</c> (a sticky PositiveEvidence red when auto-resume gives
/// up). A third, <c>MarkForDeletion</c>'s <c>SetStatusColor(Unknown, ...)</c>, was deleted by defect 23.
/// The claim mattered because it is why nobody looked: a reader who believes there is one writer does not
/// go looking for the other two, and both of them are STICKY writes that outrank this class's mapping.
///
/// WHAT READS THIS COLOUR, because that is the other thing this comment got wrong for a long time. It is
/// NOT merely a "standalone fallback" that nothing consumes. <c>StatusColor</c> rides the wire on
/// <c>SessionDto</c> and is read by live consumers on BOTH sides, including a GATEWAY colour:
/// <c>GatewayEndpoints</c> gates the voice-yellow briefing stamp on <c>StatusColor == "red"</c>, so yellow
/// on the phone and the Cockpit depends on this mapping. The desktop's "N need you" header count and the
/// the removed FIFO window's red filter read it too, as does the wingman brief's
/// CurrentColor. Deleting this computation would strand every one of them - a deleted producer under live
/// consumers, which is this repository's signature bug. It is retired only after those readers move to the
/// Gateway's fold (docs/new_architecture/sessions.html, "Still to do").
///
/// Phase 2.3 (issue #1177): the Director computes ONLY the dumb color map here - no overlays. The badge is
/// a direct, mechanical mapping from the session's <see cref="ActivityState"/> and nothing else:
///
///   Working / Starting            -> blue  ("working")
///   WaitingForInput / Perm / Idle -> red   ("needs you")
///   Exited                        -> gray  ("exited", the "unknown" color string the UI renders gray)
///
/// The richer colors that used to be layered here as overlays - transcribing orange, wingman-reading
/// yellow (briefing / auto-explain), background-running purple, brand-new "ready" green, and the
/// controlled-sub-agent slate (issue #815) - are GONE from the Director. When a Gateway is present it
/// owns every one of those: it folds them from the RAW FACTS this Director still reports on each
/// SessionDto (IsBrandNew, IsControlled, ControllerSessionId, IsBackgroundRunning, IsTranscribing,
/// IsExplaining, BriefingState, VoiceMode). The Director sets those facts on the Session as before -
/// this class simply no longer turns them into a color. A Gateway-less desktop therefore shows the
/// plain blue/red/gray map, which is the intended standalone fallback.
///
/// ActivityState itself is owned by the <see cref="TerminalStateDetector"/>, whose entire rule is:
/// bytes out of the ConPTY -> Working; <see cref="TerminalStateDetector.QuietThreshold"/> of complete
/// silence -> WaitingForInput. So in practice the badge is just blue or red.
///
/// It still wires a <see cref="PromptInjectionWatcher"/> per session, but that only mirrors text
/// Claude Code injects into its own input line back to the cc-director textbox - it never touches the
/// color.
/// </summary>
public sealed class SessionStatusWingman : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _activityHandlers = new();
    private readonly ConcurrentDictionary<Guid, PromptInjectionWatcher> _injectionWatchers = new();
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// How many sessions this class still holds an activity handler for. Exposed for tests, which
    /// must assert the COUNT rather than that a teardown method ran: a test that only checks
    /// "OnSessionRemoved was called" passes against a handler that removes from the wrong
    /// dictionary, which is the shape of the bug this guards.
    /// </summary>
    internal int TrackedHandlerCount => _activityHandlers.Count;

    /// <summary>How many prompt-injection watchers are still held. Same reasoning as
    /// <see cref="TrackedHandlerCount"/>: both dictionaries leaked, so both are asserted.</summary>
    internal int TrackedWatcherCount => _injectionWatchers.Count;

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

    /// <summary>
    /// Release everything this class holds for a session that has ended.
    ///
    /// Both dictionaries are keyed by session id and both entries reference the Session itself -
    /// <see cref="_activityHandlers"/> stores a closure that CAPTURES the session, and the
    /// <see cref="PromptInjectionWatcher"/> holds it directly. Without this hook those entries live
    /// as long as the Director does, so every session ever opened stays rooted along with its
    /// backend, its terminal buffer, and its parsers' scrollback.
    ///
    /// That was the bug: this class subscribed to OnSessionCreated and never to OnSessionRemoved,
    /// while its siblings (TerminalStateDetector, TransientErrorAutoResume) always did. On one
    /// Director at 58 hours uptime it retained 146 Sessions for 9 live ones - about 1.7 GB, the
    /// bulk of the process heap. The per-session bounds were all correct; the object count was not.
    /// </summary>
    private void OnSessionRemoved(Session session)
    {
        if (_activityHandlers.TryRemove(session.Id, out var handler))
            session.OnActivityStateChanged -= handler;

        if (_injectionWatchers.TryRemove(session.Id, out var watcher))
            watcher.Dispose();
    }

    private void WireSession(Session session, bool isNew)
    {
        if (_activityHandlers.ContainsKey(session.Id)) return;

        // Initialize color from the current activity state - the dumb standalone map, no overlays.
        var (color, reason) = ColorFromActivityState(session.ActivityState, isNew);
        session.SetStatusColor(color, reason);
        FileLog.Write($"[SessionStatusWingman] init {session.Id} -> {color} ({reason})");

        // Brand-new session: seed a canned Wingman greeting so the Wingman tab has
        // content the moment the user opens it, with no Opus call. The
        // ProactiveExplainService skips the first turn-end briefing for IsBrandNew
        // sessions (nothing useful to summarize yet); this line is what the user reads
        // until they send their first prompt. This reads IsBrandNew; it does not set it.
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
                var (c, r) = ColorFromActivityState(session.ActivityState, isNew: false);
                session.SetStatusColor(c, r);
                FileLog.Write($"[SessionStatusWingman] {session.Id} activity {oldState}->{newState} => {c} ({r})");

                // Durable record of every state transition (blue<->red), so a session's
                // history survives a Director restart. The in-memory ring on the Session
                // (RecordStateChange, populated in SetActivityState) feeds the live tab.
                StateChangeLog.Append(session.Id, new StateChangeLog.Record(
                    DateTime.UtcNow.ToString("o"), oldState.ToString(), newState.ToString(), c));

                // Keep the prompt-text mirror responsive: when the session settles at its
                // input box, nudge the injection watcher to scan now rather than waiting
                // for the next byte. This is text mirroring only - not a color decision.
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
    /// The one and only state-to-color mapping (the standalone-desktop fallback). Working (and the
    /// brief Starting state) are blue; every state that means "not producing output, your turn" is red;
    /// a gone process is gray (the "unknown" color string the UI renders gray). The
    /// <see cref="TerminalStateDetector"/> only ever emits Working and WaitingForInput, so in practice
    /// the badge is just blue or red.
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

    // VoiceColorFor is DELETED (defect 5's phase, 14 July 2026). It was a "shared, pure rule DEFINITION"
    // of the voice-mode "yellow until audio ready" rule, kept here so the rule was "defined and unit-tested
    // once". It was shared with nothing: it had ZERO production callers - only its own five tests - and its
    // comment's claim that "the Gateway's SessionOrdering.EffectiveColor and the /m client's effColor apply
    // the same rule" was false in both halves. The /m client has no such rule at all, and the Gateway
    // applies its own on the roster.
    //
    // So this was a dead copy of a rule that nothing shared - deleted rather than corrected, because a
    // shared rule definition that nothing shares is just a corpse with a footnote. The live rule is
    // SessionOrdering.IsVoicePreparing.
    //
    // NOTE (owner's ruling, 2026-07-19): the live rule DOES hold yellow while `!voiceAudioReady` - by
    // design, so a voice-mode session never flashes red before its voice is ready. Do not read this
    // tombstone as evidence that "yellow until audio" is a bug; the wedge it once caused is now prevented by
    // voice-generation reliability, not by narrowing the color. See IsVoicePreparing's own summary.

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessionManager.OnSessionCreated -= OnSessionCreated;
        _sessionManager.OnSessionRemoved -= OnSessionRemoved;

        // Unsubscribe from the sessions that are still live - we need the Session object to detach
        // the handler, and only the live list can supply it.
        foreach (var s in _sessionManager.ListSessions())
        {
            if (_activityHandlers.TryRemove(s.Id, out var h))
                s.OnActivityStateChanged -= h;
        }

        // Then drop whatever is left. Anything still here belongs to a session that has already
        // ended, so there is no Session to detach from - the entry is pure retention. The loop
        // above walks LIVE sessions only, which is exactly the set that does NOT need clearing;
        // without this line a dead session's handler survived even a full Director shutdown.
        _activityHandlers.Clear();

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
