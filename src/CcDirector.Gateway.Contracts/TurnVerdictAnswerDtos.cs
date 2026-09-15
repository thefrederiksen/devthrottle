namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The body of <c>POST /sessions/{sid}/turn-verdict/answer</c> (the Wingman-on-every-turn mission, slice E): the
/// owner's tap on one or more of a verdict's options, sent as ONE request so a multiple-select picker is answered
/// in one activation under one screen lock.
///
/// The indexes are positions in the verdict's <see cref="TurnVerdictDto.Options"/> list, in the order the owner
/// picked them - the order the route sends their bytes in. An EMPTY list is the confirm of a parked reply and is
/// accepted in that one shape only; a missing list is not the same thing and is refused.
/// </summary>
public sealed class TurnVerdictAnswerRequest
{
    /// <summary>The verdict the options came from. It must be the latest verdict of the session in the path.</summary>
    public string VerdictId { get; set; } = "";

    /// <summary>The chosen options, by position, in the order picked. Null is refused; empty confirms a parked reply.</summary>
    public List<int>? OptionIndexes { get; set; }
}

/// <summary>
/// What the answer route did. <see cref="Reason"/> is the Gateway's own sentence, written for the owner and shown
/// by the client verbatim; <see cref="Code"/> is the closed word for the same outcome, the ledger cause the
/// activation was recorded under.
/// </summary>
public sealed class TurnVerdictAnswerResponse
{
    /// <summary>True only when the Director confirmed the bytes were written into the session.</summary>
    public bool Accepted { get; set; }

    /// <summary>The closed outcome word - an <see cref="ActivityCauses"/> constant.</summary>
    public string Code { get; set; } = "";

    /// <summary>The sentence the client shows the owner, verbatim.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The verdict the request named, echoed so a client can tell which answer this is.</summary>
    public string VerdictId { get; set; } = "";
}
