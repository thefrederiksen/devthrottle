using System.Collections.Concurrent;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Time-based turn detector. TWO rules, both purely mechanical -- no footer parsing, no grid
/// diffing, no LLM judge:
///   1. Any byte out of the ConPTY means the agent is producing output, so it is Working. We
///      set Working the instant a byte arrives and re-arm the idle countdown on every byte.
///   2. When the stream has been COMPLETELY silent for <see cref="QuietThreshold"/>, we flag the
///      session as needing the user -- <see cref="ActivityState.WaitingForInput"/>, which the UI
///      renders as the red "needs you" badge.
///
/// The ONLY derived signal is "time since the last character", which the session's
/// <see cref="CircularTerminalBuffer.LastWriteAtUtc"/> already tracks; the right-side panel
/// renders that idle clock live. This is deliberately a dumb timer: a long silence is treated
/// as "needs you" regardless of WHY the output stopped (the agent may have finished cleanly, be
/// blocked on a question, or just be thinking slowly). It does not attempt to tell those apart.
/// </summary>
public sealed class TerminalStateDetector : IDisposable
{
    /// <summary>
    /// How long the ConPTY output must be COMPLETELY silent (zero bytes) before we flag the
    /// session as needing the user. Crossing this flips the session to
    /// <see cref="ActivityState.WaitingForInput"/> (the red "needs you" badge).
    /// </summary>
    public static readonly TimeSpan QuietThreshold = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Extract the screen BODY used by the continuous-idle rule: the visible rows strictly ABOVE
    /// the cursor row, joined with newlines. For an agent with an animated idle footer (Grok) the
    /// input composer and the never-quiet footer (spinner / shortcuts / clock) sit at and below
    /// the cursor, so excluding them leaves only the conversation body - which is static once the
    /// turn is done. Returns false when there is no grid or the cursor is at the top (row 0 or
    /// unknown), so the body cannot be isolated; the caller then treats the frame as activity
    /// rather than risk a false idle. Pure and side-effect free so it can be unit tested directly.
    /// </summary>
    internal static bool TryExtractBody(string[] rows, int cursorRow, out string body)
    {
        body = "";
        if (!TryExtractBodyRows(rows, cursorRow, out var bodyRows))
            return false;
        body = string.Join("\n", bodyRows);
        return true;
    }

    /// <summary>
    /// The same body as <see cref="TryExtractBody"/>, kept as ROWS. The content rule compares row
    /// against row - it asks whether a row appeared, not whether a string differs - so joining
    /// first and splitting again would be work done twice and a place for the two to drift.
    ///
    /// The cursor is the whole reason this is trustworthy. The measurement behind the rule ran on
    /// saved screens, which do not record the cursor, so it had to GUESS where the input box
    /// started by looking for a prompt-like row; of the 8,650 screens in that corpus only 1,674 had
    /// exactly one prompt-like row, so the guess was load-bearing and sometimes cut away a real
    /// reply. Production has the real cursor and guesses nothing.
    /// </summary>
    internal static bool TryExtractBodyRows(string[] rows, int cursorRow, out string[] bodyRows)
    {
        bodyRows = Array.Empty<string>();
        if (rows is null || rows.Length == 0 || cursorRow <= 0)
            return false;
        int take = Math.Min(cursorRow, rows.Length);
        bodyRows = new string[take];
        Array.Copy(rows, bodyRows, take);
        return true;
    }

    /// <summary>
    /// The environment variable that turns the content rule on, and says which candidate is
    /// authoritative. Unset, <c>0</c> or anything unrecognised means OFF, which is the shipped
    /// default: turning it on is the owner's decision and he wants the shadow numbers first.
    /// </summary>
    public const string ContentRuleVariable = "CC_DIRECTOR_CONTENT_TURN_RULE";

    /// <summary>
    /// Which content candidate decides whether a settled session opens a turn.
    /// <see cref="TurnContentRule.Off"/> is today's rule: any byte opens it.
    /// </summary>
    internal static TurnContentRule ResolveContentRule(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "1" or "row" => TurnContentRule.Row,
            "size" => TurnContentRule.Size,
            _ => TurnContentRule.Off,
        };

