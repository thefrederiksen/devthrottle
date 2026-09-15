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

    /// <summary>
    /// What the content rule is set to on this Director, read from the environment.
    ///
    /// A METHOD AND NOT A CACHED STATIC. It used to be a property initialised at type load, which
    /// no test could ever change - so "the switch resolves correctly" was pinned and "the switch
    /// reaches the detector" was not, and a detector that ignored the variable entirely would have
    /// passed every test in the repository. A detector is constructed once or twice in a process;
    /// reading one environment variable there costs nothing.
    /// </summary>
    internal static TurnContentRule ContentRuleFromEnvironment() =>
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
    private readonly ITerminalNoveltyRule _rowRule;
    private readonly ITerminalSizeRule _sizeRule;
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
    /// Test seam for exercising the silence rule without waiting for the production interval, and
    /// for handing the detector a candidate other than the two it ships with.
    /// </summary>
    /// <param name="rowRule">
    /// The row candidate, or null for the shipped one. Injectable because the interface is the
    /// single seam between this detector and the rules: an inspection found production calling the
    /// rule functions directly, which would have let work item five score one function while the
    /// Director ran another with every test still green. A test that hands in a rule and watches
    /// the Director obey it is what keeps that honest.
    /// </param>
    /// <param name="sizeRule">The size candidate, or null for the shipped one.</param>
    internal TerminalStateDetector(SessionManager sessionManager, bool driveState,
        TimeSpan quietThreshold, Activity.ActivityEventProducer? activityProducer = null,
        TurnContentRule? contentRule = null,
        TimeSpan? settleCheckDelay = null,
        TimeSpan? maxSettleCheckDeferral = null,
        ITerminalNoveltyRule? rowRule = null,
        ITerminalSizeRule? sizeRule = null)
    {
        if (quietThreshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quietThreshold));

        _sessionManager = sessionManager;
        _driveState = driveState;
        _quietThreshold = quietThreshold;
        _activityProducer = activityProducer;
        _contentRule = contentRule ?? ContentRuleFromEnvironment();
        _settleCheckDelay = settleCheckDelay ?? SettleCheckDelay;
        _maxSettleCheckDeferral = maxSettleCheckDeferral ?? MaxSettleCheckDeferral;
        _rowRule = rowRule ?? TerminalContentNovelty.RowRule;
        _sizeRule = sizeRule ?? TerminalContentNovelty.StartingSizeRule();
    }

    /// <summary>
    /// Which content candidate this detector is running - the whole chain from the environment
    /// variable to the field the per-session watcher is handed. A test seam, because "the switch
    /// resolves correctly" and "the resolved switch is what the watcher gets" are two different
    /// claims and only the first of them was pinned.
    /// </summary>
    internal TurnContentRule ContentRule => _contentRule;

    /// <summary>
    /// The settling window this detector is actually running, and its cap. A test seam for the same
    /// reason ContentRule is one: the named constants were pinned and the CONSTRUCTOR'S USE of them
    /// was not, so a detector built with 401 milliseconds and four seconds hard-coded passed every
    /// test in the repository. Every behaviour test injects its own timings, so nothing else can
    /// see what a production detector took.
    /// </summary>
    internal TimeSpan SettleCheckDelayInUse => _settleCheckDelay;

    /// <summary>The cap this detector is running. See <see cref="SettleCheckDelayInUse"/>.</summary>
    internal TimeSpan MaxSettleCheckDeferralInUse => _maxSettleCheckDeferral;

    /// <summary>
    /// Is a content check armed for this session right now? A test-only read, and it exists to tell
    /// two different facts apart that look identical from the shadow directory: a check that is
    /// scheduled and has not fired yet (the row is LATE) and no check at all (the row will NEVER be
    /// written). A test that waits for a row and times out cannot distinguish those from the files
    /// on disk, because a directory holding exactly the baseline rows is the same observation in
    /// both cases.
    /// </summary>
    internal bool HasPendingCheck(Guid sessionId)
        => _watchers.TryGetValue(sessionId, out var watcher) && watcher.HasPendingCheck;

    /// <summary>
    /// Is the ACTIVE LATCH set for this session right now? A test-only read, and it is the fact
    /// that decides whether a burst that armed no check is a product defect or a racing test.
    ///
    /// A burst that reaches an ALREADY-ACTIVE session correctly arms nothing and writes no row -
    /// that is the design, because the rule must cost a working session nothing. A burst that
    /// reaches a SETTLED session and still arms nothing is the defect. The two are indistinguishable
    /// from the shadow directory and from <see cref="HasPendingCheck"/>, so the latch has to be
    /// readable or the question cannot be answered from a run at all.
    /// </summary>
    internal bool IsActiveLatched(Guid sessionId)
        => _watchers.TryGetValue(sessionId, out var watcher) && watcher.IsActiveLatched;

    /// <summary>
    /// How many content checks have faulted CONSECUTIVELY for this session, and what the last one
    /// said. A test-only read, and the third fact the other two cannot carry.
    ///
    /// A check that faults past <c>MaxCheckRetries</c> stops retrying: it opens the turn and writes
    /// NO row. From outside, that is the same observation as a burst that never armed anything -
    /// no pending check, no new row - and the two have entirely different causes. The fault count
    /// tells them apart, and the message says what actually threw, which is otherwise only in
    /// FileLog and therefore only on the machine that ran it.
    /// </summary>
    internal (int Faults, string? LastFault) CheckFaultState(Guid sessionId)
        => _watchers.TryGetValue(sessionId, out var watcher)
            ? (watcher.ConsecutiveCheckFailures, watcher.LastCheckFault)
            : (0, null);

    /// <summary>
    /// Which branch of the byte path the LAST burst on this session took, in words. A test-only
    /// read, and the one that answers "was the session settled or already active when that burst
    /// arrived" from a run rather than from reading the code.
    ///
    /// Every terminal branch of the byte path names itself, so the answer is a positive statement
    /// about what happened rather than an inference from what did not. An absent value means no
    /// burst has reached the byte path at all, which is itself a distinct fact from every branch
    /// below it.
    /// </summary>
    internal string? LastByteDisposition(Guid sessionId)
        => _watchers.TryGetValue(sessionId, out var watcher) ? watcher.LastByteDisposition : null;

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
            _contentRule, _settleCheckDelay, _maxSettleCheckDeferral, _rowRule, _sizeRule);
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

        // The two candidates, held as INTERFACES and never as functions, so the rule work item five
        // scores is the rule the Director ran.
        //
        // ON A COMPARABLE FRAME BOTH ARE ASKED: the authoritative one decides and the other is
        // written down beside it. AN AMBIGUOUS FRAME ASKS NEITHER - it short-circuits to the
        // conservative open, with both verdicts true and the magnitude zero, and the log says which
        // kind of ambiguity it was. That is correct and is not an omission: a frame is ambiguous
        // because the screen could not be read at all, or because there is no settled screen to
        // compare it against, and neither is a question a content rule can answer. Asking a rule to
        // compare an empty list against an empty list would produce a verdict shaped like a
        // measurement and meaning nothing. A comment here used to claim both candidates are asked
        // on EVERY check; an inspection demonstrated the first ambiguous check asking neither, and
        // the claim is corrected rather than the behaviour.
        private readonly ITerminalNoveltyRule _rowRule;
        private readonly ITerminalSizeRule _sizeRule;

        // Guards the settled-session decision, which the PTY producer thread and the check timer
        // both touch. The ACTIVE path - where almost every byte lands - never takes it.
        private readonly object _checkGate = new();
        private bool _checkScheduled;
        private long _checkDeadlineTicks;      // when the check may no longer be pushed out
        private long _pendingBytes;            // bytes in the burst that produced the pending check

        // Consecutive faulted checks. Reset by any check that completes. See RestorePendingCheck.
        private int _checkFailures;

        // What the last faulted check threw, kept so a failing test can say WHY no row was written.
        // FileLog carries it too, but a FileLog line lives on the machine that ran the suite and a
        // test failure has to be readable from its own message.
        private string? _lastCheckFault;

        /// <summary>
        /// How many times a faulted check's burst may be PUT BACK. The name says retries rather
        /// than failures because the old one was off by one in every direction: at three it read
        /// as "three faults end it", while the code compared with greater-than and so ended on the
        /// FOURTH consecutive fault. The comment, the log line and the build report all repeated
        /// the wrong number. Counted here: faults one, two and three restore the burst; fault four
        /// stops retrying.
        ///
        /// It is bounded because the original check's deadline is already in the past, so an
        /// unbounded retry would fire with no delay and spin a core. What happens when the bound is
        /// exceeded is NOT a drop - see <see cref="RestorePendingCheck"/>.
        /// </summary>
        private const int MaxCheckRetries = 3;

        // The same "a check is armed" fact as _checkScheduled, readable WITHOUT taking the lock, so
        // the hot path on an already-working session pays one volatile read rather than a lock.
        private int _checkPending;

        /// <summary>See TerminalStateDetector.HasPendingCheck - a test-only read of that flag.</summary>
        internal bool HasPendingCheck => Volatile.Read(ref _checkPending) != 0;

        /// <summary>See TerminalStateDetector.IsActiveLatched - a test-only read of that latch.</summary>
        internal bool IsActiveLatched => _active;

        /// <summary>See TerminalStateDetector.LastByteDisposition.</summary>
        internal string? LastByteDisposition => Volatile.Read(ref _lastByteDisposition);

        // Which branch the last burst took. One reference store of a shared constant per burst,
        // written beside work that already arms a timer on the same path, so it is not a cost the
        // "a working session pays nothing" claim has to account for.
        private string? _lastByteDisposition;

        /// <summary>See TerminalStateDetector.CheckFaultState.</summary>
        internal int ConsecutiveCheckFailures => Volatile.Read(ref _checkFailures);

        /// <summary>See TerminalStateDetector.CheckFaultState.</summary>
        internal string? LastCheckFault => Volatile.Read(ref _lastCheckFault);

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
            TurnContentRule contentRule, TimeSpan settleCheckDelay, TimeSpan maxSettleCheckDeferral,
            ITerminalNoveltyRule rowRule, ITerminalSizeRule sizeRule)
        {
            _rowRule = rowRule;
            _sizeRule = sizeRule;
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
            {
                Stamp("suppressed as a Director-induced repaint");
                return;
            }

            // Brand-new session: Claude Code's startup splash (logo, version line, prompt box,
            // bypass-permissions footer) emits a flood of bytes BEFORE the user has done anything.
            // The byte->Working rule would flip a fresh session blue for ~QuietThreshold seconds
            // even though it is sitting idle at the prompt. Suppress it. IsBrandNew clears the
            // moment the user's first submission is verified. The resulting Working transition
            // starts the silence countdown, including when the entire response arrived first.
            if (_session.IsBrandNew)
            {
                Stamp("dropped because the session is still brand new");
                return;
            }

            // WHILE THE SESSION IS ALREADY ACTIVE, NOTHING BELOW APPLIES. Bytes re-arm the idle
            // countdown and no screen is read. That is where almost all the bytes are, so the
            // content rule costs a working session nothing at all.
            //
            // EmitsContinuousIdleOutput now selects ONLY this: whether a raw byte may re-arm the
            // quiet timer. For an agent whose footer repaints forever it may not - its idle clock
            // runs off screen-BODY changes instead, which is what ObserveBodyChange looks for.
            // It no longer selects a different activation rule; the settled case below is shared.
            if (_active)
            {
                if (_continuousIdle)
                {
                    // A footer that repaints forever is not a burst. For this driver the unit of
                    // activity is a BODY CHANGE, so a raw byte neither pushes a pending check out
                    // nor arms anything.
                    if (ObserveBodyChange())
                    {
                        MarkContinuousActive();
                        if (Volatile.Read(ref _checkPending) != 0) ScheduleContentCheck(bytes.Length);
                        Stamp("already ACTIVE (body changed); a check was only pushed out if one was armed");
                    }
                    else Stamp("already ACTIVE (no body change); nothing armed, by design");
                    return;
                }

                // A shadow check pending from the wake is still pushed out by later bytes, so the
                // screen it reads is the finished burst rather than a half-drawn one. One volatile
                // read is the whole cost of this on the hot path.
                if (Volatile.Read(ref _checkPending) != 0) ScheduleContentCheck(bytes.Length);
                ArmQuietTimer();
                Stamp("already ACTIVE; no screen read and no check armed, by design");
                return;
            }

            // THE SESSION IS SETTLED, which is the only place the content rule has anything to say.
            if (_contentRule == TurnContentRule.Off)
            {
                if (_continuousIdle)
                {
                    // Today's state rule for this driver, unchanged - the body decides - and the
                    // check now rides on the same decision instead of on raw bytes.
                    if (ObserveBodyChange())
                    {
                        MarkContinuousActive();
                        ScheduleContentCheck(bytes.Length);
                        Stamp("SETTLED, rule off, body changed; check armed");
                    }
                    else Stamp("SETTLED, rule off, body unchanged; nothing armed");
                    return;
                }

                // Today's rule, unchanged: a byte out of the ConPTY means the agent is producing
                // output, so it is working. We do not inspect what the byte is. A byte is activity.
                // Period. Full stop. The buffer already stamps LastWriteAtUtc on every write, so
                // "time since the last character" (the idle clock the panel shows) is free.
                MarkActiveFromByte(bytes.Length);

                // The state write above already happened. The check below reads the screen and
                // records what the rule WOULD have decided; it cannot change anything.
                ScheduleContentCheck(bytes.Length);
                Stamp("SETTLED, rule off; the byte opened the turn and a check was armed");
                return;
            }

            // The rule is on: the byte does not flip anything. It schedules a check a short delay
            // later, so a repaint is judged once it has finished drawing.
            if (_continuousIdle)
            {
                if (ObserveBodyChange())
                {
                    ScheduleContentCheck(bytes.Length);
                    Stamp("SETTLED, rule on, body changed; check armed");
                }
                else Stamp("SETTLED, rule on, body unchanged; nothing armed");
                return;
            }
            ScheduleContentCheck(bytes.Length);
            Stamp("SETTLED, rule on; check armed");
        }

        /// <summary>
        /// Name the branch this burst took, for <see cref="LastByteDisposition"/>. Every terminal
        /// branch of the byte path calls this, so the read is a positive statement rather than an
        /// inference from silence.
        /// </summary>
        private void Stamp(string disposition) => Volatile.Write(ref _lastByteDisposition, disposition);

        /// <summary>
        /// Today's activation, kept intact so the switch-off path is byte for byte.
        ///
        /// A FAULT NO LONGER LEAVES THE SESSION BLUE FOR EVER. The fault this fixes is OLDER than
        /// the content rule - it is on origin/main, and this is the path every Director runs while
        /// the switch ships off. Session.SetActivityState assigns Working and only THEN calls its
        /// subscribers, so a subscriber that throws leaves the session in Working with the exception
        /// escaping mid-write. The latch stayed set, the quiet timer was never armed, and every
        /// later byte took the already-active branch and armed nothing: permanently blue, with
        /// nothing left that could bring it back.
        ///
        /// THE ARM IS THE PART THAT CLOSES IT, which is why it sits in a finally. Arming on every
        /// byte is what this method always meant to do; the fault was that a throw skipped it.
        ///
        /// The latch release is conditional and that is deliberate - see
        /// <see cref="ReleaseLatchIfNothingWasWritten"/>. Clearing it unconditionally would close
        /// nothing here: OnQuietCore's first line returns unless the latch is set, so a cleared
        /// latch plus an armed timer is a countdown that fires into nothing, and the session stays
        /// Working exactly as before. That was measured, not assumed.
        /// </summary>
        private void MarkActiveFromByte(long byteCount)
        {
            try
            {
                if (_active) return;
                _active = true;
                try
                {
                    FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=ACTIVE (byte) | hook={_session.ActivityState}");
                    RecordUnexplainedWakeEvidence(byteCount, "byte");
                    if (_driveState) _session.ApplyTerminalActivityState(ActivityState.Working);
                }
                catch
                {
                    ReleaseLatchIfNothingWasWritten();
                    throw;
                }
            }
            finally
            {
                ArmQuietTimer(); // restart the idle countdown on every byte, fault or no fault
            }
        }

        /// <summary>
        /// Put the active latch back after a faulted wake - BUT ONLY IF THE SESSION IS NOT SITTING
        /// IN WORKING.
        ///
        /// The condition is the whole of it, and it is not caution. Session.SetActivityState assigns
        /// Working and THEN calls its subscribers, so a subscriber that throws leaves the session in
        /// Working with the exception escaping from the middle of the write. That session now owes a
        /// settle, and the only thing that can deliver one is the quiet timer - which returns
        /// immediately unless this latch is set (see OnQuietCore's first line). Clearing the latch
        /// there would swap one stuck-blue session for another: no later byte could restore it and
        /// the countdown would fire into nothing.
        ///
        /// A fault BEFORE the state write is the opposite case. Nothing was written, the session is
        /// still red, and the latch is a lie that makes every later byte take the already-active
        /// branch - so it goes back.
        ///
        /// Reading the session's state is the only signal that separates the two, because the throw
        /// itself cannot say how far the write got.
        /// </summary>
        private void ReleaseLatchIfNothingWasWritten()
        {
            if (_session.ActivityState != ActivityState.Working) _active = false;
        }

        /// <summary>
        /// The unit of activity for agents whose idle terminal never goes byte-silent: did the
        /// screen BODY change? We cannot trust raw bytes (the footer animates forever), so the
        /// agent is "working" only while the body moves. Body = the visible rows ABOVE the cursor;
        /// the input composer and the animated footer (spinner / shortcuts / clock) sit at and
        /// below the cursor, so the churn that never stops is excluded. The screen snapshot is
        /// taken under a lock, so it is throttled to BodyCheckIntervalTicks; between looks we
        /// deliberately do nothing, which is what lets the idle timer actually fire.
        ///
        /// Returns TRUE when the body changed, and also when the body cannot be isolated (cursor at
        /// the very top, or no grid yet) - never go idle on an ambiguous frame, the same
        /// conservative outcome the byte rule would give. Returns false for a footer-only repaint
        /// AND while the throttle says it is too soon to look again.
        ///
        /// IT IS ALSO WHAT SCHEDULES THE SHADOW CHECK for this driver. An agent that repaints
        /// forever never goes byte-silent, so a check armed on raw bytes would fire at the deferral
        /// cap for as long as the session sits idle, writing a row every few seconds for ever. A
        /// row per real body change is both cheaper and the measurement actually wanted; a row per
        /// footer frame is noise that would skew it. The honest cost: a reply on such an agent can
        /// be sampled up to one throttle interval into its drawing rather than after a quiet
        /// window, because later footer bytes no longer push the check out.
        /// </summary>
        private bool ObserveBodyChange()
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - Volatile.Read(ref _lastBodyCheckTicks) < BodyCheckIntervalTicks)
                return false;
            Volatile.Write(ref _lastBodyCheckTicks, nowTicks);

            if (!TryReadScreenBody(out var body))
                return true;

            if (string.Equals(body, _lastBody, StringComparison.Ordinal))
                return false;

            _lastBody = body;
            return true;
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
        /// BYTES INSIDE THE WINDOW PUSH THE CHECK OUT RATHER THAN BEING DROPPED, so a burst that
        /// finishes inside the maximum deferral is judged once it has stopped drawing - which
        /// matters because a half-drawn repaint looks exactly like new content.
        ///
        /// A BURST THAT OUTLIVES THE MAXIMUM DEFERRAL IS SAMPLED WHILE IT IS STILL DRAWING. That is
        /// the trade the cap makes, and it is stated rather than wished away: an agent writing
        /// something every three hundred milliseconds forever would otherwise defer its own check
        /// forever and never be judged at all, so the cap buys "judged, possibly half-drawn" in
        /// place of "never judged". (A comment here used to claim a longer burst is never sampled
        /// half drawn. That was false of this code.)
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
                Volatile.Write(ref _lastCheckFault, ex.GetType().Name + ": " + ex.Message);
                FileLog.Write($"[TerminalStateDetector] content check failed session={_session.Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Take the burst off the books and run one check on it - the work itself is in
        /// RunContentCheck, which decides first and writes the observation row afterwards.
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

            // EVERYTHING BELOW RUNS WITH THE BURST ALREADY TAKEN OFF THE BOOKS, so a fault here
            // would otherwise consume it: a one-burst reply would have no pending check and no
            // later byte to create another, and the session would sit red for good. A fault puts
            // the burst back and re-arms instead. See RestorePendingCheck for the retry bound.
            try
            {
                RunContentCheck(bytes);
                Volatile.Write(ref _checkFailures, 0);
            }
            catch
            {
                RestorePendingCheck(bytes);
                throw;
            }
        }

        /// <summary>
        /// One check: read the screen, ask both candidates through the interface, apply the state
        /// if the authoritative one opens, and only THEN write the observation row.
        ///
        /// THE ORDER IS THE POINT. The append is a synchronous file write under one process-wide
        /// lock with no latency bound, so running it before the state decision let an observation
        /// delay the very thing it observes. Appending afterwards removes that outright rather than
        /// bounding it. The cost is that a check whose state write FAULTS writes no row at all -
        /// which is correct, because that check is retried and the retry writes one.
        /// </summary>
        private void RunContentCheck(long bytes)
        {
            var settled = _settledBodyRows;
            var current = TryReadScreenBodyRows(out var rows) ? rows : Array.Empty<string>();

            // AN AMBIGUOUS FRAME CANNOT BE COMPARED, and there are two ways to get one: the screen
            // could not be read (no grid yet, or the cursor at the very top), or there is no
            // SETTLED side to compare it against. The second was missed and it could lose a turn:
            // a settle whose extraction failed leaves no baseline, and a small real reply then
            // scores under the size threshold against nothing and holds the session red, with no
            // later byte to ask again. Both take the conservative open path the byte rule gives,
            // and the log says WHICH rather than recording a silent zero.
            string? ambiguousReason =
                current.Length == 0 ? "(screen could not be read)"
                : settled.Length == 0 ? "(no settled screen to compare against)"
                : null;
            bool ambiguous = ambiguousReason is not null;

            string? firstNewRow = null;
            bool rowOpens;
            bool sizeOpens;
            int changed;
            int threshold;
            if (ambiguous)
            {
                rowOpens = true;
                sizeOpens = true;
                changed = 0;
                // No verdict was taken, so there is no verdict to read a threshold from. What goes
                // in the log is the threshold the rule DECLARES it would have used, which is why
                // that property is still on the interface.
                threshold = _sizeRule.Threshold;
            }
            else
            {
                // THROUGH THE INTERFACE, ALWAYS. Not a function call beside it and not a threshold
                // comparison repeated here: the rule that decides is the rule work item five
                // scores, and the magnitude written down is the one this verdict was taken from.
                rowOpens = _rowRule.GainedContent(settled, current, _chromeMarkers, out firstNewRow);

                // ONE CALL, not three. The verdict, the magnitude and the threshold arrive together
                // so they cannot describe different measurements - which three separate calls could,
                // and an inspection demonstrated that they were not required to agree.
                var size = _sizeRule.Evaluate(settled, current);
                sizeOpens = size.Opens;
                changed = size.Magnitude;
                threshold = size.Threshold;
            }

            bool submitted = _session.LastSubmissionAtUtc is DateTime submittedAt
                && DateTime.UtcNow - submittedAt <= Activity.ActivityEventProducer.SubmissionWindow;

            var record = new TurnDetectionShadowLog.Record(
                T: DateTime.UtcNow.ToString("o"),
                Agent: _session.Driver.Kind.ToString(),
                Mode: _continuousIdle ? "body" : "byte",
                Rule: _contentRule.ToString().ToLowerInvariant(),
                // Always true, and written down rather than assumed: a check only happens because a
                // byte reached a settled session, which is precisely what today's rule opens on.
                ByteRuleOpens: true,
                RowRuleOpens: rowOpens,
                RowEvidence: ambiguousReason ?? firstNewRow,
                SizeRuleOpens: sizeOpens,
                ChangedCharacters: changed,
                SizeThreshold: threshold,
                SettledHash: _settledBodyHash,
                CurrentHash: ambiguous ? null : Activity.ActivityEvidence.BodyHash(string.Join("\n", current)),
                Bytes: bytes,
                Submitted: submitted);

            // The state decision comes FIRST, and with the rule off there is none to make: the byte
            // already flipped the session on the way in and this check only observes.
            if (_contentRule != TurnContentRule.Off)
            {
                bool opens = _contentRule == TurnContentRule.Size ? sizeOpens : rowOpens;
                if (opens) MarkActiveFromContent(bytes, ambiguousReason ?? firstNewRow);
            }

            TurnDetectionShadowLog.Append(_session.Id, record);
        }

        /// <summary>
        /// A check faulted: put its burst back and arm the timer again, so the burst is asked about
        /// rather than swallowed.
        ///
        /// IT IS BOUNDED, because a fault that repeats would otherwise retry forever - and the
        /// deadline of the original check is already in the past, so each retry would fire
        /// immediately and spin. After <see cref="MaxCheckRetries"/> restores - that is, on the
        /// FOURTH consecutive fault - the burst is not put back again.
        ///
        /// AND THEN THE TURN OPENS. It used to be dropped, which built the exact failure this whole
        /// design forbids: a one-burst reply whose check faulted four times had no pending check and
        /// no later byte to make one, so the session sat red for ever. A rule that cannot decide
        /// must never decide against the user. Falling back to today's behaviour - a byte reached a
        /// settled session, so the turn opens - is always available and is the conservative
        /// direction: the cost of a wrong open is a blue session that settles again a quiet window
        /// later, and the cost of a wrong drop is work that sits red until someone looks at it.
        ///
        /// The failure count is deliberately NOT reset here. While the fault persists every later
        /// burst takes this same path and opens the turn immediately, which is precisely today's
        /// byte rule; the first check that completes resets the count in OnContentCheckCore.
        ///
        /// With the rule OFF there is nothing to open: the byte already flipped the session on the
        /// way in and this check only observes, so the switch-off path stays byte for byte.
        /// </summary>
        private void RestorePendingCheck(long bytes)
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            int faults = Interlocked.Increment(ref _checkFailures);
            if (faults > MaxCheckRetries)
            {
                FileLog.Write($"[TerminalStateDetector] {_session.Id} content check faulted {faults} times running; opening the turn on the burst of {bytes} bytes rather than retrying it again");
                if (_contentRule != TurnContentRule.Off)
                    MarkActiveFromContent(bytes, $"(the content check faulted {faults} times running)");
                return;
            }

            lock (_checkGate)
            {
                if (_checkScheduled)
                {
                    // A later byte already armed a fresh check; hand it the bytes and leave its own
                    // deadline alone.
                    _pendingBytes += bytes;
                }
                else
                {
                    _checkScheduled = true;
                    Volatile.Write(ref _checkPending, 1);
                    _pendingBytes = bytes;
                    // A FRESH deadline, not the original one. The original is in the past, so
                    // reusing it would make the retry fire with no delay at all.
                    _checkDeadlineTicks = DateTime.UtcNow.Ticks + _maxSettleCheckDeferral.Ticks;
                }

                try { _contentCheckTimer.Change(_settleCheckDelay, Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { /* race with Dispose */ }
            }
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
            // loose.
            //
            // A FAULT AFTER THE LATCH PUTS IT BACK - but only when nothing was written, see
            // ReleaseLatchIfNothingWasWritten. Setting it and then throwing left the session red AND
            // latched active, after which every later byte took the already-active branch and
            // scheduled nothing: permanently red with no way back. (A comment here used to claim the
            // worst outcome of the loose latch was a duplicate Working write. It was false - the
            // state write, the evidence call and the timer arm all follow the latch and any of them
            // can throw - and it is deleted rather than softened.)
            //
            // The unconditional release this used to do was itself wrong in the other direction: a
            // subscriber that throws AFTER the session is in Working leaves a session that needs a
            // settle, and only the latch lets the quiet timer deliver one.
            if (_active) return;
            _active = true;
            try
            {
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
            }
            catch
            {
                ReleaseLatchIfNothingWasWritten();
                throw;
            }
            finally
            {
                // Armed whatever happened, for the same reason as the byte path: a session already
                // written into Working needs a countdown running or nothing will settle it. This
                // used to sit inside the try, where a faulting state write skipped it and left the
                // recovery entirely to the check retry.
                ArmQuietTimer();
            }
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

        /// <summary>
        /// Body changed (or an ambiguous frame): flag Working and re-arm the idle timer from now.
        /// The quiet confirmation in OnQuietCore measures silence from this moment.
        ///
        /// Carries the same latch release and the same guaranteed arm as MarkActiveFromByte, and
        /// for the same reason: this is the switch-off path for an agent whose idle terminal never
        /// goes byte-silent, it is on origin/main, and a state-change subscriber that throws left
        /// the session latched Working with no countdown running.
        /// </summary>
        private void MarkContinuousActive()
        {
            try
            {
                _session.StampBodyActivity();
                if (_active) return;
                _active = true;
                try
                {
                    FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=ACTIVE (body) | hook={_session.ActivityState}");
                    RecordUnexplainedWakeEvidence(byteCount: 0, "body");
                    if (_driveState) _session.ApplyTerminalActivityState(ActivityState.Working);
                }
                catch
                {
                    ReleaseLatchIfNothingWasWritten();
                    throw;
                }
            }
            finally
            {
                ArmQuietTimer();
            }
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
            else
            {
                // NO BASELINE IS HONEST; THE PREVIOUS TURN'S IS A WRONG ANSWER. Leaving the old rows
                // in place made the next check compare this turn's screen against a screen from a
                // turn that had already ended, and call the difference between two unrelated turns
                // "what was gained". An absent baseline is ambiguous and opens the turn, which is
                // the conservative outcome; a stale one silently decides.
                _settledBodyRows = Array.Empty<string>();
                _settledBody = null;
                _settledBodyHash = null;
                FileLog.Write($"[TerminalStateDetector] {_session.Id} settled with NO baseline (screen could not be read); the next check is ambiguous and will open");
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
