using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// A member-for-member copy of a verdict, without serializing it - for the two places that must change one field of
/// a verdict without touching the instance somebody else holds: the reuse arm, which restamps the observed moment,
/// and the inspector's trace, which cuts an oversized refusal reason on the verdict path, where serializing is not
/// allowed.
///
/// A HAND-WRITTEN LIST GOES STALE THE DAY A FIELD IS ADDED, so <c>TurnVerdictDtoCopyTests</c> fills every public
/// property of the record by reflection and fails when this copy leaves one behind.
/// </summary>
internal static class TurnVerdictDtoCopy
{
    public static TurnVerdictDto Of(TurnVerdictDto v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return new TurnVerdictDto
        {
            VerdictId = v.VerdictId,
            JudgedAtUtc = v.JudgedAtUtc,
            TurnEndObservedAtUtc = v.TurnEndObservedAtUtc,
            ScreenHash = v.ScreenHash,
            Model = v.Model,
            ContractVersion = v.ContractVersion,
            PackageKind = v.PackageKind,
            Failed = v.Failed,
            FailureReason = v.FailureReason,
            Verdict = v.Verdict,
            Confidence = v.Confidence,
            Evidence = v.Evidence,
            Label = v.Label,
            Summary = v.Summary,
            AgentRecommends = v.AgentRecommends,
            AnswerVia = v.AnswerVia,
            Menu = v.Menu,
            Options = v.Options,
            Risk = v.Risk,
            Spoken = v.Spoken,
            NextScheduledWakeUtc = v.NextScheduledWakeUtc,
            SupersededAtUtc = v.SupersededAtUtc,
            FinishedKind = v.FinishedKind,
            Narration = v.Narration,
            FailureKind = v.FailureKind,
            NarrationFailureReason = v.NarrationFailureReason,
            RetriesMade = v.RetriesMade,
            NextRetryAtUtc = v.NextRetryAtUtc,
            OptionsDroppedReason = v.OptionsDroppedReason,
            DecidedBy = v.DecidedBy,
            DecisionReason = v.DecisionReason,
        };
    }
}
