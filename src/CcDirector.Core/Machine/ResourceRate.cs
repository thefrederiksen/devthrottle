namespace CcDirector.Core.Machine;

/// <summary>
/// THE ONE PLACE A RATE IS WORKED OUT FROM TWO COUNTER READINGS.
///
/// Every rate in a resource sample - processor busy, a disk's milliseconds per operation, a process's
/// share of the machine - is the difference between two cumulative counters divided by the time between
/// them. Three platforms would otherwise each write that division, and each would have to remember the
/// same two ways it goes wrong. It goes here once instead.
///
/// THE TWO FAILURES, AND WHY NEITHER MAY BE A ZERO:
/// <list type="bullet">
/// <item>A COUNTER THAT STEPS BACKWARDS yields null. A counter resets when a disk is swapped, a network
/// interface is reconfigured or a process id is reused, and it wraps when it overflows. The difference
/// is then meaningless, and reporting zero would show an idle machine that may be on fire.</item>
/// <item>AN INTERVAL THAT IS NOT POSITIVE yields null. There is nothing to divide by. This is the first
/// read after start-up - see <see cref="ResourceUnreadableReasons.FirstSample"/> - or two reads inside
/// one tick of the clock.</item>
/// </list>
///
/// THE INTERVAL MUST COME OFF A MONOTONIC CLOCK, never off wall-clock time. Use
/// <see cref="System.TimeProvider.GetTimestamp"/> and
/// <see cref="System.TimeProvider.GetElapsedTime(long, long)"/>, as
/// <see cref="MachineMemoryProbe"/> does. Time synchronisation steps the wall clock, and a step
/// backwards across a five-second sample would otherwise turn every rate on the machine negative or
/// enormous. Wall-clock time is for STAMPING a sample - <see cref="ResourceSample.TakenAtUtc"/> - and
/// never for measuring an interval.
/// </summary>
public static class ResourceRate
{
    /// <summary>
    /// The per-second rate at which a counter advanced, or null when it cannot be known.
    /// </summary>
    /// <param name="earlier">The counter's value at the start of the interval.</param>
    /// <param name="later">The counter's value at the end of the interval.</param>
    /// <param name="interval">The monotonic time between the two readings.</param>
    public static double? PerSecond(ulong earlier, ulong later, TimeSpan interval)
    {
        if (later < earlier) return null;
        if (interval <= TimeSpan.Zero) return null;
        return (later - earlier) / interval.TotalSeconds;
    }

    /// <summary>
    /// The share of the interval that a busy-time counter accounted for, 0 to 100, or null when it
    /// cannot be known. Both arguments are cumulative busy time in the same unit as
    /// <paramref name="interval"/>.
    ///
    /// THE RESULT IS CLAMPED AT 100 AND THAT IS NOT A GUESS. A counter sampled a few microseconds out of
    /// step with the clock can show marginally more busy time than elapsed time; the honest reading of
    /// "busier than the whole interval" is "flat out". It is never clamped UP from a negative, because a
    /// backwards counter yields null above.
    /// </summary>
    /// <param name="earlierBusy">Cumulative busy time at the start of the interval.</param>
    /// <param name="laterBusy">Cumulative busy time at the end of the interval.</param>
    /// <param name="interval">The monotonic time between the two readings.</param>
    public static double? BusyPercent(TimeSpan earlierBusy, TimeSpan laterBusy, TimeSpan interval)
    {
        if (laterBusy < earlierBusy) return null;
        if (interval <= TimeSpan.Zero) return null;
        var share = (laterBusy - earlierBusy).TotalSeconds / interval.TotalSeconds * 100;
        return Math.Min(share, 100);
    }
}
