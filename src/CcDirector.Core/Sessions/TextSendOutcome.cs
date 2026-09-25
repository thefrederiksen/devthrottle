namespace CcDirector.Core.Sessions;

/// <summary>
/// How a <see cref="Session.SendTextAsync(string, SubmissionProvenance, SendSource, InputOrigin?)"/> that did NOT throw
/// ended (Voice Delivery mission, phase 3, review finding 3). A send that throws is not delivered: the words are known
/// not to have been submitted. A send that returns is one of two things:
///  - <see cref="Confirmed"/> true: delivered - the agent's records hold the words, or the terminal proved the submit.
///  - <see cref="Confirmed"/> false: STILL DELIVERING. The Enter was pressed and the words were not seen left behind in the
///    composer, but nothing has proven the agent took them - a working Claude Code holds a prompt sent mid-turn and writes
///    it to its records only when its running tool ends, which can be after the records window. This is never a failure:
///    a caller that retried it would type the words a second time into an agent that already holds them.
/// </summary>
public sealed class TextSendOutcome
{
    /// <summary>The send was proven delivered.</summary>
    public static readonly TextSendOutcome Delivered = new(true, null, null, null);

    private TextSendOutcome(bool confirmed, string? reason, Task<LateArrival>? lateProof, TimeSpan? lateWatchLimit)
    {
        Confirmed = confirmed;
        Reason = reason;
        LateProof = lateProof;
        LateWatchLimit = lateWatchLimit;
    }

    /// <summary>A send that left the composer but is not yet proven, with no records to watch: <paramref name="reason"/>
    /// says why.</summary>
    public static TextSendOutcome StillDelivering(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new TextSendOutcome(false, reason, null, null);
    }

    /// <summary>A send that left the composer but is not yet proven: <paramref name="reason"/> says why, and
    /// <paramref name="lateProof"/> is the watch of the agent's records, bounded by <paramref name="limit"/>, that answers
    /// how it ended (<see cref="LateArrival"/>).</summary>
    public static TextSendOutcome StillDelivering(string reason, Task<LateArrival> lateProof, TimeSpan limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(lateProof);
        return new TextSendOutcome(false, reason, lateProof, limit);
    }

    /// <summary>True when the send was proven delivered; false when it is still delivering.</summary>
    public bool Confirmed { get; }

    /// <summary>Why a send that is still delivering could not be confirmed. Null when <see cref="Confirmed"/>.</summary>
    public string? Reason { get; }

    /// <summary>The watch of the agent's records that goes on after the send returned, or null when there is none (the
    /// agent keeps no records the Director can read for this send). It types nothing, ever.</summary>
    public Task<LateArrival>? LateProof { get; }

    /// <summary>How long <see cref="LateProof"/> watches the records at most; null when there is no watch.</summary>
    public TimeSpan? LateWatchLimit { get; }
}

/// <summary>How the watch of the agent's records after a still-delivering send ended (<see cref="TextSendOutcome.LateProof"/>).</summary>
public enum LateArrival
{
    /// <summary>The records showed the prompt word for word: delivered.</summary>
    Arrived,

    /// <summary>The watch's limit ended and the records never showed the prompt. The Director's record for a delivery id
    /// becomes not-delivered (the Delivery Lead's ruling: nothing stays delivering forever); nothing is typed again by
    /// itself.</summary>
    LimitEnded,

    /// <summary>The session ended before the records showed the prompt or the limit ended.</summary>
    SessionEnded,
}
