using CcDirector.Gateway.Voice;

namespace CcDirector.Gateway.Prompts;

/// <summary>
/// ONE CLAIM MECHANISM, TWO RECORDS (the Delivery Lead's ruling on the phase 5 review, finding 2): this lets
/// <see cref="Api.ClaimedSendCore"/> and <see cref="Api.DeliverySendAndAsk"/> - the pieces a RECORDING's "Send anyway"
/// goes through - read and write a TYPED prompt's decision log through the same
/// <see cref="IClaimDecisionLog"/> interface the dictation store implements. The line names are the same
/// (<see cref="DeliveryDecisions.ClaimDirectorAnswer"/>, <see cref="DeliveryDecisions.Unconfirmed"/> and the rest), so
/// a typed claim's log reads exactly as a recording claim's does and one reader reads both.
/// </summary>
internal sealed class TypedPromptClaimLog : IClaimDecisionLog
{
    private readonly TypedPromptStore _store;

    public TypedPromptClaimLog(TypedPromptStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ClaimSendHistory ReadClaimSends(string deliveryId) => _store.ReadClaimSends(deliveryId);

    public bool RecordDecision(string deliveryId, string decision, DeliveryDecisionFacts? facts = null)
        => _store.RecordDecision(deliveryId, decision, facts is null ? null : Facts(facts));

    // The typed log's facts are the same closed set the dictation log's are - states, flags, numbers and short
    // codes, never the words - so the claim's lines land in the same shape the typed store's own lines have.
    private static TypedPromptDecisionFacts Facts(DeliveryDecisionFacts facts) => new()
    {
        SessionId = facts.SessionId,
        State = facts.State,
        Reason = facts.Reason,
        Ok = facts.Ok,
        RefusedDuplicate = facts.RefusedDuplicate,
        Characters = facts.Characters,
        SentAtUtc = facts.SentAtUtc,
        AgeSeconds = facts.AgeSeconds,
        DirectorNoAnswer = facts.DirectorNoAnswer,
        Error = facts.Error,
    };
}
