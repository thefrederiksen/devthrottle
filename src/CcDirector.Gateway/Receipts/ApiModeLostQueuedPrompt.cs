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
/// <para><b>A record of the prompt is not proof that it vanished.</b> A proving event that names a prompt is a record of
/// that prompt, so the prompt is not ruled by it (<see cref="Proves"/> hands such prompts back). A <c>PromptDelivered</c>
/// or <c>PromptRefused</c> ends its own prompt by its own change. A <c>TurnStarted</c> names the prompts its turn
/// carries: the Director says it sent them, even when their <c>PromptDelivered</c> fell in the hole.</para>
///
/// <para><b>A prompt a later turn carries is ruled Unknown, linked to that turn (<see cref="IsCarriedBy"/>).</b> In the
/// Director's own events a prompt id enters <c>TurnStarted.PromptIds</c> only after its <c>PromptDelivered</c> was
/// published (<c>PiDialect</c> fills the set it starts a turn from in the <c>started</c> disposition of that event), so
/// its <c>PromptDelivered</c> fell in the hole. It is NOT kept Queued: the Director's own line dropped the prompt at its
/// delivery, so a Queued receipt would show a place in line the Director does not hold and count one prompt too many
/// ahead of every prompt behind it. It is NOT ruled Delivered either: the mission defines Delivered as the agent's own
/// acknowledgement, and the Gateway does not hold one (the Delivery Lead's ruling on pull request #4209). So the receipt
/// is ruled <see cref="ApiModeReceiptState.Unknown"/> and linked to that turn by the same event, out of the line, and
/// its words (<see cref="CarriedWords"/>) say what the Gateway knows - the record of its delivery was lost and a turn
/// carrying it started - and nothing more. A turn link is what tells it from the other Unknown rulings, which are made
/// only on Queued receipts and so never carry one (<see cref="IsCarried"/>). The turn's outcome lands on it by the
/// ordinary rules, so the session's turns stay counted, but its words do not change with it. The prompt's real
/// <c>PromptDelivered</c>, should one arrive in a generation not older than the ruling, replaces the ruling with a true
/// Delivered, as for every Unknown receipt.</para>
///
/// <para><b>Once per generation.</b> After the first proof of a generation no Queued receipt of an older generation is
/// left (each was ruled, or moved to this generation by its re-announcement), and none can
/// appear (the Gateway accepts no older generation after a newer one). So the ingest makes the proof once per session
/// generation and skips it after; a Gateway restart forgets that, and the next event proves once more, which changes
/// nothing already ruled.</para>
///
/// <para><b>The second proof: the owning Director came back as a NEW process.</b> A Director that dies (a crash, a
/// power loss, a restart) takes its queue with it - the queue is held in memory only - and a restarted Director gives
/// the session a new session id (phase 2 review, observation C), so the old session never sees a later generation and
/// the first proof never arrives. What does arrive is the Director's Hello, which carries the start time of the
/// process saying it (<c>DirectorStreamHello.StartedAt</c>, read once per process, so every reconnect of one process
/// sends the same value). A generation is made by a session stream inside its own process, so it starts at or after
/// that process's start; a hole's generation is still inside it. So when the Director that holds a receipt says Hello
/// with a start time LATER than the start of the receipt's last generation, the process that made that generation has
/// ended, and the receipt is ruled Unknown (<see cref="AppliesToEndedProcess"/>). Both times are read from the clock
/// of the one Director machine, so comparing them is sound. A reconnect of the same process sends its own start time,
/// never later than its own generations, so it rules nothing. A Hello with no start time (default) rules nothing. It
/// could fire falsely only if two live processes shared one Director id, or if the Director machine's clock stepped
/// BACKWARD after the process started, by more than the process's age at the step, and the process then reconnected:
/// a generation made after that step is stamped earlier than the process's own start. (A forward step only makes later
/// generations later still, which is safe.) Even then a later real event replaces the ruling. This proof has no event and so no position: the
/// receipt keeps the position of its last event, and any later real event replaces the ruling as above.</para>
///
/// <para><b>Restart or hole: the words claim neither.</b> No event names the Director process, so the first proof
/// cannot tell the two apart. The second does prove a restart, but not that the prompt was undelivered: a prompt
/// written to the agent just before the crash may have had its <c>PromptDelivered</c> die unpushed with the process.
/// The words (<see cref="Words"/>) therefore say only what both proofs show.</para>
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

    /// <summary>The words of a receipt ruled <see cref="ApiModeReceiptState.Unknown"/> because a later turn carried it
    /// (<see cref="IsCarriedBy"/>). Neutral, and they claim no delivery: the Gateway holds no acknowledgement.</summary>
    public const string CarriedWords = "Not known - the record of its delivery was lost when the Director reconnected; a turn carrying it started";

    /// <summary>
    /// Whether <paramref name="e"/> comes after every re-announcement of its generation (see the class summary): any
    /// event but a <c>PromptQueued</c> (and a <c>MessageText</c>, see the class summary). <paramref name="ownPromptIds"/>
    /// are the prompts the event itself names, which this proof never rules: the one a <c>PromptDelivered</c> or
    /// <c>PromptRefused</c> ends, or the ones a <c>TurnStarted</c> carries; empty for every other event.
    /// </summary>
    public static bool Proves(SessionEvent e, out IReadOnlyCollection<string> ownPromptIds)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e)
        {
            case PromptQueued:
            case MessageText:
                ownPromptIds = Array.Empty<string>();
                return false;
            case PromptDelivered d:
                ownPromptIds = new[] { d.PromptId };
                return true;
            case PromptRefused f:
                ownPromptIds = new[] { f.PromptId };
                return true;
            case TurnStarted t:
                ownPromptIds = t.PromptIds;
                return true;
            default:
                ownPromptIds = Array.Empty<string>();
                return true;
        }
    }

    /// <summary>
    /// Whether a proof at <paramref name="proof"/> rules the receipt of <paramref name="promptId"/> Unknown: it is Queued,
    /// it is not a prompt the proving event itself names (<paramref name="ownPromptIds"/>), and the last event applied to
    /// it (<paramref name="lastGeneration"/>, <paramref name="lastGenerationStartedUtc"/>) is of an older generation than
    /// the proof - so the proof's generation did not re-announce it.
    /// </summary>
    public static bool Applies(
        ApiModeEventPosition proof, IReadOnlyCollection<string> ownPromptIds, string promptId, ApiModeReceiptState state,
        string lastGeneration, DateTime lastGenerationStartedUtc)
        => state == ApiModeReceiptState.Queued
           && !Names(ownPromptIds, promptId)
           && proof.IsLaterGenerationThan(lastGeneration, lastGenerationStartedUtc);

    /// <summary>
    /// Whether the proving event <paramref name="e"/> at <paramref name="proof"/> is a <c>TurnStarted</c> that carries the
    /// Queued receipt of <paramref name="promptId"/>, last changed in an older generation: the receipt is ruled Unknown,
    /// linked to that turn, and its position moves to the proof (see the class summary).
    /// </summary>
    public static bool IsCarriedBy(
        SessionEvent e, ApiModeEventPosition proof, string promptId, ApiModeReceiptState state,
        string lastGeneration, DateTime lastGenerationStartedUtc)
        => e is TurnStarted t
           && state == ApiModeReceiptState.Queued
           && Names(t.PromptIds, promptId)
           && proof.IsLaterGenerationThan(lastGeneration, lastGenerationStartedUtc);

    /// <summary>Whether a receipt is one a later turn carried (<see cref="IsCarriedBy"/>): Unknown and linked to a turn.
    /// Its words are <see cref="CarriedWords"/>, and its turn is kept like a delivered receipt's.</summary>
    public static bool IsCarried(ApiModeReceiptState state, string? turnId)
        => state == ApiModeReceiptState.Unknown && turnId is not null;

    private static bool Names(IReadOnlyCollection<string> promptIds, string promptId)
        => promptIds.Contains(promptId, StringComparer.Ordinal);

    /// <summary>
    /// Whether a real receipt event at <paramref name="position"/> replaces the Unknown ending of a receipt last changed at
    /// (<paramref name="lastGeneration"/>, <paramref name="lastGenerationStartedUtc"/>): it does whatever its sequence,
    /// unless it is of an older generation, which can only be a replay (see the class summary).
    /// </summary>
    public static bool IsReplacedBy(ApiModeEventPosition position, string lastGeneration, DateTime lastGenerationStartedUtc)
        => string.Equals(position.Generation, lastGeneration, StringComparison.Ordinal)
           || position.IsLaterGenerationThan(lastGeneration, lastGenerationStartedUtc);

    /// <summary>
    /// Whether a Hello from the Director that holds a receipt, saying its process started at
    /// <paramref name="processStartedUtc"/>, rules that receipt Unknown: it is Queued, the start time is set, and the
    /// receipt's last generation (<paramref name="lastGenerationStartedUtc"/>) started BEFORE that process did, so it was
    /// made by an earlier process, which has ended (see the class summary). Compared at the whole milliseconds generations
    /// are kept at; a tie rules nothing.
    /// </summary>
    public static bool AppliesToEndedProcess(DateTime processStartedUtc, ApiModeReceiptState state, DateTime lastGenerationStartedUtc)
        => state == ApiModeReceiptState.Queued
           && processStartedUtc != default
           && ApiModeEventPosition.StartPrecision(lastGenerationStartedUtc) < ApiModeEventPosition.StartPrecision(processStartedUtc);

    /// <summary>The proof, named for a log line.</summary>
    public static string Describe(SessionEvent e, ApiModeEventPosition proof) => $"{e.GetType().Name} at {proof}";
}
