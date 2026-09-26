using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Voice;

/// <summary>
/// THE DECISION LOG A "SEND ANYWAY" CLAIM IS READ AGAINST AND WRITTEN TO (Voice Delivery phase 5, the Delivery Lead's
/// ruling on review finding 2): ONE claim mechanism for BOTH kinds of record a claim can name - a recording's upload in
/// the dictation store (<see cref="VoiceUploadStore"/> implements this) and a typed prompt in the typed prompt store
/// (through <c>TypedPromptClaimLog</c>, which writes the same line names there). Everything the claim mechanism does -
/// <see cref="Api.ClaimedSendCore"/> asking first, sending, holding, ruling unconfirmed or too old - goes through this
/// one interface, so a typed "Send anyway" can never disagree with a recording's about what a claim does or what its
/// decision log says.
///
/// The members are exactly the two things the recording's claim already needed: the claim SENDS history (whether an
/// earlier claimed send may have reached the Director, and when the FIRST claim naming the record was verified - the
/// "could not confirm it arrived" limit is measured from that), and the one place a decision line is written.
/// </summary>
internal interface IClaimDecisionLog
{
    /// <summary>
    /// What this record's decision log says about "Send anyway" presses of it: whether an earlier claimed send MAY have
    /// reached the Director - the log holds a <see cref="DeliveryDecisions.ClaimDirectorAnswer"/> line, written after
    /// every claimed send whatever it came to, or a line that cannot be parsed and so could be one - and when the FIRST
    /// claim naming it was verified (the time of its first <see cref="DeliveryDecisions.ClaimVerified"/> line), which
    /// the "could not confirm it arrived" limit is measured from. A re-press that sees the first asks the Director
    /// before it sends, because to a Director too old to keep a delivery record a second send is a second copy.
    /// </summary>
    ClaimSendHistory ReadClaimSends(string deliveryId);

    /// <summary>Write one decision line to the record named, when the record exists here. False (nothing written)
    /// when it does not.</summary>
    bool RecordDecision(string deliveryId, string decision, DeliveryDecisionFacts? facts = null);
}
