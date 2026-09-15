using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One activity-ledger event on its way from a producer to the Gateway's durable activity ledger, and the
/// shape the Gateway serves back (the trustworthy-Working-start plan,
/// docs/PLAN-trustworthy-working-start-2026-07-24.md).
///
/// The ledger answers, from structured history alone: what caused a session to enter or leave Working, was
/// there a submitted turn or only terminal output, which detector decided, did a transcript later prove a
/// real turn, and why did a snooze end. Two producers write it: a DIRECTOR pushes what it observed
/// (submissions, terminal output, state transitions, transcript observations), and the GATEWAY appends its
/// own snooze lifecycle decisions.
///
/// IDEMPOTENCY: <see cref="EventId"/> is minted ONCE by the producer before the event enters its outbox and
/// is preserved across retries; the Gateway treats a replayed id as an already-acknowledged duplicate, never
/// an error and never a second row. <see cref="DirectorSequence"/> is the producer's own monotonic ordering
/// (0 on Gateway-origin events), so a batch that retries after a partial failure cannot reorder history.
/// </summary>
public sealed record ActivityEventRecord
{
    /// <summary>The producer-minted identity of this event. The idempotency key: replaying it is a no-op.</summary>
    [JsonPropertyName("eventId")] public required Guid EventId { get; init; }

    /// <summary>The producer's own monotonic sequence for this event (0 on Gateway-origin events).</summary>
    [JsonPropertyName("directorSequence")] public required long DirectorSequence { get; init; }

    /// <summary>When the event actually happened (UTC, producer-stamped).</summary>
    [JsonPropertyName("occurredUtc")] public required DateTime OccurredUtc { get; init; }

    /// <summary>The Director the event belongs to ("gateway" on Gateway-origin snooze events whose owning
    /// Director is unknown).</summary>
    [JsonPropertyName("directorId")] public required string DirectorId { get; init; }

    /// <summary>The Director session (the window/slot) the event is about.</summary>
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }

    /// <summary>Which machine the session runs on, when known.</summary>
    [JsonPropertyName("machine")] public string? Machine { get; init; }

    /// <summary>The agent kind of the session's driver (e.g. "Claude"), when known.</summary>
    [JsonPropertyName("agentKind")] public string? AgentKind { get; init; }

    /// <summary>The agent's own context id at the time, when known (groups events with the prompt log).</summary>
    [JsonPropertyName("contextId")] public string? ContextId { get; init; }

    /// <summary>What happened - one of <see cref="ActivityEventTypes"/>.</summary>
    [JsonPropertyName("eventType")] public required string EventType { get; init; }

    /// <summary>The state before the event (an <c>ActivityState</c> name, or a hold state on snooze
    /// events), when the event is a transition.</summary>
    [JsonPropertyName("previousState")] public string? PreviousState { get; init; }

    /// <summary>The state after the event, when the event is a transition.</summary>
    [JsonPropertyName("newState")] public string? NewState { get; init; }

    /// <summary>Why it happened - one of <see cref="ActivityCauses"/>.</summary>
    [JsonPropertyName("cause")] public required string Cause { get; init; }

    /// <summary>A short structured control-flow note (a deadline, a requested length, a retired-edge note).
    /// NEVER prompt or terminal content.</summary>
    [JsonPropertyName("detail")] public string? Detail { get; init; }

    /// <summary>Where the input came from on a submission event (e.g. "desktop", "cockpit", "voice").</summary>
    [JsonPropertyName("inputOrigin")] public string? InputOrigin { get; init; }

    /// <summary>The send source on a submission event (the Director's <c>SendSource</c> name).</summary>
    [JsonPropertyName("sendSource")] public string? SendSource { get; init; }

    /// <summary>The detector mode that ruled (e.g. "byte", "body", "shadow"), on detector events.</summary>
    [JsonPropertyName("detectorMode")] public string? DetectorMode { get; init; }

    /// <summary>The detector version that ruled, so shadow results stay interpretable across upgrades.</summary>
    [JsonPropertyName("detectorVersion")] public string? DetectorVersion { get; init; }

    /// <summary>How many terminal bytes were seen, on terminal-output evidence events.</summary>
    [JsonPropertyName("outputByteCount")] public long? OutputByteCount { get; init; }

    /// <summary>Normalized screen-body hash BEFORE the output burst, on terminal-output evidence events.</summary>
    [JsonPropertyName("beforeScreenHash")] public string? BeforeScreenHash { get; init; }

    /// <summary>Normalized screen-body hash AFTER the output burst.</summary>
    [JsonPropertyName("afterScreenHash")] public string? AfterScreenHash { get; init; }

    /// <summary>A BOUNDED normalized changed-row diff (never the raw byte stream, never unbounded). May
    /// contain terminal content, so it is tenant-scoped customer data - never logged to process logs.</summary>
    [JsonPropertyName("boundedScreenDiff")] public string? BoundedScreenDiff { get; init; }

    // ---- what the prompt's door knew at entry (owner's ruling, 2026-09-05: source logging) - turn-submitted only

    /// <summary>The door the prompt came through: desktop-terminal, desktop-composer, desktop-dictation,
    /// gateway-prompt, gateway-dictation, gateway-terminal, fleet-message, queue-drain, framework.</summary>
    [JsonPropertyName("route")] public string? Route { get; init; }

    /// <summary>The credential kind behind the caller: local-user, device, machine-token, session, framework, unknown.</summary>
    [JsonPropertyName("identityKind")] public string? IdentityKind { get; init; }

    /// <summary>The transcript's identifier when the door had one, else null.</summary>
    [JsonPropertyName("transcriptId")] public string? TranscriptId { get; init; }

    /// <summary>The character ranges of the sent text that came from a transcript, as "start+length" pairs,
    /// comma-separated, in text order; null when none did.</summary>
    [JsonPropertyName("spokenSpans")] public string? SpokenSpans { get; init; }

    /// <summary>SHA-256 of the UTF-8 text sent, lower-case hex; null on the raw keystroke path, where the text
    /// is never in hand.</summary>
    [JsonPropertyName("contentSha256")] public string? ContentSha256 { get; init; }

    /// <summary>The length of the text sent, or the printable keystrokes since the last submit on the raw path.</summary>
    [JsonPropertyName("contentLength")] public long? ContentLength { get; init; }
}

