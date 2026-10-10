using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Activity;

/// <summary>
/// The Director's activity-evidence producer (docs/PLAN-trustworthy-working-start-2026-07-24.md,
/// increment 2). OBSERVE-ONLY: it changes no state, gates nothing, and its faults never propagate into the
/// paths it watches. It records, into the durable outbox bound for the Gateway's activity ledger:
///
///  - every SUBMISSION at the session choke points (turn-submitted, with who and from where);
///  - every authoritative <see cref="ActivityState"/> TRANSITION, each with a SHADOW CAUSE - the evidence
///    that existed at the moment the current byte-rule made its decision: a submission inside
///    <see cref="SubmissionWindow"/>, an explicit remote-backend signal, terminal output alone, or an
///    honest unknown;
///  - the detector's TERMINAL-OUTPUT-WHILE-SETTLED evidence rows (via
///    <see cref="RecordTerminalOutputWhileSettled"/>);
///  - each NEW ASSISTANT REPLY the conversation ingest stores (via <see cref="RecordTurnObserved"/>) -
///    the ground truth a real turn happened, under a DETERMINISTIC event id so a re-detection of the same
///    reply replays the same identity instead of duplicating it.
///
/// Phase 2 judges the shadow causes against the stored replies; only a driver whose evidence passes the
/// acceptance gates ever opts into submission-gated authoritative behavior. Nothing here switches on the
/// agent kind - the kind is recorded as a fact, never branched on.
/// </summary>
public sealed class ActivityEventProducer : IDisposable
{
    /// <summary>Stamped on every produced event so shadow results stay interpretable across upgrades.</summary>
    public const string DetectorVersion = "shadow-v1";

    /// <summary>How close a submission must be to a Working start to count as its explanation. Wide enough
    /// for the submit-protocol round trip, far narrower than any human think-time gap.</summary>
    internal static readonly TimeSpan SubmissionWindow = TimeSpan.FromSeconds(5);

    /// <summary>How recent terminal output must be for a transition to be attributed to it.</summary>
    internal static readonly TimeSpan TerminalEchoWindow = TimeSpan.FromSeconds(2);

    private readonly SessionManager _sessionManager;
    private readonly ActivityEventOutbox _outbox;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _transitionHandlers = new();
    private readonly ConcurrentDictionary<Guid, Action<SendSource?, InputOrigin?, SubmissionEvidence>> _submitHandlers = new();
    private readonly ConcurrentDictionary<Guid, string> _lastSubmissionCause = new();
    private bool _started;
    private bool _disposed;

