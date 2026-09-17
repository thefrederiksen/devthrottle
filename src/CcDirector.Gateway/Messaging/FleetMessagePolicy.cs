namespace CcDirector.Gateway.Messaging;

/// <summary>Why a message is allowed past the relationship and rate rules without meeting them.</summary>
public enum FleetMessageExemption
{
    /// <summary>An ordinary agent send. Every rule applies.</summary>
    None,

    /// <summary>A whole-account broadcast a human grant authorized (ruling 13). The grant IS the permission,
    /// so the relationship rule and the rate rules do not apply; the duplicate rule still does.</summary>
    HumanGrant,

    /// <summary>A notice the Gateway writes itself (ruling 11). No rule applies but the duplicate rule.</summary>
    System,
}

/// <summary>What the policy decided.</summary>
public enum FleetMessageOutcome
{
    /// <summary>Write the record.</summary>
    Queued,

    /// <summary>Do not write it: the recipient has not yet read an identical message from this sender.
    /// Not an error - the message the sender wanted to be read is already waiting.</summary>
    DuplicateDropped,

    /// <summary>The text is blank or longer than the limit.</summary>
    RefusedText,

    /// <summary>A session messaging itself.</summary>
    RefusedSelf,

    /// <summary>The recipient is neither the sender's supervisor nor one of its workers (ruling 1).</summary>
    RefusedNotRelated,

    /// <summary>The sender has used its messages for this hour (ruling 3).</summary>
    RefusedHourlyLimit,

    /// <summary>The sender wrote to this recipient too recently (ruling 3).</summary>
    RefusedRecipientSpacing,

    /// <summary>A reply that is not from the session the original was sent to, or not to the session that sent
    /// it, or to a Gateway notice (slice 3, ruling 10).</summary>
    RefusedReplyTarget,

    /// <summary>The original has already been answered; one reply per question (slice 3).</summary>
    RefusedAlreadyReplied,

    /// <summary>A reply named an id no message in the account has. Decided by the store's lookup, before the
    /// policy is asked (slice 3).</summary>
    RefusedUnknownMessage,

    /// <summary>A reply named a message that did not ask for one. Decided by the store's lookup (slice 3).</summary>
    RefusedNoReplyWanted,
}

/// <summary>The message a reply answers, as the store found it (slice 3, ruling 10).</summary>
/// <param name="MessageId">The original's id.</param>
/// <param name="OriginalSenderSessionId">The session that sent the original, or null for a Gateway notice.</param>
/// <param name="OriginalRecipientSessionId">The session the original was sent to.</param>
/// <param name="AlreadyReplied">True when a reply to the original has already been written.</param>
public sealed record FleetReplyOriginal(
    string MessageId,
    string? OriginalSenderSessionId,
    string OriginalRecipientSessionId,
    bool AlreadyReplied);

/// <summary>
/// Everything the policy needs to decide one send, gathered by the caller. The counts come from the
/// inbox table itself, so the limits survive a Gateway restart and need no counters held in memory.
/// </summary>
/// <param name="SenderSessionId">The sending session. Null only for a system notice.</param>
/// <param name="SenderControllerSessionId">The session that started the sender (its supervisor), from the
/// roster, or null when it has none or is not on the roster.</param>
/// <param name="RecipientSessionId">The session the message is for.</param>
/// <param name="RecipientControllerSessionId">The session that started the recipient, from the roster, or
/// null.</param>
/// <param name="Text">The message text.</param>
/// <param name="NowUtc">The moment of the send.</param>
/// <param name="SentBySenderInWindow">How many rate-counted messages the sender wrote in the last
/// <see cref="FleetMessageLimits.SenderWindow"/>.</param>
/// <param name="LastSentToRecipientUtc">When the sender last wrote to this recipient, or null.</param>
/// <param name="RecipientHasUnreadDuplicate">True when the recipient holds an unread message from this sender
/// with exactly this text.</param>
/// <param name="Exemption">Why, if at all, the relationship and rate rules are waived.</param>
/// <param name="Kind">The kind of message, one of <see cref="FleetMessageKinds"/>. A report is not held to
/// the per-recipient spacing - see <see cref="FleetMessagePolicy"/>.</param>
/// <param name="ReplyTo">For a <see cref="FleetMessageKinds.Reply"/>, the message it answers; null otherwise.</param>
public sealed record FleetMessageAttempt(
    string? SenderSessionId,
    string? SenderControllerSessionId,
    string RecipientSessionId,
    string? RecipientControllerSessionId,
    string Text,
    DateTime NowUtc,
    int SentBySenderInWindow,
    DateTime? LastSentToRecipientUtc,
    bool RecipientHasUnreadDuplicate,
    FleetMessageExemption Exemption = FleetMessageExemption.None,
    string Kind = FleetMessageKinds.Message,
    FleetReplyOriginal? ReplyTo = null);

