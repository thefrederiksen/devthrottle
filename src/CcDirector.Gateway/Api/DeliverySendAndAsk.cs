using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Voice;

namespace CcDirector.Gateway.Api;

/// <summary>
/// What a send of a recording's words to the Director came to, once the Gateway has asked when it had to.
/// </summary>
internal enum DeliverySendKind
{
    /// <summary>The words are in: accepted and finished, refused as a copy of one already delivered, or the question
    /// answered delivered. <see cref="DeliverySendResult.Body"/> is the Director's prompt answer when there was one,
    /// null when the question settled it.</summary>
    Delivered,

    /// <summary>The Director says it is still typing them: accepted and carrying on, refused as a copy of one being
    /// delivered, or the question answered delivering. The words may be in - hold, never retype, never show back.</summary>
    StillDelivering,

    /// <summary>The send went out, no answer came back, and the question got no answer either (the Director too old
    /// to be asked, silent, or not connected). The words may be in - hold.</summary>
    NoAnswer,

    /// <summary>The send went out, no answer came back, and the Director says it never saw the delivery id. The words
    /// are not in now, but the send that went out may still arrive; what a caller does with that is its own rule.</summary>
    NeverSeen,

    /// <summary>Definitely not in: nothing left this Gateway, the Director refused before touching the session or in a
    /// state that is not a delivery, or the question answered not delivered. Retryable.</summary>
    NotDelivered,
}

/// <summary>What <see cref="DeliverySendAndAsk.SendAsync"/> came to. <paramref name="NoAnswerKind"/> says which kind of no
/// answer the question got (<c>no-answer</c>, <c>director-too-old</c>, <c>never-left-the-gateway</c>), on
/// <see cref="DeliverySendKind.NoAnswer"/> only.
/// <paramref name="NeverLeft"/> is true on <see cref="DeliverySendKind.NotDelivered"/> when the prompt never left this
/// Gateway at all - the Director is not connected - which the owned delivery holds as "waiting for the Director" rather
/// than "retrying" (Voice Delivery phase 5).</summary>
internal sealed record DeliverySendResult(DeliverySendKind Kind, PromptResponse? Body, string? Error, bool RefusedDuplicate = false,
    string? NoAnswerKind = null, bool NeverLeft = false);

/// <summary>What the prompt verb's own answer meant, before any question: the fact a caller writes as the Director's answer.</summary>
internal sealed record PromptAnswerReading(bool Unanswered, bool Delivered, string? Error, bool RefusedDuplicate);

/// <summary>
/// THE ONE PLACE THE GATEWAY SENDS A RECORDING'S WORDS AND, WHEN NO ANSWER COMES BACK, ASKS (Voice Delivery mission,
/// phase 2). Both routes that carry a delivery id use it: the dictation complete, and the prompt route with a verified
/// "Send anyway" claim. One mechanism, so the two can never disagree about what an unanswered send means.
///
/// On 25 September 2026 a slow success was read as a failure: the Gateway stopped waiting for the prompt verb at 30
/// seconds, told the client to retry, and the owner's words reached the agent twice. The Director is the one place
/// that knows whether a delivery happened, so an unanswered send is followed by the question "what became of delivery
/// id X?" and never by a guess.
///
/// Every step is written to the recording's decision log: the Director's answer (through the caller's own writer, since
/// the two routes name that line differently), the question and why it was asked, and the Director's answer to it -
/// its delivery state, or which kind of no-answer it was. Lengths, states and times only, never the words.
/// </summary>
internal static class DeliverySendAndAsk
{
    /// <summary>
    /// Send <paramref name="prompt"/> (which carries <paramref name="deliveryId"/>) and, when no answer comes back, ask.
    /// <paramref name="recordAnswer"/> writes the Director's answer to the send, before any question, with the prompt
    /// verb's outcome and what it was read to mean.
    /// </summary>
    public static async Task<DeliverySendResult> SendAsync(SessionVerbClient route, VoiceUploadStore store, string uploadId,
        string sid, string deliveryId, PromptRequest prompt,
        Action<SessionVerbClient.PromptSendOutcome, PromptAnswerReading> recordAnswer)
    {
        var sent = await route.SendPromptAsync(sid, prompt);
        var (kind, error, refusedDuplicate) = Read(sent);
        recordAnswer(sent, new PromptAnswerReading(kind is null, kind == DeliverySendKind.Delivered, error, refusedDuplicate));
        if (kind is { } settled)
            return new DeliverySendResult(settled, sent.Body, error, refusedDuplicate,
                NeverLeft: sent.Kind == SessionVerbClient.PromptSendKind.NeverLeftTheGateway);

        // UNANSWERED: the prompt went out and no answer came back. Ask; never call it a failure.
        var asked = await AskAsync(store, uploadId, sid, deliveryId, route, DeliveryDecisions.AskReasonPromptUnanswered);
        if (asked.Kind != SessionVerbClient.DeliveryStateAskKind.Answered)
            return new DeliverySendResult(DeliverySendKind.NoAnswer, null, asked.Detail, NoAnswerKind: NoAnswerName(asked.Kind));
        return asked.Answer!.State switch
        {
            DeliveryState.Delivered => new DeliverySendResult(DeliverySendKind.Delivered, null, null),
            DeliveryState.Delivering => new DeliverySendResult(DeliverySendKind.StillDelivering, null, null),
            DeliveryState.Unknown => new DeliverySendResult(DeliverySendKind.NeverSeen, null,
                $"the Director never received delivery {deliveryId}; the send that went out got no answer ({error})"),
            DeliveryState.NotDelivered => new DeliverySendResult(DeliverySendKind.NotDelivered, null,
                $"the Director did not deliver the words: {asked.Answer.Reason ?? error ?? "no reason given"}"),
            _ => throw new InvalidOperationException($"the Director answered delivery state {asked.Answer.State}, which this Gateway does not know"),
        };
    }