/// <summary>
/// The closed set of activity-ledger event types. Wire values are string constants, not enums - the
/// contracts assembly is reference-free and old readers must tolerate new writers (the <see cref="HoldStates"/>
/// convention). The Gateway validates every appended event against this set.
/// </summary>
public static class ActivityEventTypes
{
    /// <summary>A real submission entered the session (typed Enter, Cockpit, voice, queue, agent-to-agent).</summary>
    public const string TurnSubmitted = "turn-submitted";

    /// <summary>A remote backend explicitly reported activity started (e.g. a run began).</summary>
    public const string BackendActivityStarted = "backend-activity-started";

    /// <summary>Terminal output arrived while the session was SETTLED and no submission explains it - the
    /// evidence row for a candidate phantom turn (carries the bounded terminal evidence fields).</summary>
    public const string TerminalOutputWhileSettled = "terminal-output-while-settled";

    /// <summary>The session's authoritative <c>ActivityState</c> actually changed.</summary>
    public const string ActivityTransition = "activity-transition";

    /// <summary>The conversation ingest observed a new assistant reply - ground truth that a real turn
    /// happened, used to judge the shadow rule.</summary>
    public const string TurnObservedInTranscript = "turn-observed-in-transcript";

    /// <summary>The session exited.</summary>
    public const string SessionExited = "session-exited";

    /// <summary>A snooze was created or refreshed (armed with a deadline, or deferred with a length).</summary>
    public const string SnoozeCreated = "snooze-created";

    /// <summary>A deferred snooze landed: the work ended and its clock started.</summary>
    public const string SnoozeLanded = "snooze-landed";

    /// <summary>A snooze entry was retired - the cause says why (the July 24 question).</summary>
    public const string SnoozeEnded = "snooze-ended";

    /// <summary>
    /// The session supervisor classified a terminating fault on a session that has just gone idle (issue
    /// #915). The cause carries the fault class; the detail carries the matched SIGNATURE (our own token,
    /// never the terminal line it came from).
    /// </summary>
    public const string SupervisorFaultDetected = "supervisor-fault-detected";

    /// <summary>The supervisor is waiting before it re-sends "continue" - the detail carries the attempt
    /// number and the delay it chose.</summary>
    public const string SupervisorWaiting = "supervisor-waiting";

    /// <summary>The supervisor sent "continue" into the session - the detail carries the attempt number and
    /// whether the send landed.</summary>
    public const string SupervisorContinueSent = "supervisor-continue-sent";

    /// <summary>The supervised session started working again, so the recovery episode ended successfully.</summary>
    public const string SupervisorRecovered = "supervisor-recovered";

    /// <summary>The supervisor raised its hand instead of acting: a non-recoverable fault, an unclassified
    /// one, a menu owning the screen, or the retry ceiling. The cause says which. This event type means a
    /// PERSON WAS TOLD - it is never used for an episode that merely ended.</summary>
    public const string SupervisorEscalated = "supervisor-escalated";

