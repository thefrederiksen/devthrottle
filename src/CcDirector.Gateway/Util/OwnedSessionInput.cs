using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Util;

/// <summary>
/// A SESSION MAY TYPE INTO A SESSION IT OWNS (Parent Control, fix 1, 26 September 2026).
///
/// The Message Load mission refused every session key the right to type into any session, so the owner's own
/// composer could never be typed over. That left a coordinator unable to command the sessions it started: its only
/// channel was a queued message, and a queued message reaches an idle session only through the doorbell. The owner's
/// ruling: a parent may type into its children. This class is the one place that decides whether it may, for the two
/// routes that type - <c>POST /sessions/{sid}/prompt</c> and the continuation of <c>POST /sessions/{sid}/compact-context</c>.
///
/// THE RULE. The target must be owned DIRECTLY by the calling session: its <see cref="SessionDto.ControllerSessionId"/>
/// names the caller (set by <c>--controlled-by self</c> at spawn, by a hand-over, or by the Fleet Manager taking a
/// session). A grandchild is not the caller's, a session the owner runs directly is not, a session another session owns
/// is not, and a session of another account is never located at all.
///
/// THE OWNER'S UNSENT WORDS ARE NEVER TYPED OVER. A permitted send always goes with
/// <see cref="PromptRequest.OnlyWhenWaitingForInput"/>: the Director checks at the moment of typing that the session is
/// waiting for a prompt and that its composer holds nothing the owner typed and did not send, holds the session's input
/// from that check to the Enter, and refuses otherwise without typing a character. A Director older than that check
/// would ignore the flag and type anyway, so nothing is sent to one - the send is refused here instead.
///
/// NOT COVERED, DELIBERATELY: interrupt and escape. They are the same guard entry as the prompt, but they press
/// Ctrl+C and Escape, which in a coding agent clear the composer - exactly the owner's unsent words this protects - and
/// the Director has no "only when the composer is empty" form of either. They stay the owner's until it does.
/// </summary>
public static class OwnedSessionInput
{
    /// <summary>How long a follow-up after a compaction is retried while the session is only still settling: the tool
    /// records the finished compaction a moment before the session's state reads waiting for a prompt.</summary>
    public static readonly TimeSpan SettleAfterCompaction = TimeSpan.FromSeconds(30);

    /// <summary>The spacing of those retries.</summary>
    public static readonly TimeSpan SettlePoll = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Null when <paramref name="callerSessionId"/> may type into <paramref name="target"/>; otherwise the sentence the
    /// caller reads. <paramref name="directorChecksBeforeTyping"/> is whether the target's Director said on its Hello that
    /// it honours <see cref="PromptRequest.OnlyWhenWaitingForInput"/>. <paramref name="appendEnter"/> is false when the
    /// caller asked to leave its text in the composer unsent, which is refused: text a session left there is
    /// indistinguishable from the owner's own draft, and would be sent with the owner's next Enter.
    /// </summary>
    public static string? Refusal(string callerSessionId, SessionDto target, bool directorChecksBeforeTyping, bool appendEnter)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(callerSessionId))
            throw new ArgumentException("the calling session's id is required", nameof(callerSessionId));

        if (!OwnsDirectly(callerSessionId, target))
            return AgentInputRefusal.NotYourSession;
        if (!appendEnter)
            return AgentInputRefusal.NoSubmit;
        if (!directorChecksBeforeTyping)
            return AgentInputRefusal.DirectorTooOld;
        return null;
    }

    /// <summary>
    /// The sentence for a send the Director refused and typed nothing of, from its own answer: the owner has unsent words
    /// in the composer, the session was not waiting for a prompt, or its terminal submits a whole turn in one call.
    /// </summary>
    public static string DescribeRefusedSend(PromptResponse answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var why = answer.RefusedFor switch
        {
            PromptResponse.RefusedForOwnerDraft =>
                "the owner has typed words into that session's composer and not sent them, and they are never typed over",
            PromptResponse.RefusedForOneCallSubmit =>
                "that session's terminal submits a whole turn in one call, which cannot be taken back if the owner starts typing",
            _ => $"that session was not waiting for a prompt (it was {answer.ActivityState})",
        };
        return $"Nothing was typed: {why}. Try again when it is waiting, or {AgentInputRefusal.Instead}";
    }

    /// <summary>True when <paramref name="target"/>'s owner is <paramref name="callerSessionId"/> and it is not the
    /// caller itself. The comparison ignores case, as every session id comparison in the Gateway does.</summary>
    public static bool OwnsDirectly(string callerSessionId, SessionDto target)
        => !string.IsNullOrWhiteSpace(target.ControllerSessionId)
           && string.Equals(target.ControllerSessionId, callerSessionId, StringComparison.OrdinalIgnoreCase)
           && !string.Equals(target.SessionId, callerSessionId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The request a permitted send carries: the caller's text, marked as an agent's turn, typed only when the session is
    /// waiting for a prompt and its composer holds nothing of the owner's. The Enter is always pressed: a send that
    /// left its text sitting in the composer would be exactly the unsent text this rule protects.
    /// </summary>
    public static void ApplyTo(PromptRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        req.AgentDriven = true;
        req.AppendEnter = true;
        req.OnlyWhenWaitingForInput = true;
    }
}
