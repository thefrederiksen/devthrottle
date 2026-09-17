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

    /// <summary>The longest message text accepted. A message is read from the inbox, never typed, so it may
    /// be long - but not unbounded. Never below <see cref="MinTextLength"/>: a shorter cap is refused when the
    /// limits are built (inspection 8, ruling 2).</summary>
    public int MaxTextLength
    {
        get => _maxTextLength;
        init
        {
            if (value < MinTextLength)
                throw new ArgumentOutOfRangeException(nameof(MaxTextLength), value,
                    $"The text cap must be at least {MinTextLength} characters, so a stuck notice always names its whole message id.");
            _maxTextLength = value;
        }
    }

    private readonly int _maxTextLength = 16_000;

    /// <summary>How long a message id is: a Guid written as 32 hex digits.</summary>
    public const int MessageIdLength = 32;

    /// <summary>The shortest text cap allowed: the stuck notice's fixed opening plus one whole message id, so a
    /// notice fitted to any allowed cap still names its message (<see cref="FleetDoorbell.StuckNoticeText"/>).</summary>
    public static int MinTextLength => FleetDoorbell.StuckNoticePrefix.Length + MessageIdLength;
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

    /// <summary>True for the kinds a caller may name in the request body. The other three are decided by
    /// the route, never by the caller.</summary>
    public static bool IsCallerChoosable(string? kind) => kind is Message or Report;
}
