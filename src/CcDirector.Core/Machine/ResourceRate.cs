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
/// PREFER A FORM THAT CANNOT OVERSHOOT AT ALL. Where a platform publishes the counters that make up the
/// WHOLE of an interval - Linux <c>/proc/stat</c> gives idle alongside every busy column, Windows gives
/// per-processor idle, kernel and user - work the share out as busy over (busy plus idle) from the
/// counters themselves, not against the clock. That form is a ratio of two figures taken from the same
/// source at the same moment, so it cannot read past 100 per cent and needs no allowance.
/// <see cref="BusyPercent"/> against a measured interval is for a counter that offers busy time ALONE,
/// such as a disk's input and output ticks, where there is nothing else to divide by.
///
/// WHAT THIS CANNOT ENFORCE, SAID OUT LOUD. These are functions a probe has to CHOOSE to call. A probe
/// that writes its own <c>Math.Clamp(share, 0, 100)</c>, or its own "counter went backwards, call it
/// zero", gets the made-up zero this interface exists to refuse - and no test downstream can see it,
/// because <c>ResourceSampleContract</c> judges the finished sample and a clamped-to-zero figure is
/// indistinguishable from a real zero. So this rule rests on REVIEW, not on the contract: a reviewer
/// reading a probe looks for a hand-rolled clamp or a hand-rolled division and sends it back here.
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
    /// How much of one resource's capacity a busy-time counter accounted for, 0 to 100, or null when it
    /// cannot be known. Both busy arguments are cumulative busy time in the same unit as
    /// <paramref name="interval"/>.
    ///
    /// A SMALL OVERSHOOT IS ROUNDED DOWN; A LARGE ONE IS NULL, AND THE DIFFERENCE MATTERS. A counter
    /// sampled a few microseconds out of step with the clock can show marginally more busy time than
    /// elapsed time, and the honest reading of "busier than the whole interval" is "flat out" - so up to
    /// <see cref="OvershootAllowed"/> per cent is taken as 100. Beyond that the figure is not a rounding
    /// artefact but a sign the arithmetic is wrong: the usual cause is a per-core counter divided by a
    /// whole-machine interval, which reads as several hundred per cent. Clamping THAT to 100 would hide
    /// the one defect the whole shared contract exists to catch, and would hide it everywhere at once,
    /// because a clamped figure passes every range check there is. It is null instead, on the same
    /// reasoning as a backwards counter: a figure that cannot be true is not reported as a figure.
    /// </summary>
    /// <param name="earlierBusy">Cumulative busy time at the start of the interval.</param>
    /// <param name="laterBusy">Cumulative busy time at the end of the interval.</param>
    /// <param name="interval">The monotonic time between the two readings.</param>
    public static double? BusyPercent(TimeSpan earlierBusy, TimeSpan laterBusy, TimeSpan interval)
    {
        if (laterBusy < earlierBusy) return null;
        if (interval <= TimeSpan.Zero) return null;

        var share = (laterBusy - earlierBusy).TotalSeconds / interval.TotalSeconds * 100;
        if (share > 100 + OvershootAllowed) return null;
        return Math.Min(share, 100);
    }

    /// <summary>
    /// How far past 100 per cent a busy figure may read before it is treated as wrong rather than as
    /// sampling skew.
    ///
    /// WHERE THE NUMBER COMES FROM, so nobody widens it on a hunch. The tightest real case is Windows,
    /// which accounts processor time in 15.6-millisecond ticks: one thread flat out across a ONE-second
    /// interval on a single-core machine can read 101.6 per cent, and across the five-second sampling
    /// interval this mission actually uses, 0.3 per cent. Two points covers the worst of those with room
    /// to spare, and is nowhere near enough to swallow a per-core figure, which overshoots by multiples.
    /// If a figure needs more than this, the arithmetic is wrong - widening the allowance would only hide
    /// it.
    /// </summary>
    public const double OvershootAllowed = 2;

    /// <summary>
    /// A process's or a resource's share OF THE WHOLE MACHINE, 0 to 100, or null when it cannot be known.
    ///
    /// THIS IS THE ONE PLACE THE DIVISION BY CORE COUNT HAPPENS, and that is the point. A process holding
    /// one core of ten must read 10, not 100 - the single rule the shared contract exists to enforce, and
    /// the one three platforms are most likely to answer differently. Written by hand in three probes it
    /// would be got wrong in at least one of them, and a per-core figure cannot be detected downstream
    /// once it is in the sample. So a probe divides nothing itself: it hands over the busy time it read,
    /// the interval it measured and the core count, and gets back a figure that means the same thing on
    /// every operating system.
    /// </summary>
    /// <param name="earlierBusy">The process's cumulative processor time at the start of the interval.</param>
    /// <param name="laterBusy">The process's cumulative processor time at the end of the interval.</param>
    /// <param name="interval">The monotonic time between the two readings.</param>
    /// <param name="logicalCoreCount">
    /// How many logical cores the machine has. Null, zero or negative yields null: without it the figure
    /// cannot be expressed as a share of the machine at all, and the per-core figure it would otherwise
    /// return is exactly the wrong answer.
    /// </param>
    public static double? ShareOfMachine(
        TimeSpan earlierBusy, TimeSpan laterBusy, TimeSpan interval, int? logicalCoreCount)
    {
        if (logicalCoreCount is null or <= 0) return null;
        if (laterBusy < earlierBusy) return null;
        if (interval <= TimeSpan.Zero) return null;

        var machineCapacity = interval.TotalSeconds * logicalCoreCount.Value;
        var share = (laterBusy - earlierBusy).TotalSeconds / machineCapacity * 100;
        if (share > 100 + OvershootAllowed) return null;
        return Math.Min(share, 100);
    }
}
