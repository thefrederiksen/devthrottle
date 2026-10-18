using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Gateway.Receipts;

/// <summary>
/// THE ENDING OF A QUEUED PROMPT WHOSE QUEUE THE GATEWAY LOST SIGHT OF (mission #4070, phase 2 follow-on, item 9). A
/// receipt the Gateway holds as Queued (the prompt waiting, or written to the agent with its acknowledgement not yet
/// arrived) changes only when the Director sends that prompt's next event. When the prompt leaves the Director's queue
/// inside a hole in the push (pull request #4090: the memory bound dropped the event, it was too large for any batch,
/// or it was published while the Gateway was turned off), its <c>PromptDelivered</c> or <c>PromptRefused</c> never
/// arrives, and without this rule the receipt reads <c>Queued: &lt;why&gt;</c> until the 90-day purge.
///
/// <para><b>The proof.</b> Pull request #4190: every new generation STARTS by re-announcing the queue - one
/// <c>PromptQueued</c> for each prompt still waiting for its receipt when the generation began (the Director keeps that
/// line from its own events, so a written prompt not yet acknowledged is in it), oldest first, from sequence 1, before
/// ANY other event of that generation. So the first event of a later generation that is not a <c>PromptQueued</c>
/// comes after every re-announcement that generation will make. A receipt that is still Queued from an OLDER
/// generation at that event was neither re-announced nor ended: its prompt was not in the Director's queue when the
/// generation began, and the event that took it out was lost. The receipt store and the live ruling both decide through
/// <see cref="Proves"/> and <see cref="Applies"/>, at the same event, so the receipts read and the live stream give the
/// same words.</para>
///
/// <para><b>What a proof is NOT.</b> A <c>PromptQueued</c> never proves anything: it may be a re-announcement, and a
/// prompt queued fresh in the new generation looks the same. A <c>MessageText</c> is not taken as a proof either,
/// although it would be a valid one: a reply streams many of them a second, the store would read the session's queued
/// receipts for each, and the next event of any other kind (the turn's own end at the latest) proves the same thing.
/// Inside ONE generation the rule never applies: there every event the Director published reached the Gateway.</para>
///
/// <para><b>Restart or hole: the Gateway cannot tell, so the words claim neither.</b> No event names the Director
/// process. A hole's generation starts exactly one millisecond after the one it continues, but that is how the pusher
/// labels it, not a promise of the event model, and the Gateway need not have seen the generation it continues. And
/// today a Director restart never reaches this rule at all: a restarted Director gives the session a new session id
/// (phase 2 review, observation C), so the old session never sees a later generation. The words
/// (<see cref="Words"/>) therefore say only what the events show.</para>
///
/// <para><b>The ending is the Gateway's inference, never history.</b> The receipt keeps everything it had (its queue
/// reason, its prompt's text). A later real event for the prompt - its re-announcement, its delivery, its refusal -
/// always replaces the ending, whatever its sequence (<see cref="IsReplacedBy"/>). The one event that does not is an
/// event of a generation OLDER than the proof: that can only be a replay of an event the receipt already had, since
/// the proof's generation exists and the Gateway accepts no older generation after a newer one.</para>
///
/// <para><b>A Director older than #4190</b> re-announces nothing, so after a hole its still-queued prompts read
/// <see cref="Words"/> until their real delivery or refusal arrives and replaces it.</para>
/// </summary>
public static class ApiModeLostQueuedPrompt
{
    /// <summary>The words of a receipt ruled <see cref="ApiModeReceiptState.Unknown"/>. Neutral: nothing is known to have
    /// gone wrong.</summary>
    public const string Words = "Not known - the Director reconnected without this prompt in its queue or any record of sending it";

    /// <summary>
    /// Whether <paramref name="e"/> comes after every re-announcement of its generation (see the class summary): any
    /// event but a <c>PromptQueued</c> (and a <c>MessageText</c>, see the class summary). <paramref name="ownPromptId"/> is
    /// the prompt the event itself ends (a <c>PromptDelivered</c> or <c>PromptRefused</c>), which its own change rules,
    /// not this one; null for every other event.
    /// </summary>
    public static bool Proves(SessionEvent e, out string? ownPromptId)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e)
        {
            case PromptQueued:
            case MessageText:
                ownPromptId = null;
                return false;
            case PromptDelivered d:
                ownPromptId = d.PromptId;
                return true;
            case PromptRefused f:
                ownPromptId = f.PromptId;
                return true;
            default:
                ownPromptId = null;
                return true;
        }
    }

    /// <summary>
    /// Whether a proof at <paramref name="proof"/> rules the receipt of <paramref name="promptId"/> Unknown: it is Queued,
    /// it is not the prompt the proving event itself ends (<paramref name="ownPromptId"/>), and the last event applied to
    /// it (<paramref name="lastGeneration"/>, <paramref name="lastGenerationStartedUtc"/>) is of an older generation than
    /// the proof - so the proof's generation did not re-announce it.
    /// </summary>
    public static bool Applies(
        ApiModeEventPosition proof, string? ownPromptId, string promptId, ApiModeReceiptState state,
        string lastGeneration, DateTime lastGenerationStartedUtc)
        => state == ApiModeReceiptState.Queued
           && !string.Equals(promptId, ownPromptId, StringComparison.Ordinal)
           && proof.IsLaterGenerationThan(lastGeneration, lastGenerationStartedUtc);

    /// <summary>
    /// Whether a real receipt event at <paramref name="position"/> replaces the Unknown ending of a receipt last changed at
    /// (<paramref name="lastGeneration"/>, <paramref name="lastGenerationStartedUtc"/>): it does whatever its sequence,
    /// unless it is of an older generation, which can only be a replay (see the class summary).
    /// </summary>
    public static bool IsReplacedBy(ApiModeEventPosition position, string lastGeneration, DateTime lastGenerationStartedUtc)
        => string.Equals(position.Generation, lastGeneration, StringComparison.Ordinal)
           || position.IsLaterGenerationThan(lastGeneration, lastGenerationStartedUtc);

    /// <summary>The proof, named for a log line.</summary>
    public static string Describe(SessionEvent e, ApiModeEventPosition proof) => $"{e.GetType().Name} at {proof}";
}