    public ActivityEventProducer(SessionManager sessionManager, ActivityEventOutbox outbox)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _sessionManager.OnSessionCreated += Wire;
        _sessionManager.OnSessionRemoved += Unwire;
        foreach (var s in _sessionManager.ListSessions())
            Wire(s);
        FileLog.Write($"[ActivityEventProducer] Start (observe-only, version={DetectorVersion})");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionManager.OnSessionCreated -= Wire;
        _sessionManager.OnSessionRemoved -= Unwire;
        foreach (var s in _sessionManager.ListSessions())
            Unwire(s);
        _transitionHandlers.Clear();
        _submitHandlers.Clear();
    }

    internal void Wire(Session session)
    {
        Action<ActivityState, ActivityState> onTransition = (old, @new) => Guarded(() => RecordTransition(session, old, @new));
        Action<SendSource?, InputOrigin?, SubmissionEvidence> onSubmit = (source, origin, evidence) => Guarded(() => RecordTurnSubmitted(session, source, origin, evidence));
        if (_transitionHandlers.TryAdd(session.Id, onTransition))
            session.OnActivityStateChanged += onTransition;
        if (_submitHandlers.TryAdd(session.Id, onSubmit))
            session.OnTurnSubmitted += onSubmit;
    }

    internal void Unwire(Session session)
    {
        if (_transitionHandlers.TryRemove(session.Id, out var t))
            session.OnActivityStateChanged -= t;
        if (_submitHandlers.TryRemove(session.Id, out var s))
            session.OnTurnSubmitted -= s;
        _lastSubmissionCause.TryRemove(session.Id, out _);
    }

    /// <summary>Observation must never break the observed path: every producer entry point is guarded.</summary>
    private static void Guarded(Action record)
    {
        try { record(); }
        catch (Exception ex) { FileLog.Write($"[ActivityEventProducer] record FAILED: {ex.Message}"); }
    }

    // ---- the producers -----------------------------------------------------------------------------

    private void RecordTurnSubmitted(Session session, SendSource? source, InputOrigin? origin, SubmissionEvidence evidence)
    {
        var cause = SubmissionCause(source, origin);
        _lastSubmissionCause[session.Id] = cause;
        // WHAT THE DOOR KNEW AT ENTRY, onto the ledger row (owner's ruling, 2026-09-05: source logging). Every
        // field is the door's own statement or the choke point's own digest; nothing here is derived.
        var provenance = evidence.Provenance;
        _outbox.Enqueue(Base(session) with
        {
            EventType = ActivityEventTypes.TurnSubmitted,
            Cause = cause,
            SendSource = source?.ToString(),
            InputOrigin = origin is InputOrigin o ? $"{o.ModalityToken}/{o.SurfaceToken}" : null,
            Route = provenance.Route,
            IdentityKind = provenance.IdentityKind,
            TranscriptId = provenance.TranscriptId,
            SpokenSpans = SubmissionProvenance.SpansToText(provenance.SpokenSpans),
            ContentSha256 = evidence.ContentSha256,
            ContentLength = evidence.ContentLength,
        });
    }

    private void RecordTransition(Session session, ActivityState old, ActivityState @new)
    {
        _outbox.Enqueue(Base(session) with
        {
            EventType = @new == ActivityState.Exited ? ActivityEventTypes.SessionExited : ActivityEventTypes.ActivityTransition,
            Cause = ClassifyTransition(session, old, @new),
            PreviousState = old.ToString(),
            NewState = @new.ToString(),
        });
    }

    /// <summary>
    /// The detector saw terminal output on a SETTLED session that no submission explains - the candidate
    /// phantom turn, with its bounded evidence. Called by <c>TerminalStateDetector</c> at the flip site.
    /// </summary>
    public void RecordTerminalOutputWhileSettled(
        Session session, long outputByteCount, string? beforeScreenHash, string? afterScreenHash,
        string? boundedScreenDiff, string detectorMode)
        => Guarded(() => _outbox.Enqueue(Base(session) with
        {
            EventType = ActivityEventTypes.TerminalOutputWhileSettled,
            Cause = ActivityCauses.TerminalOutputOnly,
            DetectorMode = detectorMode,
            OutputByteCount = outputByteCount,
            BeforeScreenHash = beforeScreenHash,
            AfterScreenHash = afterScreenHash,
            BoundedScreenDiff = boundedScreenDiff,
        }));

    /// <summary>
    /// The conversation ingest detected a NEW assistant reply - the ground truth that a real turn
    /// happened. <paramref name="dedupKey"/> is the ingest's own content watermark key; the event id is
    /// derived from it DETERMINISTICALLY, so the retry path (a reply re-detected because an earlier
    /// prompt push failed) replays the same identity and the Gateway acknowledges a duplicate instead of
    /// storing a second observation.
    /// </summary>
    public void RecordTurnObserved(Session session, DateTime replyTsUtc, string? contextId, string dedupKey)
        => Guarded(() => _outbox.Enqueue(Base(session) with
        {
            EventId = DeterministicEventId(session.Id, dedupKey),
            OccurredUtc = replyTsUtc,
            ContextId = contextId ?? session.ClaudeSessionId,
            EventType = ActivityEventTypes.TurnObservedInTranscript,
            Cause = ActivityCauses.DriverCompletion,
        }));

    // ---- classification ----------------------------------------------------------------------------

    /// <summary>
    /// The shadow cause of one authoritative transition - the evidence that existed at the moment the
    /// current rule decided. Recorded, never acted on.
    /// </summary>
    internal string ClassifyTransition(Session session, ActivityState old, ActivityState @new)
    {
        var now = DateTime.UtcNow;
        var wasRunning = old is ActivityState.Working or ActivityState.Starting;
        var isRunning = @new is ActivityState.Working or ActivityState.Starting;

        if (@new == ActivityState.Exited)
            return ActivityCauses.SessionExit;

        if (isRunning && !wasRunning)
        {
            if (session.LastSubmissionAtUtc is DateTime submitted && now - submitted <= SubmissionWindow)
                return _lastSubmissionCause.TryGetValue(session.Id, out var cause) ? cause : ActivityCauses.OwnerSubmit;
            if (session.IsRemote)
                return ActivityCauses.BackendSignal;
            if (session.Buffer is { } buffer && now - buffer.LastWriteAtUtc <= TerminalEchoWindow)
                return ActivityCauses.TerminalOutputOnly;
            return ActivityCauses.Unknown;
        }

        if (wasRunning && @new is ActivityState.WaitingForInput or ActivityState.WaitingForPerm)
            return ActivityCauses.QuietThreshold;

        return ActivityCauses.Unknown;
    }

    /// <summary>Who drove a submission, from the same facts the owner-turn rule reads: a tagged human
    /// origin or an owner-driven source is the owner; agent and framework name themselves; a raw submit
    /// byte with no origin and no source is honestly unknown.</summary>
    internal static string SubmissionCause(SendSource? source, InputOrigin? origin)
    {
        if (origin is not null || source is SendSource.UserInput or SendSource.Delivery)
            return ActivityCauses.OwnerSubmit;
        return source switch
        {
            SendSource.Agent => ActivityCauses.AgentSubmit,
            SendSource.Framework => ActivityCauses.FrameworkSubmit,
            _ => ActivityCauses.Unknown,
        };
    }

    // ---- record plumbing ---------------------------------------------------------------------------

    /// <summary>The shared facts of every event this Director produces. The outbox mints the identity.</summary>
    private ActivityEventRecord Base(Session session) => new()
    {
        EventId = Guid.Empty,
        DirectorSequence = 0,
        OccurredUtc = DateTime.UtcNow,
        DirectorId = string.IsNullOrWhiteSpace(_sessionManager.DirectorId) ? "unknown-director" : _sessionManager.DirectorId,
        SessionId = session.Id.ToString(),
        Machine = Environment.MachineName,
        AgentKind = session.AgentKind.ToString(),
        ContextId = session.ClaudeSessionId,
        EventType = ActivityEventTypes.ActivityTransition,
        Cause = ActivityCauses.Unknown,
        DetectorVersion = DetectorVersion,
    };

    /// <summary>A stable event id from the observation's content key: the same reply always derives the
    /// same id, which is what makes transcript re-detection idempotent end to end.</summary>
    internal static Guid DeterministicEventId(Guid sessionId, string dedupKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"turn-observed|{sessionId:N}|{dedupKey}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
