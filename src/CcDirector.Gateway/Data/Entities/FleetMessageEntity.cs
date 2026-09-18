namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One fleet message, held in the recipient's inbox (the Message Load mission, slice 1).
///
/// THE RECORD IS THE DELIVERY. A message is not typed into the recipient's terminal. It is written here,
/// and the send succeeds when this row exists - the sender is told "queued", never "delivered". The
/// recipient reads it with <c>cc-devthrottle message inbox</c>, and that read is the acknowledgement:
/// <see cref="ReadAtUtc"/> is stamped by the read and by nothing else.
///
/// WHY (tenant, message id) IS THE KEY. The message id is minted by the Gateway, so it cannot collide, but
/// every key on this model leads with the tenant so the global query filter and the index agree, and so a
/// row can never be addressed without naming its account.
///
/// STATUS IS DERIVED, NOT STORED. Read when <see cref="ReadAtUtc"/> is set; stuck when
/// <see cref="StuckAtUtc"/> is set and it is still unread; open otherwise. Two stored words that could
/// disagree with the two timestamps would be one more thing to keep equal.
///
/// COLUMNS THE LATER SLICES WRITE. The ring columns (<see cref="RingCount"/>, <see cref="LastRungAtUtc"/>,
/// <see cref="StuckAtUtc"/>) are written by the doorbell (slice 2, <see cref="Messaging.FleetDoorbell"/>); a
/// read clears <see cref="StuckAtUtc"/>. The reply columns (<see cref="CorrelationId"/>,
/// <see cref="InReplyToMessageId"/>, <see cref="ReplyByUtc"/>) were created by slice 1 and are written by slice 3
/// (replies, <see cref="Messaging.FleetMessageStore.TryReply"/>); slice 3 added the two it needed beside them,
/// <see cref="RepliedAtUtc"/> and <see cref="ReplyOverdueAtUtc"/>.
///
/// A MESSAGE THAT WANTS A REPLY carries a <see cref="CorrelationId"/> and a <see cref="ReplyByUtc"/>, and no
/// <see cref="InReplyToMessageId"/>. A REPLY is kind <c>reply</c> and carries the original's
/// <see cref="CorrelationId"/> and <see cref="InReplyToMessageId"/>. A NO-REPLY NOTICE is kind <c>system</c> and
/// carries them too, so the reader can be shown which question went unanswered.
/// </summary>
public sealed class FleetMessageEntity : TenantScopedEntity
{
    /// <summary>The message's own id, minted by the Gateway (32 hexadecimal characters).</summary>
    public string MessageId { get; set; } = "";

    /// <summary>The session whose inbox this message is in.</summary>
    public string RecipientSessionId { get; set; } = "";

    /// <summary>The session that sent it, taken from the session key that authenticated the send. Null for
    /// a system notice, whose sender is the Gateway itself.</summary>
    public string? SenderSessionId { get; set; }

    /// <summary>The sender's display name as the roster knew it at the moment of sending, or null.</summary>
    public string? SenderName { get; set; }

    /// <summary>The machine the sender was running on at the moment of sending, or null.</summary>
    public string? SenderMachine { get; set; }

    /// <summary>What kind of message this is - one of <see cref="Messaging.FleetMessageKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The full message text, exactly as sent. It may span many lines; it is never typed anywhere.</summary>
    public string Text { get; set; } = "";

    /// <summary>A hash of <see cref="Text"/>, so "the recipient has not yet read an identical message" is an
    /// indexed equality rather than a comparison of whole message bodies.</summary>
    public string TextHash { get; set; } = "";

    /// <summary>When the Gateway wrote the record (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the recipient read it (UTC), or null while it is unread.</summary>
    public DateTime? ReadAtUtc { get; set; }

    /// <summary>How many times the doorbell has been rung for this message (a deferred ring is not counted).</summary>
    public int RingCount { get; set; }

    /// <summary>When the doorbell was last rung (UTC).</summary>
    public DateTime? LastRungAtUtc { get; set; }

    /// <summary>When the message was marked stuck after unanswered rings (UTC); cleared again if the message is read.</summary>
    public DateTime? StuckAtUtc { get; set; }

    /// <summary>The correlation id of a message that wants a reply (32 hexadecimal characters, minted by the
    /// Gateway); on a reply or a no-reply notice, the correlation id of the message it is about.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>On a reply or a no-reply notice, the id of the message it is about. Null on an original.</summary>
    public string? InReplyToMessageId { get; set; }

    /// <summary>When a wanted reply is due (UTC). Set only on a message that asked for a reply.</summary>
    public DateTime? ReplyByUtc { get; set; }

    /// <summary>When the FIRST reply to this message was written (UTC), or null. Stamped in the same save as the
    /// reply. A reply that arrives after <see cref="ReplyOverdueAtUtc"/> still stamps it.</summary>
    public DateTime? RepliedAtUtc { get; set; }

    /// <summary>When the Gateway found this message's reply deadline passed with no reply (UTC), or null. Written
    /// in the same save as the no-reply notice to the sender, so the notice is written once.</summary>
    public DateTime? ReplyOverdueAtUtc { get; set; }
}
