using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Folds a stored reading into what the session card says about a Wingman failure (mission "Wingman error and
/// retry", 2026-09-19). THE place that is ruled, in the sense of CLAUDE.md rule 7: the tag, the reason, which
/// retry is next, when, how many remain and whether the schedule is used up are all decided here and rendered
/// verbatim by every client.
///
/// IT DOES NOT DEPEND ON VOICE MODE. The owner's words were "it now has to run every time": a reading that failed
/// is an error whether or not anybody is listening, because the narration text on the card is how he reads a
/// stop without opening the terminal.
///
/// EVERY SENTENCE ABOUT A COMING RETRY IS RENDERED FROM ONE FIELD, the record's own
/// <see cref="TurnVerdictDto.NextRetryAtUtc"/> - the same field the sweep retries from. So the card cannot say an
/// attempt is coming when none is booked, and that is structural rather than a matter of two places agreeing.
/// </summary>
public static class WingmanErrorFold
{
    public const string TagText = "Wingman error";
    public const string AskAgainText = "Ask again";

    /// <summary>The whole line once the schedule is used up.</summary>
    public const string ExhaustedLine = "The Wingman could not read this stop. Nothing more is scheduled.";

    /// <summary>The reason for a reading whose audio could not be made. The words exist; only the voice is missing.</summary>
    public const string SpeechFailedReason = "The voice for this turn could not be made.";

    /// <summary>
    /// The error for one row, or null when there is none to show: no reading, a reading that succeeded, or a
    /// session that is working (its last reading describes a screen that is gone).
    /// </summary>
    public static WingmanErrorDisplay? For(TurnVerdictDto? reading, bool agentWorking)
    {
        if (reading is null || agentWorking || !WingmanRetrySchedule.NeedsRetry(reading)) return null;
        return ForSchedule(ReasonFor(reading), reading.RetriesMade, reading.NextRetryAtUtc);
    }

    /// <summary>The same shape from the bare schedule facts, for the speech leg, which keeps its own.</summary>
    public static WingmanErrorDisplay ForSchedule(string reason, int retriesMade, DateTime? nextRetryAtUtc)
    {
        var total = WingmanRetrySchedule.Total;
        var exhausted = nextRetryAtUtc is null;
        var nextNumber = exhausted ? 0 : Math.Min(retriesMade + 1, total);
        return new WingmanErrorDisplay
        {
            Tag = TagText,
            Reason = reason,
            NextRetryNumber = nextNumber,
            RetriesTotal = total,
            RetriesRemaining = exhausted ? 0 : total - nextNumber + 1,
            NextRetryAtUtc = nextRetryAtUtc,
            Exhausted = exhausted,
            RetryLabel = exhausted ? "" : $"retry {nextNumber} of {total}",
            ExhaustedText = exhausted ? ExhaustedLine : "",
            AskAgainLabel = AskAgainText,
        };
    }

    /// <summary>
    /// The short plain reason, chosen from the record's closed failure word. Never the stored failure reason: that
    /// quotes exception messages and contract rules, and was written for the debug view.
    /// </summary>
    public static string ReasonFor(TurnVerdictDto reading) => reading.FailureKind switch
    {
        WingmanFailureKinds.DidNotAnswer => "The model did not answer in time.",
        WingmanFailureKinds.RateLimited => "The model was busy and asked us to wait.",
        WingmanFailureKinds.Unavailable => "The model could not be reached.",
        WingmanFailureKinds.Refused => "The model's answer could not be used.",
        WingmanFailureKinds.NarrationFailed => "The model read this stop and did not write it up.",
        // A record stored before the failure word existed.
        _ => "The Wingman could not read this stop.",
    };
}
