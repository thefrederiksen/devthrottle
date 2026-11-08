namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE MESSAGE IN THE OWNER'S INBOX, in the <c>your_inbox_messages</c> table (Factory Inbox mission, phase 3, "Your inbox").
/// A session sends it with <c>cc-devthrottle inbox send</c>; a factory's Boss writes one when it declines, cannot, or pushes
/// back; a factory's morning summary becomes one. It replaces the email the fleet sent the owner.
///
/// WHO SENT IT IS RECORDED WHEN IT IS SENT, from the Gateway's own record of the sending session - never from the
/// request - so a message still says who wrote it after that session's history is gone. Every word shown for it is
/// folded on the Gateway (<c>Inbox.YourInboxFold</c>, CLAUDE.md rule 7).
///
/// NOTHING IS DELETED BY THE SYSTEM. A message is unread, read or archived; there is no delete, and no sweep removes one.
/// </summary>
public sealed class YourInboxMessageEntity : GatewayMintedKeyEntity
{
    /// <summary>The subject line, one line.</summary>
    public string Subject { get; set; } = "";

    /// <summary>The message itself.</summary>
    public string Body { get; set; } = "";

    /// <summary>Who it is from: <c>session</c> or <c>factory</c> (<c>Inbox.YourInboxWords.Senders</c>).</summary>
    public string FromKind { get; set; } = "";

    /// <summary>The session that wrote it - for a factory's message, the Boss session that wrote it - or null when no
    /// session wrote it (a summary the Gateway built).</summary>
    public string? SessionId { get; set; }

    /// <summary>That session's three-digit number when it was sent, or null when the Gateway did not know it.</summary>
    public int? SessionNumber { get; set; }

    /// <summary>That session's name when it was sent, or null when the Gateway did not know it.</summary>
    public string? SessionName { get; set; }

    /// <summary>The factory it came from, in the Gateway's one spelling of a factory id, or null.</summary>
    public string? Factory { get; set; }

    /// <summary>The factory seat that wrote it, or null.</summary>
    public string? Seat { get; set; }

    /// <summary>The dev report it is about, or null.</summary>
    public Guid? ReportId { get; set; }

    /// <summary>That report's title when the message was sent, or null.</summary>
    public string? ReportTitle { get; set; }

    /// <summary>The Factory inbox message it answers (a Boss's decision), or null.</summary>
    public Guid? FactoryMessageId { get; set; }

    /// <summary><c>unread</c>, <c>read</c> or <c>archived</c>.</summary>
    public string State { get; set; } = "";

    /// <summary>When it was sent (UTC).</summary>
    public DateTime SentAtUtc { get; set; }

    /// <summary>When the owner last marked it read (UTC), or null while it has never been read.</summary>
    public DateTime? ReadAtUtc { get; set; }

    /// <summary>When the owner archived it (UTC), or null while it is in the inbox.</summary>
    public DateTime? ArchivedAtUtc { get; set; }
}

/// <summary>
/// ONE REPLY THE OWNER SENT FROM YOUR INBOX, in the <c>your_inbox_replies</c> table (Factory Inbox mission, phase 3). Kept
/// beside the message it answers, with where it went: into the sending session's inbox while that session runs, or into
/// a factory's Factory inbox. A reply that could not go anywhere is not stored - the owner is told why and offered where
/// it can go instead.
/// </summary>
public sealed class YourInboxReplyEntity : GatewayMintedKeyEntity
{
    /// <summary>The Your inbox message this answers.</summary>
    public Guid MessageId { get; set; }

    /// <summary>The reply as the owner wrote it.</summary>
    public string Text { get; set; } = "";

    /// <summary>Where it went: <c>session</c> or <c>factory</c> (<c>Inbox.YourInboxWords.ReplyTargets</c>).</summary>
    public string RoutedTo { get; set; } = "";

    /// <summary>The session it went to, or null.</summary>
    public string? SessionId { get; set; }

    /// <summary>The factory it went to, or null.</summary>
    public string? Factory { get; set; }

    /// <summary>The fleet message it was delivered as, when it went to a session.</summary>
    public string? FleetMessageId { get; set; }

    /// <summary>The Factory inbox message it was left as, when it went to a factory.</summary>
    public Guid? FactoryMessageId { get; set; }

    /// <summary>The device id of the owner's phone or browser that sent it.</summary>
    public string SentBy { get; set; } = "";

    /// <summary>When it was sent (UTC).</summary>
    public DateTime SentAtUtc { get; set; }
}

/// <summary>
/// ONE THING AN AGENT DID TO A MESSAGE IN YOUR INBOX, in the <c>your_inbox_agent_actions</c> table (Factory Inbox mission,
/// "agents use the inboxes", the owner's decision of 7 November 2026). An agent reads, marks, archives, restores and
/// replies to the account's mail exactly as the owner does, and every one of those acts is kept here with the session
/// that did it - its number and name as the Gateway knew them at that moment - and the time, so the message can say
/// "Archived by an agent: session 114 ..." and the owner can tell his own acts from an agent's.
///
/// NOTHING IS DELETED: a row is written once and never changed.
/// </summary>
public sealed class YourInboxAgentActionEntity : GatewayMintedKeyEntity
{
    /// <summary>The Your inbox message the agent acted on.</summary>
    public Guid MessageId { get; set; }

    /// <summary>What it did: one of <c>Inbox.YourInboxWords.AgentActs</c> - read, marked read, marked unread, archived,
    /// restored, replied.</summary>
    public string Action { get; set; } = "";

    /// <summary>The session that did it.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>That session's three-digit number at the time, or null when the Gateway did not know it.</summary>
    public int? SessionNumber { get; set; }

    /// <summary>That session's name at the time, or null when the Gateway did not know it.</summary>
    public string? SessionName { get; set; }

    /// <summary>The reply it sent, for a reply; null for every other act.</summary>
    public Guid? ReplyId { get; set; }

    /// <summary>When it did it (UTC).</summary>
    public DateTime AtUtc { get; set; }
}