    /// <summary>What the content rule is set to on this Director. Read once, at type load.</summary>
    internal static TurnContentRule ContentRuleDefault { get; } =
        ResolveContentRule(Environment.GetEnvironmentVariable(ContentRuleVariable));

    /// <summary>
    /// How long after a byte at a SETTLED session the screen is read, so a repaint is judged once
    /// it has finished drawing rather than half drawn. Bytes arriving inside this window push the
    /// check out rather than being dropped.
    /// </summary>
    internal static readonly TimeSpan SettleCheckDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The cap on that pushing-out. Without it an agent that writes something every 300
    /// milliseconds forever would defer its check forever and never be judged at all.
    /// </summary>
    internal static readonly TimeSpan MaxSettleCheckDeferral = TimeSpan.FromSeconds(3);

    private readonly SessionManager _sessionManager;
    private readonly bool _driveState;
    private readonly TimeSpan _quietThreshold;
    private readonly Activity.ActivityEventProducer? _activityProducer;
    private readonly TurnContentRule _contentRule;
    private readonly TimeSpan _settleCheckDelay;
    private readonly TimeSpan _maxSettleCheckDeferral;
    private readonly ConcurrentDictionary<Guid, Watcher> _watchers = new();
    private bool _started;
    private bool _disposed;

    /// <param name="driveState">
    /// When true the detector is authoritative and sets <see cref="Session.ActivityState"/> to
    /// Working on byte activity. When false it is observe-only (logs, writes nothing).
    /// </param>
    /// <param name="activityProducer">
    /// The shadow-evidence producer (docs/PLAN-trustworthy-working-start-2026-07-24.md). When present,
    /// a flip from settled to active that NO recent submission explains records a
    /// terminal-output-while-settled evidence event - the candidate phantom turn - with the bounded
    /// screen evidence. Purely observational: the detector's rules and writes are byte-for-byte
    /// unchanged whether this is null or not.
    /// </param>
    public TerminalStateDetector(SessionManager sessionManager, bool driveState,
        Activity.ActivityEventProducer? activityProducer = null)
        : this(sessionManager, driveState, QuietThreshold, activityProducer)
    {
    }

    /// <summary>
    /// Test seam for exercising the silence rule without waiting for the production interval.
    /// </summary>
    internal TerminalStateDetector(SessionManager sessionManager, bool driveState,
        TimeSpan quietThreshold, Activity.ActivityEventProducer? activityProducer = null,
        TurnContentRule? contentRule = null,
        TimeSpan? settleCheckDelay = null,
        TimeSpan? maxSettleCheckDeferral = null)
    {
        if (quietThreshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quietThreshold));

        _sessionManager = sessionManager;
        _driveState = driveState;
        _quietThreshold = quietThreshold;
        _activityProducer = activityProducer;
        _contentRule = contentRule ?? ContentRuleDefault;
        _settleCheckDelay = settleCheckDelay ?? SettleCheckDelay;
        _maxSettleCheckDeferral = maxSettleCheckDeferral ?? MaxSettleCheckDeferral;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        var rule = _contentRule == TurnContentRule.Off
            ? "byte->working"
            : $"content:{_contentRule.ToString().ToLowerInvariant()}";
        FileLog.Write($"[TerminalStateDetector] Start (mode={(_driveState ? "authoritative" : "observe")}, rule={rule}, quiet={_quietThreshold.TotalSeconds}s, shadow={(TurnDetectionShadowLog.Enabled ? "on" : "off")})");

