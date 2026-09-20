using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Speech;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Everything the turn-verdict seat needs from the Gateway around it, as one seam - the same shape, and for
/// the same reason, as <see cref="Supervision.ISupervisorEnvironment"/>. Production wires
/// <see cref="GatewayTurnVerdictEnvironment"/>; the tests wire a fake with no clock, no tunnel and no model,
/// and the held-check test wires the PRODUCTION environment over a real push store.
/// </summary>
public interface ITurnVerdictEnvironment
{
    /// <summary>This account's turn-verdict settings (the two switches, the ceiling, the settle, the timeout).</summary>
    TurnVerdictSettings Settings(TenantId tenant);

    /// <summary>
    /// The session's facts and whether a live owning session is holding it (the pushed field is still named
    /// HasLiveSupervisor), both taken from ONE fresh snapshot of the account's pushed roster. The held answer
    /// is resolved across that WHOLE snapshot - never read off the session's own row, because the push store
    /// nulls the role and the liveness answer at ingest so that only the Gateway can decide them. A check that
    /// read the row directly would answer "not held" for every session on the fleet and read every worker.
    /// One snapshot for both, so the role and the facts can never describe two different moments.
    /// </summary>
    TurnVerdictSessionState ReadSessionState(TenantId tenant, string sessionId);

    /// <summary>The live screen over the tunnel, or null when it cannot be read. Unreadable is carried as no rows.</summary>
    Task<ScreenGridResponse?> ReadScreenGridAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct);

    /// <summary>The session's stored conversation, read inside the account's scope. Null: nothing stored yet.</summary>
    StoredConversation? ReadConversation(TenantId tenant, string sessionId);

    /// <summary>The account's spoken language - the "spoken" field is written in it.</summary>
    SpokenLanguage Language(TenantId tenant);

    /// <summary>The account's own narration instructions when it has replaced the shipped default, else null.</summary>
    string? CustomSpokenRules();

    /// <summary>Whether this account's plan includes the Wingman's narration (see <see cref="NarrationPlanRule"/>).</summary>
    NarrationPlan PlanForNarration(TenantId tenant);

    /// <summary>The model id the judge runs on for this account, recorded on every verdict including a failed one.</summary>
    string JudgeModel(TenantId tenant);

    /// <summary>Ask the judge ONE question and return its raw answer. Throws on no answer, a rate limit, or an
    /// unusable provider; the service turns each into a stored failed record, and so does any other exception
    /// a verdict request meets.</summary>
    /// <param name="timeout">How long this one call may take. The service passes the account's
    /// <see cref="TurnVerdictSettings.JudgeTimeoutSeconds"/> for a first attempt and
    /// <see cref="TurnVerdictSettings.ListenedToReattemptTimeoutSeconds"/> for the one re-attempt a listened-to
    /// session may get; nothing else decides it.</param>
    Task<TurnVerdictJudgeAnswer> AskJudgeAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct);

    /// <summary>Ask the NARRATION CALL (slice J) one question and return its raw answer: the same model as the judge,
    /// through a separate seam, because it is not a judgement and is never counted as one. Throws exactly as
    /// <see cref="AskJudgeAsync"/> does.</summary>
    Task<TurnVerdictJudgeAnswer> AskNarratorAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct);

    /// <summary>This session's latest stored verdict in this account, or null.</summary>
    TurnVerdictDto? Latest(TenantId tenant, string sessionId);

    /// <summary>Every session's latest stored verdict in this account, in one read - for the carrying-on clock.</summary>
    IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant);

    /// <summary>The sessions this session owns, resolved across the account's whole fresh roster, or null when it
    /// owns none (or is not in the roster).</summary>
    OwnedSessionsFacts? OwnedSessions(TenantId tenant, string sessionId);

    /// <summary>Store one verdict record, accepted or failed.</summary>
    void Store(TenantId tenant, string sessionId, TurnVerdictDto verdict);

    /// <summary>Forget this session's stored verdicts. Returns how many rows went.</summary>
    int Invalidate(TenantId tenant, string sessionId);

    /// <summary>Whether somebody is listening to this session - a voice session is judged whatever the judge
    /// switch says, because its narration IS the verdict's spoken section.</summary>
    bool IsVoiceSession(TenantId tenant, string sessionId);

    /// <summary>Wait. The seat's only clock, so a test can run the settle delay instantly.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);

    /// <summary>Append one record to the activity ledger. Closed event and cause words; never screen text.</summary>
    void Record(TurnVerdictRecord record);

    /// <summary>
    /// Keep one judgement whole for the Wingman inspector: the package, the prompt, the raw reply and the verdict -
    /// or, for a stop that stood down, its cause. The seat calls it only for an account whose judge switch is on.
    ///
    /// IT MUST NOT BLOCK AND MUST NOT THROW. The seat calls it on the verdict path, after the verdict is stored and
    /// before the flight completes, so a call that waited would delay every caller joined to the flight and a call
    /// that threw would land in the flight's exception boundary. Production hands the trace to
    /// <see cref="TurnVerdictTraceWriter"/>, whose enqueue can do neither.
    ///
    /// A HELD SESSION IS TRACED ONLY WHEN A PERSON ASKS - OR WHEN THE ACCOUNT'S FLEET MANAGER HOLDS IT. Every automatic request
    /// skips a session that is held for judging before its screen is read, so its trace carries a cause and no
    /// content. A person pressing Explain on a held session is judged - that is the on-demand rule - and its trace
    /// keeps what the judge was given, because the person asked to see exactly that and it belongs to the same
    /// account. A session the account's Fleet Manager holds is judged automatically on its turn end and snooze
    /// expiry (owner ruling, 2026-09-16), so its trace keeps its content the same way; it is the same account's
    /// session.
    /// </summary>
    void RecordTrace(TenantId tenant, TurnVerdictTrace trace);

    /// <summary>
    /// A stop's trace that the seat itself cannot hand in - nothing can say whether this account is traced, because no
    /// judgement for the session read its settings, or building the trace failed. Logged and COUNTED with the losses the
    /// writer already counts, so a stop observed by the seat is always either a row or a counted loss, never only a log
    /// line. Production passes it to <see cref="TurnVerdictTraceWriter.NotKept"/>.
    ///
    /// IT MUST NOT BLOCK AND MUST NOT THROW, for the same reasons as <see cref="RecordTrace"/>.
    /// </summary>
    void TraceNotKept(TenantId tenant, TurnVerdictTrace trace, string cause);

    /// <summary>Now, in UTC.</summary>
    DateTime NowUtc();
}

/// <summary>One snapshot's answer about one session: its facts (null when it is not in the fresh roster), whether
/// a live owning session holds it, and whether that direct owner is the account's Fleet Manager.</summary>
/// <param name="Held">A live owning session holds this one. HELD FOR NARRATION: the Wingman never reads a held
/// session aloud to the owner, whoever holds it.</param>
/// <param name="OwnedByFleetManager">The live owner is the session the account has marked as its Fleet Manager
/// (only ever true when <paramref name="Held"/> is). Such a session is still judged automatically and its verdict
/// stored under its own id, so it is not <see cref="HeldForJudging"/>. Carrying that verdict to the Fleet Manager
/// is step 4 of the Fleet Manager mission and is not done here.</param>
public sealed record TurnVerdictSessionState(SessionDto? Facts, bool Held, bool OwnedByFleetManager = false)
{
    /// <summary>HELD FOR JUDGING: the automatic turn verdict stands down. Every held session except one a Fleet
    /// Manager owns (owner ruling, 2026-09-16).</summary>
    public bool HeldForJudging => Held && !OwnedByFleetManager;
}

/// <summary>What one narration call produced: the finished spoken text, or why there is none. The prompt is kept so
/// a test can read what the call was given.</summary>
/// <param name="RawReply">The narrator's answer exactly as received, for the debug view. Null when no answer arrived -
/// a timeout, a rate limit, or a call that was never made.</param>
public sealed record NarrationCallResult(string? Spoken, string? FailureDetail, string Prompt, double ReplySeconds,
    string? RawReply = null);

/// <summary>The judge's raw answer, which model gave it, and how long it took.</summary>
public sealed record TurnVerdictJudgeAnswer(string Raw, string Model, double ReplySeconds);

/// <summary>One line of the turn-verdict ledger. <paramref name="Detail"/> is control flow only - verdict
/// word, kind, verdict id, trigger - and never a word of a screen or a conversation.</summary>
public sealed record TurnVerdictRecord(
    TenantId Tenant,
    string DirectorId,
    string SessionId,
    string EventType,
    string Cause,
    string Detail);

/// <summary>What asked for a verdict. The free checks differ by trigger, so the trigger is carried rather
/// than guessed from the call site.</summary>
public enum TurnVerdictTrigger
{
    /// <summary>The detector observed a turn end. Every free check applies, the settle delay is waited out,
    /// the judge switch applies (except for a voice session), and the ceiling applies.</summary>
    TurnEnd,

    /// <summary>A voice session's own narration. Held and exited sessions are skipped; the judge switch and the
    /// ceiling do not apply, because somebody is listening. It reuses a stored reading of an unchanged screen,
    /// failed or not: a failed reading is asked again only by <see cref="Retry"/>, on its booked time.</summary>
    Voice,

    /// <summary>The idle voice sweep. Like <see cref="Voice"/>, but capped, and it never re-asks the judge
    /// about a screen whose last answer was refused - that would be a paid call every sweep pass.</summary>
    Sweep,

    /// <summary>A person pressed a button (explain, a spoken reply). Only the brand-new check applies.</summary>
    OnDemand,

    /// <summary>
    /// A snooze's clock ran out with a stop nothing had judged (slice F, ruling 10). Automatic, so every free
    /// check applies, and the judge switch and the ceiling apply with them - it is unattended and it runs off
    /// the fold, which is the hot path. No settle delay: the stop it asks about is minutes or hours old and has
    /// long since stopped repainting.
    /// </summary>
    SnoozeExpiry,

    /// <summary>
    /// A FAILED READING'S BOOKED RETRY HAS COME DUE (mission "Wingman error and retry", 2026-09-19). It re-does
    /// the turn end's reading, so it takes the turn end's rules - held for judging, the judge switch, the
    /// ceiling - without the settle delay, because the stop is at least a minute old. It is the ONLY automatic
    /// trigger that asks again about a failed record on an unchanged screen, and only when that record's own
    /// <see cref="TurnVerdictDto.NextRetryAtUtc"/> has passed. See <see cref="WingmanRetrySchedule"/>.
    /// </summary>
    Retry,
}

/// <summary>
/// A verdict flight has ended, with its outcome: whatever started it (a turn end, a snooze expiry, a voice
/// narration, a person). <see cref="StopObservedAtUtc"/> is the stop the flight stood on.
/// </summary>
public sealed record TurnVerdictReadingCompleted(
    TenantId Tenant,
    string SessionId,
    string DirectorId,
    TurnVerdictTrigger Trigger,
    DateTime StopObservedAtUtc,
    TurnVerdictOutcome Outcome);

/// <summary>How a verdict request ended.</summary>
public enum TurnVerdictOutcomeKind
{
    /// <summary>The judge was asked and the contract accepted its answer.</summary>
    Judged,
    /// <summary>The screen was unchanged, so the stored verdict was returned and nobody was asked.</summary>
    Reused,
    /// <summary>A check stood the request down before the judge was asked. NOT necessarily before anything was
    /// read: the account ceiling comes after the screen AND the conversation. Which check costs what is the boundary block above JudgeAsync.
    /// (This summary used to say "before anything was read or asked", which was true of only some of them.)</summary>
    Skipped,
    /// <summary>No usable verdict came back - the judge failed, or the request met an exception it did not
    /// expect. A failed record was stored and the ledger says so. The one case with no stored record is a store
    /// that itself throws; that is logged, and the ledger event is still written.</summary>
    Failed,
    /// <summary>The session started working while the verdict was being formed. Nothing was stored.</summary>
    Cancelled,
}

/// <summary>Why a verdict request failed.</summary>
public enum TurnVerdictFailureKind
{
    None,
    /// <summary>No answer inside the timeout, or the call never arrived. Worth another attempt.</summary>
    DidNotAnswer,
    /// <summary>The provider said "not now". Worth another attempt after <see cref="TurnVerdictOutcome.RetryAfter"/>.</summary>
    RateLimited,
    /// <summary>The judge answered and the contract refused the answer.</summary>
    Refused,
    /// <summary>The judge could not be asked at all - no key, or an answered provider error.</summary>
    Unavailable,
}

/// <summary>The answer to one verdict request.</summary>
public sealed record TurnVerdictOutcome
{
    public TurnVerdictOutcomeKind Kind { get; init; }

    /// <summary>The verdict record: accepted on <see cref="TurnVerdictOutcomeKind.Judged"/> and
    /// <see cref="TurnVerdictOutcomeKind.Reused"/>, the failed record on <see cref="TurnVerdictOutcomeKind.Failed"/>.</summary>
    public TurnVerdictDto? Verdict { get; init; }

    /// <summary>The closed cause word a skip was recorded under (<see cref="ActivityCauses"/>).</summary>
    public string? SkipCause { get; init; }

    public TurnVerdictFailureKind Failure { get; init; }

    /// <summary>Plain words for the log. Never shown and never spoken.</summary>
    public string? FailureDetail { get; init; }

    /// <summary>How long the provider asked us to wait, on a rate limit that said.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>The full-grid hash of the screen this request read, "" when it read none.</summary>
    public string ScreenHash { get; init; } = "";

    /// <summary>The source the stop was judged from - the reply, or the failure text - as
    /// <see cref="WingmanNarrationSource.Select"/> chose it over the same screen read. Null when there was none.</summary>
    public string? SourceText { get; init; }

    /// <summary>How long the judge took, when it was asked.</summary>
    public double ReplySeconds { get; init; }

    /// <summary>
    /// The package the judge was given for this stop, for the narration call (slice J). Lazy, because only a stop
    /// somebody is listening to reads it: a judged stop hands in the package it already built, and a reused one
    /// builds it from the same screen read and conversation only when asked. Null on a skip or a cancellation.
    /// </summary>
    public Lazy<TurnVerdictPackage>? NarrationPackage { get; init; }

    /// <summary>
    /// For a REFUSED but readable judge answer only: the judge's decision as it wrote it - how the person answers, the
    /// menu, the options - read by <see cref="TurnVerdictContract.SalvageNarrationDecision"/> for the narration call's
    /// input (slice J, Architect ruling). Never stored, never on the row, never offered as a button: the record in
    /// <see cref="Verdict"/> stays refused with none of them. Null on an accepted verdict, whose own fields are the decision.
    /// </summary>
    public TurnVerdictDto? NarrationDecision { get; init; }

    /// <summary>True when there is an accepted verdict to act on.</summary>
    public bool HasAcceptedVerdict => Verdict is { Failed: false }
        && Kind is TurnVerdictOutcomeKind.Judged or TurnVerdictOutcomeKind.Reused;
}

/// <summary>
/// The turn-verdict seat (the Wingman-on-every-turn mission, slice C): at a stop, ask ONE model ONE
/// structured question about what the stop MEANS, validate the answer mechanically, and store it with the
/// agent's own words as the receipt. The voice path takes its spoken words from the same answer, so a voice
/// session's stop costs exactly one model call.
///
/// WHAT IT NEVER DOES. It never decides whether a stop happened (the detector does), never types into a
/// session, never snoozes or closes one, never reads a session a live owning session is holding - except that a
/// turn end or a snooze expiry of a session the account's Fleet Manager holds IS judged and its verdict stored
/// (owner ruling, 2026-09-16; carrying it to the Fleet Manager is step 4, not built here); it is still never
/// narrated - and never keeps a verdict across a Working transition.
///
/// THE ORDER, and it is the order of cost. Everything that is free is asked before anything is read, and
/// everything read is read before anything is paid for: the per-session gate (a second stop for a session
/// already being judged joins that judgement and asks nothing of its own, or, once that judgement is ending, waits in
/// line behind it); ONE roster snapshot, from which an automatic request skips
/// a session that is held, gone, brand-new, exited or crashed, or Working; the judge switch; the settle delay;
/// the same snapshot checks again, from a new snapshot, immediately before the read; ONE screen read and its
/// full-grid hash; reuse when the hash is the one last judged; the conversation and the source it gives; the
/// provider's own wait; the account's ceiling; and only then the package, the call, the validation and the
/// store.
///
/// A VERDICT THAT SURVIVES IS NEWER THAN THE LAST WORKING TRANSITION, BY CONSTRUCTION WITHIN THIS PROCESS.
/// <see cref="OnSessionWorking"/> bumps the session's epoch and invalidates its stored verdicts under the
/// same gate every store takes. Every flight captures the epoch when it starts, before anything is read, and
/// both of its arms - the new judgement and the reuse of a stored verdict - store only when that captured
/// epoch is still current, and return nothing when it is not. So neither an answer that arrives after the
/// session went back to work nor an old verdict reused across that transition is written over a live
/// session.
///
/// GAP, NOT PROVEN: THE SUPERVISOR'S KEYSTROKES AND THIS SCREEN READ ARE NOT ORDERED AGAINST EACH OTHER. The
/// session supervisor and the rules launcher run their own background work off the same turn-end boundary,
/// in parallel with this seat, and nothing makes one wait for the other. The judge reads the turn-end
/// screen; a "stuck-recoverable" verdict is the judge's word for what the supervisor is handling; and a
/// Working transition caused by the supervisor's "continue" invalidates the verdict. What is not proven is
/// that the two never act on one screen at the same moment - a "continue" landing between this seat's screen
/// read and its store leaves a verdict about the pre-continue screen until the Working edge arrives.
///
/// GAP, NOT PROVEN: A SESSION WHOSE OWNING SESSION EXITS WITH NO NEW STOP IS NOT RE-JUDGED. It was skipped as held
/// at its stop; when that owning session exits nothing wakes this seat, so the session surfaces raw red (the right
/// direction) and is judged at its next stop.
/// </summary>
public sealed class TurnVerdictService : IDisposable
{
    private readonly ITurnVerdictEnvironment _env;

