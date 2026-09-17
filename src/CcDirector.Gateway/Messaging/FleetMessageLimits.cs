namespace CcDirector.Gateway.Messaging;

/// <summary>
/// The numbers that bound fleet messaging (the Message Load mission, ruling 3). The owner approved these
/// on 16 September 2026; they live in this one class so a change to any of them is one edit, and so the
/// policy's tests can name them rather than repeat them.
///
/// <see cref="Default"/> is the product. A test may build its own instance to move a boundary, but the
/// Gateway only ever uses <see cref="Default"/>.
/// </summary>
public sealed record FleetMessageLimits
{
    /// <summary>The limits the Gateway runs with.</summary>
    public static readonly FleetMessageLimits Default = new();

    /// <summary>How many messages one session may send in any rolling hour.</summary>
    public int PerSenderPerHour { get; init; } = 6;

    /// <summary>The rolling window <see cref="PerSenderPerHour"/> is counted over.</summary>
    public TimeSpan SenderWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The shortest gap between two messages from one sender to one recipient.</summary>
    public TimeSpan PerRecipientSpacing { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long an unread message waits before the doorbell is rung again (<see cref="FleetRingSchedule"/>).</summary>
    public TimeSpan RingGrace { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How many unanswered rings mark a message stuck (<see cref="FleetRingSchedule"/>).</summary>
    public int StuckAfterRings { get; init; } = 3;

    /// <summary>How long a message is kept after it was written. Thirty days, matching the activity ledger.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How far back <c>message inbox --all</c> reaches for messages already read (inspection 1,
    /// ruling 4). Reading marks a message read before its text has reached the reader, so a read whose answer
    /// was lost must be recoverable; messages read inside this window come back, newest first, up to
    /// <see cref="RecentReadCap"/>.
    /// </summary>
    public TimeSpan RecentReadWindow { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The most already-read messages one <c>message inbox --all</c> returns (inspection 2, ruling 1).
    /// Newest first; a read that had more says so with <c>truncated</c> and the full count, so the shared
    /// Gateway never loads an unbounded day of sixteen-thousand-character rows for one request.</summary>
    public int RecentReadCap { get; init; } = 200;

    /// <summary>How long a sender waits for a wanted reply when it names no deadline (slice 3, ruling 10). When it
    /// passes with no reply, the sender gets one no-reply notice.</summary>
    public TimeSpan DefaultReplyWindow { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>The shortest reply deadline a sender may name.</summary>
    public TimeSpan MinReplyWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The longest reply deadline a sender may name. A day: a question nobody answers in a day is not
    /// waiting on a reply any more, and the no-reply notice is what tells the sender to carry on.</summary>
    public TimeSpan MaxReplyWindow { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The longest message text accepted. A message is read from the inbox, never typed, so it may
    /// be long - but not unbounded.</summary>
    public int MaxTextLength { get; init; } = 16_000;
}

/// <summary>The kinds of fleet message. Stored in <c>fleet_messages.Kind</c>.</summary>
public static class FleetMessageKinds
{
    /// <summary>One session to one other session (<c>message send</c>).</summary>
    public const string Message = "message";

    /// <summary>A worker telling its supervisor what it did (<c>session report</c>).</summary>
    public const string Report = "report";

    /// <summary>One copy of a message to the sender's own workers (<c>message send all</c>).</summary>
    public const string Team = "team";

    /// <summary>One copy of a whole-account broadcast a human grant authorized (<c>--everyone</c>).</summary>
    public const string Everyone = "everyone";

    /// <summary>A notice written by the Gateway itself. No sender.</summary>
    public const string System = "system";

    /// <summary>An answer to a message that asked for one (slice 3, <c>message reply</c>). Written only by the
    /// reply route, to the sender of the original, and never chosen in a send's body.</summary>
    public const string Reply = "reply";

    /// <summary>True for the kinds a caller may name in the request body. The others are decided by
    /// the route, never by the caller.</summary>
    public static bool IsCallerChoosable(string? kind) => kind is Message or Report;
}
