namespace CcDirector.Core.Machine;

/// <summary>
/// HOW LATE A TIMER CALLBACK WAS TO START, AND WHETHER THE READER IT FEEDS HAD GONE STALE (issue #2818).
///
/// WHY THIS IS NEEDED AT ALL. Everything that timed the Director's roster push measured from the
/// moment the callback BEGAN RUNNING. That makes the one failure that matters invisible: a callback
/// can be queued for twenty-five seconds behind blocked session workers, start, find a connected
/// tunnel with no push in flight, complete its push in eighty milliseconds, and log a perfectly
/// healthy line - while the Gateway's pushed cache has already aged past its staleness cut and every
/// action on that Director's sessions is being refused. The lateness has to be measured from the
/// schedule, not from the callback.
///
/// LATENESS AND STALENESS ARE TWO DIFFERENT QUANTITIES, and an earlier version of this file conflated
/// them. It subtracted the cadence from the gap between callbacks and then compared THAT remainder
/// with the freshness window - so a ten second cadence, a twenty second window and a twenty-five
/// second gap gave a lateness of fifteen seconds, concluded fifteen is under twenty, and printed "no
/// action was refused" about a snapshot that was twenty-five seconds old and long stale. The Gateway
/// measures elapsed time since it RECEIVED a snapshot; it knows nothing of our cadence. So staleness
/// is now derived from the age of the last accepted snapshot, which is the same quantity the Gateway
/// uses, and lateness is reported beside it rather than standing in for it.
///
/// A pure function of times, so it is tested without a clock.
/// </summary>
public static class TimerLateness
{
    /// <summary>
    /// How much later than its cadence this callback started. Zero when it was on time or early; never
    /// negative. With no previous tick there is nothing to measure against, which reports zero.
    /// </summary>
    public static TimeSpan Of(TimeSpan interval, DateTime? previousTickUtc, DateTime nowUtc)
    {
        if (previousTickUtc is null) return TimeSpan.Zero;

        var actualGap = nowUtc - previousTickUtc.Value;
        var late = actualGap - interval;
        return late > TimeSpan.Zero ? late : TimeSpan.Zero;
    }

    /// <summary>
    /// Whether the Gateway's pushed cache had actually aged out by now, judged the way the GATEWAY
    /// judges it: on the age of the snapshot it last accepted, not on our callback timing.
    ///
    /// Null age means no snapshot has ever been accepted, and that is NOT an answer of "fresh" - it is
    /// an absence, and the caller must say so rather than assert either way.
    /// </summary>
    public static bool? CacheHadAgedOut(TimeSpan? sinceLastAcceptedSnapshot, TimeSpan stalenessWindow) =>
        sinceLastAcceptedSnapshot is null ? null : sinceLastAcceptedSnapshot.Value >= stalenessWindow;

    /// <summary>
    /// A log-safe sentence about a late callback, or null when it was on time and there is nothing to
    /// say.
    ///
    /// It reports the lateness and the snapshot age as SEPARATE facts, and it only claims that actions
    /// were refused when the snapshot age says so. It also states that any memory reading logged beside
    /// it was taken AFTER the delay: a probe read at the end of a twenty-five second stall describes
    /// the machine now, not the machine throughout the stall, and presenting it as proof of conditions
    /// during the delay would be evidence about the wrong moment.
    /// </summary>
    public static string? Describe(
        TimeSpan lateness, TimeSpan interval, TimeSpan stalenessWindow, TimeSpan? sinceLastAcceptedSnapshot)
    {
        if (lateness <= TimeSpan.Zero) return null;

        var freshness = CacheHadAgedOut(sinceLastAcceptedSnapshot, stalenessWindow) switch
        {
            true => $"the Gateway last accepted a snapshot {sinceLastAcceptedSnapshot!.Value.TotalSeconds:F1}s ago, " +
                    $"past its {stalenessWindow.TotalSeconds:F0}s freshness window - ACTIONS ON THIS DIRECTOR'S " +
                    "SESSIONS WERE BEING REFUSED",
            false => $"the Gateway last accepted a snapshot {sinceLastAcceptedSnapshot!.Value.TotalSeconds:F1}s ago, " +
                     $"still inside its {stalenessWindow.TotalSeconds:F0}s freshness window",
            null => "no snapshot has been accepted yet, so whether the Gateway's cache was fresh cannot be said",
        };

        return $"this tick started {lateness.TotalSeconds:F1}s later than its {interval.TotalSeconds:F0}s cadence; " +
               $"{freshness}. The delay is BEFORE any work this callback did; any memory reading logged " +
               "beside it was taken after the delay and describes the machine now, not during it.";
    }
}