    private sealed class Flight
    {
        public readonly CancellationTokenSource Cts = new();

        // TWO COMPLETIONS, AND THEY ARE NOT THE SAME THING (issue #2905, round 3). Outcome is this judgement's RESULT, and
        // it is the answer only for a stop this judgement covered - its own, or one that joined it while its list was
        // open. Done is the DRAIN signal: it completes once this flight has written every row it owes and left the gate,
        // and only the shutdown drain waits on it. A caller that wants a verdict never waits on Done, and never on the
        // Outcome of a flight whose list had closed before it arrived - a stop queued behind that flight is covered by the
        // successor, so it waits on that queued stop's own completion (see CoverageLocked).
        public readonly TaskCompletionSource<TurnVerdictOutcome> Outcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // THE EVIDENCE THIS FLIGHT HAS GATHERED, for the Wingman inspector's trace. Held on the flight rather than
        // in locals so that an arm reached from OUTSIDE the judgement - a cancellation, or an exception caught at the
        // flight's boundary after the judge had already answered - still records what the judge was given and what
        // it said, instead of only the exception type.
        public TurnVerdictPackage? Package;
        public string? Prompt;
        public string? RawReply;
        public double? ReplySeconds;

        // THE STOP AND THE SETTINGS THIS FLIGHT STANDS ON, captured when it starts. A cancellation or a boundary failure
        // is handled after the session has moved on, so it must neither look up "the latest observed stop" again (by
        // then it can be a later stop) nor read the settings again (the read can fail there, and a switch flipped
        // mid-flight must not change what this flight records). Settings stays null until the flight has read them.
        public DateTime ObservedAt;
        public TurnVerdictSettings? Settings;

        // THE SETTINGS OF THE FLIGHT THIS ONE WAS HANDED THE GATE BY, for a successor that never gets to read its own
        // because the service is shutting down: its stops were observed under those settings, so they are traced under
        // them. Null for a flight that took the gate itself - and for one handed the gate by a flight that never read its
        // own, so HandedOver says which it is.
        public bool HandedOver;
        public TurnVerdictSettings? HandedOverSettings;

        // The rate limit hold generation when this flight started. Its hold is written only if no clear came since.
        public long HoldGeneration;

        // A PERSON ASKED ABOUT THIS FLIGHT: it was started by an explain, or an explain joined it BEFORE its attempt loop
        // decided. Read by the loop at that decision, so a turn end on a session nobody was listening to gets the
        // listened-to re-attempt when somebody presses explain in time. Both fields change only under _decisionGate.
        private readonly object _decisionGate = new();
        private bool _askedOnDemand;
        private bool _attemptsDecided;

        /// <summary>Whether a person had asked about this flight when its attempt loop decided, or has asked so far.</summary>
        public bool AskedOnDemand { get { lock (_decisionGate) return _askedOnDemand; } }

        /// <summary>
        /// An explain arriving at this flight. True when the loop has not decided yet: the flight now serves the explain,
        /// re-attempt included. False once it has decided (round two of the slice I inspection): a flight held at its
        /// trace write after a timed-out call has already refused its re-attempt, so the explain must wait for the
        /// flight to end and then ask through its own request.
        /// </summary>
        public bool TryServeOnDemand()
        {
            lock (_decisionGate)
            {
                if (_attemptsDecided) return false;
                _askedOnDemand = true;
                return true;
            }
        }

        /// <summary>
        /// The attempt loop's decision after one call, taken under the same gate a joining explain takes. Returns true
        /// for one more attempt. The moment it returns false the attempts are decided, and no later explain is served.
        /// </summary>
        public bool DecideReattempt(bool mayReattempt, Func<bool> isVoiceSession)
        {
            lock (_decisionGate)
            {
                if (mayReattempt && (_askedOnDemand || isVoiceSession()))
                    return true;
                _attemptsDecided = true;
                return false;
            }
        }

        // THE STOPS THAT JOINED THIS FLIGHT while it ran. They ask nothing of their own - one model call per stop - and
        // this flight records them as it ends, with the settings it already read. Held here rather than traced by the
        // caller so the work is inside the flight shutdown waits for, and in the order the stops were observed.
        //
        // THE LINE BEHIND IT (issue #2905, round 2): a stop that arrives once the joined list is closed cannot join, and
        // it may not take the gate while this flight still holds it - so it is appended here, under the same lock that
        // closed the list, and this flight hands the gate to the head of the line as it leaves. A stop waiting for this
        // session is therefore always registered somewhere the shutdown drain can see, and never behind a stop that
        // arrived after it. Both lists and both flags are guarded by Sync.
        public readonly object Sync = new();
        private readonly List<DateTime> _joined = new();
        private readonly List<QueuedStop> _line = new();
        private bool _closedToJoins;
        private bool _leftGate;

        /// <summary>Attach a stop to this flight: joined while its list is open, queued behind it once the list is
        /// closed, or <see cref="Attach.Left"/> when this flight has already left the gate (it has been replaced or removed
        /// by then, so the caller looks at the gate again).</summary>
        public Attach JoinOrQueue(TurnEndSignal signal, out QueuedStop? queued)
        {
            lock (Sync)
            {
                queued = null;
                if (_leftGate) return Attach.Left;
                if (!_closedToJoins)
                {
                    _joined.Add(signal.ObservedAtUtc);
                    return Attach.Joined;
                }
                queued = new QueuedStop(signal);
                _line.Add(queued);
                return Attach.Queued;
            }
        }

        /// <summary>Join stops handed over with the gate, before this flight is registered. Its list is open.</summary>
        public void JoinHandedOver(IEnumerable<QueuedStop> stops)
        {
            lock (Sync)
                foreach (var stop in stops) _joined.Add(stop.Signal.ObservedAtUtc);
        }

        /// <summary>
        /// The completion that carries the verdict for the session's CURRENT screen, for a caller that wants its words
        /// while this flight holds the gate: this flight's own result while its list is open (it covers the screen), the
        /// NEWEST queued stop's own completion once the list is closed and a stop is waiting (the successor covers it, not
        /// this flight), and this flight's result when the list is closed and nobody is waiting. Null when this flight has
        /// already left the gate, so the caller looks at the gate again.
        /// </summary>
        public Task<TurnVerdictOutcome>? Coverage()
        {
            lock (Sync)
            {
                if (_leftGate) return null;
                if (_closedToJoins && _line.Count > 0) return _line[^1].Covered.Task;
                return Outcome.Task;
            }
        }

        /// <summary>Close the list and take what it holds. Called once, by the flight, as it ends.</summary>
        public IReadOnlyList<DateTime> TakeJoined()
        {
            lock (Sync)
            {
                _closedToJoins = true;
                if (_joined.Count == 0) return Array.Empty<DateTime>();
                var taken = _joined.ToArray();
                _joined.Clear();
                return taken;
            }
        }

        /// <summary>Take the whole line. Caller holds <see cref="Sync"/>.</summary>
        public QueuedStop[] TakeLineLocked()
        {
            var taken = _line.ToArray();
            _line.Clear();
            return taken;
        }

        /// <summary>Mark this flight gone from the gate, so a stop that still holds a reference to it looks again.
        /// Caller holds <see cref="Sync"/>, and replaces or removes the gate entry in the same step.</summary>
        public void MarkLeftGateLocked() => _leftGate = true;
    }

    private enum Attach { Joined, Queued, Left }

    /// <summary>A stop waiting in a flight's line, and its two answers.</summary>
    private sealed class QueuedStop
    {
        public QueuedStop(TurnEndSignal signal) => Signal = signal;
        public TurnEndSignal Signal { get; }

        /// <summary>What the turn-end caller that queued this stop is answered: the successor's result for the head of the
        /// line, the "already judging" skip for a stop that joined it, or the cancellation at shutdown.</summary>
        public TaskCompletionSource<TurnVerdictOutcome> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The verdict of the judgement that actually covered this stop - the successor's result, whether this
        /// stop is its head or joined it - or the cancellation at shutdown. A caller asking for the current screen's verdict
        /// while this stop is the newest in line waits on this.</summary>
        public TaskCompletionSource<TurnVerdictOutcome> Covered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Answer both at once - the stop was cancelled, or its judgement faulted.</summary>
        public void Answer(TurnVerdictOutcome outcome)
        {
            Result.TrySetResult(outcome);
            Covered.TrySetResult(outcome);
        }

        public void Fault(Exception ex)
        {
            Result.TrySetException(ex);
            Covered.TrySetException(ex);
        }
    }

    // The per-session gate. Presence means a verdict is being formed for this session right now.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), Flight> _inFlight = new();

    // How many judgements each account has in flight, against its ceiling.
    private readonly ConcurrentDictionary<TenantId, StrongBox<int>> _tenantLoad = new();

    // Bumped on every Working transition. A store lands only under the epoch it started in.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), long> _epochs = new();

    // Sessions whose verdict is being read right now - the "reading" state slice D paints.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _reading = new();

    // The moment the detector last observed a stop for each session - the join key a verdict carries.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), DateTime> _lastObserved = new();

    // Sessions known to have no stored verdict in this process, so a Working edge costs no database round
    // trip for them. Cleared the moment a verdict is stored; a session not in it is invalidated for real.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _knownEmpty = new();

    private readonly object _storeGate = new();

    /// <summary>Test seam: runs, with the session id, the moment an ending judgement has closed its joined list and
    /// before it writes a single joined row - the point issue #2905's two windows opened at. Null in production.</summary>
    internal Action<string>? OnJoinedListClosedForTests;

    /// <summary>Test seam: runs, with the session id, the moment an ending judgement has left the gate - replaced by its
    /// successor or removed - and before it completes <see cref="Flight.Done"/>. Null in production.</summary>
    internal Action<string>? OnLeftGateForTests;

    /// <summary>Test seam: runs, with the session id, inside admission - after a request has found the service not
    /// disposed and before it is registered on the gate (issue #2905, round 3). Null in production.</summary>
    internal Action<string>? OnAdmissionCheckedForTests;

    // ADMISSION AND DISPOSAL ARE ONE STEP EACH, UNDER THIS LOCK (issue #2905, round 3). A request reads _disposed and
    // registers itself - takes the gate, joins the judgement holding it, or queues behind it - without releasing this
    // lock, and Dispose flips _disposed under it. So a request is either registered before disposal, where the shutdown
    // drain finds it, or refused after it and recorded as cancelled at shutdown. Nothing is awaited inside it.
    private readonly object _admission = new();

    private long _capSkips;
    private volatile bool _disposed;

    public TurnVerdictService(ITurnVerdictEnvironment environment)
        => _env = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>
    /// Raised once for every verdict flight that ends with an outcome, after the outcome is stored and every joined
    /// caller is released - whatever started the flight. A reader that must hear of EVERY reading (the Fleet
    /// Manager's events, step 4) listens here rather than on one trigger's entry point, so a stop read by a snooze
    /// expiry is heard exactly as one read at its turn end. A handler that throws is logged and never reaches the
    /// flight. Not raised for a stop that JOINED a flight (that flight raises it) nor for a flight admitted after
    /// shutdown began.
    /// </summary>
    public event Action<TurnVerdictReadingCompleted>? ReadingCompleted;

    /// <summary>How many stops have been stood down by an account's in-flight ceiling since start.</summary>
    public long CapSkips => Interlocked.Read(ref _capSkips);

    /// <summary>True while a verdict is being formed for this session.</summary>
    public bool IsReading(TenantId tenant, string sessionId) => _reading.ContainsKey((tenant, sessionId));

    /// <summary>Is a live owning session holding this session? The one held check every narration caller asks,
    /// resolved against the whole roster. HELD FOR NARRATION: true for a session the account's Fleet Manager holds
    /// too, so the Wingman never reads it aloud to the owner. The automatic judgement asks
    /// <see cref="TurnVerdictSessionState.HeldForJudging"/> instead.</summary>
    public bool IsHeld(TenantId tenant, string sessionId) => _env.ReadSessionState(tenant, sessionId).Held;

    /// <summary>This session's latest stored verdict, or null.</summary>
    public TurnVerdictDto? Latest(TenantId tenant, string sessionId) => _env.Latest(tenant, sessionId);

    /// <summary>
    /// A stop was observed. Fire-and-forget: the gate is taken synchronously, before this returns, so a voice
    /// narration started right after this call joins the same judgement instead of asking a second time.
    /// </summary>
    public void OnTurnEnd(TurnEndSignal signal) => _ = StartTurnEnd(signal);

    /// <summary>
    /// The awaited form of <see cref="OnTurnEnd"/>, for tests and for callers that want the outcome. A stop
    /// for a session already being judged comes back skipped with <see cref="ActivityCauses.AlreadyJudging"/>.
    /// </summary>
    public Task<TurnVerdictOutcome> StartTurnEnd(TurnEndSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        // Not a stop the seat can attribute to an account or a session, so there is nothing to trace it under.
        if (!signal.Tenant.IsValid || string.IsNullOrEmpty(signal.SessionId))
            return Task.FromResult(new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Skipped, SkipCause = ActivityCauses.Unknown });

