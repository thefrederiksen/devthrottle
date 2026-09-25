using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Voice;

/// <summary>
/// The names of every delivery decision the Gateway writes into an upload's decision log
/// (<c>decisions.jsonl</c>, beside its <c>record.json</c>). One place, so a reader of the log and a writer of
/// it cannot spell a decision two ways.
///
/// WHY THE LOG EXISTS (Voice Delivery mission, 25 September 2026). That morning the owner's spoken prompts
/// were dropped, doubled and left unsent, and the timeline had to be rebuilt from his Director's log and the
/// agents' own conversation files - because the hosted Gateway's log lives in the container's temporary
/// folder and is not kept, and a finished upload's record was deleted the moment the client acknowledged it.
/// Every decision is now written down, keyed by the upload id (the delivery id), in the upload's own durable
/// directory, so "what happened to my words?" is answerable afterwards without the container's log.
///
/// THE LOG NEVER HOLDS THE WORDS. It records lengths, counts, states and error text only; the transcript
/// lives on the record under the record's own retention rules, and <see cref="DeliveryDecisionFacts"/> has
/// no field that could carry it.
/// </summary>
public static class DeliveryDecisions
{
    /// <summary>The upload was registered (or re-opened) and holds a PENDING marker.</summary>
    public const string Received = "received";
    /// <summary>The assembled audio was turned into text. Facts carry the character count, never the text.</summary>
    public const string Transcribed = "transcribed";
    /// <summary>The composed message was handed to the owning Director's prompt verb.</summary>
    public const string SentToDirector = "sent-to-director";
    /// <summary>The Director answered the prompt verb: accepted, or refused with its error.</summary>
    public const string DirectorAnswer = "director-answer";
    /// <summary>A complete arrived as a resumed attempt (every attempt after the client's first).</summary>
    public const string Retried = "retried";
    /// <summary>A FAILED record was put back to PENDING so an explicit retry can re-drive it.</summary>
    public const string ClearedFailed = "cleared-failed";
    /// <summary>
    /// The byte rule judged the session to have moved on and did not deliver. That rule was deleted in phase 2 of the
    /// Voice Delivery mission and nothing writes this name any more; it stays so the records written before then
    /// still read with the name they were written under. Its replacement is <see cref="TooOld"/>.
    /// </summary>
    public const string MovedOn = "moved-on";
    /// <summary>
    /// The recording was more than the age limit old from Send when its words were known not to be in the session,
    /// so it was not typed: it is kept and shown back to the owner with "Send anyway". Facts carry the age in
    /// seconds (<see cref="DeliveryDecisionFacts.AgeSeconds"/>). See <c>GatewayDictationEndpoint.MaxDeliveryAgeMinutes</c>.
    /// </summary>
    public const string TooOld = "too-old";
    /// <summary>
    /// The Gateway asked the Director what became of this delivery id instead of guessing. The reason says why:
    /// <see cref="AskReasonPromptUnanswered"/> (the prompt verb went out and no answer came back) or
    /// <see cref="AskReasonRetryAsksFirst"/> (a retry of a recording already sent once asks before paying for a
    /// transcript).
    /// </summary>
    public const string AskedDirector = "asked-director";
    /// <summary>
    /// The Director's answer to <see cref="AskedDirector"/>: the delivery state it holds for the id
    /// (<see cref="DeliveryDecisionFacts.State"/>), or, when it gave none, which kind of no-answer it was
    /// (<see cref="DeliveryDecisionFacts.Reason"/>: <c>no-answer</c>, <c>director-too-old</c> or
    /// <c>never-left-the-gateway</c>).
    /// </summary>
    public const string DeliveryStateAnswer = "delivery-state-answer";
    /// <summary>
    /// The recording may already be in the session, so it is HELD as still delivering: the client is answered 202,
    /// keeps its copy and asks again. Never shown back and never judged too old. Facts carry the Director's state
    /// as the client is told it (<c>delivering</c>, <c>unknown</c> or <c>no-answer</c>).
    /// </summary>
    public const string StillDelivering = "still-delivering";
    /// <summary>
    /// A verified "Send anyway" claim whose prompt stopped on the Gateway before it reached the Director, so nothing
    /// was typed and the Director never answered; the reason says where it stopped (the menu guard, the session
    /// lookup, no tenant for the menu guard).
    /// </summary>
    public const string ClaimStoppedBeforeDirector = "send-anyway-stopped-before-director";

