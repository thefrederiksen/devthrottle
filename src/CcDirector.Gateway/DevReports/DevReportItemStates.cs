namespace CcDirector.Gateway.DevReports;

/// <summary>
/// THE ONE PLACE A DEV REPORT ITEM'S DELIVERY STATE GETS ITS WORDS (issue #2958, mission ruling 4). The page
/// and every app show <see cref="Label"/> verbatim; nothing downstream decides what a status means. Adding a
/// state is one edit here.
///
/// The states, and when each is true, are specified in <c>docs/missions/dev-reports/PLAN-phase-2.md</c>:
/// <list type="bullet">
/// <item><c>held</c> - the session is working or its machine is not connected; it goes when the turn ends.</item>
/// <item><c>sending</c> - committed BEFORE the prompt is sent, so a crash between the send and its answer can never
/// send it again: an item found in <c>sending</c> that no drain in this process owns is settled delivered,
/// unconfirmed, and never re-sent.</item>
/// <item><c>delivered</c>, confirmed - the Director accepted the prompt.</item>
/// <item><c>delivered</c>, unconfirmed - the send left the Gateway and nothing confirmed it. Never retried.</item>
/// <item><c>replaced</c> - a later answer to the same question superseded this one while it was still held.</item>
/// <item><c>refused</c> - the session has ended, or the item's id is one the report already holds with different
/// content. Either way the Gateway does not hold the item and the page keeps it queued.</item>
/// <item><c>queued</c> - accepted, while the send request is still deciding.</item>
/// </list>
/// </summary>
internal static class DevReportItemStates
{
    public const string Queued = "queued";
    public const string Held = "held";
    public const string Sending = "sending";
    public const string Delivered = "delivered";
    public const string Replaced = "replaced";
    public const string Refused = "refused";

    /// <summary>One status and the words the owner sees for it.</summary>
    internal readonly record struct State(string Status, string Label);

    /// <summary>What a delivery attempt came to, in the three answers the prompt verb can give.</summary>
    internal enum SendOutcome
    {
        /// <summary>The Director accepted the prompt.</summary>
        Accepted,

        /// <summary>Nothing left this Gateway. The item stays held.</summary>
        NeverLeft,

        /// <summary>The send went out and nothing confirmed it.</summary>
        Unconfirmed,

        /// <summary>The Director answered with a definite refusal: nothing was typed. The item goes back to held,
        /// and the settle pass refuses it if the session has ended.</summary>
        Refused,
    }

    public static readonly State QueuedState = new(Queued, "Accepted");
    public static readonly State HeldState = new(Held, "Delivered when the agent finishes its turn");
    public static readonly State SendingState = new(Sending, "Sending to the session");
    public static readonly State DeliveredState = new(Delivered, "Delivered to the session");
    public static readonly State UnconfirmedState = new(Delivered, "Sent to the session, not confirmed");
    public static readonly State ReplacedState = new(Replaced, "Replaced by a later answer");
    public static readonly State SessionEndedState = new(Refused, "This session has ended");
    public static readonly State IdCollisionState = new(Refused,
        "Not sent: a different note already has this id. Remove this one and write it again");

    /// <summary>The state an item is in after a delivery attempt. <see cref="SendOutcome.NeverLeft"/> and
    /// <see cref="SendOutcome.Refused"/> put it back to held: nothing was typed, so the settle pass decides again.</summary>
    public static State AfterSend(SendOutcome outcome) => outcome switch
    {
        SendOutcome.Accepted => DeliveredState,
        SendOutcome.Unconfirmed => UnconfirmedState,
        SendOutcome.NeverLeft => HeldState,
        SendOutcome.Refused => HeldState,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "an outcome this fold does not know"),
    };

    /// <summary>True for the states that have not yet been settled - still waiting to go, or going now. These are
    /// what the report's open count counts.</summary>
    public static bool IsOpen(string status) => status is Queued or Held or Sending;

    /// <summary>True for the states still waiting to go and not yet handed to a send. Only these may be replaced by a
    /// later answer or drained: an item already <c>sending</c> is out of the Gateway's hands.</summary>
    public static bool IsWaiting(string status) => status is Queued or Held;
}