    /// <summary>
    /// Read the prompt verb's answer: a settled kind, or null when the send went out and no answer came back (ask). An
    /// accepted answer that carries no delivery state (a Director older than the field) is read exactly as it always
    /// was: delivered.
    /// </summary>
    private static (DeliverySendKind? Kind, string? Error, bool RefusedDuplicate) Read(SessionVerbClient.PromptSendOutcome sent)
    {
        switch (sent.Kind)
        {
            case SessionVerbClient.PromptSendKind.Unanswered:
                return (null, sent.Detail, false);
            case SessionVerbClient.PromptSendKind.NeverLeftTheGateway:
            case SessionVerbClient.PromptSendKind.DirectorRefused:
                return (DeliverySendKind.NotDelivered, sent.Detail, false);
            case SessionVerbClient.PromptSendKind.Accepted:
                break;
            default:
                throw new InvalidOperationException($"unknown prompt send outcome {sent.Kind}");
        }
        var body = sent.Body;
        if (body is { Accepted: false, DeliveryState: not null })
        {
            // A REFUSED COPY: the Director typed nothing because this delivery id was already delivered or is still
            // being delivered. Delivered means an earlier attempt landed after this Gateway had stopped waiting for it.
            return body.DeliveryState switch
            {
                DeliveryState.Delivered => (DeliverySendKind.Delivered, null, true),
                DeliveryState.Delivering => (DeliverySendKind.StillDelivering, body.DeliveryStateReason ?? body.Error, false),
                _ => (DeliverySendKind.NotDelivered,
                    body.Error ?? $"the Director refused the delivery (state {DeliveryStates.Format(body.DeliveryState.Value)})", false),
            };
        }
        // Accepted, and the Director answered at its own budget with the send still going (Voice Delivery phase 3): the
        // words may yet land or fail, so this is held like any other "still delivering".
        if (body is { Accepted: true, DeliveryState: DeliveryState.Delivering })
            return (DeliverySendKind.StillDelivering, null, false);
        return (DeliverySendKind.Delivered, null, false);
    }

