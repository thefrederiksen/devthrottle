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
    public static readonly TextSendOutcome Delivered = new(true, null, null);

    private TextSendOutcome(bool confirmed, string? reason, Task<bool>? lateProof)
    {
        Confirmed = confirmed;
        Reason = reason;
        LateProof = lateProof;
    }

    /// <summary>A send that left the composer but is not yet proven: <paramref name="reason"/> says why, and
    /// <paramref name="lateProof"/>, when there is one, is the bounded watch of the agent's records that answers true if
    /// the words show up there later and false if its limit ends first.</summary>
    public static TextSendOutcome StillDelivering(string reason, Task<bool>? lateProof)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new TextSendOutcome(false, reason, lateProof);
    }

    /// <summary>True when the send was proven delivered; false when it is still delivering.</summary>
    public bool Confirmed { get; }

    /// <summary>Why a send that is still delivering could not be confirmed. Null when <see cref="Confirmed"/>.</summary>
    public string? Reason { get; }

    /// <summary>The watch of the agent's records that goes on after the send returned, or null when there is none (the
    /// agent keeps no records the Director can read for this send). It types nothing, ever.</summary>
    public Task<bool>? LateProof { get; }
}
