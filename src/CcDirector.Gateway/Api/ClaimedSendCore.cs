using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Voice;

namespace CcDirector.Gateway.Api;

/// <summary>What one attempt of a "Send anyway" with a verified claim came to.</summary>
internal enum ClaimAttemptKind
{
    /// <summary>The words are in: typed now, refused as a copy of one already delivered, or the question said so.
    /// <see cref="ClaimAttempt.Body"/> is the answer to give.</summary>
    Delivered,
    /// <summary>A re-press asked first and heard "delivered": nothing was sent. <see cref="ClaimAttempt.Body"/> is the
    /// answer to give, in the shape of the Director's own refusal of a copy.</summary>
    AlreadyDelivered,
    /// <summary>Not final: the words may be in, or the Gateway will send them. <see cref="ClaimAttempt.DirectorState"/>
    /// is what the client is told.</summary>
    Held,
    /// <summary>No answer of any kind for more than the limit from the first verified claim: could not confirm it.</summary>
    Unconfirmed,
    /// <summary>The question said the words are not in (a press by the client only; the Gateway's own attempt holds
    /// that as <see cref="Held"/> "retrying", or <see cref="TooOld"/> past the limit).</summary>
    NotIn,
    /// <summary>The Gateway's own attempt heard that the words are not in, more than the limit after the first verified
    /// claim: nothing is sent, and the words are shown back with "Send anyway" (Voice Delivery phase 5, section 4).</summary>
    TooOld,
}

internal sealed record ClaimAttempt(ClaimAttemptKind Kind, PromptResponse? Body = null, string? DirectorState = null, string? Error = null);

