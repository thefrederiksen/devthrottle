namespace CcDirector.Avalonia.SmartRestart;

/// <summary>What the owner chose in the Smart shutdown dialog.</summary>
public enum SmartShutdownChoiceKind
{
    /// <summary>Nothing happens. Also what closing the window by its own X means.</summary>
    Cancelled,

    /// <summary>Shut the sessions down nicely, each with a handover, inside the time allowed.</summary>
    SmartShutdown,

    /// <summary>End every session at once; no handovers are written.</summary>
    IgnoreAllSessions,
}

/// <summary>
/// The one result the Smart shutdown dialog gives back. <see cref="TimeAllowed"/> is set only for
/// <see cref="SmartShutdownChoiceKind.SmartShutdown"/>; the other two choices have no time to allow.
/// </summary>
public sealed record SmartShutdownChoice(SmartShutdownChoiceKind Choice, TimeSpan? TimeAllowed)
{
    public static SmartShutdownChoice Cancelled { get; } = new(SmartShutdownChoiceKind.Cancelled, null);

    public static SmartShutdownChoice IgnoreAllSessions { get; } = new(SmartShutdownChoiceKind.IgnoreAllSessions, null);

    public static SmartShutdownChoice SmartShutdown(TimeSpan timeAllowed) =>
        new(SmartShutdownChoiceKind.SmartShutdown, timeAllowed);
}
