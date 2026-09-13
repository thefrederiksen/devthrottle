namespace CcDirector.Core.Machine;

/// <summary>
/// HOW LATE A TIMER CALLBACK WAS TO START (issue #2818).
///
/// WHY THIS IS NEEDED AT ALL. Everything that timed the Director's roster push measured from the
/// moment the callback BEGAN RUNNING. That makes the one failure that matters invisible: a callback
/// can be queued for twenty-five seconds behind blocked session workers, start, find a connected
/// tunnel with no push in flight, complete its push in eighty milliseconds, and log a perfectly
/// healthy line - while the Gateway's pushed cache has already aged past its twenty second staleness
/// cut and every action on that Director's sessions is being refused. The existing "skipped" and
/// "slow" messages cannot fire on that path. The lateness has to be measured from the schedule, not
/// from the callback.
///
/// A pure function of three times, so it is tested without a clock.
/// </summary>
public static class TimerLateness
{
    /// <summary>
    /// How much later than its cadence this callback started. Zero when it was on time or early; never
    /// negative. The first tick has no previous tick to measure from and reports zero.
    /// </summary>
    public static TimeSpan Of(TimeSpan interval, DateTime? previousTickUtc, DateTime nowUtc)
    {
        if (previousTickUtc is null) return TimeSpan.Zero;

        var actualGap = nowUtc - previousTickUtc.Value;
        var late = actualGap - interval;
        return late > TimeSpan.Zero ? late : TimeSpan.Zero;
    }

    /// <summary>
    /// True when the callback was late enough that the reader it feeds may already have aged out.
    ///
    /// The test is on the STALENESS WINDOW, not on the cadence: being one cadence late is ordinary on a
    /// busy machine and costs nobody anything, because the window is twice the cadence. Being late by a
    /// whole window means the Gateway has, or nearly has, stopped believing in this Director's sessions.
    /// </summary>
    public static bool MattersToTheReader(TimeSpan lateness, TimeSpan stalenessWindow) =>
        lateness >= stalenessWindow;

    /// <summary>
    /// A log-safe sentence about the lateness, or null when it was on time and there is nothing to say.
    ///
    /// It deliberately reports the lateness SEPARATELY from any memory reading the caller adds, and
    /// says that the reading was taken afterwards: a probe read at the end of a twenty-five second
    /// stall describes the machine now, not the machine throughout the stall, and presenting it as
    /// proof of conditions during the delay would be evidence about the wrong moment.
    /// </summary>
    public static string? Describe(TimeSpan lateness, TimeSpan interval, TimeSpan stalenessWindow)
    {
        if (lateness <= TimeSpan.Zero) return null;

        var severity = MattersToTheReader(lateness, stalenessWindow)
            ? "THE GATEWAY'S CACHE WILL HAVE AGED OUT - actions on this Director's sessions were being refused"
            : "within the staleness window, so no action was refused";

        return $"this tick started {lateness.TotalSeconds:F1}s later than its {interval.TotalSeconds:F0}s cadence " +
               $"({severity}). The delay is BEFORE any work this callback did; any memory reading logged " +
               "beside it was taken after the delay and describes the machine now, not during it.";
    }
}