/// <summary>The decision and the sentence that explains it. <see cref="Reason"/> is what the sender reads;
/// it is empty only for <see cref="FleetMessageOutcome.Queued"/>.</summary>
public readonly record struct FleetMessageVerdict(FleetMessageOutcome Outcome, string Reason)
{
    /// <summary>True when the record should be written.</summary>
    public bool Queued => Outcome == FleetMessageOutcome.Queued;

    /// <summary>True for an outcome that is a refusal, as opposed to a queue or a dropped duplicate.</summary>
    public bool Refused => Outcome is not (FleetMessageOutcome.Queued or FleetMessageOutcome.DuplicateDropped);
}

/// <summary>
/// WHO MAY MESSAGE WHOM, AND HOW OFTEN (the Message Load mission, rulings 1 and 3). One pure function on
/// the facts in a <see cref="FleetMessageAttempt"/>, so every rule is provable without a database, a
/// roster or a clock.
///
/// RULE 1: a session may message only its supervisor (the session that started it) and its own workers
/// (the sessions it started). Nothing else - not a sibling, not a session on the same Mission, not the
/// session on the next desk. A session with no supervisor and no workers can message nobody.
///
/// RULE 3: at most <see cref="FleetMessageLimits.PerSenderPerHour"/> messages per rolling hour per sender;
/// at most one per recipient per <see cref="FleetMessageLimits.PerRecipientSpacing"/>; and a message
/// identical to one the recipient has not yet read is dropped, because the one it would add is already
/// waiting.
///
/// A REPORT IS NOT HELD TO THE PER-RECIPIENT SPACING. Every rate refusal tells the sender to put what it
/// wanted to say in its report, so refusing the report itself because the worker asked its supervisor a
/// question five minutes earlier would send it round in a circle. The hourly limit and the duplicate rule
/// still apply to a report, and a report refused by the hourly limit is told to leave its answer in its own
/// session, where the supervisor reads it.
///
/// A REPLY HAS ITS OWN RULE (slice 3, ruling 10). It is allowed from the session the original was sent to, to
/// the session that sent it, whatever their relationship - the sender asked, so the answer may come back. It
/// goes to nobody else. It is held to the text rules and the duplicate rule, and to one reply per question,
/// but NOT to the hourly limit or the per-recipient spacing: a reply is an answer, not a new demand. It is
/// not counted towards the replier's hourly six either (<see cref="FleetMessageStore"/> leaves it out of the
/// count). A reply after the deadline is allowed like any other.
///
/// THE ORDER IS PART OF THE RULE. Text first (nothing else can be judged about a blank message); then
/// self and relationship, because a session that may not write to this recipient at all should be told
/// THAT, not that it is sending too fast; then the duplicate, so a repeat of a waiting message is dropped
/// quietly rather than counted as a refusal; then the two rates.
///
/// This replaces the Core <c>MessageSteward</c> the Gateway used to consult (three-second duplicate
/// window, sixty per minute), which judged a message only by how often it was sent and never by who it
/// was for. The <see cref="Api.BroadcastGovernor"/> still owns the human grants for a whole-account
/// broadcast and that broadcast's own rate; it decides nothing this class decides.
/// </summary>
public static class FleetMessagePolicy
{
    /// <summary>The advice every rate refusal ends with (ruling 3).</summary>
    public const string PutItInYourReport =
        "Put it in your report instead: what you would have sent belongs in the answer you give when your turn ends.";

    /// <summary>The advice a refused REPORT gets - it cannot be told to put itself in a report.</summary>
    public const string LeaveItInYourSession =
        "Leave your report as the last thing you write in this session; your supervisor reads it there when it opens you.";