    /// <summary>The supervisor stopped without acting and without raising a hand - the session is gone, or its
    /// state is no longer one a "continue" may be sent to. Nothing needed a person's attention.</summary>
    public const string SupervisorStoodDown = "supervisor-stood-down";

    /// <summary>The Wingman judged a stop and its answer was ACCEPTED by the turn-verdict contract (the
    /// Wingman-on-every-turn mission). The detail carries the verdict word, the package kind, the verdict id,
    /// the trigger and the model - never a word of the screen or the conversation.</summary>
    public const string TurnVerdictJudged = "turn-verdict-judged";

    /// <summary>The Wingman did not ask the judge because the screen is the one it last judged, so the stored
    /// verdict still describes it. The cause is <see cref="ActivityCauses.ScreenUnchanged"/>.</summary>
    public const string TurnVerdictReused = "turn-verdict-reused";

    /// <summary>The Wingman asked the judge and got no usable verdict: no answer, a rate limit, or an answer
    /// the contract refused. The cause says which. A failed record is stored and the row stays red.</summary>
    public const string TurnVerdictFailed = "turn-verdict-failed";

    /// <summary>The Wingman did not judge this stop at all, and the cause says why - most importantly
    /// <see cref="ActivityCauses.Held"/>, a session a live owning session is holding, which is never read.</summary>
    public const string TurnVerdictSkipped = "turn-verdict-skipped";

    /// <summary>A judgement in flight was abandoned because the session started working again, so its answer
    /// would have described a screen that no longer exists. Nothing is stored.</summary>
    public const string TurnVerdictCancelled = "turn-verdict-cancelled";

    /// <summary>Every legal event type, for validation.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        TurnSubmitted, BackendActivityStarted, TerminalOutputWhileSettled, ActivityTransition,
        TurnObservedInTranscript, SessionExited, SnoozeCreated, SnoozeLanded, SnoozeEnded,
        SupervisorFaultDetected, SupervisorWaiting, SupervisorContinueSent, SupervisorRecovered,
        SupervisorEscalated, SupervisorStoodDown,
        TurnVerdictJudged, TurnVerdictReused, TurnVerdictFailed, TurnVerdictSkipped, TurnVerdictCancelled,
    };
}

/// <summary>
/// The closed set of activity-ledger causes - WHY an event happened. String constants on the wire, same
/// convention as <see cref="ActivityEventTypes"/>.
/// </summary>
public static class ActivityCauses
{
    /// <summary>The owner submitted the turn.</summary>
    public const string OwnerSubmit = "owner-submit";

    /// <summary>Another agent submitted the turn (agent-to-agent prompt).</summary>
    public const string AgentSubmit = "agent-submit";

    /// <summary>A framework/queue path submitted the turn.</summary>
    public const string FrameworkSubmit = "framework-submit";

    /// <summary>An explicit backend activity signal (remote run status), not terminal bytes.</summary>
    public const string BackendSignal = "backend-signal";

    /// <summary>Only terminal output was observed - no submission, no backend signal.</summary>
    public const string TerminalOutputOnly = "terminal-output-only";

    /// <summary>The quiet threshold elapsed (the settle edge).</summary>
    public const string QuietThreshold = "quiet-threshold";

    /// <summary>The driver reported positive completion.</summary>
    public const string DriverCompletion = "driver-completion";

    /// <summary>The owner drove a turn (supersedes a hold).</summary>
    public const string OwnerTurn = "owner-turn";

    /// <summary>A manual release (the owner turned the alarm off).</summary>
    public const string ManualRelease = "manual-release";

    /// <summary>The snooze deadline elapsed - the timer itself ended the snooze.</summary>
    public const string TimerExpired = "timer-expired";

    /// <summary>The session exited.</summary>
    public const string SessionExit = "session-exit";

    /// <summary>The session was reported Working - the "work ends a snooze" policy fired. THE July 24
    /// cause: whether that Working was real is what the rest of the ledger proves.</summary>
    public const string WorkingObservation = "working-observation";

    /// <summary>A snooze was asked for (created or refreshed).</summary>
    public const string SnoozeRequested = "snooze-requested";

    /// <summary>The work a deferred snooze was waiting on settled, landing it.</summary>
    public const string WorkSettled = "work-settled";

    /// <summary>The owning Director was removed from the fleet.</summary>
    public const string DirectorRemoved = "director-removed";

    /// <summary>The session is no longer in its Director's authoritative live list.</summary>
    public const string SessionNotLive = "session-not-live";

