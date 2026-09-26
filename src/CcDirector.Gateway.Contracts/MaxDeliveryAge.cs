namespace CcDirector.Gateway.Contracts;

/// <summary>
/// HOW OLD A PROMPT MAY BE AND STILL BE TYPED, stated once for the Gateway and the Director (Voice Delivery
/// mission, phase 5, QA finding F6).
///
/// THE RULE: the Gateway puts the Send time on every prompt request (<see cref="PromptRequest.SentAtUtc"/>) and the
/// Director refuses to type one strictly older than this limit - on receipt, and again immediately before the first
/// character is typed, after every gate and wait the send passes through. Nothing is typed, the delivery id is
/// recorded not-delivered with the too-old reason, and the verb answers not-delivered with that reason. The
/// boundary is strict: 4:59 and 5:00 from Send are typed, 5:01 is not.
///
/// THE NUMBER LIVES HERE AND ONLY HERE. Before phase 5 it lived in the Gateway's dictation endpoint alone, so it
/// guarded only what the Gateway still held: a command already handed to the Director had no age limit at all, and
/// on 25 September 2026 a frozen Director typed a spoken command seven minutes old the moment it woke. Both sides
/// read this one number now - <c>GatewayDictationEndpoint.MaxDeliveryAge</c> is it, and the Director's check is it -
/// so the two separately shipped halves cannot drift apart.
///
/// The owner, 25 September 2026: "if it's five minutes in, we don't send it" ... "I don't wanna wait 12 seconds and
/// then it says it's moved on." The limit was chosen in dev report de5eeaea: "5 minutes".
///
/// THE CLOCK NOTE: the check compares the Gateway's clock with the Director machine's. The measured age is written
/// into the refusal's log line, so a skewed clock is visible; no correction is added.
/// </summary>
public static class MaxDeliveryAge
{
    /// <summary>
    /// How old a prompt may be, in minutes from the moment the Gateway sent it, and still be typed into the session
    /// automatically. Older than this - strictly more - and the Director types nothing. See the class summary for
    /// the owner's words.
    /// </summary>
    public const int Minutes = 5;

    /// <summary>The limit as a span; see <see cref="Minutes"/>.</summary>
    public static readonly TimeSpan Span = TimeSpan.FromMinutes(Minutes);

    /// <summary>
    /// The fixed reason word of an age refusal, on the Director's answer and its delivery record: "too-old". It
    /// rides beside the limit because the two halves must agree on it as on the number: a Gateway reading a
    /// Director's not-delivered answer matches this word to know the words are known NOT in the session (a retry
    /// cannot double them) and shows them back to the owner.
    /// </summary>
    public const string TooOldReason = "too-old";
}
