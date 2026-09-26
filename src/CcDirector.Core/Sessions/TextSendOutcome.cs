namespace CcDirector.Core.Sessions;

/// <summary>
/// How a <see cref="Session.SendTextAsync(string, SubmissionProvenance, SendSource, InputOrigin?, DateTime?)"/> that did NOT throw
/// ended (Voice Delivery mission, phase 3, review finding 3). A send that throws is not delivered: the words are known
/// not to have been submitted. A send that returns is one of three things:
///  - <see cref="Confirmed"/> true: delivered - the agent's records hold the words, or the terminal proved the submit.
///  - <see cref="NothingTyped"/> true: REFUSED BEFORE THE FIRST KEYSTROKE (phase 5, QA finding F6) - the prompt was
///    older than the delivery age limit, so nothing was typed and the words are KNOWN not submitted. This is never
///    "still delivering" and carries no late watch: there is nothing to watch for. The caller answers not-delivered
///    with the too-old reason; it must not retry the same text.
///  - otherwise (<see cref="Confirmed"/> false): STILL DELIVERING. The Enter was pressed and the words were not seen left behind in the
///    composer, but nothing has proven the agent took them - a working Claude Code holds a prompt sent mid-turn and writes
///    it to its records only when its running tool ends, which can be after the records window. This is never a failure:
///    a caller that retried it would type the words a second time into an agent that already holds them.
/// </summary>
public sealed class TextSendOutcome
{
    /// <summary>The send was proven delivered.</summary>
    public static readonly TextSendOutcome Delivered = new(true, null, null, null, nothingTyped: false);

    private TextSendOutcome(bool confirmed, string? reason, Task<LateWatchEnd>? lateProof, TimeSpan? lateWatchLimit, bool nothingTyped)
    {
        Confirmed = confirmed;
        Reason = reason;
        LateProof = lateProof;
        LateWatchLimit = lateWatchLimit;
        NothingTyped = nothingTyped;
    }

    /// <summary>A send that left the composer but is not yet proven: <paramref name="reason"/> says why, and
    /// <paramref name="lateProof"/> is the late watch, bounded by <paramref name="limit"/>, that answers how it ended
    /// (<see cref="LateWatchEnd"/>). Every still-delivering send has one - nothing stays delivering forever (round 2c),
    /// so even a send with no records to watch waits out the same limit and then says so.</summary>
    public static TextSendOutcome StillDelivering(string reason, Task<LateWatchEnd> lateProof, TimeSpan limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(lateProof);
        return new TextSendOutcome(false, reason, lateProof, limit, nothingTyped: false);
    }

    /// <summary>True when the send was proven delivered; false when it is still delivering.</summary>
    public bool Confirmed { get; }

    /// <summary>Why a send that is still delivering could not be confirmed, and the reason of a refusal that typed
    /// nothing. Null when <see cref="Confirmed"/>.</summary>
    public string? Reason { get; }

    /// <summary>The late watch that goes on after a still-delivering send returned; null only when <see cref="Confirmed"/>.
    /// It reads the agent's records where there are any, and ends at <see cref="LateWatchLimit"/> or when the session
    /// ends. It types nothing, ever, and it never throws: a failed read of the records is one of its endings.</summary>
    public Task<LateWatchEnd>? LateProof { get; }

    /// <summary>How long <see cref="LateProof"/> runs at most; null only when <see cref="Confirmed"/>.</summary>
    public TimeSpan? LateWatchLimit { get; }

    /// <summary>True when NOTHING was typed: the send was refused before the first keystroke because the prompt was
    /// older than the delivery age limit (phase 5, QA finding F6). The words are known not submitted - never a
    /// still-delivering send and never a failure. False on every other outcome.</summary>
    public bool NothingTyped { get; }

    /// <summary>
    /// A send that typed NOTHING: the prompt was refused at the first keystroke for being strictly older than
    /// <see cref="Gateway.Contracts.MaxDeliveryAge"/>. <paramref name="reason"/> is the too-old reason with the
    /// measured age, the same string the verb answers and the delivery record keeps. Not "delivered", not "still
    /// delivering": no late watch is attached, because nothing was typed and there is nothing to watch for.
    /// </summary>
    public static TextSendOutcome RefusedBeforeTyping(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new TextSendOutcome(false, reason, null, null, nothingTyped: true);
    }
}

/// <summary>How the late watch after a still-delivering send ended (<see cref="TextSendOutcome.LateProof"/>).</summary>
public enum LateArrival
{
    /// <summary>The records showed the prompt word for word: delivered.</summary>
    Arrived,

    /// <summary>The watch's limit ended and the records never showed the prompt. The Director's record for a delivery id
    /// becomes not-delivered (the Delivery Lead's ruling: nothing stays delivering forever); nothing is typed again by
    /// itself.</summary>
    LimitEnded,

    /// <summary>The watch's limit ended with no records to watch - the agent keeps none the Director can read for this
    /// send. Not delivered: it could not be confirmed (the Tech Lead's ruling, round 2c, case 1).</summary>
    NoRecordsToWatch,

    /// <summary>Reading the records failed, and the watch's limit then ended. Not delivered: it could not be confirmed
    /// (round 2c, case 2); <see cref="LateWatchEnd.WatchFailure"/> names the failure.</summary>
    WatchFailed,

    /// <summary>The session ended before the records showed the prompt or the limit ended. Not delivered at once
    /// (round 2c, case 3): a retry into an ended session cannot double anything.</summary>
    SessionEnded,
}

/// <summary>How the late watch ended, and for <see cref="LateArrival.WatchFailed"/> the failure's message.</summary>
public sealed record LateWatchEnd(LateArrival Ended, string? WatchFailure = null);
