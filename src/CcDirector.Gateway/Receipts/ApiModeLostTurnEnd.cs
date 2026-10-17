using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Gateway.Receipts;

/// <summary>
/// THE ENDING OF A TURN WHOSE END WAS LOST (mission #4070, phase 2 follow-on, item 7). When the Director cannot send the
/// Gateway an event - the memory bound dropped it, it was too large for any batch, or it was published while the Gateway
/// was turned off - what follows goes up as a NEW generation (pull request #4090). A <c>TurnEnded</c> that fell in that
/// hole never arrives, and without this rule the receipts of the prompts its turn carried read "Delivered" with no turn
/// outcome for ever, while the session reads as if that turn were still running.
///
/// <para><b>The ending is the Gateway's inference, never history.</b> Its outcome word is <see cref="Outcome"/>; its
/// words say the delivery is certain and how the turn ended is not known. A real <c>TurnEnded</c> for that turn always
/// replaces it, whatever its position (<see cref="ApiModeReceiptStore.RecordTurnEnded"/>,
/// <see cref="ApiModeDisplay.ApiModeLiveRuling"/>).</para>
///
/// <para><b>It is ruled only on proof that the turn is over, never merely because a new generation began.</b> A hole
/// leaves the Director's session stream and its Pi process running, so the same turn may still be under way and end in
/// the new generation under the same turn id. The proof is an event of a LATER generation than the last event applied
/// to the receipt that is one of these three, each by a rule the Director keeps (<c>PiDialect</c>):</para>
/// <list type="bullet">
/// <item><c>TurnStarted</c> of a DIFFERENT turn. A session runs one turn at a time: the dialect starts a turn only when
/// none is running (<c>OnAgentStart</c> returns while one is), and no prompt is written to Pi while a turn holds prompts
/// in flight (<c>CanWritePrompt</c>), so a new turn means the earlier one has ended.</item>
/// <item><c>ConnectionEnded</c>. The agent process is gone, and the dialect ends a running turn <c>failed</c> BEFORE it
/// sends <c>ConnectionEnded</c> (<c>OnProcessExited</c>; event model rule 2), so no turn of that process continues.</item>
/// <item><c>ConnectionReady</c>. A new agent process is up (sent once per process); the earlier process ended its turn
/// before it went, as above, and a turn never moves between processes.</item>
/// </list>
/// <para><b>A turn whose start was lost too.</b> When the hole swallowed the carrying turn's <c>TurnStarted</c> as well,
/// the receipt reads Delivered with no turn id and no outcome. The same proofs rule it lost under the same condition,
/// with one difference: a <c>TurnStarted</c> that names the prompt is that prompt's own turn starting, not a proof that
/// it is over. Such a receipt's later real <c>TurnStarted</c> replaces the lost ending, as a real <c>TurnEnded</c>
/// replaces it on a receipt that has its turn (<see cref="ApiModeReceiptStore.RecordTurnStarted"/>).</para>
///
/// <para>Every other event in the new generation - a reply's text, a tool card, the same turn's own events - proves
/// nothing, and the receipt is left as it was. Inside ONE generation the rule never applies: there every event the
/// Director published reached the Gateway, so a missing <c>TurnEnded</c> there is not a lost one.</para>
///
/// <para>The receipt store and the live ruling both decide through <see cref="Applies"/>, so the receipts read and the
/// live stream give the same words for the same receipt.</para>
/// </summary>
public static class ApiModeLostTurnEnd
{
    /// <summary>The turn outcome the Gateway rules for a turn whose end was lost. Not an event-model word: no Director
    /// sends it.</summary>
    public const string Outcome = "lost";

    /// <summary>
    /// Whether <paramref name="e"/> proves every earlier turn of its session is over (see the class summary for why each
    /// one does). <paramref name="continuing"/> is the turn the event itself belongs to, which it does not prove over: the
    /// turn a <c>TurnStarted</c> starts, with the prompts it names; <see cref="Continuing.None"/> for a connection event.
    /// </summary>
    public static bool Proves(SessionEvent e, out Continuing continuing)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e)
        {
            case TurnStarted s:
                continuing = new Continuing(s.TurnId, s.PromptIds);
                return true;
            case ConnectionReady:
            case ConnectionEnded:
                continuing = Continuing.None;
                return true;
            default:
                continuing = Continuing.None;
                return false;
        }
    }

    /// <summary>
    /// Whether a proof at <paramref name="proof"/> rules the lost ending onto the receipt of <paramref name="promptId"/>:
    /// it is Delivered, its turn has no outcome yet, the proof is of a later generation than the last event applied to the
    /// receipt (<paramref name="lastGeneration"/>, <paramref name="lastGenerationStartedUtc"/>), and the proof does not
    /// belong to the receipt's own turn - a turn other than <paramref name="continuing"/>'s when the receipt has its turn
    /// id, and, when that turn's start was lost too (no turn id), a proving turn that does not name the prompt.
    /// </summary>
    public static bool Applies(
        ApiModeEventPosition proof, Continuing continuing, string promptId,
        ApiModeReceiptState state, string? turnId, string? turnOutcome,
        string lastGeneration, DateTime lastGenerationStartedUtc)
        => IsOpenOutside(continuing, promptId, state, turnId, turnOutcome)
           && proof.IsLaterGenerationThan(lastGeneration, lastGenerationStartedUtc);

    /// <summary><see cref="Applies"/> without the generation: the receipt is Delivered, its turn has no outcome, and the
    /// proof does not belong to that turn.</summary>
    public static bool IsOpenOutside(Continuing continuing, string promptId, ApiModeReceiptState state, string? turnId, string? turnOutcome)
        => state == ApiModeReceiptState.Delivered
           && turnOutcome is null
           && (turnId is not null
               ? !string.Equals(turnId, continuing.TurnId, StringComparison.Ordinal)
               : !continuing.PromptIds.Contains(promptId, StringComparer.Ordinal));

    /// <summary>The proof, named for a log line.</summary>
    public static string Describe(SessionEvent e, ApiModeEventPosition proof) => e switch
    {
        TurnStarted s => $"TurnStarted of turn {s.TurnId} at {proof}",
        _ => $"{e.GetType().Name} at {proof}",
    };

    /// <summary>The turn a proving event belongs to, which it does not prove over: its id and the prompts it names. None
    /// for a connection event, which belongs to no turn.</summary>
    public readonly record struct Continuing(string? TurnId, IReadOnlyList<string> PromptIds)
    {
        /// <summary>No turn: a connection event.</summary>
        public static Continuing None { get; } = new(null, Array.Empty<string>());
    }
}