        var key = (signal.Tenant, signal.SessionId);
        _lastObserved[key] = signal.ObservedAtUtc;
        // A NEW TURN ENDS THE PREVIOUS STOP'S RATE LIMIT WAIT, exactly as an observed Working event does: the wait was
        // named for that stop, and a new stop on an unreadable screen with the same reply text is still a new stop
        // (slice I inspection, round three).
        if (signal.IsNewTurn) ClearRateLimitHold(key);
        // The gate is still taken synchronously inside TakeGateOrJoin, before this returns; only the narration for the
        // user waits on the outcome.
        //
        // NOTHING IS STARTED AFTER THE JUDGEMENT ANY MORE. A wrapper here used to fire the narration call for a
        // session that answers to the user, detached, once the outcome was in hand. Contract v3 makes the
        // narration part of the reading itself, so by the time this returns the words are already on the record
        // or were never coming. Leaving the wrapper in place was not merely redundant: it saw an empty Narration
        // on a reading whose call had FAILED, claimed it, and made a second call - an automatic re-attempt, which
        // is exactly what the rule beside TryClaimNarration says no automatic path may do.
        return TakeGateOrJoin(key, signal);
    }

    private enum Admitted { Refused, TookGate, Joined, Queued }

    /// <summary>
    /// ONE STEP: the shutdown check and the registration, under <see cref="_admission"/>. Refused when the service is
    /// disposed; otherwise registered - the gate taken with <paramref name="flight"/>, or this stop joined to the judgement
    /// holding it, or queued behind that judgement. A judgement found already leaving the gate is looked past; it replaces
    /// or removes its entry in the same step it marks itself gone, so the next look finds the gate changed.
    /// </summary>
    private Admitted Admit((TenantId Tenant, string SessionId) key, Flight flight, TurnEndSignal? signal, out QueuedStop? queued,
        out Task<TurnVerdictOutcome>? coverage, out Flight? joinedFlight)
    {
        queued = null;
        coverage = null;
        joinedFlight = null;
        lock (_admission)
        {
            if (_disposed) return Admitted.Refused;
            OnAdmissionCheckedForTests?.Invoke(key.SessionId);
            while (true)
            {
                if (_inFlight.TryAdd(key, flight)) return Admitted.TookGate;
                if (!_inFlight.TryGetValue(key, out var running)) continue;
                // A request that is not a stop (the voice path, the sweep, a snooze expiry) does not attach to the line. It
                // is handed the completion that will carry the verdict for the current screen - never the Done of the
                // flight it found - and decides for itself what to do with it.
                if (signal is null)
                {
                    coverage = running.Coverage();
                    if (coverage is null) continue;
                    joinedFlight = running;
                    return Admitted.Joined;
                }
                switch (running.JoinOrQueue(signal, out queued))
                {
                    case Attach.Joined: return Admitted.Joined;
                    case Attach.Queued: return Admitted.Queued;
                    default: continue;
                }
            }
        }
    }

    /// <summary>
    /// Take this session's gate, or join the judgement that holds it. On the ordinary path both happen synchronously,
    /// before this returns - the property <see cref="OnTurnEnd"/> promises a voice narration started right after it.
    ///
    /// THE ONE PATH THAT WAITS (issue #2905): the judgement holding the gate has closed its joined list and is writing
    /// what joined it. It stays on the gate until those rows are written - that is what keeps a shutdown wait covering
    /// them and keeps a newer judgement for this session from handing in a row first - so this stop can neither join
    /// it nor take the gate yet. It is QUEUED on that judgement, in arrival order, and the judgement hands the gate to
    /// the head of its line as it leaves, in the same step (see <see cref="LeaveGate"/>). A queued stop is registered
    /// the whole time, so the shutdown drain waits for it, and no later stop can take the gate in front of it. What this
    /// path does not do is stamp the stop "reading" before returning: it has no gate to pair the stamp with until the
    /// hand-over.
    /// </summary>
    private Task<TurnVerdictOutcome> TakeGateOrJoin((TenantId Tenant, string SessionId) key, TurnEndSignal signal)
    {
        var flight = new Flight();
        switch (Admit(key, flight, signal, out var queued, out _, out _))
        {
            case Admitted.Refused:
                // Observed, and refused because shutdown has begun: recorded through the one door like every other stop.
                flight.Cts.Dispose();
                return Task.FromResult(CancelledAtShutdown(key, signal, settings: null));
            case Admitted.Joined:
                // A JUDGEMENT IS ALREADY RUNNING FOR THIS SESSION. This stop asks nothing of its own; that judgement
                // records it when it ends.
                flight.Cts.Dispose();
                return Task.FromResult(JoinedOutcome(signal));
            case Admitted.Queued:
                flight.Cts.Dispose();
                FileLog.Write($"[TurnVerdictService] OnTurnEnd: sid={signal.SessionId} arrived as the judgement it would join was ending; queued behind it");
                return queued!.Result.Task;
            default:
                return StartAdmittedTurnEnd(key, flight, signal);
        }
    }

    /// <summary>
    /// A turn end that holds the gate: the shutdown check, the free checks on the caller's thread, and the flight. Reached
    /// by a stop that took the gate itself, and by the head of a line the gate was handed to.
    /// </summary>
    private Task<TurnVerdictOutcome> StartAdmittedTurnEnd((TenantId Tenant, string SessionId) key, Flight flight, TurnEndSignal signal)
    {
        // SHUTDOWN BEGAN AFTER THIS STOP WAS REGISTERED. Admission is one step with disposal, so the drain is already
        // waiting for this flight; nothing more is read or asked. The stop was observed before shutdown, so it is
        // recorded as cancelled at shutdown - under the settings of the flight that handed it the gate, when there was
        // one that read them - and anybody who joined or queued in the meantime is released the same way.
        if (_disposed)
            return Task.FromResult(StandDownAfterDispose(key, flight, signal.DirectorId, signal));

        FileLog.Write($"[TurnVerdictService] OnTurnEnd: sid={signal.SessionId} tenant={signal.Tenant.ToLogString()} newTurn={signal.IsNewTurn}");

        // NO RED FRAME BEFORE THE WINGMAN READS (owner ruling, 2026-09-15). The free checks run HERE, synchronously, on
        // the caller's thread, so a stop that WILL be judged is stamped "reading" before this returns: before the settle
        // wait, before the screen read, and before the fold the caller runs next pushes the stop. A stop that will not
        // be judged - held, not live, brand-new, exited, working, the judge switch off, the account's ceiling reached -
        // is not stamped and shows the detector's red, as before. The flight clears the stamp on every exit.
        TurnVerdictSessionState? firstState = null;
        try
        {
            firstState = _env.ReadSessionState(signal.Tenant, signal.SessionId);
            if (WillJudgeAutomatic(signal.Tenant, signal.SessionId, firstState, TurnVerdictTrigger.TurnEnd))
                _reading[key] = 1;
        }
        catch (Exception ex)
        {
            // Not swallowed into a verdict: the flight reads the roster again inside its own boundary, where a fault
            // becomes a stored failed record and a ledger event, exactly as it did before this check moved here.
            firstState = null;
            FileLog.Write($"[TurnVerdictService] OnTurnEnd: the free checks FAILED on the caller's thread, sid={signal.SessionId}: {ex.GetType().FullName}: {ex.Message}");
        }

        var state = firstState;
        return Task.Run(() => RunFlightAsync(key, flight, signal.DirectorId, signal.ObservedAtUtc,
            TurnVerdictTrigger.TurnEnd, screenReader: null, firstState: state));
    }

    /// <summary>
    /// A SNOOZE EXPIRY ASKS FOR A VERDICT (the Wingman-on-every-turn mission, slice F, ruling 10). The owner's
    /// quiet ran out over a stop nothing had judged, so the judge is asked about the session's current screen.
    ///
    /// IT ANSWERS SYNCHRONOUSLY WHETHER THE WINGMAN IS NOW READING, and that answer is the whole reason this is
    /// its own entry point rather than a bare call to <see cref="VerdictForCurrentScreenAsync"/>. It is called
    /// from inside the fold, and the fold is about to serve the very row it is asking about: with no answer it
    /// serves that row RED and the yellow only appears on some later poll, which is the red frame the owner ruled
    /// out in slice E. So the free checks run HERE, on the fold's own thread, before the settle, before the screen
    /// read - exactly as <see cref="StartTurnEnd"/> does it - and a stop that will be judged is stamped "reading"
    /// before this returns.
    ///
    /// THE GATE IS TAKEN BEFORE THE STAMP, so the stamp is always paired with a flight that clears it. A stamp
    /// made without the gate could be left behind by a judgement that had already ended, and the row would sit
    /// yellow for the life of the process with nothing reading it.
    ///
    /// THE JUDGEMENT ITSELF IS FIRE AND FORGET. The fold is the hot path and waits for no model call.
    /// </summary>
    /// <returns>True when the Wingman is now reading this session - the caller may show the row yellow. False
    /// when this stop will not be judged at all (the judge switch is off, the ceiling is full, the free checks
    /// refuse it, or a judgement was already in flight and this request stood aside), and then the row keeps
    /// whatever the rest of the fold made it.</returns>
    public bool StartSnoozeExpiryReJudge(TenantId tenant, string directorId, string sessionId)
    {
        if (_disposed || !tenant.IsValid || string.IsNullOrEmpty(sessionId)) return false;

        var key = (tenant, sessionId);
        var flight = new Flight();
        switch (Admit(key, flight, signal: null, out _, out _, out _))
        {
            case Admitted.Refused:
                flight.Cts.Dispose();
                return false;
            case Admitted.Joined:
                // A JUDGEMENT IS ALREADY BEING FORMED for this session, which is the answer this expiry wanted: one
                // model call per stop, and it is already being paid for. Whether the row shows yellow is that
                // judgement's to say, so this reports what IT has stamped rather than stamping anything itself.
                flight.Cts.Dispose();
                return IsReading(tenant, sessionId);
        }

        // Registered before disposal, and shutdown began since - see StartAdmittedTurnEnd. Not a stop, so no trace.
        if (_disposed)
        {
            StandDownAfterDispose(key, flight, directorId, stop: null);
            return false;
        }

        TurnVerdictSessionState? firstState = null;
        var reading = false;
        try
        {
            firstState = _env.ReadSessionState(tenant, sessionId);
            reading = WillJudgeAutomatic(tenant, sessionId, firstState, TurnVerdictTrigger.SnoozeExpiry);
            if (reading) _reading[key] = 1;
        }
        catch (Exception ex)
        {
            // Not swallowed into a verdict: the flight reads the roster again inside its own boundary, where a
            // fault becomes a stored failed record and a ledger event.
            firstState = null;
            FileLog.Write($"[TurnVerdictService] StartSnoozeExpiryReJudge: the free checks FAILED on the caller's thread, sid={sessionId}: {ex.GetType().FullName}: {ex.Message}");
        }

        var observedAt = _lastObserved.TryGetValue(key, out var seen) ? seen : _env.NowUtc();
        var state = firstState;
        var judgement = Task.Run(() => RunFlightAsync(key, flight, directorId, observedAt,
            TurnVerdictTrigger.SnoozeExpiry, screenReader: null, firstState: state));
        // The fold does not wait for the judgement, but a fault must not go unread: the flight answers every
        // ordinary failure with a stored record, so anything reaching here is the boundary itself failing.
        _ = judgement.ContinueWith(
            t => FileLog.Write($"[TurnVerdictService] the snooze-expiry judgement FAULTED: sid={sessionId}: " +
                               $"{t.Exception?.GetBaseException().GetType().FullName}: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
        return reading;
    }

    /// <summary>
    /// The verdict for this session's CURRENT screen, for a caller that needs its words now - a voice session's
    /// narration, the idle sweep, a person pressing explain.
    ///
    /// ONE MODEL CALL PER STOP. A judgement already in flight for this session is JOINED, not repeated: its
    /// screen was read after the settle delay, which is the read this stop is judged on, so reading again here
    /// first would risk seeing an earlier repaint and asking twice. With nothing in flight the screen is read
    /// once; an unchanged screen returns the stored verdict; only a changed one asks the judge.
    /// </summary>
    /// <param name="screenReader">The caller's own screen read, when it already holds the route (the voice
    /// path) or has already read the screen (the sweep). Null uses the environment's tunnel read.</param>
    public async Task<TurnVerdictOutcome> VerdictForCurrentScreenAsync(
        TenantId tenant,
        string directorId,
        string sessionId,
        TurnVerdictTrigger trigger,
        Func<CancellationToken, Task<ScreenGridResponse?>>? screenReader = null,
        CancellationToken ct = default)
    {
        if (!tenant.IsValid) throw new ArgumentException("A verdict needs a valid tenant.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session id is required.", nameof(sessionId));
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = (tenant, sessionId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var flight = new Flight();
            switch (Admit(key, flight, signal: null, out _, out var coverage, out var joinedFlight))
            {
                case Admitted.Refused:
                    flight.Cts.Dispose();
                    throw new ObjectDisposedException(nameof(TurnVerdictService));
                case Admitted.Joined:
                {
                    flight.Cts.Dispose();
                    // Somebody is listening to the running flight now, whatever started it - provided it has not already
                    // decided its attempts. An explain that arrives after that decision is not served by the flight: it
                    // waits for the covering judgement to end and then runs its own request (slice I inspection, rounds
                    // one and two).
                    var lateExplain = trigger == TurnVerdictTrigger.OnDemand && !joinedFlight!.TryServeOnDemand();
                    // THE VERDICT OF THE JUDGEMENT THAT COVERS THE CURRENT SCREEN (issue #2905, round 3): the running
                    // judgement's own result while its list is open, or - once it is ending with a stop queued behind it -
                    // that newest queued stop's own completion, which its successor sets. Never the ending judgement's
                    // result for a stop it did not judge.
                    var joined = await coverage!.WaitAsync(ct).ConfigureAwait(false);
                    if (lateExplain)
                    {
                        FileLog.Write($"[TurnVerdictService] sid={sessionId}: an explain arrived after the running flight decided its attempts - it runs its own request");
                        continue;
                    }
                    // A turn-end judgement that stood down for a reason that binds only the turn-end path - this
                    // account's ceiling, or its judge switch - is not an answer for somebody who is listening.
                    if (!(joined.Kind == TurnVerdictOutcomeKind.Skipped
                          && joined.SkipCause is ActivityCauses.InFlightCap or ActivityCauses.JudgeSwitchOff))
                        return joined;
                    continue;
                }
            }

            // Registered before disposal, and shutdown began since - see StartAdmittedTurnEnd. Not a stop, so no trace.
            if (_disposed)
            {
                StandDownAfterDispose(key, flight, directorId, stop: null);
                throw new ObjectDisposedException(nameof(TurnVerdictService));
            }

            var observedAt = _lastObserved.TryGetValue(key, out var seen) ? seen : _env.NowUtc();
            return await RunFlightAsync(key, flight, directorId, observedAt, trigger, screenReader, firstState: null).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"[TurnVerdictService] could not take or join the verdict gate for sid={sessionId} after three tries; "
            + "something is holding and releasing it in a tight loop.");
    }

    /// <summary>
    /// The session is working again. Whatever verdict it carried described a screen that is gone: the
    /// in-flight judgement is cancelled, the reading state cleared, and the stored verdict invalidated - so
    /// the row goes back to exactly what the detector says.
    /// </summary>
    public void OnSessionWorking(TenantId tenant, string sessionId)
    {
        if (_disposed || !tenant.IsValid || string.IsNullOrEmpty(sessionId)) return;
        var key = (tenant, sessionId);

        lock (_storeGate)
        {
            _epochs.AddOrUpdate(key, 1, (_, epoch) => epoch + 1);
            if (!_knownEmpty.ContainsKey(key))
            {
                var removed = _env.Invalidate(tenant, sessionId);
                _knownEmpty[key] = 1;
                if (removed > 0)
                    FileLog.Write($"[TurnVerdictService] OnSessionWorking: sid={sessionId} tenant={tenant.ToLogString()} invalidated {removed} stored verdict(s)");
            }
        }

        _reading.TryRemove(key, out _);
        ClearRateLimitHold(key);
        // The last observed stop is over too. A verdict request that starts before the detector observes the NEXT
        // stop carries the moment it started, and takes the observed moment when the detector catches up.
        _lastObserved.TryRemove(key, out _);
        if (_inFlight.TryGetValue(key, out var flight))
        {
            try { flight.Cts.Cancel(); }
            catch (ObjectDisposedException) { /* the flight already finished and disposed it */ }
        }
    }

    private async Task<TurnVerdictOutcome> RunFlightAsync(
        (TenantId Tenant, string SessionId) key,
        Flight flight,
        string directorId,
        DateTime observedAt,
        TurnVerdictTrigger trigger,
        Func<CancellationToken, Task<ScreenGridResponse?>>? screenReader,
        TurnVerdictSessionState? firstState)
    {
        // THE EPOCH THIS FLIGHT STANDS ON, captured before anything is read - before the roster, the screen and
        // the stored verdict. Every store this flight makes, on any arm, lands only while it is still current.
        var epoch = _epochs.GetOrAdd(key, 0);
        flight.ObservedAt = observedAt;
        flight.HoldGeneration = HoldGeneration(key);
        TurnVerdictOutcome? outcome = null;
        try
        {
            try
            {
                outcome = await JudgeAsync(key, flight, epoch, directorId, observedAt, trigger, screenReader, firstState).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (flight.Cts.IsCancellationRequested)
            {
                outcome = Cancelled(key.Tenant, directorId, key.SessionId, trigger, "the session started working while its verdict was being formed", flight);
            }
            catch (Exception ex)
            {
                // The flight's boundary: it runs on the thread pool for a turn-end callback that has already
                // returned, so nothing above it can catch. Logged loud, and answered like every other failure - a
                // stored failed record and a ledger event - never as calm and never silently.
                FileLog.Write($"[TurnVerdictService] verdict FAILED: sid={key.SessionId} tenant={key.Tenant.ToLogString()} trigger={trigger}: {ex.GetType().FullName}: {ex.Message}");
                outcome = FailedAtBoundary(key, flight, epoch, directorId, observedAt, trigger, ex);
            }
            finally
            {
                // EVERY EXIT CLEARS READING - a skip after the settle wait, a reuse, a cancel, a failure - and it is cleared
                // BEFORE the gate is released, so it can never clear the stamp of the next flight, which is set only after
                // that flight takes the gate.
                _reading.TryRemove(key, out _);
                // THE JOINED STOPS ARE WRITTEN WHILE THIS FLIGHT STILL HOLDS THE GATE, then the gate is handed to the next
                // stop in line or released, then Done completes in the outer finally (issue #2905).
                LeaveGate(key, flight, directorId, stoodDown: false);
            }
        }
        finally
        {
            OnLeftGateForTests?.Invoke(key.SessionId);
            // EVERY CALLER JOINED TO THIS FLIGHT IS RELEASED, whatever happened above. If a handler itself threw, the
            // joined callers receive that fault instead of waiting on a result that will never be set.
            if (outcome is null)
                flight.Outcome.TrySetException(new InvalidOperationException(
                    $"[TurnVerdictService] the verdict flight for sid={key.SessionId} ended without an outcome; see the log for the fault"));
            else
                flight.Outcome.TrySetResult(outcome);
            flight.Done.TrySetResult();
            flight.Cts.Dispose();
        }

        RaiseReadingCompleted(new TurnVerdictReadingCompleted(key.Tenant, key.SessionId, directorId, trigger,
            observedAt, outcome!));

        // Reaching here means an arm above assigned the outcome; a handler that threw left through the finally instead.
        return outcome!;
    }

    /// <summary>
    /// The failure an exception nobody expected leaves behind: a failed record whose reason is the exception's
    /// type (its message goes to the log only, because a message can quote a screen), stored under the flight's
    /// epoch, and a ledger event under <see cref="ActivityCauses.JudgeUnavailable"/>. When the session has
    /// worked since the flight began nothing is stored and the request is cancelled, exactly as on every other
    /// arm. The boundary itself must not throw, so a store or ledger write that throws here is logged.
    /// </summary>
    private TurnVerdictOutcome FailedAtBoundary(
        (TenantId Tenant, string SessionId) key, Flight flight, long epoch, string directorId, DateTime observedAt,
        TurnVerdictTrigger trigger, Exception ex)
    {
        var (tenant, sid) = key;
        var reason = "the verdict could not be formed: " + ex.GetType().FullName;
        var record = new TurnVerdictDto
        {
            VerdictId = Guid.NewGuid().ToString("N"),
            TurnEndObservedAtUtc = observedAt,
            ScreenHash = "",
            ContractVersion = TurnVerdictContract.Version,
            Failed = true,
            FailureReason = reason,
        };
        try
        {
            record.JudgedAtUtc = _env.NowUtc();
            record.Model = _env.JudgeModel(tenant);
            record.FailureKind = WingmanFailureKinds.Unavailable;
            StampRetrySchedule(record, _env.Latest(tenant, sid), trigger, providerWait: null);
            if (!StoreIfCurrent(key, epoch, record))
                return Cancelled(tenant, directorId, sid, trigger, "the session worked before the failed record could be stored", flight);
        }
        catch (Exception storeEx)
        {
            FileLog.Write($"[TurnVerdictService] the failed record could NOT be stored either: sid={sid} tenant={tenant.ToLogString()}: {storeEx.GetType().FullName}: {storeEx.Message}");
        }

        // The evidence the flight had gathered before the exception - when the judge had already answered and it was the
        // store that threw, that is the prompt and the whole answer, which is exactly what is needed to see what was lost.
        // The exception's type is the cause; its message stays in the log, as the record's does. Under the settings the
        // flight read when it started - and when the exception came before that read, a STOP is still owed its row, so the
        // door counts it as lost; a request that is not a stop never reached the judge and owes nothing.
        if (flight.Settings is not null || trigger == TurnVerdictTrigger.TurnEnd)
            TraceStop(tenant, sid, TriggerWord(trigger), TurnVerdictTraceOutcomes.Unavailable, observedAt, flight.Settings,
                colour => WithEvidence(NewTrace(sid, directorId, trigger, TurnVerdictTraceOutcomes.Unavailable, record, colour), flight)
                    with { Cause = ex.GetType().Name });

        try
        {
            _env.Record(new TurnVerdictRecord(tenant, directorId ?? "", sid, ActivityEventTypes.TurnVerdictFailed,
                ActivityCauses.JudgeUnavailable,
                $"trigger={TriggerWord(trigger)} id={record.VerdictId} model={record.Model} exception={ex.GetType().Name}"));
        }
        catch (Exception recordEx)
        {
            FileLog.Write($"[TurnVerdictService] the ledger event for a failed verdict could NOT be written: sid={sid}: {recordEx.GetType().FullName}: {recordEx.Message}");
        }

        return new TurnVerdictOutcome
        {
            Kind = TurnVerdictOutcomeKind.Failed,
            Verdict = record,
            Failure = TurnVerdictFailureKind.Unavailable,
            FailureDetail = reason,
        };
    }

    private async Task<TurnVerdictOutcome> JudgeAsync(
        (TenantId Tenant, string SessionId) key,
        Flight flight,
        long epoch,
        string directorId,
        DateTime observedAt,
        TurnVerdictTrigger trigger,
        Func<CancellationToken, Task<ScreenGridResponse?>>? screenReader,
        TurnVerdictSessionState? firstState)
    {
        var (tenant, sid) = key;
        var ct = flight.Cts.Token;
        var settings = _env.Settings(tenant);
        flight.Settings = settings;
        var automatic = trigger != TurnVerdictTrigger.OnDemand;
        if (trigger == TurnVerdictTrigger.OnDemand) flight.TryServeOnDemand();

        // THE BOUNDARY, IN THE ORDER THE CODE RUNS IT. A read means the session's screen or its stored
        // conversation; the pushed roster and the verdict store are consulted as well and are not counted.
        // 1. The checks that read neither: held, live, not brand new, not exited, not working; then the
        //    judge switch, checked ONCE in the flight, before the settle wait. Then, for a turn end, the
        //    settle wait. Then, for an automatic request, the held, live, brand-new, exited and working
        //    checks again - but NOT the switch, so a switch turned off during a flight does not stop that
        //    flight. A stop refused here costs NO reads.
        // 2. The screen read, and its one full-grid hash.
        // 3. The reuse check: a stored verdict formed on the same hash is reused and the judge is not
        //    asked. A stop answered here costs ONE read. The one exception is a rate limit's named wait:
        //    while one is held for the session, this check reads the stored conversation as well, to tell
        //    whether the reply is still the stop the wait was named for, and a stop answered by that wait
        //    has read both.
        // 4. The conversation read.
        // 5. The account ceiling. A stop refused here costs TWO reads.
        // 6. The model call.
        //
        // The line above used to read "the free checks: nothing is read and nothing is paid for until every one of
        // them passes". That was false - steps 3 and 5 both come after the screen read - and it is recorded here
        // because the charter and the specification had copied it. The same block, word for word, is in
        // docs/wingman/WINGMAN.md section 3b and docs/architecture/wingman/TURN_VERDICT.md section 6.
        //
        // ONE SNAPSHOT for the role and the facts, so the two cannot describe different moments. HELD FIRST: a
        // session a live owning session holds is not the owner's to be read, so its screen is never read and no
        // model is asked about it - unless the account's Fleet Manager holds it and this is a turn end or a snooze
        // expiry, when it is judged and stored (see SessionStateSkipCause).
        // A turn end hands in the snapshot its synchronous check already took, so the roster is read once for that
        // check and not twice.
        var state = firstState ?? _env.ReadSessionState(tenant, sid);
        if (SessionStateSkipCause(state, trigger) is { } cause)
            return Skip(tenant, directorId, sid, trigger, cause, settings, observedAt);
        var facts = state.Facts;
        // THE JUDGE SWITCH binds the two triggers nobody is waiting on: the detector's turn end, and a snooze
        // expiry with a stop nothing has judged. A voice session is the standing exception - somebody is
        // listening to it - and a person's own request is not automatic at all.
        if (trigger is TurnVerdictTrigger.TurnEnd or TurnVerdictTrigger.SnoozeExpiry or TurnVerdictTrigger.Retry
            && !settings.JudgeEnabled && !_env.IsVoiceSession(tenant, sid))
            return Skip(tenant, directorId, sid, trigger, ActivityCauses.JudgeSwitchOff, settings, observedAt);

        if (trigger == TurnVerdictTrigger.TurnEnd && settings.SettleMs > 0)
            await _env.DelayAsync(TimeSpan.FromMilliseconds(settings.SettleMs), ct).ConfigureAwait(false);

        // RESOLVED AGAIN, after the settle wait and immediately before the read. A session can become held, or
        // start working, while this request waits; the first answer does not license a read made later.
        if (automatic)
        {
            state = _env.ReadSessionState(tenant, sid);
            if (SessionStateSkipCause(state, trigger) is { } lateCause)
                return Skip(tenant, directorId, sid, trigger, lateCause, settings, observedAt);
            facts = state.Facts;
        }

        // ---- ONE screen read, and its full-grid hash ----
        var grid = await ReadScreenAsync(tenant, directorId, sid, screenReader, ct).ConfigureAwait(false);
        var rows = grid is { HasGrid: true, Rows.Count: > 0 } ? (IReadOnlyList<string>)grid.Rows : Array.Empty<string>();
        var hash = rows.Count == 0 ? "" : WingmanScreenVerdictCache.HashRows(rows);

        // ---- reuse: the screen the stored verdict was formed on ----
        var latest = _env.Latest(tenant, sid);
        // AN UNREADABLE SCREEN IS NEVER REUSED, except by the sweep. Every unreadable screen hashes to "", so two
        // different stops on a Director that cannot be reached would look like one screen, and the second would
        // be played the first one's words. The sweep is the exception because it comes past every pass, and
        // re-asking the judge about an unreachable session each time would be a paid call on a loop.
        if (latest is not null && IsReusable(key, latest, hash, trigger,
                () => WingmanNarrationSource.Select(_env.ReadConversation(tenant, sid)?.Widgets, rows)?.Content))
            return Reuse(key, epoch, ct, directorId, trigger, observedAt, latest, hash, rows, settings, facts, grid);

        // The source this stop is judged from, chosen ONCE, over this one screen read.
        var conversation = _env.ReadConversation(tenant, sid)
                           ?? new StoredConversation(false, Array.Empty<TurnWidgetDto>());
        var source = WingmanNarrationSource.Select(conversation.Widgets, rows);

        // ---- the account's ceiling ----
        var capped = trigger is TurnVerdictTrigger.TurnEnd or TurnVerdictTrigger.Sweep or TurnVerdictTrigger.SnoozeExpiry
            or TurnVerdictTrigger.Retry;
        var load = _tenantLoad.GetOrAdd(tenant, _ => new StrongBox<int>());
        if (capped && Interlocked.Increment(ref load.Value) > settings.MaxInFlight)
        {
            Interlocked.Decrement(ref load.Value);
            Interlocked.Increment(ref _capSkips);
            return Skip(tenant, directorId, sid, trigger, ActivityCauses.InFlightCap, settings, observedAt);
        }

        _reading[key] = 1;
        try
        {
            var signal = new TurnEndSignal(sid, directorId, tenant, observedAt, IsNewTurn: trigger == TurnVerdictTrigger.TurnEnd);
            // The owner's own sessions (owner ruling, 2026-09-15), counted by the same crew summary the row prints.
            var owned = _env.OwnedSessions(tenant, sid);
            var package = TurnVerdictPackageBuilder.Build(
                signal,
                facts ?? new SessionDto { SessionId = sid },
                conversation,
                grid,
                latest is { Failed: false } ? latest.Label : null,
                owned is null ? null : new OwnedSessionCounts(owned.Working, owned.Stopped, owned.NeedYou));
            // The judge is asked the contract's own question and nothing else from contract v3: it answers no
            // prose a person hears, so the account's language and its own narration instructions belong to the
            // narration call below, which takes both.
            var prompt = TurnVerdictPrompt.BuildVerdictPrompt(package);
            flight.Package = package;
            flight.Prompt = prompt;

            TurnVerdictDto record;
            var failure = TurnVerdictFailureKind.None;
            TimeSpan? retryAfter = null;
            string? detail = null;
            double replySeconds = 0;
            string? rawReply = null;
            // ONE CALL PER STOP, WITH ONE EXCEPTION (owner ruling, 2026-09-16, slice I). A stop somebody is listening
            // to - a voice session, or a person who pressed explain - gets exactly ONE re-attempt, with the wider
            // deadline, when the first call got no usable words at all: the judge did not answer, or its answer was
            // not a JSON object. Those are the two failures that leave voice with nothing to say. A refusal of
            // READABLE JSON keeps its spoken text (TurnVerdictContract), a rate limit names its own wait, and every
            // stop nobody is listening to keeps the one-call rule.
            var timeout = TimeSpan.FromSeconds(settings.JudgeTimeoutSeconds);
            for (var attempt = 1; ; attempt++)
            {
                failure = TurnVerdictFailureKind.None;
                retryAfter = null;
                detail = null;
                var answerWasReadable = false;
                try
                {
                    var answer = await _env.AskJudgeAsync(tenant, prompt, timeout, ct).ConfigureAwait(false);
                    replySeconds = answer.ReplySeconds;
                    rawReply = answer.Raw;
                    flight.RawReply = answer.Raw;
                    flight.ReplySeconds = answer.ReplySeconds;
                    record = TurnVerdictContract.ParseAndValidate(answer.Raw, package, answer.Model, observedAt);
                    // The carrying-on clock's first source travels on the stored record, so it survives a restart.
                    record.NextScheduledWakeUtc = package.NextScheduledWakeUtc;
                    // THE SCREEN DECIDES WHETHER THERE IS A PICKER (issue 2976). A keys answer the read screen does
                    // not support is corrected to a reply here, before the record is stored, so nothing downstream -
                    // the row's buttons, the narration's menu shape - is built on a menu the judge invented.
                    if (InventedMenuCheck.Correct(record, rows))
                        FileLog.Write($"[TurnVerdictService] invented menu corrected: sid={sid} id={record.VerdictId} - the judge answered keys and the read screen carries no drawn menu; stored as a reply with no options");
                    answerWasReadable = TurnVerdictContract.IsReadableJsonObject(answer.Raw);
                    if (record.Failed)
                    {
                        failure = TurnVerdictFailureKind.Refused;
                        detail = record.FailureReason;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (WingmanModelRateLimitedException rl)
                {
                    failure = TurnVerdictFailureKind.RateLimited;
                    retryAfter = rl.RetryAfter;
                    detail = rl.Message;
                    record = FailedRecord(package, tenant, observedAt, "the judge was rate limited: " + rl.Message);
                }
                catch (Exception ex) when (ex is TimeoutException or HttpRequestException or OperationCanceledException)
                {
                    failure = TurnVerdictFailureKind.DidNotAnswer;
                    detail = ex.Message;
                    record = FailedRecord(package, tenant, observedAt, "the judge did not answer: " + ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    failure = TurnVerdictFailureKind.Unavailable;
                    detail = ex.Message;
                    record = FailedRecord(package, tenant, observedAt, "the judge could not be asked: " + ex.Message);
                }

                var gotNoWords = failure == TurnVerdictFailureKind.DidNotAnswer
                                 || (failure == TurnVerdictFailureKind.Refused && !answerWasReadable);
                if (!flight.DecideReattempt(attempt < MaxJudgeAttemptsWhenListenedTo && gotNoWords,
                        () => _env.IsVoiceSession(tenant, sid)))
                    break;

                ct.ThrowIfCancellationRequested();
                FileLog.Write($"[TurnVerdictService] sid={sid}: the judge gave no usable words ({failure}: {detail}) and somebody is listening - one re-attempt with a {TurnVerdictSettings.ListenedToReattemptTimeoutSeconds}s deadline");
                timeout = TimeSpan.FromSeconds(TurnVerdictSettings.ListenedToReattemptTimeoutSeconds);
            }

            ct.ThrowIfCancellationRequested();

            // THE JOIN KEY IS THE LATEST OBSERVED MOMENT, found on the rig. The idle sweep began judging a stop ten
            // seconds before the detector's reconcile tick observed it; the tick's turn end was dropped by the gate
            // (rightly - one call per stop), and the stored verdict carried the PREVIOUS stop's moment, so the
            // turn-log record of this stop paired with nothing. A stop observed while this flight was running is
            // the stop this screen belongs to - a Working edge in between would have cancelled the flight - so
            // the verdict carries that later moment.
            if (_lastObserved.TryGetValue(key, out var seenSince) && seenSince > record.TurnEndObservedAtUtc)
                record.TurnEndObservedAtUtc = seenSince;

            // ======================================================================================
            // A READING IS NOT FINISHED UNTIL BOTH CALLS ARE DONE (the owner's ruling on the Wingman redesign
            // report, 18 September 2026). Nothing is stored, nothing is shown, nothing is spoken and no play
            // control appears until the whole reading exists.
            //
            // THIS IS THE SINGLE MOST IMPORTANT CHANGE IN THAT REPORT, and it is one line moved: the narration
            // call is made HERE, before the store, rather than fired off afterwards against a record that was
            // already on the owner's screen. Three separate defects were the same defect - audio that restarted
            // mid-sentence, a row that re-worded itself while he looked at it, and hearing an older turn - and all
            // three were publishing a reading before it was finished.
            //
            // It costs the row nothing it was not already costing: the session is stopped, the row is red, and
            // IsReading keeps saying "being read" for the whole flight instead of for half of it. The flight's
            // token still cancels the call when the session goes back to work, and StoreIfCurrent still refuses a
            // record about a screen that is gone - so a longer reading cannot store a staler answer.
            //
            // A REFUSED JUDGEMENT IS NOT NARRATED. There is nothing to narrate: contract v3 leaves no prose on a
            // refused record, and a reading that could not be read says so rather than half-speaking.
            var narration = record.Failed
                ? new NarrationCallResult(null, null, "", 0)
                : await NarrateForReadingAsync(tenant, sid, record, new Lazy<TurnVerdictPackage>(package), ct)
                    .ConfigureAwait(false);
            if (narration.Spoken is { Length: > 0 } words)
            {
                // ONE TEXT, READ OR HEARD. Summary and Spoken are the same words as the narration from contract v3 -
                // see TurnVerdictDto - so the hundred readers of a "summary" and the voice path both read what the
                // owner is actually shown, and a screen never shows a short version and a long version of one turn.
                record.Narration = words;
                record.Summary = words;
                record.Spoken = words;
            }

            // A NARRATION THAT WAS OWED AND DID NOT COME IS A FAILED READING TO THE PERSON LOOKING AT IT. The judge's
            // answer stands - the row keeps its colour and its label - and the record says the words are missing, so it
            // shows the same tag and goes on the same schedule as any other failure. A narration that was never owed
            // (a session another live session owns) carries no failure detail and is not one.
            if (!record.Failed && narration.Spoken is not { Length: > 0 } && narration.FailureDetail is { Length: > 0 } noWords)
                record.NarrationFailureReason = noWords;
            record.FailureKind = record.Failed ? FailureKindWord(failure)
                : record.NarrationFailureReason is not null ? WingmanFailureKinds.NarrationFailed
                : null;

            // WHERE THIS STOP IS ON ITS RETRY SCHEDULE is written on the failed record itself, before it is stored, so
            // the record a card is rendered from and the record the sweep retries from are one record.
            if (WingmanRetrySchedule.NeedsRetry(record)) StampRetrySchedule(record, latest, trigger, retryAfter);

            if (!StoreIfCurrent(key, epoch, record))
                return Cancelled(tenant, directorId, sid, trigger, "the session worked between the read and the store; the answer describes a screen that is gone", flight);

            // THE INSPECTOR'S RECORD, written for exactly what was stored: the package the judge was given, the
            // prompt it was asked, its answer as received, and how long it took - and the SAME THREE for the
            // narration call, so the debug view can show both halves of one reading. An answer the contract refused
            // is kept with the raw reply that failed - that is the case the record exists for.
            var judgedOutcome = TraceOutcome(record, failure);
            // The row is ABOUT this flight's own stop, whatever later stop the verdict now carries as its join key.
            TraceStop(tenant, sid, TriggerWord(trigger), judgedOutcome, observedAt, settings,
                colour => NewTrace(sid, directorId, trigger, judgedOutcome, record, colour) with
                {
                    ReplySeconds = rawReply is null ? null : replySeconds,
                    Package = package,
                    Prompt = prompt,
                    RawReply = rawReply,
                    NarrationPrompt = narration.Prompt.Length == 0 ? null : narration.Prompt,
                    NarrationRawReply = narration.RawReply,
                    NarrationSeconds = narration.Prompt.Length == 0 ? null : narration.ReplySeconds,
                    NarrationFailureDetail = narration.FailureDetail,
                });

            if (record.Failed)
            {
                if (failure == TurnVerdictFailureKind.RateLimited && retryAfter is { } namedWait)
                    WriteRateLimitHoldIfCurrent(key, flight, (record.VerdictId, _env.NowUtc() + namedWait, source?.Content));
                // The refused answer's own decision, for a narration call about this record - now or on a later reuse.
                var narrationDecision = failure == TurnVerdictFailureKind.Refused
                    ? TurnVerdictContract.SalvageNarrationDecision(rawReply)
                    : null;
                // A refused answer's salvaged decision is checked against the screen too (issue 2976): it reaches the
                // narration call, which would otherwise tell a listener to press a button that is not there.
                if (InventedMenuCheck.Correct(narrationDecision, rows))
                    FileLog.Write($"[TurnVerdictService] invented menu corrected on a refused record's salvaged decision: sid={sid} id={record.VerdictId}");
                if (narrationDecision is not null)
                    _refusedNarrationDecisions[key] = (record.VerdictId, narrationDecision);
                _env.Record(new TurnVerdictRecord(tenant, directorId, sid, ActivityEventTypes.TurnVerdictFailed,
                    FailureCause(failure),
                    $"trigger={TriggerWord(trigger)} kind={record.PackageKind} id={record.VerdictId} model={record.Model}"));
                return new TurnVerdictOutcome
                {
                    Kind = TurnVerdictOutcomeKind.Failed,
                    Verdict = record,
                    Failure = failure,
                    FailureDetail = detail,
                    RetryAfter = retryAfter,
                    ScreenHash = hash,
                    SourceText = source?.Content,
                    ReplySeconds = replySeconds,
                    NarrationPackage = new Lazy<TurnVerdictPackage>(package),
                    NarrationDecision = narrationDecision,
                };
            }

            // The menu cache the send-time guards read (WaitingScreenReader.ConfirmedMenuAsync) is fed from the
            // verdict, under the same full-grid hash, so an unchanged screen is answered without a second call.
            if (rows.Count > 0)
                WingmanScreenVerdictCache.Store($"{tenant}/{sid}", hash, ScreenNeeds(record));

            _env.Record(new TurnVerdictRecord(tenant, directorId, sid, ActivityEventTypes.TurnVerdictJudged,
                ActivityCauses.JudgeAnswered,
                $"trigger={TriggerWord(trigger)} verdict={record.Verdict} confidence={record.Confidence} kind={record.PackageKind} id={record.VerdictId} model={record.Model}"));
            return new TurnVerdictOutcome
            {
                Kind = TurnVerdictOutcomeKind.Judged,
                Verdict = record,
                ScreenHash = hash,
                SourceText = source?.Content,
                ReplySeconds = replySeconds,
                NarrationPackage = new Lazy<TurnVerdictPackage>(package),
            };
        }
        finally
        {
            _reading.TryRemove(key, out _);
            if (capped) Interlocked.Decrement(ref load.Value);
        }
    }

    /// <param name="epoch">The epoch the flight captured before <paramref name="latest"/> was read. A Working edge
    /// after that read bumps it, and then this arm stores nothing and returns nothing: the stored verdict
    /// describes a screen that is gone, and writing it back would restore it over a working session.</param>
    /// <param name="ct">The flight's token, cancelled by that same Working edge.</param>
    private TurnVerdictOutcome Reuse(
        (TenantId Tenant, string SessionId) key,
        long epoch,
        CancellationToken ct,
        string directorId,
        TurnVerdictTrigger trigger,
        DateTime observedAt,
        TurnVerdictDto latest,
        string hash,
        IReadOnlyList<string> rows,
        TurnVerdictSettings settings,
        SessionDto? facts,
        ScreenGridResponse? grid)
    {
        var (tenant, sid) = key;
        var verdict = latest;
        ct.ThrowIfCancellationRequested();

        // A new stop on an unchanged screen is the same verdict about a later moment: refresh the join key so
        // the turn-log record of THIS stop still pairs with a verdict row.
        if (trigger == TurnVerdictTrigger.TurnEnd && !latest.Failed && latest.TurnEndObservedAtUtc != observedAt)
        {
            var refreshed = Copy(latest);
            refreshed.TurnEndObservedAtUtc = observedAt;
            if (!StoreIfCurrent(key, epoch, refreshed))
                return Cancelled(tenant, directorId, sid, trigger, "the session worked after its stored verdict was read; that verdict is not reused",
                settings: settings, observedAt: observedAt);
            verdict = refreshed;
        }

        var conversation = _env.ReadConversation(tenant, sid);
        var source = WingmanNarrationSource.Select(conversation?.Widgets, rows);
        // Built only if the narration call reads it, from this same screen read and conversation: the package the
        // judge would be given for this screen now. Nothing more is read to build it.
        var narrationPackage = new Lazy<TurnVerdictPackage>(() => TurnVerdictPackageBuilder.Build(
            new TurnEndSignal(sid, directorId, tenant, observedAt, IsNewTurn: false),
            facts ?? new SessionDto { SessionId = sid },
            conversation ?? new StoredConversation(false, Array.Empty<TurnWidgetDto>()),
            grid,
            previousVerdictLabel: null));

        if (_epochs.GetOrAdd(key, 0) != epoch)
            return Cancelled(tenant, directorId, sid, trigger, "the session worked after its stored verdict was read; that verdict is not reused",
                settings: settings, observedAt: observedAt);

        // A reuse is a STOP only on the turn-end path. The voice path and the sweep come past an unchanged screen
        // over and over, and a trace for each pass would bury the stops under the passes.
        if (trigger == TurnVerdictTrigger.TurnEnd)
            TraceStop(tenant, sid, TriggerWord(trigger), TurnVerdictTraceOutcomes.Reused, observedAt, settings,
                colour => NewTrace(sid, directorId, trigger, TurnVerdictTraceOutcomes.Reused, verdict, colour));

        _env.Record(new TurnVerdictRecord(tenant, directorId, sid, ActivityEventTypes.TurnVerdictReused,
            ActivityCauses.ScreenUnchanged,
            $"trigger={TriggerWord(trigger)} id={verdict.VerdictId} failed={verdict.Failed}"));

        var holdLeft = RateLimitHoldLeft(key, verdict);
        return verdict.Failed
            ? new TurnVerdictOutcome
            {
                Kind = TurnVerdictOutcomeKind.Failed,
                Verdict = verdict,
                Failure = holdLeft is null ? TurnVerdictFailureKind.Refused : TurnVerdictFailureKind.RateLimited,
                RetryAfter = holdLeft,
                FailureDetail = holdLeft is not null
                    ? "the judge was rate limited about this unchanged screen, and it is not asked again inside the wait it named"
                    : verdict.NextRetryAtUtc is { } nextRetry
                        ? $"the last answer about this unchanged screen failed; retry {verdict.RetriesMade + 1} of {WingmanRetrySchedule.Total} is booked for {nextRetry:u}, and it is not asked before then"
                        : "the last answer about this unchanged screen failed, the retry schedule is used up, and nothing more is scheduled",
                ScreenHash = hash,
                SourceText = source?.Content,
                NarrationPackage = narrationPackage,
                NarrationDecision = _refusedNarrationDecisions.TryGetValue(key, out var salvaged)
                                    && string.Equals(salvaged.VerdictId, verdict.VerdictId, StringComparison.Ordinal)
                    ? salvaged.Decision
                    : null,
            }
            : new TurnVerdictOutcome
            {
                Kind = TurnVerdictOutcomeKind.Reused,
                Verdict = verdict,
                ScreenHash = hash,
                SourceText = source?.Content,
                NarrationPackage = narrationPackage,
            };
    }

    /// <summary>
    /// Make the narration call (slice J) for one stop: the version 10 fidelity prompt over the package the judge was
    /// given, plus the judge's decision, asked once with a sixty second deadline. The CALLER decides whether a stop
    /// gets one - only a voice session or a person's explain, and once per verdict id - and this method only makes it.
    /// A failed call is an answer, not an exception: the judge's spoken text is already playable, so a failure leaves
    /// it in place, and it is logged. Nothing is re-attempted.
    /// </summary>
    /// <param name="outcome">A judged, reused, or refused-but-worded outcome, with its verdict and package.</param>
    internal Task<NarrationCallResult> NarrateAsync(TenantId tenant, string sid, TurnVerdictOutcome outcome, CancellationToken ct)
    {
        if (outcome.Verdict is not { } verdict || outcome.NarrationPackage is not { } lazyPackage)
            throw new ArgumentException("A narration call needs a verdict and the package it was formed from.", nameof(outcome));
        return NarrateAsync(tenant, sid, verdict, lazyPackage, ct, outcome.NarrationDecision);
    }

    /// <summary>The same call, given its verdict and package directly - the form the reading itself uses, before
    /// there is an outcome to carry them in.</summary>
    internal async Task<NarrationCallResult> NarrateAsync(
        TenantId tenant, string sid, TurnVerdictDto verdict, Lazy<TurnVerdictPackage> lazyPackage, CancellationToken ct,
        TurnVerdictDto? salvagedDecision = null)
    {

        // THE PLAN FIRST (owner ruling, 2026-09-17): an account whose plan does not include the Wingman gets the Pro
        // sentence as its narration, with no model call; a plan that could not be read gets nothing, and no call.
        switch (_env.PlanForNarration(tenant))
        {
            case NarrationPlan.NeedsPro:
                FileLog.Write($"[TurnVerdictService] narration call sid={sid} verdict={verdict.VerdictId}: the account's plan does not include the Wingman - the Pro sentence stands in, no model call");
                return new NarrationCallResult(NarrationPlanRule.NeedsProText, null, "", 0);
            case NarrationPlan.Unknown:
                FileLog.Write($"[TurnVerdictService] narration call sid={sid} verdict={verdict.VerdictId} NOT MADE: the account's plan could not be read");
                return new NarrationCallResult(null, "the account's plan could not be read, so the narration call was not made", "", 0);
        }

        // A refused record carries no decision of its own; the call is given the one its readable answer held.
        var decision = verdict.Failed && salvagedDecision is { } salvaged ? salvaged : verdict;
        var prompt = NarrationCall.BuildPrompt(_env.Language(tenant), _env.CustomSpokenRules(), lazyPackage.Value, decision);
        var timeout = TimeSpan.FromSeconds(TurnVerdictSettings.NarrationCallTimeoutSeconds);
        FileLog.Write($"[TurnVerdictService] narration call sid={sid} verdict={verdict.VerdictId} failed={verdict.Failed} answerVia={decision.AnswerVia} salvagedDecision={!ReferenceEquals(decision, verdict)} promptLen={prompt.Length}");
        try
        {
            var answer = await _env.AskNarratorAsync(tenant, prompt, timeout, ct).ConfigureAwait(false);
            var spoken = NarrationCall.SpokenFrom(answer.Raw);
            if (spoken.Length == 0)
            {
                FileLog.Write($"[TurnVerdictService] narration call sid={sid} verdict={verdict.VerdictId} FAILED: the answer held no words");
                return new NarrationCallResult(null, "the narration call answered with no words", prompt, answer.ReplySeconds, answer.Raw);
            }
            FileLog.Write($"[TurnVerdictService] narration call sid={sid} verdict={verdict.VerdictId} OK: spokenLen={spoken.Length} replySeconds={answer.ReplySeconds:F1}");
            return new NarrationCallResult(spoken, null, prompt, answer.ReplySeconds, answer.Raw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or HttpRequestException or OperationCanceledException
                                       or WingmanModelRateLimitedException or InvalidOperationException)
        {
            FileLog.Write($"[TurnVerdictService] narration call sid={sid} verdict={verdict.VerdictId} FAILED ({ex.GetType().Name}): {ex.Message}");
            return new NarrationCallResult(null, ex.Message, prompt, 0);
        }
    }

    /// <summary>
    /// THE NARRATION CALL MADE INSIDE THE READING, so that the reading is atomic (the owner's ruling of 2026-09-18).
    /// Answers the two questions the old deferred path answered separately - IS one owed, and what did it say - and
    /// never throws: a stop that is owed none, or whose call failed, comes back with no words, and the record is
    /// stored without a narration.
    ///
    /// WHO IS OWED ONE: every stop of a session that answers to the USER. A session another LIVE session owns - a
    /// Worker under a Manager - is read by that owner rather than by the user, and gets none, exactly as before.
    /// A voice session is no longer left to the voice path: that path made the same call a second time, against an
    /// already-published record, and that second call is the seam this ruling removes. It now finds the words
    /// already on the record and speaks them.
    ///
    /// IT GETS ONE IMMEDIATE SECOND ATTEMPT, and the reason is that this call changed meaning under it. This leg
    /// had no second attempt at all, deliberately: the idle sweep comes past every forty-five seconds, and spending a model call to
    /// improve on words the reader ALREADY HAD was not worth it. Contract v3 cut the judge's own prose, so these
    /// are now the only words a reading has, and "no automatic re-attempt" silently stopped meaning "you keep the
    /// short version" and started meaning silence.
    ///
    /// ONE, NOT THREE, AND WITH NO BACKOFF, because the ruling above puts this inside the reading: every second
    /// spent here is a second the row says "being read" and the owner is told nothing. Two attempts at the
    /// sixty-second deadline is the most a person will sit in front of; the judge's ladder can afford three and a
    /// ninety-second wait because it books them for later instead of holding a reading open.
    ///
    /// NOT ASKED AGAIN: a rate limit, which named its own delay and has nowhere to wait it out here, and a plan
    /// that stood in or could not be read, where no call was made and asking twice asks the same unanswerable
    /// question. After both attempts, only a PERSON pressing the button buys another.
    /// </summary>
    private async Task<NarrationCallResult> NarrateForReadingAsync(
        TenantId tenant, string sid, TurnVerdictDto record, Lazy<TurnVerdictPackage> package, CancellationToken ct)
    {
        if (_env.ReadSessionState(tenant, sid).Held)
        {
            FileLog.Write($"[TurnVerdictService] narration not owed: sid={sid} verdict={record.VerdictId} - a live session owns this one and reads it");
            return new NarrationCallResult(null, null, "", 0);
        }
        // The claim still exists so that the phone's explain button and the voice path cannot ask a second time for
        // a stop this reading is already narrating. It covers BOTH attempts below - they are one reading's call.
        if (!TryClaimNarration(tenant, sid, record.VerdictId))
            return new NarrationCallResult(null, "a narration call for this stop was already running", "", 0);
        try
        {
            var first = await NarrateAsync(tenant, sid, record, package, ct).ConfigureAwait(false);
            if (first.Spoken is { Length: > 0 }) return first;
            // No prompt means no call was made: the plan stood in, or could not be read.
            if (first.Prompt.Length == 0) return first;
            if (WasRateLimited(first.FailureDetail))
            {
                FileLog.Write($"[TurnVerdictService] narration sid={sid} verdict={record.VerdictId}: rate limited - no second attempt, it named its own delay and a reading cannot wait it out");
                return first;
            }
            ct.ThrowIfCancellationRequested();
            FileLog.Write($"[TurnVerdictService] narration sid={sid} verdict={record.VerdictId}: first attempt gave no words ({first.FailureDetail}) - one immediate second attempt");
            var second = await NarrateAsync(tenant, sid, record, package, ct).ConfigureAwait(false);
            FileLog.Write(second.Spoken is { Length: > 0 }
                ? $"[TurnVerdictService] narration sid={sid} verdict={record.VerdictId}: the second attempt answered"
                : $"[TurnVerdictService] narration sid={sid} verdict={record.VerdictId}: BOTH attempts gave no words - this reading has none, and only a person asking buys another");
            return second;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // NarrateAsync catches every call failure it expects; anything past it is unexpected and must not turn a
            // good judgement into a refused one. The reading is stored with no narration, and the fault is logged.
            FileLog.Write($"[TurnVerdictService] narration inside the reading FAILED: sid={sid} verdict={record.VerdictId}: {ex.GetType().Name}: {ex.Message}");
            return new NarrationCallResult(null, ex.Message, "", 0);
        }
        finally
        {
            NarrationCallFinished(tenant, sid, record.VerdictId);
        }
    }

    /// <summary>
    /// Did this narration attempt fail because the provider rate-limited it? Read off the recorded reason, because
    /// <see cref="NarrateAsync"/> catches the rate limit and hands back a result rather than throwing - so the
    /// exception type is gone by the time a caller decides whether to ask again.
    /// </summary>
    private static bool WasRateLimited(string? failureDetail)
        => failureDetail is { Length: > 0 } detail
           && (detail.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("rate-limit", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("429", StringComparison.Ordinal));

    /// <summary>
    /// Claim one verdict id's narration call for a session. True for the first claim of that id, false while a call for
    /// that same id is IN FLIGHT, whichever path asks - the turn end for the user, the voice path, or a person's explain.
    ///
    /// THE CLAIM COVERS A CALL, NOT A VERDICT, and that distinction is the whole of issue "Turn not narrated". The claim
    /// was taken before the model call and never given back, so "one call per verdict id" silently became "one ATTEMPT
    /// per verdict id, for the life of the process". A first attempt that timed out, was rate limited, or answered with
    /// no words left the verdict permanently unnarratable: the sweep could not retry it, and - the part the owner saw -
    /// the phone's "Generate narration now" button asked for the claim, was refused, made no call at all, and changed
    /// nothing on screen. The screen said "ask for the narration again" next to a button that could not ask.
    ///
    /// So every caller that takes a claim MUST give it back when the call produced no words - see
    /// <see cref="ReleaseNarrationClaim"/>. A claim that is held is a call that is running; a claim that is released is
    /// an attempt that failed and may be made again. The claim is still never released on SUCCESS, because the saved
    /// narration is then what stops the second call (both paths check <c>Narration</c> before they ask).
    /// </summary>
    internal bool TryClaimNarration(TenantId tenant, string sid, string verdictId)
    {
        if (string.IsNullOrWhiteSpace(verdictId)) return false;
        var key = (tenant, sid);
        var mine = new NarrationClaim(verdictId, Running: true);
        while (true)
        {
            if (_narrationClaims.TryGetValue(key, out var claimed))
            {
                if (string.Equals(claimed.VerdictId, verdictId, StringComparison.Ordinal)) return false;
                if (_narrationClaims.TryUpdate(key, mine, claimed)) return true;
            }
            else if (_narrationClaims.TryAdd(key, mine))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// One narration call has finished, however it ended. The claim STAYS - no automatic path narrates this same
    /// record again, which is what keeps a stop that fails from spending a paid model call on every sweep pass (the
    /// booked retry makes a NEW reading, with its own record, at most eight times) - but it stops
    /// being a RUNNING call, which is what lets a PERSON ask again. See <see cref="ReleaseNarrationClaimForRequest"/>.
    /// </summary>
    internal void NarrationCallFinished(TenantId tenant, string sid, string verdictId)
    {
        if (string.IsNullOrWhiteSpace(verdictId)) return;
        var key = (tenant, sid);
        if (_narrationClaims.TryGetValue(key, out var claimed)
            && string.Equals(claimed.VerdictId, verdictId, StringComparison.Ordinal)
            && claimed.Running)
            _narrationClaims.TryUpdate(key, claimed with { Running = false }, claimed);
    }

    /// <summary>
    /// A PERSON asked for this stop's narration - "Generate narration now", or entering voice mode - so give back a
    /// SPENT claim and let the call be made. True when the caller may now claim; false only when a call for this same
    /// verdict is running right now, so the request joins it instead of duplicating a paid call.
    ///
    /// WHY. The claim was taken before the model call and never given back, so "one call per verdict id" silently
    /// became "one ATTEMPT per verdict id, for the life of the process". A first attempt that timed out, was rate
    /// limited, or answered with no words left that stop unable to be narrated again by anyone, including a person
    /// asking for it.
    ///
    /// WHAT THAT DOES AND DOES NOT EXPLAIN, measured on the live fleet on 2026-09-17 rather than assumed. It bites
    /// only when the stop is UNCHANGED, because then the stored verdict is reused and its id is the same, so the ask
    /// is refused and no narration call is made - the person gets the judge's short text and never the fuller
    /// narration, however many times they press. When the screen or the reply HAS moved on, the re-judge mints a new
    /// verdict id, the claim on the old one is irrelevant, and the button works; that was observed recovering a
    /// "Turn not narrated" card in about twenty seconds. So this is NOT the cause of that card - that verdict comes
    /// from the SPEECH leg's own spent ladder (see WingmanVoiceService.NoteNarrationAttemptFailed) - and the two
    /// should not be conflated. It is the reason the card's own instruction, "ask for the narration again", can be
    /// followed and still produce nothing new.
    ///
    /// ONLY A PERSON RELEASES IT, deliberately. The automatic paths - the turn end and the idle sweep - keep the older
    /// restraint that <c>AFailedNarrationCall_LeavesTheJudgesWordsPlayable_AndIsNotReattempted</c> pins: a stop whose
    /// narration failed is not narrated again on every pass, because the sweep comes past every forty-five seconds
    /// and a stop that keeps failing would keep costing a call. What asks again by itself is the booked retry
    /// (<see cref="StartDueRetries"/>), eight times at most and as a new reading; a person asking is bounded by the
    /// person.
    /// </summary>
    internal bool ReleaseNarrationClaimForRequest(TenantId tenant, string sid, string verdictId)
    {
        if (string.IsNullOrWhiteSpace(verdictId)) return false;
        var key = (tenant, sid);
        if (!_narrationClaims.TryGetValue(key, out var claimed)) return true;   // nothing held: the caller may claim
        if (!string.Equals(claimed.VerdictId, verdictId, StringComparison.Ordinal)) return true;   // a newer stop owns it
        if (claimed.Running) return false;   // a call for this same stop is in flight - never make a second one
        // ICollection's Remove is the only compare-and-remove ConcurrentDictionary exposes.
        var removed = ((ICollection<KeyValuePair<(TenantId Tenant, string SessionId), NarrationClaim>>)_narrationClaims)
            .Remove(new KeyValuePair<(TenantId Tenant, string SessionId), NarrationClaim>(key, claimed));
        if (removed)
            FileLog.Write($"[TurnVerdictService] narration claim released on request: sid={sid} verdict={verdictId} - a person asked, and the previous attempt made no words");
        return true;
    }

    /// <summary>One session's narration claim: which stop it is for, and whether that call is running right now.</summary>
    private sealed record NarrationClaim(string VerdictId, bool Running);

    /// <summary>
    /// Save a narration onto the verdict it describes, when that verdict is still this session's latest. A newer verdict
    /// has its own stop and its own narration, so text for an older one is dropped and logged. Returns whether it was saved.
    /// </summary>
    internal bool SaveNarration(TenantId tenant, string sid, string verdictId, string narration)
    {
        lock (_storeGate)
        {
            var latest = _env.Latest(tenant, sid);
            if (latest is null || !string.Equals(latest.VerdictId, verdictId, StringComparison.Ordinal))
            {
                FileLog.Write($"[TurnVerdictService] SaveNarration: sid={sid} verdict={verdictId} is no longer the latest (latest={latest?.VerdictId ?? "none"}) - not saved");
                return false;
            }
            var updated = Copy(latest);
            updated.Narration = narration;
            // THE WORDS ARRIVED, so this reading is no longer one with no words: the tag clears and nothing stays booked.
            if (updated.NarrationFailureReason is not null)
            {
                updated.NarrationFailureReason = null;
                updated.FailureKind = null;
                updated.RetriesMade = 0;
                updated.NextRetryAtUtc = null;
            }
            _env.Store(tenant, sid, updated);
            FileLog.Write($"[TurnVerdictService] SaveNarration: sid={sid} verdict={verdictId} saved, length={narration.Length}");
            return true;
        }
    }

    // The verdict id each session's narration call was last claimed for (see TryClaimNarration).
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), NarrationClaim> _narrationClaims = new();

    // The narration calls for the user running detached from the turn end that started them.

    /// <summary>How many times one stop may ask the judge when somebody is listening: the first call and ONE
    /// re-attempt. Every other stop asks once.</summary>
    internal const int MaxJudgeAttemptsWhenListenedTo = 2;

    // Is somebody listening to this stop? Decided by Flight.DecideReattempt: a person's own request (explain, a spoken
    // reply) that started the flight or joined it before the decision, or a voice session - read at the moment of the
    // decision, so a session whose voice was switched off while the first call ran does not buy a second one.

    /// <summary>Whether a person has asked about the flight running for this session - it was started by explain, or
    /// explain joined it. False when no flight is running. For tests that must know a join has landed.</summary>
    internal bool FlightAskedOnDemand(TenantId tenant, string sessionId)
        => _inFlight.TryGetValue((tenant, sessionId), out var flight) && flight.AskedOnDemand;

    // A RATE LIMIT'S OWN WAIT, per session: the failed record it produced and when the wait it named ends. An explain
    // on the unchanged screen does not ask again inside it.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), (string VerdictId, DateTime Until, string? SourceText)> _rateLimitHolds = new();

    // THE DECISION A REFUSED BUT READABLE ANSWER HELD, per session, keyed by the refused record's verdict id: the narration
    // call's input only (slice J). One per session - a newer refused record replaces it - and never read for the row.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), (string VerdictId, TurnVerdictDto Decision)> _refusedNarrationDecisions = new();

    // THE HOLD GENERATION, per session: every clear (a new turn, a Working event) moves it on, under _holdGate, and a
    // flight writes its hold only when the generation is still the one it captured at start. Without it a flight that
    // was still ending when a new turn cleared the hold wrote the old stop's wait back over the new stop.
    private readonly object _holdGate = new();
    private readonly Dictionary<(TenantId Tenant, string SessionId), long> _holdGenerations = new();

    private long HoldGeneration((TenantId Tenant, string SessionId) key)
    {
        lock (_holdGate) return _holdGenerations.TryGetValue(key, out var generation) ? generation : 0;
    }

    private void ClearRateLimitHold((TenantId Tenant, string SessionId) key)
    {
        lock (_holdGate)
        {
            _holdGenerations[key] = (_holdGenerations.TryGetValue(key, out var generation) ? generation : 0) + 1;
            _rateLimitHolds.TryRemove(key, out _);
        }
    }

    private void WriteRateLimitHoldIfCurrent((TenantId Tenant, string SessionId) key, Flight flight, (string VerdictId, DateTime Until, string? SourceText) hold)
    {
        lock (_holdGate)
        {
            var current = _holdGenerations.TryGetValue(key, out var generation) ? generation : 0;
            if (current != flight.HoldGeneration)
            {
                FileLog.Write($"[TurnVerdictService] sid={key.SessionId}: rate limit wait NOT recorded - a new turn or a Working event cleared the hold after this flight started");
                return;
            }
            _rateLimitHolds[key] = hold;
        }
    }

    /// <summary>Whether a rate limit wait is recorded for this session. For tests.</summary>
    internal bool HasRateLimitHold(TenantId tenant, string sessionId) => _rateLimitHolds.ContainsKey((tenant, sessionId));

    /// <summary>How much of a rate limit's named wait is left for this failed record, or null when it is not one or the
    /// wait is over.</summary>
    private TimeSpan? RateLimitHoldLeft((TenantId Tenant, string SessionId) key, TurnVerdictDto record)
    {
        if (!record.Failed || !_rateLimitHolds.TryGetValue(key, out var hold)
            || !string.Equals(hold.VerdictId, record.VerdictId, StringComparison.Ordinal))
            return null;
        var left = hold.Until - _env.NowUtc();
        return left > TimeSpan.Zero ? left : null;
    }

    /// <summary>
    /// May the stored verdict answer this request without asking the judge? It must be about this very screen, and
    /// an unreadable screen - every one hashes to "" - is reused only by the sweep, which comes past every pass.
    ///
    /// A FAILED record is reused by the sweep, for the same reason, and by a voice session's narration - its first
    /// attempt and its speech re-attempt alike - whether or not it carries spoken words. A refused answer's words
    /// are narrated exactly as an accepted verdict's are (slice I), and a timeout or a rate limit was already that
    /// stop's one call. Asking again would make the call count depend on whether the voice refresh arrived before or
    /// after the turn end's judgement ended: the slice I inspection measured two calls after a rate limit.
    ///
    /// Inside the wait a rate limit named, every trigger reuses the failed record, checked before the screen. Otherwise an
    /// explain may ask again about a failed record once, but not about a failure its own attempts produced. A turn end
    /// and a snooze expiry never reuse a failed record outside a rate limit's wait.
    /// </summary>
    /// <param name="currentSource">The source this stop would be judged from now, read only when a rate limit's wait is
    /// running for the stored record - so the wait binds the stop it was named for, and a new reply on the same
    /// unreadable screen is still a new stop.</param>
    private bool IsReusable((TenantId Tenant, string SessionId) key, TurnVerdictDto latest, string hash, TurnVerdictTrigger trigger,
        Func<string?> currentSource)
    {
        // INSIDE A RATE LIMIT'S NAMED WAIT NOTHING ASKS AGAIN ABOUT THAT STOP, for every trigger and whatever the screen
        // now reads - an unreadable screen included (slice I inspection, round two). Checked before the screen is compared
        // at all. "That stop" is the source it was judged from: a new reply is a new stop and is never held back by the
        // previous one's wait (WingmanVoiceServiceTests.TheProvidersHold_DoesNotDelayANewTurnsNarration).
        if (RateLimitHoldLeft(key, latest) is not null
            && _rateLimitHolds.TryGetValue(key, out var hold)
            && string.Equals(hold.SourceText, currentSource(), StringComparison.Ordinal))
            return true;
        if (!string.Equals(latest.ScreenHash, hash, StringComparison.Ordinal)) return false;
        if (hash.Length == 0 && trigger != TurnVerdictTrigger.Sweep) return false;
        // A READING WITH NO WORDS is asked again by its booked retry exactly as a failed one is. Every other trigger
        // reuses it: the judge's answer on it is good, and a person asking buys the narration call alone.
        if (!latest.Failed)
            return !(trigger == TurnVerdictTrigger.Retry
                     && latest.NarrationFailureReason is not null
                     && WingmanRetrySchedule.IsDue(latest.NextRetryAtUtc, _env.NowUtc()));
        return trigger switch
        {
            // The sweep and a voice session's refresh reuse EVERY failed record, with or without words: a refused
            // answer's words are narrated, and a timeout or a rate limit was already that stop's one call (and its
            // listened-to re-attempt). Asking again from here would make the number of calls depend on whether the
            // refresh arrived before or after the turn end's judgement ended (inspection of slice I, finding 1).
            TurnVerdictTrigger.Sweep or TurnVerdictTrigger.Voice => true,
            // THE ONE AUTOMATIC PATH THAT ASKS AGAIN, and only when the failed record's own booked retry has come due
            // (mission "Wingman error and retry"). Before that moment it reuses the failure like every other path.
            TurnVerdictTrigger.Retry => !WingmanRetrySchedule.IsDue(latest.NextRetryAtUtc, _env.NowUtc()),
            // A PERSON ASKING ALWAYS MAKES ONE ATTEMPT (owner ruling, 2026-09-19: "a press makes one attempt at once").
            // It is bounded by the person, and it neither resets nor consumes the schedule - see StampRetrySchedule.
            // (Inside a rate limit's wait the check above has already answered.)
            TurnVerdictTrigger.OnDemand => false,
            _ => false,
        };
    }

    /// <summary>
    /// Write where a failed stop is on its retry schedule onto the failed record (<see cref="WingmanRetrySchedule"/>).
    ///
    /// THE SCHEDULE IS PER STOP. A turn end is a new stop and starts it again, and so does a failure with no failed
    /// record before it (a Working edge invalidates the store, so "the latest record is a failure" means "this same
    /// stop failed before"). Every other automatic attempt after a failure spends one retry and books the next, or
    /// books nothing when the eight are spent.
    ///
    /// A PERSON ASKING NEITHER RESETS NOR CONSUMES IT (owner ruling, 2026-09-19): the press made one attempt now, and
    /// the failed record it leaves carries the schedule exactly as it stood - the same count, the same booked time.
    /// </summary>
    private void StampRetrySchedule(TurnVerdictDto failed, TurnVerdictDto? previous, TurnVerdictTrigger trigger, TimeSpan? providerWait)
    {
        var now = _env.NowUtc();
        var sameStop = previous is not null && WingmanRetrySchedule.NeedsRetry(previous) && trigger != TurnVerdictTrigger.TurnEnd;
        if (sameStop && trigger == TurnVerdictTrigger.OnDemand)
        {
            failed.RetriesMade = previous!.RetriesMade;
            failed.NextRetryAtUtc = previous.NextRetryAtUtc;
            // A provider that named a wait on the person's own attempt is still honoured: a booked retry moves later,
            // never earlier, and a schedule with nothing booked stays with nothing booked.
            if (providerWait is { } named && failed.NextRetryAtUtc is { } booked && booked < now + named)
                failed.NextRetryAtUtc = now + named;
            return;
        }
        failed.RetriesMade = sameStop ? Math.Min(previous!.RetriesMade + 1, WingmanRetrySchedule.Total) : 0;
        failed.NextRetryAtUtc = WingmanRetrySchedule.NextRetryAtUtc(failed.RetriesMade, now, providerWait);
    }

    /// <summary>
    /// THE IDLE SWEEP'S QUESTION, "IS A RETRY DUE", for one account (mission "Wingman error and retry"). Every
    /// stored FAILED reading whose booked retry has come due is asked about again, under <see cref="TurnVerdictTrigger.Retry"/>.
    /// Returns how many retries were started.
    ///
    /// NOTHING IS HELD IN MEMORY BETWEEN PASSES. The schedule is on the stored record, so a Gateway restart forgets no
    /// booked retry, and a record with nothing booked is never touched here at all.
    ///
    /// FIRE AND FORGET, like the snooze expiry: the sweep waits for no model call. The flight takes the session's one
    /// gate, so a retry that comes due while a judgement is already running joins it instead of paying twice. A
    /// session that cannot be read right now - held, not live, exited, working, the ceiling reached - is left with its
    /// retry still due, and the next pass asks again; nothing is spent and nothing is rebooked.
    ///
    /// THE ONE RECORD IT REWRITES: a due retry on an account whose judge switch has since been turned off, for a
    /// session nobody is listening to. No retry will ever run for it, so the booking is withdrawn rather than left on
    /// the card as a promise - goal 6 of the mission: no card says an attempt is coming when none is booked.
    /// </summary>
    public int StartDueRetries(TenantId tenant)
    {
        if (_disposed || !tenant.IsValid) return 0;
        var now = _env.NowUtc();
        var settings = _env.Settings(tenant);
        var started = 0;
        foreach (var (sid, snapshot) in _env.SnapshotLatest(tenant))
        {
            if (!WingmanRetrySchedule.NeedsRetry(snapshot) || !WingmanRetrySchedule.IsDue(snapshot.NextRetryAtUtc, now)) continue;

            if (!settings.JudgeEnabled && !_env.IsVoiceSession(tenant, sid))
            {
                WithdrawBookedRetry(tenant, sid, snapshot);
                continue;
            }

            var state = _env.ReadSessionState(tenant, sid);
            if (SessionStateSkipCause(state, TurnVerdictTrigger.Retry) is not null) continue;

            var directorId = state.Facts?.DirectorId ?? "";
            var retryNumber = snapshot.RetriesMade + 1;
            FileLog.Write($"[TurnVerdictService] StartDueRetries: sid={sid} tenant={tenant.ToLogString()} retry {retryNumber} of {WingmanRetrySchedule.Total} is due (failed record {snapshot.VerdictId}) - asking again");
            started++;
            var retry = Task.Run(() => VerdictForCurrentScreenAsync(tenant, directorId, sid, TurnVerdictTrigger.Retry));
            _ = retry.ContinueWith(
                t => FileLog.Write($"[TurnVerdictService] retry {retryNumber} FAULTED: sid={sid}: " +
                                   $"{t.Exception?.GetBaseException().GetType().FullName}: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        return started;
    }

    /// <summary>Take the booked retry off a failed record that no retry will ever run for. Stored only while that
    /// record is still the session's latest, under the same gate a Working edge invalidates under.</summary>
    private void WithdrawBookedRetry(TenantId tenant, string sid, TurnVerdictDto snapshot)
    {
        lock (_storeGate)
        {
            var current = _env.Latest(tenant, sid);
            if (current is null
                || !string.Equals(current.VerdictId, snapshot.VerdictId, StringComparison.Ordinal)
                || current.NextRetryAtUtc is null)
                return;
            var withdrawn = Copy(current);
            withdrawn.NextRetryAtUtc = null;
            _env.Store(tenant, sid, withdrawn);
        }
        FileLog.Write($"[TurnVerdictService] WithdrawBookedRetry: sid={sid} tenant={tenant.ToLogString()} - the judge switch is off and nobody is listening, so no retry will run; the booking on {snapshot.VerdictId} is withdrawn");
    }

    private bool StoreIfCurrent((TenantId Tenant, string SessionId) key, long epoch, TurnVerdictDto record)
    {
        lock (_storeGate)
        {
            if (_epochs.GetOrAdd(key, 0) != epoch) return false;
            _env.Store(key.Tenant, key.SessionId, record);
            _knownEmpty.TryRemove(key, out _);
            return true;
        }
    }

    private async Task<ScreenGridResponse?> ReadScreenAsync(
        TenantId tenant, string directorId, string sid,
        Func<CancellationToken, Task<ScreenGridResponse?>>? screenReader, CancellationToken ct)
    {
        if (screenReader is null)
            return await _env.ReadScreenGridAsync(tenant, directorId, sid, ct).ConfigureAwait(false);
        try
        {
            return await screenReader(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Unreadable is not evidence of anything: carried as no rows, which the contract binds to cannot-tell.
            FileLog.Write($"[TurnVerdictService] screen read FAILED sid={sid}: {ex.Message} - judging without a screen");
            return null;
        }
    }

    /// <summary>
    /// A request stood down before anything was read or asked. A STOP that stood down - the turn-end trigger - leaves a
    /// trace with its cause and no content, so the inspector can tell "not judged, because it was held" apart from "no
    /// record". The voice path and the sweep come past the same sessions over and over, so their skips stay in the
    /// ledger only. A judge switch that is off leaves no trace: nothing is ever traced for such an account.
    /// </summary>
    /// <param name="settings">The settings the flight read, and <paramref name="observedAt"/> the stop it stands on. A
    /// skip given no stop leaves no trace of its own - that is the already-judging skip, whose row the judgement it
    /// joined writes under its own outcome.</param>
    private TurnVerdictOutcome Skip(TenantId tenant, string directorId, string sid, TurnVerdictTrigger trigger, string cause,
        TurnVerdictSettings? settings = null, DateTime? observedAt = null)
    {
        _env.Record(new TurnVerdictRecord(tenant, directorId ?? "", sid, ActivityEventTypes.TurnVerdictSkipped,
            cause, $"trigger={TriggerWord(trigger)}"));
        if (trigger == TurnVerdictTrigger.TurnEnd && cause != ActivityCauses.JudgeSwitchOff && observedAt is { } stop)
            TraceStop(tenant, sid, TriggerWord(trigger), TurnVerdictTraceOutcomes.Skipped, stop, settings,
                colour => NewUnjudgedTrace(sid, directorId, trigger, TurnVerdictTraceOutcomes.Skipped, cause, stop, colour));
        return new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Skipped, SkipCause = cause };
    }

    /// <summary>
    /// The outcome for a stop that joined a judgement already running for its session: the ledger records it stood
    /// down under "already judging", and one model call still serves the stop. The TRACE for it is written by the
    /// judgement it joined, as that judgement ends.
    /// </summary>
    private TurnVerdictOutcome JoinedOutcome(TurnEndSignal signal)
        => Skip(signal.Tenant, signal.DirectorId, signal.SessionId, TurnVerdictTrigger.TurnEnd, ActivityCauses.AlreadyJudging);

    /// <summary>
    /// The stops that joined this flight, recorded as it ends - with the settings it read at its start, so nothing is
    /// read on the turn-end handler, and inside the flight itself, so shutdown waits for this work like any other part
    /// of the judgement. Found in review: as a background task it could be dropped at shutdown and could record two
    /// stops out of order. Each goes through <see cref="TraceStop"/>, so a flight that never read its settings leaves a
    /// counted loss per stop.
    /// </summary>
    /// <param name="stoodDown">The flight never ran because the service was shutting down. Its joined stops were then never
    /// served by any judgement, so they are recorded as cancelled at shutdown, under the settings of the flight that handed
    /// this one the gate when there was one that read them.</param>
    private void WriteJoinedTraces((TenantId Tenant, string SessionId) key, Flight flight, string directorId, bool stoodDown)
    {
        var joined = flight.TakeJoined();
        OnJoinedListClosedForTests?.Invoke(key.SessionId);
        foreach (var observedAt in joined)
        {
            if (stoodDown)
            {
                CancelledAtShutdown(key, new TurnEndSignal(key.SessionId, directorId, key.Tenant, observedAt, IsNewTurn: true), flight.HandedOverSettings);
                continue;
            }
            TraceStop(key.Tenant, key.SessionId, TriggerWord(TurnVerdictTrigger.TurnEnd), TurnVerdictTraceOutcomes.Joined, observedAt, flight.Settings,
                colour => NewUnjudgedTrace(key.SessionId, directorId, TurnVerdictTrigger.TurnEnd,
                    TurnVerdictTraceOutcomes.Joined, ActivityCauses.AlreadyJudging, observedAt, colour));
        }
    }

    private void RaiseReadingCompleted(TurnVerdictReadingCompleted completed)
    {
        var observers = ReadingCompleted;
        if (observers is null) return;
        foreach (var observer in observers.GetInvocationList())
        {
            try { ((Action<TurnVerdictReadingCompleted>)observer)(completed); }
            catch (Exception ex)
            {
                FileLog.Write($"[TurnVerdictService] ReadingCompleted handler FAILED: sid={completed.SessionId} " +
                              $"trigger={completed.Trigger}: {ex.GetType().FullName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A flight leaves the gate (issue #2905, round 2). Its joined stops are written first, while it still holds the gate.
    /// Then, under the lock its line is guarded by:
    ///
    /// - AN EMPTY LINE: the flight is marked gone and removed from the gate in the same step, so a stop that still holds a
    ///   reference to it looks at the gate again and finds it free.
    /// - A LINE, WHILE THE SERVICE RUNS: a successor flight for the head of the line replaces this one on the gate in the
    ///   same step, carrying the rest of the line as stops that joined it - so the session's gate entry is never absent
    ///   while any stop for it is pending, and nothing can take the gate between the two. The head is then started
    ///   exactly as a stop that took the gate itself.
    /// - A LINE, ONCE SHUTDOWN HAS BEGUN: nothing more will be judged, so each queued stop is written as cancelled at
    ///   shutdown while this flight still holds the gate - the drain is still waiting for it - and the line is looked at
    ///   again, because a stop admitted just before disposal can still be queuing.
    ///
    /// Done completes only after this returns, in the caller.
    /// </summary>
    private void LeaveGate((TenantId Tenant, string SessionId) key, Flight flight, string directorId, bool stoodDown)
    {
        try
        {
            WriteJoinedTraces(key, flight, directorId, stoodDown);
        }
        finally
        {
            HandOverOrRelease(key, flight);
        }
    }

    /// <summary>The second half of <see cref="LeaveGate"/>: the line, then the gate. Returns once this flight is off the
    /// gate. Nothing in it may throw past a queued stop: each answer is set whatever happens to the others.</summary>
    private void HandOverOrRelease((TenantId Tenant, string SessionId) key, Flight flight)
    {
        while (true)
        {
            QueuedStop[] line;
            Flight? successor = null;
            var shuttingDown = false;
            lock (flight.Sync)
            {
                line = flight.TakeLineLocked();
                if (line.Length == 0)
                {
                    flight.MarkLeftGateLocked();
                    _inFlight.TryRemove(new KeyValuePair<(TenantId, string), Flight>(key, flight));
                }
                else if (_disposed)
                {
                    shuttingDown = true;
                }
                else
                {
                    successor = new Flight { HandedOver = true, HandedOverSettings = flight.Settings ?? flight.HandedOverSettings };
                    successor.JoinHandedOver(line.Skip(1));
                    flight.MarkLeftGateLocked();
                    // Only a flight replaces or removes its own entry, so this flight is the entry and the update holds.
                    if (!_inFlight.TryUpdate(key, successor, flight))
                        throw new InvalidOperationException(
                            $"[TurnVerdictService] the gate for sid={key.SessionId} was not held by the flight handing it over");
                }
            }

            if (line.Length == 0) return;

            if (shuttingDown)
            {
                var settings = flight.Settings ?? flight.HandedOverSettings;
                foreach (var stop in line)
                {
                    try { stop.Answer(CancelledAtShutdown(key, stop.Signal, settings)); }
                    catch (Exception ex) { stop.Fault(ex); }
                }
                continue;
            }

            FileLog.Write($"[TurnVerdictService] sid={key.SessionId}: the gate is handed to the next stop in line; {line.Length - 1} more join it");
            foreach (var stop in line.Skip(1))
            {
                // COVERED BY THE SUCCESSOR, so its verdict is the successor's result - not this flight's (round 3).
                var joiner = stop;
                successor!.Outcome.Task.ContinueWith(run =>
                {
                    if (run.IsFaulted) joiner.Covered.TrySetException(run.Exception!.InnerExceptions);
                    else joiner.Covered.TrySetResult(run.Result);
                }, TaskContinuationOptions.ExecuteSynchronously);
                try { stop.Result.TrySetResult(JoinedOutcome(stop.Signal)); }
                catch (Exception ex) { stop.Result.TrySetException(ex); }
            }
            var head = line[0];
            StartAdmittedTurnEnd(key, successor!, head.Signal).ContinueWith(run =>
            {
                if (run.IsFaulted) head.Fault(run.Exception!.GetBaseException());
                else if (run.IsCanceled) { head.Result.TrySetCanceled(); head.Covered.TrySetCanceled(); }
                else head.Answer(run.Result);
            }, TaskContinuationOptions.ExecuteSynchronously);
            return;
        }
    }

    /// <summary>
    /// A stop that was observed and will not be judged because the service is shutting down - queued behind an ending
    /// judgement, handed the gate as shutdown began, registered just before disposal, or refused just after it. It
    /// leaves a cancelled trace under <see cref="ActivityCauses.Shutdown"/> through <see cref="TraceStop"/>: a row when
    /// the writer takes it, and a counted loss when the writer refuses it or no judgement read the settings. Never throws
    /// on its own account; the environment's trace calls are bound not to throw.
    /// </summary>
    private TurnVerdictOutcome CancelledAtShutdown((TenantId Tenant, string SessionId) key, TurnEndSignal signal, TurnVerdictSettings? settings)
    {
        FileLog.Write($"[TurnVerdictService] cancelled sid={key.SessionId} tenant={key.Tenant.ToLogString()}: the service is shutting down; observed={signal.ObservedAtUtc:O}");
        try
        {
            _env.Record(new TurnVerdictRecord(key.Tenant, signal.DirectorId ?? "", key.SessionId, ActivityEventTypes.TurnVerdictCancelled,
                ActivityCauses.Shutdown, $"trigger={TriggerWord(TurnVerdictTrigger.TurnEnd)}"));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnVerdictService] the ledger event for a stop cancelled at shutdown could NOT be written: sid={key.SessionId}: {ex.GetType().FullName}: {ex.Message}");
        }

        TraceStop(key.Tenant, key.SessionId, TriggerWord(TurnVerdictTrigger.TurnEnd), TurnVerdictTraceOutcomes.Cancelled, signal.ObservedAtUtc, settings,
            colour => NewUnjudgedTrace(key.SessionId, signal.DirectorId, TurnVerdictTrigger.TurnEnd,
                TurnVerdictTraceOutcomes.Cancelled, ActivityCauses.Shutdown, signal.ObservedAtUtc, colour));
        return new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Cancelled };
    }

    /// <summary>A flight registered before <see cref="Dispose"/> and reached after it: nothing is read or asked. Its own
    /// stop, when it is one, is cancelled at shutdown; anybody who joined or queued behind it is recorded the same way; then
    /// it leaves the gate and completes.</summary>
    /// <param name="stop">The turn-end stop this flight was started for, or null for a request that is not a stop.</param>
    private TurnVerdictOutcome StandDownAfterDispose((TenantId Tenant, string SessionId) key, Flight flight, string directorId, TurnEndSignal? stop)
    {
        var outcome = new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Skipped, SkipCause = ActivityCauses.Unknown };
        try
        {
            if (stop is not null)
                outcome = CancelledAtShutdown(key, stop, flight.HandedOverSettings);
        }
        finally
        {
            // A STOP MAY ALREADY HAVE JOINED THIS FLIGHT, or queued behind it, in the moment between its registration and
            // this check. The same order as a flight that ran: joined stops first, then the line, then the gate, then Done.
            try
            {
                LeaveGate(key, flight, directorId, stoodDown: true);
            }
            finally
            {
                flight.Outcome.TrySetResult(outcome);
                flight.Done.TrySetResult();
            }
        }
        flight.Cts.Dispose();
        FileLog.Write($"[TurnVerdictService] a verdict request reached the gate as the service was shutting down and stood down: sid={key.SessionId}");
        return outcome;
    }

    /// <remarks>
    /// NOTHING HERE IS LOOKED UP AGAIN. The stop and the settings come from the flight (or, on the reuse arm, from the
    /// caller that already holds them): by the time a cancellation is handled the session has moved on, so "the latest
    /// observed stop" can be a later stop, and a settings read can fail or see a switch flipped mid-flight. A stop
    /// cancelled before its flight read its settings is a counted loss, through <see cref="TraceStop"/>.
    /// </remarks>
    private TurnVerdictOutcome Cancelled(TenantId tenant, string directorId, string sid, TurnVerdictTrigger trigger, string why,
        Flight? flight = null, TurnVerdictSettings? settings = null, DateTime? observedAt = null)
    {
        FileLog.Write($"[TurnVerdictService] cancelled sid={sid} tenant={tenant.ToLogString()}: {why}");
        _env.Record(new TurnVerdictRecord(tenant, directorId ?? "", sid, ActivityEventTypes.TurnVerdictCancelled,
            ActivityCauses.WorkingObservation, $"trigger={TriggerWord(trigger)}"));
        var traceSettings = flight?.Settings ?? settings;
        var stop = flight is not null ? flight.ObservedAt : observedAt;
        if ((trigger == TurnVerdictTrigger.TurnEnd || flight?.Prompt is not null)
            && stop is { } observed && observed != default)
            TraceStop(tenant, sid, TriggerWord(trigger), TurnVerdictTraceOutcomes.Cancelled, observed, traceSettings, colour =>
            {
                var trace = NewUnjudgedTrace(sid, directorId, trigger, TurnVerdictTraceOutcomes.Cancelled,
                    ActivityCauses.WorkingObservation, observed, colour);
                return flight is null ? trace : WithEvidence(trace, flight);
            });
        return new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Cancelled };
    }

    /// <summary>The cause a stop's trace is counted lost under when no judgement for its session read the settings.</summary>
    internal const string SettingsNeverReadCause = "no judgement for this session read its settings";

    /// <summary>
    /// THE ONE DOOR EVERY TRACE LEAVES THE SEAT THROUGH (issue #2905, round 3). Nothing else in this service calls
    /// <see cref="ITurnVerdictEnvironment.RecordTrace"/> or <see cref="ITurnVerdictEnvironment.TraceNotKept"/>, and nothing
    /// in this service logs a trace as not kept - the writer does that, and counts it. The rule, decided once, here:
    ///
    /// - SETTINGS SAY THE JUDGE SWITCH IS OFF: nothing is owed. Nothing is ever traced for such an account.
    /// - NO SETTINGS: nothing can say whether this account is traced, so the trace is COUNTED AS LOST with that cause. The
    ///   settings are not read here - at shutdown the read can fail or reach a database being disposed, and a switch read
    ///   now would not be the one the stop was observed under.
    /// - OTHERWISE: the trace is built and handed to the writer, which keeps it or logs and counts it. Building or handing
    ///   it over that throws is counted as lost with the exception's type.
    ///
    /// A TRACE CARRIES THE OBSERVED TIME OF THE STOP IT IS ABOUT, and the door stamps it (round 3 inspection). A verdict
    /// takes the LATEST observed stop as its join key, so a judgement that a later stop joined stores that later time -
    /// and a trace copied from the verdict named the joiner twice and the founding stop never. The judged row is about
    /// the founding stop, each joined row about its joiner, and the caller says which by <paramref name="observedAt"/>.
    /// </summary>
    /// <param name="build">Builds the trace, given whether the account's colour switch is on.</param>
    private void TraceStop(TenantId tenant, string sid, string trigger, string outcome, DateTime observedAt,
        TurnVerdictSettings? settings, Func<bool, TurnVerdictTrace> build)
    {
        if (settings is { JudgeEnabled: false }) return;
        if (settings is null)
        {
            _env.TraceNotKept(tenant, LostTrace(sid, trigger, outcome, observedAt), SettingsNeverReadCause);
            return;
        }

        TurnVerdictTrace trace;
        try
        {
            trace = build(settings.ColourEnabled) with { TurnEndObservedAtUtc = observedAt };
            // THE CARRYING-ON CLOCK AS IT STOOD AT JUDGEMENT (the Wingman inspector, phase 2). The deadline depends on
            // the owned sessions' live activity, which nothing keeps, so it is read now or never. Only a carrying-on
            // verdict has a clock, so only one of those costs the roster read.
            if (trace.Verdict is { Failed: false } carryingOn
                && string.Equals(carryingOn.Verdict, TurnVerdictVocabulary.ContinuesAlone, StringComparison.Ordinal))
                trace = trace with { ClockDeadlineUtc = TurnVerdictWatchdog.DeadlineFor(carryingOn, _env.OwnedSessions(tenant, sid)) };
        }
        catch (Exception ex)
        {
            _env.TraceNotKept(tenant, LostTrace(sid, trigger, outcome, observedAt), "building the trace failed: " + ex.GetType().FullName + ": " + ex.Message);
            return;
        }
        _env.RecordTrace(tenant, trace);
    }

    /// <summary>What a counted loss is described by: the session, the stop and the outcome. Built without the environment,
    /// so describing a loss cannot fail.</summary>
    private static TurnVerdictTrace LostTrace(string sid, string trigger, string outcome, DateTime observedAt) => new()
    {
        TraceId = Guid.NewGuid().ToString("N"),
        SessionId = sid,
        RecordedAtUtc = DateTime.UtcNow,
        TurnEndObservedAtUtc = observedAt,
        Trigger = trigger,
        Outcome = outcome,
    };

    private TurnVerdictDto FailedRecord(TurnVerdictPackage package, TenantId tenant, DateTime observedAt, string reason)
        => new()
        {
            VerdictId = Guid.NewGuid().ToString("N"),
            JudgedAtUtc = _env.NowUtc(),
            TurnEndObservedAtUtc = observedAt,
            ScreenHash = package.ScreenHash,
            Model = _env.JudgeModel(tenant),
            ContractVersion = TurnVerdictContract.Version,
            PackageKind = TurnVerdictPackage.WireName(package.Kind),
            Failed = true,
            FailureReason = reason,
        };

    private static TurnVerdictDto Copy(TurnVerdictDto v) => TurnVerdictDtoCopy.Of(v);

    /// <summary>
    /// THE CARRYING-ON CLOCK'S TICK for one account (<see cref="TurnVerdictWatchdog"/>): every stored
    /// "continues-alone" verdict whose deadline has passed is replaced by a "needed-you" verdict labelled "Said it
    /// would continue and did not". Returns how many expired.
    ///
    /// THE SAME TICK UNDOES AN EXPIRY (the owner's ruling, 2026-09-17, and <see cref="UndoExpiry"/>): a record the
    /// CLOCK wrote, for a session that owns a live session again, goes back to carrying on and the clock starts
    /// again from that moment. Undos are not counted in the return value, which is what the sweep logs as verdicts
    /// that ran out of time; they carry their own log line, their own trace outcome and their own ledger row.
    ///
    /// For every account whose JUDGE switch is on, whether or not its colour switch is (the Architect's ruling on
    /// slice D, decision 5 reversed). A shadow account's stored verdicts are what the product would have shown,
    /// so its purple must expire exactly like a live one; a purple that never expires overstates "carrying on" in
    /// every grading report. What reaches a screen does not change: the row stamp still reads the colour switch.
    /// The judge's own answer is never overwritten either way - the expiry is a new, later record.
    ///
    /// A WORKING TRANSITION STOPS THE CLOCK, AND IT CANNOT LOSE A RACE WITH THIS. The snapshot is read outside the
    /// gate; each expiry re-reads the session's latest verdict INSIDE the gate <see cref="OnSessionWorking"/>
    /// invalidates under, and stores only when it is still the same carrying-on verdict and still past its
    /// deadline. A session that worked in between has no verdict left, and one judged again has a different one.
    ///
    /// A CHILD'S WORKING TRANSITION is not seen through the verdict at all - it invalidates only the child's own
    /// verdict - so the deadline inside the gate is computed from the owned sessions read AGAIN inside the gate,
    /// immediately before the store, never from the read taken before it. A child that is Working in the roster
    /// at that moment stands the expiry down. The read and the store follow each other inside the gate with
    /// nothing awaited between them.
    /// </summary>
    public int ExpireCarryingOn(TenantId tenant)
    {
        if (_disposed || !tenant.IsValid) return 0;
        var settings = _env.Settings(tenant);
        if (!settings.JudgeEnabled) return 0;

        var now = _env.NowUtc();
        var expired = 0;
        var undone = 0;
        foreach (var (sid, snapshot) in _env.SnapshotLatest(tenant))
        {
            // THE UNDO (the owner's ruling, 2026-09-17) runs on the same tick as the expiry, before it: a record the
            // clock itself wrote is the one kind of verdict that can be taken back, and a session that owns a live
            // session again is carrying on after all. Only a record the clock wrote costs a roster read here.
            if (TurnVerdictWatchdog.IsClockExpiry(snapshot))
            {
                if (UndoExpiry(tenant, sid, snapshot, now, settings)) undone++;
                continue;
            }
            // Only a carrying-on verdict has a clock at all, so only one of those costs a roster read.
            if (TurnVerdictWatchdog.DeadlineFor(snapshot) is null) continue;
            // THE OWNER'S OWN SESSIONS (owner ruling, 2026-09-15): while any session this one owns is working its
            // clock does not run, and once none is, it counts from the moment the last one stopped.
            var owned = _env.OwnedSessions(tenant, sid);
            if (!TurnVerdictWatchdog.IsExpired(snapshot, now, owned)) continue;

            TurnVerdictDto replacement;
            lock (_storeGate)
            {
                var current = _env.Latest(tenant, sid);
                if (current is null
                    || !string.Equals(current.VerdictId, snapshot.VerdictId, StringComparison.Ordinal))
                    continue;

                // A CHILD'S WORKING TRANSITION DOES NOT TOUCH ITS OWNER'S VERDICT, so the owned sessions read above
                // can be stale by now: a child that started Working since then must still hold its owner purple.
                // They are read again here, inside the gate and immediately before the store, and the expiry
                // stands down when any of them is Working.
                var ownedNow = _env.OwnedSessions(tenant, sid);
                if (!TurnVerdictWatchdog.IsExpired(current, now, ownedNow))
                    continue;

                replacement = TurnVerdictWatchdog.Expire(current, now);
                _env.Store(tenant, sid, replacement);
                _knownEmpty.TryRemove((tenant, sid), out _);
            }

            expired++;
            var expiredDirectorId = _env.ReadSessionState(tenant, sid).Facts?.DirectorId ?? "";
            var expiredVerdict = replacement;
            TraceStop(tenant, sid, ClockTrigger, TurnVerdictTraceOutcomes.Expired, expiredVerdict.TurnEndObservedAtUtc, settings,
                colour => NewTrace(sid, expiredDirectorId, ClockTrigger, TurnVerdictTraceOutcomes.Expired, expiredVerdict, colour)
                    with { ReplacedVerdictId = snapshot.VerdictId });
            _env.Record(new TurnVerdictRecord(tenant, expiredDirectorId, sid,
                ActivityEventTypes.TurnVerdictExpired, ActivityCauses.CarryingOnExpired,
                $"expired={snapshot.VerdictId} id={replacement.VerdictId}"));
            FileLog.Write($"[TurnVerdictService] ExpireCarryingOn: sid={sid} tenant={tenant.ToLogString()} said it would continue and did not; verdict {snapshot.VerdictId} replaced by {replacement.VerdictId}");
        }

        if (undone > 0)
            FileLog.Write($"[TurnVerdictService] ExpireCarryingOn: tenant={tenant.ToLogString()} {undone} expiry(ies) undone - the session owns a live session again");

        return expired;
    }

    /// <summary>
    /// ONE EXPIRY UNDONE, or false when this one stands. The session owns a LIVE session again - one in the fresh
    /// roster that has not exited and is not snoozed - so the clock's own red is taken back: a carrying-on verdict
    /// is stored in its place and the clock runs again from this moment
    /// (<see cref="TurnVerdictWatchdog.CarryOnAgain"/>).
    ///
    /// IT GOES THROUGH THE SAME STORE-IF-CURRENT PATH AS THE EXPIRY, for the same reason: the latest verdict is
    /// re-read inside the gate <see cref="OnSessionWorking"/> invalidates under, and nothing is stored unless it is
    /// still the same record this tick read. A session judged again in between has a different verdict, and the
    /// judge's answer must not be overwritten by a clock. The owned sessions are read again inside the gate too,
    /// so an undo cannot be written off a roster that went stale while the tick ran.
    ///
    /// ONLY A RECORD THE CLOCK WROTE. The caller has already asked <see cref="TurnVerdictWatchdog.IsClockExpiry"/>,
    /// and it is asked again here against the record read inside the gate.
    /// </summary>
    private bool UndoExpiry(TenantId tenant, string sid, TurnVerdictDto snapshot, DateTime now, TurnVerdictSettings settings)
    {
        if (!OwnsALiveSession(_env.OwnedSessions(tenant, sid))) return false;

        TurnVerdictDto replacement;
        lock (_storeGate)
        {
            var current = _env.Latest(tenant, sid);
            if (current is null
                || !string.Equals(current.VerdictId, snapshot.VerdictId, StringComparison.Ordinal)
                || !TurnVerdictWatchdog.IsClockExpiry(current))
                return false;
            if (!OwnsALiveSession(_env.OwnedSessions(tenant, sid))) return false;

            replacement = TurnVerdictWatchdog.CarryOnAgain(current, now);
            _env.Store(tenant, sid, replacement);
            _knownEmpty.TryRemove((tenant, sid), out _);
        }

        var directorId = _env.ReadSessionState(tenant, sid).Facts?.DirectorId ?? "";
        TraceStop(tenant, sid, ClockTrigger, TurnVerdictTraceOutcomes.ExpiryUndone, replacement.TurnEndObservedAtUtc, settings,
            colour => NewTrace(sid, directorId, ClockTrigger, TurnVerdictTraceOutcomes.ExpiryUndone, replacement, colour)
                with { ReplacedVerdictId = snapshot.VerdictId });
        _env.Record(new TurnVerdictRecord(tenant, directorId, sid,
            ActivityEventTypes.TurnVerdictExpiryUndone, ActivityCauses.CarryingOnAgain,
            $"undone={snapshot.VerdictId} id={replacement.VerdictId}"));
        FileLog.Write($"[TurnVerdictService] UndoExpiry: sid={sid} tenant={tenant.ToLogString()} owns a live session again; expiry {snapshot.VerdictId} replaced by carrying-on {replacement.VerdictId}");
        return true;
    }

    /// <summary>Does this session own a session that is still running underneath it? The one question the undo
    /// turns on, and the same one the clock stops on - alive, never "worked in the last ten seconds".</summary>
    private static bool OwnsALiveSession(OwnedSessionsFacts? owned)
        => owned is { Live: > 0 } or { Working: > 0 };

    /// <summary>The three-word screen verdict the menu cache has always held, read off the verdict: a picker
    /// the answer selects from is a menu; a stop that needs a person is waiting on an answer; anything else
    /// needs nothing.</summary>
    internal static string ScreenNeeds(TurnVerdictDto verdict)
    {
        if (string.Equals(verdict.AnswerVia, "keys", StringComparison.Ordinal) && verdict.Menu is not null) return "menu";
        return string.Equals(verdict.Verdict, TurnVerdictVocabulary.NeededYou, StringComparison.Ordinal) ? "answer" : "nothing";
    }

    /// <summary>
    /// The skip one roster snapshot decides, or null when the request may go on. Every AUTOMATIC trigger - the
    /// turn end, a voice session's narration and its re-attempts, the idle sweep - stands down for a session that
    /// is held, not in the fresh roster, brand-new, exited or crashed, or Working: a Working session's screen
    /// is a turn in progress, not a stop. A person's own request stands down only for a brand-new session.
    ///
    /// WHICH "HELD" DEPENDS ON THE TRIGGER (owner ruling, 2026-09-16). The two triggers that only JUDGE - the turn
    /// end and the snooze expiry - ask <see cref="TurnVerdictSessionState.HeldForJudging"/>, so a session the
    /// account's Fleet Manager holds is judged. The two that exist to NARRATE - a voice session's narration and the
    /// idle sweep - ask <see cref="TurnVerdictSessionState.Held"/>, so that session is never read aloud to the owner.
    /// </summary>
    private static string? SessionStateSkipCause(TurnVerdictSessionState state, TurnVerdictTrigger trigger)
    {
        var automatic = trigger != TurnVerdictTrigger.OnDemand;
        var held = trigger is TurnVerdictTrigger.TurnEnd or TurnVerdictTrigger.SnoozeExpiry or TurnVerdictTrigger.Retry
            ? state.HeldForJudging
            : state.Held;
        if (automatic && held) return ActivityCauses.Held;
        if (state.Facts is null) return automatic ? ActivityCauses.SessionNotLive : null;
        if (state.Facts.IsBrandNew) return ActivityCauses.BrandNew;
        if (automatic && IsExited(state.Facts)) return ActivityCauses.SessionExit;
        if (automatic && IsWorking(state.Facts)) return ActivityCauses.WorkingObservation;
        return null;
    }

    /// <summary>
    /// Will an AUTOMATIC request for this session actually reach the judge? The three gates that bind every
    /// unattended trigger, asked synchronously and cheaply: the free checks over the session's own state, the
    /// account's judge switch (a voice session is the standing exception), and the account's ceiling.
    ///
    /// It is asked by the two triggers nobody is waiting on - the detector's turn end and a snooze expiry - so
    /// that a stop which WILL be judged is stamped "reading" before anything is read, and a stop that will not be
    /// judged keeps the detector's red. It is deliberately NOT the judgement itself: the flight asks all of this
    /// again, properly, inside its own boundary - so a stop this answers yes for and the flight then skips shows
    /// reading only until the flight exits.
    /// </summary>
    private bool WillJudgeAutomatic(TenantId tenant, string sid, TurnVerdictSessionState state, TurnVerdictTrigger trigger)
    {
        if (SessionStateSkipCause(state, trigger) is not null) return false;
        var settings = _env.Settings(tenant);
        if (!settings.JudgeEnabled && !_env.IsVoiceSession(tenant, sid)) return false;
        var load = _tenantLoad.TryGetValue(tenant, out var box) ? Volatile.Read(ref box.Value) : 0;
        return load < settings.MaxInFlight;
    }

    private static bool IsExited(SessionDto s)
        => s.Crashed || string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorking(SessionDto s)
        => string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase);

    /// <summary>The closed word stored on a failed record for the card's plain reason.</summary>
    private static string FailureKindWord(TurnVerdictFailureKind failure) => failure switch
    {
        TurnVerdictFailureKind.DidNotAnswer => WingmanFailureKinds.DidNotAnswer,
        TurnVerdictFailureKind.RateLimited => WingmanFailureKinds.RateLimited,
        TurnVerdictFailureKind.Refused => WingmanFailureKinds.Refused,
        _ => WingmanFailureKinds.Unavailable,
    };

    private static string FailureCause(TurnVerdictFailureKind failure) => failure switch
    {
        TurnVerdictFailureKind.DidNotAnswer => ActivityCauses.JudgeDidNotAnswer,
        TurnVerdictFailureKind.RateLimited => ActivityCauses.RateLimited,
        TurnVerdictFailureKind.Refused => ActivityCauses.JudgeRefused,
        _ => ActivityCauses.JudgeUnavailable,
    };

    /// <summary>The trigger word a trace of the carrying-on clock's expiry carries - it is not a request trigger.</summary>
    private const string ClockTrigger = "clock";

    private TurnVerdictTrace NewTrace(string sid, string directorId, TurnVerdictTrigger trigger, string outcome,
        TurnVerdictDto verdict, bool colourEnabled)
        => NewTrace(sid, directorId, TriggerWord(trigger), outcome, verdict, colourEnabled);

    private TurnVerdictTrace NewTrace(string sid, string directorId, string trigger, string outcome,
        TurnVerdictDto verdict, bool colourEnabled) => new()
    {
        TraceId = Guid.NewGuid().ToString("N"),
        SessionId = sid,
        DirectorId = directorId ?? "",
        RecordedAtUtc = _env.NowUtc(),
        TurnEndObservedAtUtc = verdict.TurnEndObservedAtUtc,
        Trigger = trigger,
        Outcome = outcome,
        VerdictId = verdict.VerdictId,
        ColourEnabled = colourEnabled,
        Verdict = verdict,
    };

    /// <summary>A trace for a request that stored no verdict - a skip or a cancellation - stamped with the stop the
    /// request stood on, and no verdict.</summary>
    private TurnVerdictTrace NewUnjudgedTrace(string sid, string? directorId, TurnVerdictTrigger trigger,
        string outcome, string cause, DateTime observedAt, bool colourEnabled)
    {
        return new TurnVerdictTrace
        {
            TraceId = Guid.NewGuid().ToString("N"),
            SessionId = sid,
            DirectorId = directorId ?? "",
            RecordedAtUtc = _env.NowUtc(),
            TurnEndObservedAtUtc = observedAt,
            Trigger = TriggerWord(trigger),
            Outcome = outcome,
            Cause = cause,
            ColourEnabled = colourEnabled,
        };
    }

    private static TurnVerdictTrace WithEvidence(TurnVerdictTrace trace, Flight flight) => trace with
    {
        Package = flight.Package,
        Prompt = flight.Prompt,
        RawReply = flight.RawReply,
        ReplySeconds = flight.ReplySeconds,
    };

    private static string TraceOutcome(TurnVerdictDto record, TurnVerdictFailureKind failure)
    {
        if (!record.Failed) return TurnVerdictTraceOutcomes.Judged;
        return failure switch
        {
            TurnVerdictFailureKind.Refused => TurnVerdictTraceOutcomes.Refused,
            TurnVerdictFailureKind.DidNotAnswer => TurnVerdictTraceOutcomes.DidNotAnswer,
            TurnVerdictFailureKind.RateLimited => TurnVerdictTraceOutcomes.RateLimited,
            _ => TurnVerdictTraceOutcomes.Unavailable,
        };
    }

    private static string TriggerWord(TurnVerdictTrigger trigger) => trigger switch
    {
        TurnVerdictTrigger.TurnEnd => "turn-end",
        TurnVerdictTrigger.Voice => "voice",
        TurnVerdictTrigger.Sweep => "sweep",
        TurnVerdictTrigger.Retry => "retry",
        TurnVerdictTrigger.SnoozeExpiry => "snooze-expiry",
        _ => "on-demand",
    };

    /// <summary>
    /// Wait for every judgement in flight to finish - for shutdown, after <see cref="Dispose"/> has cancelled them, so a
    /// cancelled judgement hands in its trace before the trace writer is closed. Answers false when the timeout passed
    /// first. A flight that ended in a fault counts as finished. A flight writes every row it owes - its own, the stops
    /// that joined it, and the stops queued behind it - before it leaves the gate, so a flight this does not find has
    /// nothing left to hand in.
    ///
    /// IT WAITS UNTIL THE GATE IS EMPTY, not for one snapshot of it: a flight can hand the gate to a successor for the
    /// next stop in line, and that successor is registered before the flight completes, so each pass finds it.
    /// </summary>
    public async Task<bool> WaitForFlightsAsync(TimeSpan timeout)
    {
        var deadline = Task.Delay(timeout);
        while (true)
        {
            var pending = _inFlight.Values.Select(f => f.Done.Task).ToArray();
            if (pending.Length == 0) return true;
            var all = Task.WhenAll(pending);
            if (await Task.WhenAny(all, deadline).ConfigureAwait(false) != all) return false;
        }
    }

    public void Dispose()
    {
        // One step with admission: a request registered before this is in the gate the loop below cancels and the drain
        // waits for; a request after it is refused and recorded as cancelled at shutdown.
        lock (_admission)
        {
            if (_disposed) return;
            _disposed = true;
        }
        foreach (var flight in _inFlight.Values)
        {
            try { flight.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