    /// <summary>The producer could not decide (the shadow classifier's honest "unknown").</summary>
    public const string Unknown = "unknown";

    /// <summary>A transient transport fault ended the turn - a name-resolution failure, a reset connection,
    /// a dropped socket (issue #915). The class the supervisor recovers from.</summary>
    public const string TransientTransport = "transient-transport";

    /// <summary>The model provider rate-limited the turn, so the supervisor backs off and resumes.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>The agent's context window filled up. Recovering it needs a compaction, which is phase 2
    /// (thefrederiksen/devthrottle_internal#1403), so phase 1 escalates rather than sending into a session
    /// that swallows prompts.</summary>
    public const string ContextFull = "context-full";

    /// <summary>The work itself cannot proceed - out of allowance, out of credits, a failed sign-in. Never
    /// auto-continued.</summary>
    public const string NonRecoverable = "non-recoverable";

    /// <summary>The turn ended on a fault the deterministic classifier does not recognize, and the model
    /// fallback either was switched off or could not decide.</summary>
    public const string UnclassifiedFault = "unclassified-fault";

    /// <summary>A menu owns the session's screen, so typing "continue" would answer it. The supervisor
    /// refuses and raises its hand.</summary>
    public const string MenuOwnsScreen = "menu-owns-screen";

    /// <summary>The supervisor exhausted its retry ceiling, so a real outage raises a hand instead of
    /// retrying forever.</summary>
    public const string RetryCeiling = "retry-ceiling";

    /// <summary>The judge answered and the contract accepted the answer.</summary>
    public const string JudgeAnswered = "judge-answered";

    /// <summary>The screen is the one the stored verdict was formed on, so no judge was asked.</summary>
    public const string ScreenUnchanged = "screen-unchanged";

    /// <summary>The judge did not answer within its timeout, or the call never reached it.</summary>
    public const string JudgeDidNotAnswer = "judge-did-not-answer";

    /// <summary>The judge answered and the turn-verdict contract refused the answer.</summary>
    public const string JudgeRefused = "judge-refused";

    /// <summary>The judge could not be asked at all - no account key, or the provider answered an error.</summary>
    public const string JudgeUnavailable = "judge-unavailable";

    /// <summary>A live owning session holds this session, so it is not the owner's to be read.</summary>
    public const string Held = "held";

    /// <summary>The session has taken no turn yet, so there is no stop to judge.</summary>
    public const string BrandNew = "brand-new";

    /// <summary>This account has not switched turn judging on.</summary>
    public const string JudgeSwitchOff = "judge-switch-off";

    /// <summary>This account already has as many judgements in flight as its ceiling allows.</summary>
    public const string InFlightCap = "in-flight-cap";

    /// <summary>A judgement for this same session is already in flight, so a second stop is not queued.</summary>
    public const string AlreadyJudging = "already-judging";

    /// <summary>A voice narration's speech re-attempt found no verdict it could reuse. A re-attempt never asks the
    /// judge, so it gives up for that stop instead of making a second model call for it.</summary>
    public const string ReattemptNeverJudges = "reattempt-never-judges";

    /// <summary>Every legal cause, for validation.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        OwnerSubmit, AgentSubmit, FrameworkSubmit, BackendSignal, TerminalOutputOnly, QuietThreshold,
        DriverCompletion, OwnerTurn, ManualRelease, TimerExpired, SessionExit, WorkingObservation,
        SnoozeRequested, WorkSettled, DirectorRemoved, SessionNotLive, Unknown,
        TransientTransport, RateLimited, ContextFull, NonRecoverable, UnclassifiedFault,
        MenuOwnsScreen, RetryCeiling,
        JudgeAnswered, ScreenUnchanged, JudgeDidNotAnswer, JudgeRefused, JudgeUnavailable,
        Held, BrandNew, JudgeSwitchOff, InFlightCap, AlreadyJudging, ReattemptNeverJudges,
    };
}

/// <summary>A producer's push of activity events to the Gateway's ledger.</summary>
public sealed record ActivityEventIngestRequest
{
    [JsonPropertyName("events")] public required IReadOnlyList<ActivityEventRecord> Events { get; init; }
}

/// <summary>
/// What the Gateway durably holds after the batch, so the producer can drop acknowledged outbox records
/// honestly: an event is acknowledged when it was <see cref="Written"/> now or was already there
/// (<see cref="Duplicates"/> - a successful idempotent replay, not an error).
/// </summary>
public sealed record ActivityEventIngestResponse
{
    [JsonPropertyName("written")] public required int Written { get; init; }
    [JsonPropertyName("duplicates")] public required int Duplicates { get; init; }
}