        _sessionManager.OnSessionCreated += OnSessionCreated;
        _sessionManager.OnSessionRemoved += OnSessionRemoved;
        foreach (var s in _sessionManager.ListSessions())
            Wire(s);
    }

    private void OnSessionCreated(Session session) => Wire(session);

    /// <summary>
    /// Tear down the per-session watcher (and its idle timer) BEFORE the session's
    /// terminal buffer is disposed. Required: an armed timer firing after the buffer
    /// is gone would fault on a disposed lock and crash the process.
    /// </summary>
    private void OnSessionRemoved(Session session)
    {
        if (_watchers.TryRemove(session.Id, out var w))
            w.Dispose();
    }

    private void Wire(Session session)
    {
        if (session.Buffer is null) return;
        // Remote (GitHub Actions) sessions self-report activity from authoritative run
        // status via the backend's ActivitySink. The silence heuristic would misfire
        // (a queued run emits no bytes yet is genuinely Working), so skip them entirely.
        if (session.BackendType == Backends.SessionBackendType.GitHubActions) return;
        if (_watchers.ContainsKey(session.Id)) return;
        var w = new Watcher(session, _driveState, _quietThreshold, _activityProducer,
            _contentRule, _settleCheckDelay, _maxSettleCheckDeferral);
        if (_watchers.TryAdd(session.Id, w))
            w.Start();
        else
            w.Dispose();
    }

    /// <summary>
    /// The settled screen this detector captured for a session, as rows. A test seam: it exists so
    /// the capture can be proved to happen on a settle with no shadow evidence producer wired,
    /// which is the whole of work item two. Returns false when the session is not watched or has
    /// not settled yet.
    /// </summary>
    internal bool TryGetSettledBodyRows(Guid sessionId, out string[] rows)
    {
        rows = Array.Empty<string>();
        if (!_watchers.TryGetValue(sessionId, out var watcher)) return false;
        rows = watcher.SettledBodyRows;
        return rows.Length > 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionManager.OnSessionCreated -= OnSessionCreated;
        _sessionManager.OnSessionRemoved -= OnSessionRemoved;
        foreach (var w in _watchers.Values)
            w.Dispose();
        _watchers.Clear();
    }

    /// <summary>Per-session: byte -> working, plus the idle countdown.</summary>
    private sealed class Watcher : IDisposable
    {
        // How often, at most, the continuous-idle path takes the (locked) screen snapshot to
        // diff the body. Between checks it does nothing - it never re-arms the idle timer on a
        // footer-only repaint. 500ms is far inside the 10s QuietThreshold, so a changing body is
        // still caught ~20 times before the idle flip, while keeping the per-byte cost trivial.
        private static readonly long BodyCheckIntervalTicks = TimeSpan.FromMilliseconds(500).Ticks;

        private readonly Session _session;
        private readonly CircularTerminalBuffer _buffer;
        private readonly bool _driveState;
        private readonly TimeSpan _quietThreshold;
        private readonly Activity.ActivityEventProducer? _activityProducer;
        private readonly Action<byte[]> _onBytes;
        private readonly System.Threading.Timer _quietTimer;

        // The content rule: which candidate is authoritative, and the settling window the check
        // waits out. Off is today's rule - any byte at a settled session opens the turn.
        private readonly TurnContentRule _contentRule;
        private readonly TimeSpan _settleCheckDelay;
        private readonly TimeSpan _maxSettleCheckDeferral;
        private readonly IReadOnlyCollection<string> _chromeMarkers;
        private readonly System.Threading.Timer _contentCheckTimer;

        // Guards the settled-session decision, which the PTY producer thread and the check timer
        // both touch. The ACTIVE path - where almost every byte lands - never takes it.
        private readonly object _checkGate = new();
        private bool _checkScheduled;
        private long _checkDeadlineTicks;      // when the check may no longer be pushed out
        private long _pendingBytes;            // bytes in the burst that produced the pending check

        // The same "a check is armed" fact as _checkScheduled, readable WITHOUT taking the lock, so
        // the hot path on an already-working session pays one volatile read rather than a lock.
        private int _checkPending;

        // The screen body captured at the last flip to settled, and its normalized hash - the "before"
        // side of the bounded evidence an unexplained wake records. Touched only on the PTY producer
        // thread and the quiet-timer thread, which never race the same flip (the flip direction decides
        // which side reads/writes it).
        private string? _settledBody;
        private string? _settledBodyHash;

        // The same settled screen kept as ROWS, which is what the content rule compares against.
        // Empty until the session has settled at least once.
        private string[] _settledBodyRows = Array.Empty<string>();

        // True for agents whose idle terminal never goes byte-silent (Grok): an animated footer
        // keeps repainting forever. For these the byte rule is replaced by a screen-body rule.
        private readonly bool _continuousIdle;

        private bool _active;
        private int _disposed;

        // Continuous-idle state. _lastBody (the screen body above the cursor at the last check) is
        // touched only on the PTY producer thread. The body-change TIMESTAMP lives on the Session
        // (Session.LastBodyActivityAtUtc) so the idle clock can read it too; the detector stamps it
        // via Session.StampBodyActivity. _lastBodyCheckTicks throttles the locked snapshot.
        private string? _lastBody;
        private long _lastBodyCheckTicks;

        public Watcher(Session session, bool driveState, TimeSpan quietThreshold,
            Activity.ActivityEventProducer? activityProducer,
            TurnContentRule contentRule, TimeSpan settleCheckDelay, TimeSpan maxSettleCheckDeferral)
        {
            _session = session;
            _buffer = session.Buffer!;
            _driveState = driveState;
            _quietThreshold = quietThreshold;
            _activityProducer = activityProducer;
            _continuousIdle = session.Driver.EmitsContinuousIdleOutput;
            _contentRule = contentRule;
            _settleCheckDelay = settleCheckDelay;
            _maxSettleCheckDeferral = maxSettleCheckDeferral;
            _chromeMarkers = session.Driver.SelfDescribingRowMarkers;
            _onBytes = OnBytes;
            _quietTimer = new System.Threading.Timer(OnQuiet, null, Timeout.Infinite, Timeout.Infinite);
            _contentCheckTimer = new System.Threading.Timer(OnContentCheck, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            _session.OnActivityStateChanged += OnActivityStateChanged;
            _buffer.OnBytesWritten += _onBytes;
            if (_driveState && _session.ActivityState == ActivityState.Working)
                MarkActiveFromWorkingState();
            else
                ArmQuietTimer();
        }

        /// <summary>
        /// Keep the detector's active latch aligned with the state it is responsible for driving.
        /// A fast first response can finish while <see cref="Session.IsBrandNew"/> still suppresses
        /// its bytes, before verified submission marks the session Working. The Working transition
        /// must therefore start the silence countdown even when no later terminal byte arrives.
        /// </summary>
        private void OnActivityStateChanged(ActivityState _, ActivityState newState)
        {
            if (!_driveState || Volatile.Read(ref _disposed) != 0)
                return;

            if (newState == ActivityState.Working)
            {
                MarkActiveFromWorkingState();
                return;
            }

            _active = false;
        }

        private void MarkActiveFromWorkingState()
        {
            if (!_active)
            {
                _active = true;
                FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=ACTIVE (working-state)");
            }
            ArmQuietTimer();
        }

        private void ArmQuietTimer()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _quietTimer.Change(_quietThreshold, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* race with Dispose */ }
        }

        private void OnBytes(byte[] bytes)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { OnBytesCore(bytes); }
            catch (Exception ex)
            {
                // This runs on the PTY producer thread. An escaped exception would be
                // unhandled and terminate the whole process. Log and swallow.
                FileLog.Write($"[TerminalStateDetector] OnBytes failed session={_session.Id}: {ex.Message}");
            }
        }

        private void OnBytesCore(byte[] bytes)
        {
            // Director-induced repaint guard: when the Director issues a PTY resize (on switching
            // to a session, force-refresh, or a layout change), Claude Code repaints its whole
            // screen and emits a burst of bytes. Those bytes are OUR doing, not the agent working.
            // Ignore them entirely -- no Working flip, no idle-countdown re-arm -- so switching to
            // an idle session does not flip it blue. The window is short (well under
            // QuietThreshold), so a genuine work-start inside it is only delayed until the next
            // byte after the window, which re-flags Working.
            if (DateTime.UtcNow < _session.SuppressActivityUntilUtc)
                return;

            // Brand-new session: Claude Code's startup splash (logo, version line, prompt box,
            // bypass-permissions footer) emits a flood of bytes BEFORE the user has done anything.
            // The byte->Working rule would flip a fresh session blue for ~QuietThreshold seconds
            // even though it is sitting idle at the prompt. Suppress it. IsBrandNew clears the
            // moment the user's first submission is verified. The resulting Working transition
            // starts the silence countdown, including when the entire response arrived first.
            if (_session.IsBrandNew)
                return;

            // WHILE THE SESSION IS ALREADY ACTIVE, NOTHING BELOW APPLIES. Bytes re-arm the idle
            // countdown and no screen is read. That is where almost all the bytes are, so the
            // content rule costs a working session nothing at all.
            //
            // EmitsContinuousIdleOutput now selects ONLY this: whether a raw byte may re-arm the
            // quiet timer. For an agent whose footer repaints forever it may not - its idle clock
            // runs off screen-BODY changes instead, which is what OnBytesContinuousIdle stamps.
            // It no longer selects a different activation rule; the settled case below is shared.
            if (_active)
            {
                // A shadow check pending from the wake is still pushed out by later bytes, so the
                // screen it reads is the finished burst rather than a half-drawn one. One volatile
                // read is the whole cost of this on the hot path.
                if (Volatile.Read(ref _checkPending) != 0) ScheduleContentCheck(bytes.Length);
                if (_continuousIdle) OnBytesContinuousIdle();
                else ArmQuietTimer();
                return;
            }

            // THE SESSION IS SETTLED, which is the only place the content rule has anything to say.
            if (_contentRule == TurnContentRule.Off)
            {
                // Today's rule, unchanged: a byte out of the ConPTY means the agent is producing
                // output, so it is working. We do not inspect what the byte is. A byte is activity.
                // Period. Full stop. The buffer already stamps LastWriteAtUtc on every write, so
                // "time since the last character" (the idle clock the panel shows) is free.
                if (_continuousIdle) OnBytesContinuousIdle();
                else MarkActiveFromByte(bytes.Length);

                // The state write above already happened. The check below reads the screen and
                // records what the rule WOULD have decided; it cannot change anything.
                ScheduleContentCheck(bytes.Length);
                return;
            }

            // The rule is on: the byte does not flip anything. It schedules a check a short delay
            // later, so a repaint is judged once it has finished drawing.
            ScheduleContentCheck(bytes.Length);
        }

        /// <summary>Today's activation, kept intact so the switch-off path is byte for byte.</summary>
        private void MarkActiveFromByte(long byteCount)
        {
            if (!_active)
            {
                _active = true;
                FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=ACTIVE (byte) | hook={_session.ActivityState}");
                RecordUnexplainedWakeEvidence(byteCount, "byte");
                if (_driveState) _session.ApplyTerminalActivityState(ActivityState.Working);
            }
            ArmQuietTimer(); // restart the idle countdown on every byte
        }

        /// <summary>
        /// Activity rule for agents whose idle terminal never goes byte-silent. We cannot trust
        /// raw bytes (the footer animates forever), so the agent is "working" only while the
        /// screen BODY changes. Body = the visible rows ABOVE the cursor; the input composer and
        /// the animated footer (spinner / shortcuts / clock) sit at and below the cursor, so the
        /// churn that never stops is excluded. The screen snapshot is taken under a lock, so it is
        /// throttled to BodyCheckIntervalTicks; between checks we deliberately do nothing, which is
        /// what lets the idle timer actually fire. When the body cannot be isolated (cursor at the
        /// very top, or no grid yet) we treat the frame as activity - never go idle on an ambiguous
        /// frame, the same conservative outcome the byte rule would give.
        /// </summary>
        private void OnBytesContinuousIdle()
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - Volatile.Read(ref _lastBodyCheckTicks) < BodyCheckIntervalTicks)
                return;
            Volatile.Write(ref _lastBodyCheckTicks, nowTicks);

            if (!TryReadScreenBody(out var body))
            {
                MarkContinuousActive();
                return;
            }

            if (!string.Equals(body, _lastBody, StringComparison.Ordinal))
            {
                _lastBody = body;
                MarkContinuousActive();
            }
            // else: body unchanged (footer-only repaint) - do nothing. The quiet timer keeps
            // running from the last real change and will flip the session to WaitingForInput.
        }

        /// <summary>Read the screen body (rows strictly above the cursor) as one string. Returns
        /// false when there is no grid or the cursor is at the top so no body can be isolated.</summary>
        private bool TryReadScreenBody(out string body)
        {
            var (rows, cursorRow, _) = _session.SnapshotScreenRowsWithCursor();
            return TryExtractBody(rows, cursorRow, out body);
        }

        /// <summary>The same read, kept as rows for the content rule.</summary>
        private bool TryReadScreenBodyRows(out string[] bodyRows)
        {
            var (rows, cursorRow, _) = _session.SnapshotScreenRowsWithCursor();
            return TryExtractBodyRows(rows, cursorRow, out bodyRows);
        }

        /// <summary>The settled screen as rows, for the tests that prove it was captured.</summary>
        internal string[] SettledBodyRows => _settledBodyRows;

        // ----------------------------------------------------------------------------------
        // The content rule: one check per burst, at a settled session
        // ----------------------------------------------------------------------------------

        /// <summary>
        /// Arm the check, or push an armed one further out. Called on every byte that reaches a
        /// settled session, and on a byte that arrives while a check from the wake is still
        /// pending.
        ///
        /// BYTES INSIDE THE WINDOW PUSH THE CHECK OUT RATHER THAN BEING DROPPED. A burst longer
        /// than the window is therefore never sampled half drawn - which matters because a
        /// half-drawn repaint looks exactly like new content. The deadline caps the pushing: an
        /// agent writing something every three hundred milliseconds forever would otherwise defer
        /// its own check forever and never be judged at all.
        /// </summary>
        private void ScheduleContentCheck(long byteCount)
        {
            if (_contentRule == TurnContentRule.Off && !TurnDetectionShadowLog.Enabled) return;
            if (Volatile.Read(ref _disposed) != 0) return;

            lock (_checkGate)
            {
                long now = DateTime.UtcNow.Ticks;
                if (!_checkScheduled)
                {
                    _checkScheduled = true;
                    Volatile.Write(ref _checkPending, 1);
                    _checkDeadlineTicks = now + _maxSettleCheckDeferral.Ticks;
                    _pendingBytes = byteCount;
                }
                else
                {
                    _pendingBytes += byteCount;
                }

                long fireAt = Math.Min(now + _settleCheckDelay.Ticks, _checkDeadlineTicks);
                var due = TimeSpan.FromTicks(Math.Max(0, fireAt - now));
                try { _contentCheckTimer.Change(due, Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { /* race with Dispose */ }
            }
        }

        private void OnContentCheck(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { OnContentCheckCore(); }
            catch (Exception ex)
            {
                // A timer-thread exception would be unhandled and would take the process with it.
                FileLog.Write($"[TerminalStateDetector] content check failed session={_session.Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Read the screen, ask both candidates whether the conversation gained anything, write the
        /// row, and - only when the rule is on - open the turn.
        ///
        /// A CHECK THAT FINDS NOTHING CHANGES NOTHING AND ARMS THE NEXT ONE. That is the guarantee
        /// the whole "a miss can only delay a turn" claim rests on: the scheduled flag is cleared
        /// here, so the very next byte schedules another check. An earlier draft of this step had
        /// no such guarantee, and an independent reviewer pointed out that one filtered burst could
        /// then leave a working session red indefinitely - a lost turn, not a late one.
        /// </summary>
        private void OnContentCheckCore()
        {
            long bytes;
            lock (_checkGate)
            {
                if (!_checkScheduled) return;
                _checkScheduled = false;
                Volatile.Write(ref _checkPending, 0);
                bytes = _pendingBytes;
                _pendingBytes = 0;
            }

            var settled = _settledBodyRows;
            var current = TryReadScreenBodyRows(out var rows) ? rows : Array.Empty<string>();

            // An ambiguous frame - no grid yet, or the cursor at the very top - cannot be compared.
            // Never go idle on one: treat it as content, the same conservative outcome the byte
            // rule gives, and say so in the log rather than recording a silent zero.
            bool ambiguous = current.Length == 0;

            string? firstNewRow = null;
            bool rowOpens;
            bool sizeOpens;
            int changed;
            if (ambiguous)
            {
                rowOpens = true;
                sizeOpens = true;
                changed = 0;
            }
            else
            {
                rowOpens = TerminalContentNovelty.GainedContent(settled, current, _chromeMarkers, out firstNewRow);
                changed = TerminalContentNovelty.ChangedCharacters(settled, current);
                sizeOpens = changed >= TerminalContentNovelty.StartingChangedCharacterThreshold;
            }

            bool submitted = _session.LastSubmissionAtUtc is DateTime submittedAt
                && DateTime.UtcNow - submittedAt <= Activity.ActivityEventProducer.SubmissionWindow;

            TurnDetectionShadowLog.Append(_session.Id, new TurnDetectionShadowLog.Record(
                T: DateTime.UtcNow.ToString("o"),
                Agent: _session.Driver.Kind.ToString(),
                Mode: _continuousIdle ? "body" : "byte",
                Rule: _contentRule.ToString().ToLowerInvariant(),
                // Always true, and written down rather than assumed: a check only happens because a
                // byte reached a settled session, which is precisely what today's rule opens on.
                ByteRuleOpens: true,
                RowRuleOpens: rowOpens,
                RowEvidence: ambiguous ? "(screen could not be read)" : firstNewRow,
                SizeRuleOpens: sizeOpens,
                ChangedCharacters: changed,
                SizeThreshold: TerminalContentNovelty.StartingChangedCharacterThreshold,
                SettledHash: _settledBodyHash,
                CurrentHash: ambiguous ? null : Activity.ActivityEvidence.BodyHash(string.Join("\n", current)),
                Bytes: bytes,
                Submitted: submitted));

            // With the rule off the row above is the entire effect of this check.
            if (_contentRule == TurnContentRule.Off) return;

            bool opens = _contentRule == TurnContentRule.Size ? sizeOpens : rowOpens;
            if (!opens) return;

            MarkActiveFromContent(bytes, ambiguous ? "(screen could not be read)" : firstNewRow);
        }

        /// <summary>
        /// The turn opens because the conversation gained something. The row that changed goes into
        /// the log on every open, so a wrong decision is readable afterwards rather than being
        /// something the owner has to reconstruct.
        /// </summary>
        private void MarkActiveFromContent(long byteCount, string? evidence)
        {
            // _active is also written by the quiet timer and by the state-change handler, as it was
            // before this method existed; this adds one more thread to a latch that was already
            // loose. The worst outcome is a duplicate Working write, not a wrong colour.
            if (_active) return;
            _active = true;
            FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=ACTIVE (content:{_contentRule.ToString().ToLowerInvariant()}) gained={QuoteRow(evidence)} | hook={_session.ActivityState}");

            if (_continuousIdle)
            {
                // This agent's idle clock runs off body changes rather than bytes, so stamp one:
                // the quiet threshold must measure silence from this moment.
                if (TryReadScreenBody(out var body)) _lastBody = body;
                _session.StampBodyActivity();
            }

            RecordUnexplainedWakeEvidence(byteCount, _continuousIdle ? "body" : "content");
            if (_driveState) _session.ApplyTerminalActivityState(ActivityState.Working);
            ArmQuietTimer();
        }

        /// <summary>One line, bounded, for the log. A screen row can be long and can carry
        /// anything.</summary>
        private static string QuoteRow(string? row)
        {
            if (string.IsNullOrEmpty(row)) return "(none)";
            var flat = row.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (flat.Length > 120) flat = flat[..120] + "...";
            return "\"" + flat + "\"";
        }

        /// <summary>Body changed (or an ambiguous frame): flag Working and re-arm the idle timer
        /// from now. The quiet confirmation in OnQuietCore measures silence from this moment.</summary>
        private void MarkContinuousActive()
        {
            _session.StampBodyActivity();
            if (!_active)
            {
                _active = true;
                FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=ACTIVE (body) | hook={_session.ActivityState}");
                RecordUnexplainedWakeEvidence(byteCount: 0, "body");
                if (_driveState) _session.ApplyTerminalActivityState(ActivityState.Working);
            }
            ArmQuietTimer();
        }

        /// <summary>
        /// Shadow evidence at the flip from settled to active (docs/PLAN-trustworthy-working-start-
        /// 2026-07-24.md): when NO recent submission explains this wake, record a terminal-output-while-
        /// settled event carrying the bounded before/after evidence - the candidate phantom turn the
        /// July 24 snooze died to. A wake inside the submission window is the ordinary echo of a
        /// submitted turn and records nothing. Observational only; the state write that follows is
        /// untouched, and the producer guards its own faults.
        /// </summary>
        private void RecordUnexplainedWakeEvidence(long byteCount, string mode)
        {
            if (_activityProducer is null) return;
            if (_session.LastSubmissionAtUtc is DateTime submitted
                && DateTime.UtcNow - submitted <= Activity.ActivityEventProducer.SubmissionWindow)
                return;

            string? afterBody = TryReadScreenBody(out var body) ? body : null;
            var afterHash = afterBody is null ? null : Activity.ActivityEvidence.BodyHash(afterBody);
            var diff = afterBody is not null && _settledBody is not null
                ? Activity.ActivityEvidence.BoundedRowDiff(_settledBody, afterBody)
                : null;
            _activityProducer.RecordTerminalOutputWhileSettled(
                _session, byteCount, _settledBodyHash, afterHash, diff, mode);
        }

        private void OnQuiet(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { OnQuietCore(); }
            catch (Exception ex)
            {
                // This runs on a System.Threading.Timer thread. An escaped exception
                // would be unhandled and terminate the whole process (this was the
                // ObjectDisposedException-on-disposed-buffer crash). Log and swallow.
                FileLog.Write($"[TerminalStateDetector] OnQuiet failed session={_session.Id}: {ex.Message}");
            }
        }

        private void OnQuietCore()
        {
            if (!_active) return;

            // Confirm the silence is real (guard a raced timer fire). For a byte-silent agent,
            // "silent" means no bytes (LastWriteAtUtc). For a continuous-idle agent the bytes
            // never stop, so "silent" means no BODY change since the last one - measure against
            // _lastBodyChangeTicks instead, or the footer heartbeat would re-arm us forever.
            var lastChange = _continuousIdle
                ? _session.LastBodyActivityAtUtc
                : _buffer.LastWriteAtUtc;
            var idle = DateTime.UtcNow - lastChange;
            if (idle + TimeSpan.FromMilliseconds(250) < _quietThreshold)
            {
                ArmQuietTimer();
                return;
            }

            // The stream has been completely silent for QuietThreshold. Flag the session as
            // needing the user: WaitingForInput, which the UI renders as the red "needs you"
            // badge. This is the dumb time-based rule -- we do not try to tell "finished cleanly"
            // apart from "blocked on a question"; a long silence means "needs you".
            _active = false;

            // Capture the settled screen - the "before" side of every later comparison: the
            // evidence an unexplained wake records, and the screen the content rule asks whether
            // anything was added to.
            //
            // This used to happen only when the shadow evidence producer was wired, on the grounds
            // that a locked snapshot nobody reads is waste. It is now taken on EVERY settle,
            // because the content rule needs it whether that producer exists or not, and a session
            // that settled without one would otherwise have no "before" to compare against and
            // would open a turn on the next byte exactly as it does today - silently, and only for
            // some sessions. The cost is one locked snapshot per SETTLE, which happens at most once
            // per turn, not per byte.
            if (TryReadScreenBodyRows(out var settledRows))
            {
                _settledBodyRows = settledRows;
                _settledBody = string.Join("\n", settledRows);
                _settledBodyHash = Activity.ActivityEvidence.BodyHash(_settledBody);
            }
            FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=NEEDS-YOU after {idle.TotalSeconds:F1}s silent | hook={_session.ActivityState}");
            if (_driveState) _session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _session.OnActivityStateChanged -= OnActivityStateChanged;
            _buffer.OnBytesWritten -= _onBytes;
            _quietTimer.Dispose();
            _contentCheckTimer.Dispose();
        }
    }
}
