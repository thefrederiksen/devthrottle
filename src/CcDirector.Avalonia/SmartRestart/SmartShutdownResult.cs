namespace CcDirector.Avalonia.SmartRestart;

/// <summary>What the owner chose in the Smart shutdown dialog.</summary>
public enum SmartShutdownChoice
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
/// <see cref="SmartShutdownChoice.SmartShutdown"/>; the other two choices have no time to allow.
/// </summary>
public sealed record SmartShutdownResult(SmartShutdownChoice Choice, TimeSpan? TimeAllowed)
{
    public static SmartShutdownResult Cancelled { get; } = new(SmartShutdownChoice.Cancelled, null);

    public static SmartShutdownResult IgnoreAllSessions { get; } = new(SmartShutdownChoice.IgnoreAllSessions, null);

    public static SmartShutdownResult SmartShutdown(TimeSpan timeAllowed) =>
        new(SmartShutdownChoice.SmartShutdown, timeAllowed);
}