/// <summary>
/// ONE ATTEMPT OF A "SEND ANYWAY" WITH A VERIFIED CLAIM, for both callers: the prompt route when the owner presses it,
/// and the Gateway's driver when it presses it again itself after a "still delivering" answer (Voice Delivery phase 5,
/// contract section 4). One piece, so the two cannot disagree about what a "Send anyway" does - it was the prompt
/// route's local function until the driver needed the same logic, and a second copy is exactly what the ruling forbids.
///
/// It sends through <see cref="DeliverySendAndAsk"/>, the one piece dictation sends through. A re-press asks first when
/// an earlier claimed send may have reached the Director (phase 2, change 1), because to a Director too old to keep a
/// delivery record a second send is a second copy. Its time limit is phase 2's: the limit from the FIRST verified claim.
/// Every step is written to the recording's decision log. It writes nothing to the claim's own held record - what to
/// keep, and when, is each caller's.
/// </summary>
internal static class ClaimedSendCore
{
    /// <param name="gatewayDriven">True for the Gateway's own re-press. Then "the words are not in" is never a failure
    /// answer - under the limit it is held as "retrying" (the Gateway sends again on its next attempt), past it the words
    /// are shown back as too old - because there is no client waiting to be told to show them back.</param>
    public static async Task<ClaimAttempt> AttemptAsync(SessionVerbClient route, string sid, PromptRequest req,
        VoiceUploadStore store, string deliveryId, string? activityState, TimeProvider clock, bool gatewayDriven)
    {
        var history = store.ReadClaimSends(deliveryId);
        if (history.MayHaveReachedDirector)
        {
            var earlier = await DeliverySendAndAsk.AskAsync(store, deliveryId, sid, deliveryId, route,
                DeliveryDecisions.AskReasonSendAnywayAsksFirst);
            if (earlier.Kind != SessionVerbClient.DeliveryStateAskKind.Answered)
                return HoldOrUnconfirmed(store, sid, deliveryId, history, DeliverySendAndAsk.NoAnswerName(earlier.Kind)!,
                    DeliverySendAndAsk.NoAnswerState, clock);
            switch (earlier.Answer!.State)
            {
                case DeliveryState.Delivered:
                    FileLog.Write($"[ClaimedSendCore] sid={sid} claimed delivery {deliveryId} already delivered; nothing sent again");
                    return new ClaimAttempt(ClaimAttemptKind.AlreadyDelivered, new PromptResponse
                    {
                        Accepted = false,
                        DeliveryState = DeliveryState.Delivered,
                        DeliveryStateReason = GatewayEndpoints.AlreadyDeliveredBySendAnyway,
                        Error = GatewayEndpoints.AlreadyDeliveredBySendAnyway,
                        ActivityState = activityState ?? "",
                    });
                case DeliveryState.Delivering:
                    return Held(store, sid, deliveryId, DeliveryStates.Delivering);
                case DeliveryState.NotDelivered:
                case DeliveryState.Unknown:
                    // Known not to be in. The owner's own press sends, exactly as a first press does; the Gateway's own
                    // re-press sends only under the limit from the first claim, and past it shows the words back.
                    if (gatewayDriven && IsPastLimit(history, deliveryId, clock, out var age))
                        return TooOld(store, sid, deliveryId, age);
                    break;
                default:
                    throw new InvalidOperationException($"the Director answered delivery state {earlier.Answer.State}, which this Gateway does not know");
            }
        }

        var sent = await DeliverySendAndAsk.SendAsync(route, store, deliveryId, sid, deliveryId, req,
            (answer, reading) => RecordClaimAnswer(store, deliveryId, sid, answer, reading));
        switch (sent.Kind)
        {
            case DeliverySendKind.Delivered:
                // The Director's own answer when it gave one; when the question settled it, the words are in.
                return new ClaimAttempt(ClaimAttemptKind.Delivered, sent.Body ?? new PromptResponse
                {
                    Accepted = true,
                    DeliveryState = DeliveryState.Delivered,
                    ActivityState = activityState ?? "",
                });
            case DeliverySendKind.StillDelivering:
                return Held(store, sid, deliveryId, DeliveryStates.Delivering);
            case DeliverySendKind.NoAnswer:
                return HoldOrUnconfirmed(store, sid, deliveryId, store.ReadClaimSends(deliveryId), sent.NoAnswerKind!,
                    DeliverySendAndAsk.NoAnswerState, clock);
            case DeliverySendKind.NeverSeen:
            case DeliverySendKind.NotDelivered:
                if (!gatewayDriven)
                {
                    FileLog.Write($"[ClaimedSendCore] sid={sid} claimed delivery {deliveryId} NOT delivered: {sent.Error}");
                    return new ClaimAttempt(ClaimAttemptKind.NotIn, Error: sent.Error);
                }
                if (IsPastLimit(store.ReadClaimSends(deliveryId), deliveryId, clock, out var sentAge))
                    return TooOld(store, sid, deliveryId, sentAge);
                return Held(store, sid, deliveryId,
                    sent.NeverLeft ? DeliverySendAndAsk.WaitingForDirectorState : DeliverySendAndAsk.RetryingState);
            default:
                throw new InvalidOperationException($"unknown delivery send outcome {sent.Kind}");
        }
    }

    /// <summary>
    /// The Director of the claim's session is not connected, so nothing could be asked or sent (the Gateway's own
    /// re-press only): held as "waiting for the Director" within the limit from the first verified claim, and past it
    /// "could not confirm it arrived" - an earlier claimed send may already be in, so it is never shown back with
    /// "Send anyway".
    /// </summary>
    public static ClaimAttempt DirectorNotConnected(VoiceUploadStore store, string sid, string deliveryId, TimeProvider clock)
        => HoldOrUnconfirmed(store, sid, deliveryId, store.ReadClaimSends(deliveryId), DeliverySendAndAsk.DirectorNotConnected,
            DeliverySendAndAsk.WaitingForDirectorState, clock);

    private static ClaimAttempt Held(VoiceUploadStore store, string sid, string deliveryId, string directorState)
    {
        DeliverySendAndAsk.RecordHeld(store, deliveryId, sid, directorState);
        FileLog.Write($"[ClaimedSendCore] sid={sid} claimed delivery {deliveryId} HELD as still delivering ({directorState})");
        return new ClaimAttempt(ClaimAttemptKind.Held, DirectorState: directorState);
    }

