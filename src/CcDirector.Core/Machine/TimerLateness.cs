namespace CcDirector.Core.Machine;

/// <summary>
/// HOW LATE A TIMER CALLBACK WAS TO START (issue #2818).
///
/// WHY THIS IS NEEDED AT ALL. Everything that timed the Director's roster push measured from the
/// moment the callback BEGAN RUNNING. That makes the one failure that matters invisible: a callback
/// can be queued for twenty-five seconds behind blocked session workers, start, find a connected
/// tunnel with no push in flight, complete its push in eighty milliseconds, and log a perfectly
/// healthy line - while the Gateway's pushed cache may already have aged past its staleness cut and
/// actions on that Director's sessions be refused. The lateness has to be measured from the schedule,
/// not from the callback.
///
/// IT MEASURES LATENESS AND REPORTS IT. IT DOES NOT RULE ON STALENESS, and that boundary took three
/// corrections to find. Lateness is a fact this side owns. Cache freshness is not: it lives on the
/// Gateway, is refreshed by routes this side does not count, and is stamped at a moment this side
/// does not observe. Two successive versions of this file inferred a freshness verdict anyway - first
/// from the lateness itself, then from the age of the last full snapshot - and each produced a
/// confident sentence about an outage that had not happened. <see cref="Describe"/> now states the
/// measurements and explicitly declines the conclusion; the reasoning is written there in full.
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
    /// A log-safe sentence about a late callback, or null when it was on time and there is nothing to
    /// say.
    ///
    /// IT REPORTS FACTS AND DRAWS NO CONCLUSION ABOUT WHETHER ANYTHING WAS REFUSED, and that restraint
    /// is the second correction this file has taken from review.
    ///
    /// The first version inferred staleness from lateness, which is a different quantity. The second
    /// inferred it from the age of the last FULL snapshot - better, but still wrong, because that is
    /// not the Gateway's freshness clock. <c>PushedSessionStore.ApplyDelta</c> and <c>ApplyRemove</c>
    /// stamp <c>ReceivedAtUtc</c> exactly as <c>ApplySnapshot</c> does, so a delta accepted between two
    /// full pushes keeps the cache fresh while this side's full-snapshot clock keeps running. A full
    /// snapshot at zero, a delta accepted at twenty-two seconds and a late tick at twenty-five would
    /// have announced that actions were being refused about a cache three seconds old.
    ///
    /// It is not a bound in EITHER direction, either, and calling it an upper bound was the third
    /// correction this file took. This side stamps its clock after <c>InvokeAsync</c> RETURNS, which is
    /// later than the moment the Gateway stamped <c>ReceivedAtUtc</c>, so for that one snapshot the
    /// elapsed time here UNDERSTATES its age; accepted deltas push the same figure the other way by
    /// refreshing a clock this side never sees. The two errors have opposite signs and neither is
    /// bounded, so the number is reported as exactly what it is - time since this Director's last
    /// full-snapshot acknowledgement - and nothing is concluded from it. A reader who needs the real
    /// answer has the Gateway's own logs.
    ///
    /// It also states that any memory reading logged beside it was taken AFTER the delay: a probe read
    /// at the end of a twenty-five second stall describes the machine now, not throughout the stall.
    /// </summary>
    /// <summary>
    /// Below this, a tick is on time. A timer callback is always a few milliseconds behind its
    /// schedule - that is how timers work, not a stall - and the line below prints the lateness to a
    /// tenth of a second, so anything under that printed as "0.0s later" and said nothing. On one
    /// Director that was 3,016 such lines in a day, against 10 that showed any lateness at all, the
    /// worst of them half a second. A real stall - the twenty-five second kind that takes a Director's
    /// sessions off the air - is seconds, not milliseconds.
    /// </summary>
    public static readonly TimeSpan WorthReporting = TimeSpan.FromSeconds(1);

    public static string? Describe(
        TimeSpan lateness, TimeSpan interval, TimeSpan stalenessWindow, TimeSpan? sinceLastFullSnapshot)
    {
        if (lateness < WorthReporting) return null;

        var snapshotAge = sinceLastFullSnapshot is null
            ? "no full snapshot has been pushed yet"
            : $"the last FULL snapshot was accepted {sinceLastFullSnapshot.Value.TotalSeconds:F1}s ago " +
              $"(freshness window {stalenessWindow.TotalSeconds:F0}s)";

        return $"this tick started {lateness.TotalSeconds:F1}s later than its {interval.TotalSeconds:F0}s cadence; " +
               $"{snapshotAge}. That elapsed time is measured from this Director's own acknowledgement and is " +
               "NOT the Gateway's cache age in either direction - accepted deltas refresh that cache without " +
               "being counted here, and this clock starts after the push was acknowledged rather than when the " +
               "Gateway stamped it - so cache freshness, and whether any action was refused, CANNOT be inferred " +
               "from this line; the Gateway's own logs have that answer. The delay is BEFORE any work this " +
               "callback did; any memory reading logged beside it was taken after the delay and describes the " +
               "machine now, not during it.";
    }
}
