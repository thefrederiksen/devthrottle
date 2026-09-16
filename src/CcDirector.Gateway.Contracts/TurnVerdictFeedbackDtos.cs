namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The body of <c>POST /sessions/{sid}/turn-verdict/feedback</c> (the Wingman-on-every-turn mission, slice G):
/// the owner saying that a verdict was WRONG, and which word he thinks was right.
///
/// This is the one place a person's reading of a stop re-enters the product, and it goes on to the labelled
/// corpus as an OWNER LABEL - the one label that outranks two independent reviewers agreeing, because the
/// person whose session it was is the only reader who knows what he actually wanted. So the shape is
/// deliberately small: a word from the shared vocabulary and, when he has one, a sentence in his own words.
/// Nothing about the report is interpreted here or anywhere else on the way.
/// </summary>
public sealed class TurnVerdictFeedbackRequest
{
    /// <summary>The verdict being corrected. It must be a verdict of the session in the path - including one
    /// already superseded, which is the ordinary case: answering a red row is what supersedes its verdict.</summary>
    public string VerdictId { get; set; } = "";

    /// <summary>The verdict word the owner says was right - one of the shared vocabulary's words. Anything else
    /// is refused rather than stored, because a corpus label nobody can compare is worse than no label.</summary>
    public string CorrectVerdict { get; set; } = "";

    /// <summary>What he wants to add, in his own words, or null/empty when he adds nothing.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// What the feedback route did. <see cref="Reason"/> is the Gateway's own sentence, written for the owner and
/// shown by the client verbatim; <see cref="Code"/> is the closed word for the same outcome.
/// </summary>
public sealed class TurnVerdictFeedbackResponse
{
    /// <summary>True when the correction was stored.</summary>
    public bool Accepted { get; set; }

    /// <summary>The closed outcome word - a <see cref="TurnVerdictFeedbackCodes"/> constant.</summary>
    public string Code { get; set; } = "";

    /// <summary>The sentence the client shows the owner, verbatim.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The verdict the request named, echoed so a client can tell which report this is.</summary>
    public string VerdictId { get; set; } = "";
}

/// <summary>
/// The closed outcome words the feedback route answers with.
///
/// THEY LIVE HERE, BESIDE THE RESPONSE THEY ARE CARRIED ON, and each REFUSAL word is spelled identically to an
/// <see cref="ActivityCauses"/> constant, because the route writes one ledger line on every refusal under that
/// same word - one word for one idea, rather than one spelling for the owner and another for the record.
/// <c>FeedbackCodesAreLedgerCausesTests</c> fails if the two ever differ.
///
/// <see cref="Recorded"/> is the one with no cause beside it, and that is the point rather than an omission: an
/// accepted correction writes a durable row carrying its own moment, word and note, and that row IS the record.
/// A refusal writes nothing anywhere, which is why it is the half that needs the ledger. The inspection found
/// that gap - authorisation and refusal outcomes sitting outside the operational record entirely, visible only
/// in a log file.
/// </summary>
public static class TurnVerdictFeedbackCodes
{
    /// <summary>The correction was stored.</summary>
    public const string Recorded = "feedback-recorded";

    /// <summary>The request named no verdict, no corrected word, or could not be read at all.</summary>
    public const string Malformed = "feedback-malformed";

    /// <summary>The request named a verdict this account does not hold, or one that belongs to another of its
    /// sessions. The two are one answer on purpose: which of another session's verdict ids exist is not a
    /// question this route answers.</summary>
    public const string VerdictNotFound = "feedback-verdict-not-found";

    /// <summary>The corrected word is not one of the shared vocabulary's words.</summary>
    public const string UnknownVerdict = "feedback-unknown-verdict";

    /// <summary>A session key tried to report while the account's verdict colours are off. The verdicts are a
    /// shadow record then, and the reads refuse a session key for the same reason.</summary>
    public const string ShadowRecord = "feedback-shadow-record";

    /// <summary>This Gateway holds no verdicts at all - no store, or no settings to read the account's switches
    /// from.</summary>
    public const string Unavailable = "feedback-unavailable";
}