    // A claim whose question got no answer of any kind: held within the limit from the FIRST verified claim, and past it
    // the verdict "could not confirm it arrived", written with the age and which kind of no answer. The first claim's time
    // is the Gateway's own record; the client sends nothing new for it.
    private static ClaimAttempt HoldOrUnconfirmed(VoiceUploadStore store, string sid, string deliveryId, ClaimSendHistory history,
        string noAnswerKind, string heldState, TimeProvider clock)
    {
        if (!IsPastLimit(history, deliveryId, clock, out var age))
            return Held(store, sid, deliveryId, heldState);
        store.RecordDecision(deliveryId, DeliveryDecisions.Unconfirmed, new DeliveryDecisionFacts
        {
            SessionId = sid,
            AgeSeconds = (long)Math.Floor(age.TotalSeconds),
            DirectorNoAnswer = noAnswerKind,
        });
        FileLog.Write($"[ClaimedSendCore] sid={sid} claimed delivery {deliveryId}: {age.TotalSeconds:0}s since the first Send anyway " +
            $"and the Director gave no answer ({noAnswerKind}); ruled unconfirmed, nothing more is sent");
        return new ClaimAttempt(ClaimAttemptKind.Unconfirmed);
    }

    private static ClaimAttempt TooOld(VoiceUploadStore store, string sid, string deliveryId, TimeSpan age)
    {
        store.RecordDecision(deliveryId, DeliveryDecisions.TooOld, new DeliveryDecisionFacts
        {
            SessionId = sid,
            AgeSeconds = (long)Math.Floor(age.TotalSeconds),
        });
        FileLog.Write($"[ClaimedSendCore] sid={sid} claimed delivery {deliveryId}: {age.TotalSeconds:0}s since the first Send anyway " +
            "and the words are not in; shown back with Send anyway, nothing sent");
        return new ClaimAttempt(ClaimAttemptKind.TooOld);
    }

    private static bool IsPastLimit(ClaimSendHistory history, string deliveryId, TimeProvider clock, out TimeSpan age)
    {
        // Every press is verified and written before it gets here, so there is always a first claim.
        var firstClaim = history.FirstClaimVerifiedAtUtc
            ?? throw new InvalidOperationException($"claimed delivery {deliveryId} has no verified claim in its decision log");
        return DeliverySendAndAsk.IsPastConfirmLimit(clock.GetUtcNow().UtcDateTime, firstClaim, out age);
    }

    // The Director's answer to a "Send anyway" that carried a delivery id, written to that recording's decision log
    // (phase 1 review finding 2): accepted and typed, refused as delivered or delivering with nothing typed, failed, or -
    // phase 2 - unanswered, which the next lines of the log follow with the question. This is the line that answers "why
    // did my words go in once when I pressed Send twice?" without the container log.
    private static void RecordClaimAnswer(VoiceUploadStore store, string deliveryId, string sid,
        SessionVerbClient.PromptSendOutcome sent, PromptAnswerReading reading)
    {
        var body = sent.Body;
        var refused = body is { Accepted: false, DeliveryState: DeliveryState.Delivered or DeliveryState.Delivering };
        store.RecordDecision(deliveryId, DeliveryDecisions.ClaimDirectorAnswer, new DeliveryDecisionFacts
        {
            SessionId = sid,
            Ok = body?.Accepted ?? false,
            State = body?.DeliveryState is { } state ? DeliveryStates.Format(state) : null,
            RefusedDuplicate = refused,
            Error = body is null ? reading.Error : body.Accepted ? null : body.DeliveryStateReason ?? body.Error,
            Reason = reading.Unanswered ? DeliveryDecisions.AskReasonPromptUnanswered : null,
        });
    }
}