    /// <summary>Why the Gateway asked: the prompt verb went out and no answer came back.</summary>
    public const string AskReasonPromptUnanswered = "prompt-unanswered";
    /// <summary>Why the Gateway asked: a retry of a recording already sent once asks before it transcribes again.</summary>
    public const string AskReasonRetryAsksFirst = "retry-asks-first";
    /// <summary>The session had exited, so the recording was resolved without being typed anywhere.</summary>
    public const string SessionExited = "session-exited";
    /// <summary>The upload reached the DELIVERED tombstone with the words submitted (or an empty clip resolved).</summary>
    public const string Delivered = "delivered";
    /// <summary>The upload was parked FAILED (a transcription failure), its chunks kept for a retry.</summary>
    public const string Failed = "failed";
    /// <summary>The upload reached the ABANDONED tombstone: given up by the user, or expired with no activity.</summary>
    public const string Abandoned = "abandoned";
    /// <summary>The client acknowledged the outcome; the audio and the words were deleted, the record kept.</summary>
    public const string Acknowledged = "acknowledged";
    /// <summary>The assembled recording was empty, so nothing was transcribed; the audio was deleted and the
    /// record retired as an acknowledgement leaves it. The client is answered an error, as before.</summary>
    public const string EmptyRecording = "empty-recording";
    /// <summary>The upload is missing chunks; the client re-sends them and completes again.</summary>
    public const string Incomplete = "incomplete";
    /// <summary>The target session could not be found, so nothing was transcribed or typed.</summary>
    public const string SessionNotFound = "session-not-found";
    /// <summary>The complete stopped on an error before any delivery was decided (no transcription key, an exception).</summary>
    public const string CompleteError = "complete-error";
    /// <summary>
    /// A "Send anyway" claim naming this recording was believed: the prompt carries the recording's delivery id, so
    /// the Director refuses it if the words are already in. Facts carry the record's state and the reason.
    /// </summary>
    public const string ClaimVerified = "send-anyway-claim-verified";
    /// <summary>
    /// A "Send anyway" claim naming this recording was dropped, and the words went as an ordinary prompt; the reason
    /// says why (another session, past the claim window, a record that cannot be read).
    /// </summary>
    public const string ClaimDropped = "send-anyway-claim-dropped";
    /// <summary>
    /// The Director's answer to a "Send anyway" that carried this recording's delivery id: accepted and typed, or
    /// refused with nothing typed because the id was already delivered or being delivered
    /// (<see cref="DeliveryDecisionFacts.RefusedDuplicate"/>), or failed.
    /// </summary>
    public const string ClaimDirectorAnswer = "send-anyway-director-answer";
    /// <summary>A line of the log that could not be parsed on read, for example one half-written by a crash.</summary>
    public const string UnreadableLine = "unreadable-line";
}

/// <summary>
/// The small facts written with one decision. Every field is optional and a field left null is not written.
/// Deliberately a closed set of numbers, flags, states and short codes - there is no field for the words,
/// so the words cannot reach the log by accident.
/// </summary>
public sealed record DeliveryDecisionFacts
{
    /// <summary>The longest error text kept on a line. Error text is a diagnostic, not a transcript.</summary>
    public const int MaxErrorLength = 500;

    private readonly string? _error;

    public string? SessionId { get; init; }
    public string? State { get; init; }
    public string? PreviousState { get; init; }
    public string? Reason { get; init; }
    public int? StatusCode { get; init; }
    public bool? Ok { get; init; }
    public bool? Resumed { get; init; }
    public bool? Submitted { get; init; }
    public bool? MovedOn { get; init; }
    public bool? SpokenAlone { get; init; }
    /// <summary>
    /// The Director typed nothing because this delivery id had already been delivered: a second copy it refused.
    /// Written on the <see cref="DeliveryDecisions.DirectorAnswer"/> line, where "why was this delivered only once"
    /// is read from.
    /// </summary>
    public bool? RefusedDuplicate { get; init; }
    /// <summary>A character count: of the transcript, or of the composed message. Never the characters.</summary>
    public int? Characters { get; init; }
    public long? AudioBytes { get; init; }
    public long? BytesDeleted { get; init; }
    public int? TotalChunks { get; init; }
    public int? MissingChunks { get; init; }
    /// <summary>When the recording was resolved, on a "Send anyway" claim line: the claim window is measured from it.</summary>
    public DateTime? ResolvedAtUtc { get; init; }
    /// <summary>When the owner pressed Send, as the client stamped it on the complete call.</summary>
    public DateTime? SentAtUtc { get; init; }
    /// <summary>How long after Send the recording was judged, in whole seconds, on a <see cref="DeliveryDecisions.TooOld"/> line.</summary>
    public long? AgeSeconds { get; init; }

    /// <summary>Error text, cut to <see cref="MaxErrorLength"/> characters.</summary>
    public string? Error
    {
        get => _error;
        init => _error = value is { Length: > MaxErrorLength } ? value[..MaxErrorLength] : value;
    }
}

/// <summary>One line of an upload's decision log: when, which decision, and its facts.</summary>
public sealed record DeliveryDecisionLine(DateTime AtUtc, string Decision, DeliveryDecisionFacts? Facts)
{
    /// <summary>The one serializer shape for the log file and the read route: camel case, nulls left out.</summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

/// <summary>
/// What <see cref="VoiceUploadStore.ReadDecisions"/> found for one upload id: whether the upload is known at
/// all in this partition, how its record read, and the decision lines in the order they were written.
/// </summary>
public sealed record DeliveryDecisionLog(
    bool Found,
    DictationRecordRead Record,
    IReadOnlyList<DeliveryDecisionLine> Lines)
{
    public static readonly DeliveryDecisionLog NotFound =
        new(false, DictationRecordRead.Absent, Array.Empty<DeliveryDecisionLine>());
}