    /// <summary>Decide one send.</summary>
    public static FleetMessageVerdict Decide(FleetMessageAttempt attempt, FleetMessageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(limits);

        var text = attempt.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return new(FleetMessageOutcome.RefusedText, "The message is blank. Say what you need.");
        if (text.Length > limits.MaxTextLength)
            return new(FleetMessageOutcome.RefusedText,
                $"The message is {text.Length} characters; the limit is {limits.MaxTextLength}. Shorten it.");

        var exempt = attempt.Exemption != FleetMessageExemption.None;
        var recipient = attempt.RecipientSessionId;
        var sender = attempt.SenderSessionId;

        if (attempt.Kind == FleetMessageKinds.Reply)
            return DecideReply(attempt, recipient, sender);

        if (!exempt)
        {
            if (string.IsNullOrWhiteSpace(sender))
                return new(FleetMessageOutcome.RefusedNotRelated,
                    "The sender could not be identified, so who it may message cannot be decided.");

            if (Same(sender, recipient))
                return new(FleetMessageOutcome.RefusedSelf, "A session cannot message itself.");

            var recipientIsMySupervisor = Same(attempt.SenderControllerSessionId, recipient);
            var recipientIsMyWorker = Same(attempt.RecipientControllerSessionId, sender);
            if (!recipientIsMySupervisor && !recipientIsMyWorker)
                return new(FleetMessageOutcome.RefusedNotRelated,
                    $"You may message only the session that started you and the sessions you started. " +
                    $"{Short(recipient)} is neither, so nothing was queued. {PutItInYourReport}");
        }

        if (attempt.RecipientHasUnreadDuplicate)
            return Duplicate(recipient);

        if (!exempt)
        {
            var isReport = attempt.Kind == FleetMessageKinds.Report;
            if (attempt.SentBySenderInWindow >= limits.PerSenderPerHour)
                return new(FleetMessageOutcome.RefusedHourlyLimit,
                    $"You have sent {attempt.SentBySenderInWindow} messages in the last " +
                    $"{Describe(limits.SenderWindow)}; the limit is {limits.PerSenderPerHour}. " +
                    $"Nothing was queued. {(isReport ? LeaveItInYourSession : PutItInYourReport)}");

            if (!isReport && attempt.LastSentToRecipientUtc is { } last)
            {
                var since = attempt.NowUtc - last;
                if (since < limits.PerRecipientSpacing)
                {
                    var wait = limits.PerRecipientSpacing - since;
                    return new(FleetMessageOutcome.RefusedRecipientSpacing,
                        $"You messaged {Short(recipient)} {Describe(since)} ago; one message per recipient every " +
                        $"{Describe(limits.PerRecipientSpacing)}. The next one is allowed in {Describe(wait)}. " +
                        $"Nothing was queued. {PutItInYourReport}");
                }
            }
        }

        return new(FleetMessageOutcome.Queued, "");
    }

    private static FleetMessageVerdict DecideReply(FleetMessageAttempt attempt, string recipient, string? sender)
    {
        var original = attempt.ReplyTo;
        if (original is null || attempt.Exemption != FleetMessageExemption.None)
            return new(FleetMessageOutcome.RefusedReplyTarget,
                "A reply must name the message it answers, and only a session sends one.");
        if (string.IsNullOrWhiteSpace(original.OriginalSenderSessionId))
            return new(FleetMessageOutcome.RefusedReplyTarget,
                $"Message {original.MessageId} is a notice from the Gateway; there is nobody to reply to.");
        if (!Same(sender, original.OriginalRecipientSessionId))
            return new(FleetMessageOutcome.RefusedReplyTarget,
                $"Only the session message {original.MessageId} was sent to may reply to it. Nothing was queued.");
        if (!Same(recipient, original.OriginalSenderSessionId))
            return new(FleetMessageOutcome.RefusedReplyTarget,
                $"A reply to message {original.MessageId} goes only to the session that sent it, " +
                $"{Short(original.OriginalSenderSessionId)}. Nothing was queued.");

        if (attempt.RecipientHasUnreadDuplicate)
            return Duplicate(recipient);

        if (original.AlreadyReplied)
            return new(FleetMessageOutcome.RefusedAlreadyReplied,
                $"You have already replied to message {original.MessageId}; one reply per question. " +
                $"Nothing was queued. {PutItInYourReport}");

        return new(FleetMessageOutcome.Queued, "");
    }

    private static FleetMessageVerdict Duplicate(string recipient) =>
        new(FleetMessageOutcome.DuplicateDropped,
            $"{Short(recipient)} has not yet read an identical message from you, so this copy was dropped. " +
            "The one already waiting will be read.");

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Short(string id) => string.IsNullOrEmpty(id) ? "(unknown)" : (id.Length <= 8 ? id : id[..8]);

    /// <summary>A duration in the words a person reads: whole minutes, rounded up, never "0 minutes".</summary>
    internal static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes >= 60 && span.TotalMinutes % 60 == 0)
        {
            var hours = (int)span.TotalHours;
            return hours == 1 ? "hour" : $"{hours} hours";
        }
        var minutes = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