    /// <summary>
    /// Ask the Director "what became of delivery id X?" and write the question and its answer to the decision log.
    /// <paramref name="why"/> is <see cref="DeliveryDecisions.AskReasonPromptUnanswered"/> or
    /// <see cref="DeliveryDecisions.AskReasonRetryAsksFirst"/>. The answer line carries the Director's state, or, when it
    /// gave none, which kind of no-answer it was - they are different facts and are never folded into "unknown".
    /// </summary>
    public static async Task<SessionVerbClient.DeliveryStateAsk> AskAsync(
        VoiceUploadStore store, string uploadId, string sid, string deliveryId, SessionVerbClient route, string why)
    {
        store.RecordDecision(uploadId, DeliveryDecisions.AskedDirector, new DeliveryDecisionFacts { SessionId = sid, Reason = why });
        var asked = await route.GetDeliveryStateAsync(sid, deliveryId);
        var noAnswer = NoAnswerName(asked.Kind);
        store.RecordDecision(uploadId, DeliveryDecisions.DeliveryStateAnswer, new DeliveryDecisionFacts
        {
            SessionId = sid,
            State = asked.Answer is { } answer ? DeliveryStates.Format(answer.State) : null,
            Reason = noAnswer ?? asked.Answer?.Reason,
            Error = noAnswer is null ? null : asked.Detail,
        });
        FileLog.Write($"[DeliverySendAndAsk] sid={sid} upload={uploadId}: asked the Director ({why}); " +
            $"answer={(asked.Answer is { } a ? DeliveryStates.Format(a.State) : noAnswer)} {asked.Detail}");
        return asked;
    }

    /// <summary>
    /// The name written for a question the Director gave no answer to - which kind of no answer it was - or null when it
    /// answered. They are different facts and are never folded into "unknown".
    /// </summary>
    public static string? NoAnswerName(SessionVerbClient.DeliveryStateAskKind kind) => kind switch
    {
        SessionVerbClient.DeliveryStateAskKind.Answered => null,
        SessionVerbClient.DeliveryStateAskKind.DirectorTooOld => "director-too-old",
        SessionVerbClient.DeliveryStateAskKind.NoAnswer => NoAnswerState,
        SessionVerbClient.DeliveryStateAskKind.NeverLeftTheGateway => "never-left-the-gateway",
        _ => throw new InvalidOperationException($"unknown delivery-state answer kind {kind}"),
    };

    /// <summary>
    /// "COULD NOT CONFIRM IT ARRIVED" (Voice Delivery phase 2, change 1; the Delivery Lead's ruling): a recording whose
    /// question got no answer of any kind is not held forever. Once more than the age limit
    /// (<see cref="GatewayDictationEndpoint.MaxDeliveryAge"/>, the same strict boundary) has passed since
    /// <paramref name="since"/> - Send on the dictation path, the first verified claim on a "Send anyway" - the Gateway
    /// rules it unconfirmed. The two routes share this one test so they cannot disagree about where the line is.
    /// </summary>
    public static bool IsPastConfirmLimit(DateTime nowUtc, DateTime since, out TimeSpan age)
    {
        age = nowUtc - since;
        return age > GatewayDictationEndpoint.MaxDeliveryAge;
    }

    /// <summary>
    /// Write that a recording which may already be in the session is HELD as still delivering, with the Director's state
    /// as the client is told it (<c>delivering</c>, <c>unknown</c> or <c>no-answer</c>).
    /// </summary>
    public static void RecordHeld(VoiceUploadStore store, string uploadId, string sid, string directorState)
    {
        store.RecordDecision(uploadId, DeliveryDecisions.StillDelivering, new DeliveryDecisionFacts
        {
            SessionId = sid,
            State = directorState,
        });
        FileLog.Write($"[DeliverySendAndAsk] sid={sid} upload={uploadId}: HELD as still delivering (directorState={directorState})");
    }

    /// <summary>
    /// The <c>directorState</c> of the held answer when the Director gave no answer at all - not connected, too old to
    /// be asked, or silent. The other held states are the Director's own words (<see cref="DeliveryStates"/>).
    /// </summary>
    public const string NoAnswerState = "no-answer";

    /// <summary>
    /// The <c>directorState</c> of an owned delivery the Gateway cannot reach right now (Voice Delivery phase 5): the
    /// session's Director is not connected - the session could not be located, or the prompt never left the Gateway.
    /// The Gateway sends it itself when that Director's tunnel comes back.
    /// </summary>
    public const string WaitingForDirectorState = "waiting-for-director";

    /// <summary>
    /// The <c>directorState</c> of an owned delivery the Gateway will try again itself (Voice Delivery phase 5): the
    /// Director said the words are not in, or a Gateway-side step such as the transcription failed.
    /// </summary>
    public const string RetryingState = "retrying";

    /// <summary>
    /// Which kind of no answer is written on the "could not confirm it arrived" line when the session's Director was
    /// not connected at all, so the question could not even be asked.
    /// </summary>
    public const string DirectorNotConnected = "director-not-connected";
}
