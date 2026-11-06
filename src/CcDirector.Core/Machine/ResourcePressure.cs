using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE ONE PLACE A HISTORY OF RESOURCE SAMPLES BECOMES A LEVEL PER RESOURCE (mission sections 5a and
/// 5c): Normal, Tight or Critical for the processor, memory and swap - the meter's two bars and the swap
/// figure behind them. There is no disk level: the disk is not a meter, and reaches the owner only
/// through <see cref="SlownessDiagnoser"/>, in words.
///
/// Built the same way as <see cref="MemoryPressure"/>: a pure function of the samples it is handed - no
/// clock, no platform call, no state - so every branch is tested with no machine. Time is read only from
/// each sample's place on a MONOTONIC timeline (<see cref="TimelineSample.Timeline"/>), handed over by the
/// sampler that owns the clock. No window is ever measured by subtracting two
/// <see cref="ResourceSample.TakenAtUtc"/> stamps: a stepped wall clock would then pick the wrong samples and
/// change the verdict with no change in load (the Implementation Lead's ruling of 2026-11-06).
///
/// SUSTAINED MEANS EVERY SAMPLE IN THE WINDOW. The processor counts only when held over a limit for
/// <see cref="SustainedWindow"/>: every readable sample from the newest back to, and including, the
/// first one taken at least that long before the newest must be over it. One quiet sample inside the
/// window and the processor is not over the limit. A machine that has not yet recorded that much
/// history is Normal for the processor - a reading exists, and nothing has been held long enough to
/// count - and is Unknown only when the newest reading itself cannot be read.
///
/// UNKNOWN IS NEVER NORMAL. A resource whose newest reading cannot be read is Unknown, with the reason.
/// An unreadable sample further back inside the window is skipped rather than allowed to break or prove
/// the run, so one failed read does not reset a real warning; the readable samples must still reach
/// back the whole window.
///
/// MEMORY IS JUDGED ON THE NEWEST SAMPLE, by the existing tested <see cref="MemoryPressure"/> levels, and
/// is not held for sixty seconds. Mission section 5c gives memory "the existing MemoryPressure Tight and
/// Critical levels, unchanged", and those levels are already a judgement of how much is left rather than a
/// spike: available memory does not swing in and out the way a processor's busy share does. The sixty-second
/// rule of section 5c is applied to the processor, where a compile burst would otherwise warn.
///
/// THE BUSIEST SINGLE CORE NEVER RAISES A LEVEL. One stuck core of thirty-two is not a machine out of
/// processor; it is shown in the detail screen and nowhere else.
///
/// THE PROCESSOR LIMITS ARE PROPOSALS until the live load check has run them on all three platforms
/// (mission section 5c). Memory's are the existing tested ones and are not proposals.
/// </summary>
public static class ResourcePressure
{
    /// <summary>
    /// How long the processor must stay over a limit to count: 60 seconds (mission section 5c). Long
    /// enough that a compile burst does not warn; short enough that a machine pinned for a minute is
    /// shown as pinned within the 70 seconds the mission promises.
    /// </summary>
    public static readonly TimeSpan SustainedWindow = TimeSpan.FromSeconds(60);

    /// <summary>Above this whole-machine processor busy percent, held for 60 seconds, the processor is Tight (amber). A proposal.</summary>
    public const double CpuTightPercent = 85.0;

    /// <summary>Above this whole-machine processor busy percent, held for 60 seconds, the processor is Critical (red). A proposal.</summary>
    public const double CpuCriticalPercent = 95.0;

    /// <summary>
    /// How far back swap growth is measured: five minutes (mission section 5c). Swap that climbs fast is
    /// a machine starting to page, which is felt before the swap file is anywhere near full.
    /// </summary>
    public static readonly TimeSpan SwapGrowthWindow = TimeSpan.FromMinutes(5);

    /// <summary>Swap that grew by more than this inside <see cref="SwapGrowthWindow"/> is Tight (amber): 1 GB.</summary>
    public const ulong SwapGrowthTightBytes = 1024UL * 1024 * 1024;

    /// <summary>Swap more than this share used is Critical (red): 75 percent.</summary>
    public const double SwapUsedCriticalPercent = 75.0;

    private const double BytesPerGigabyte = 1024.0 * 1024 * 1024;

    /// <summary>
    /// Fold a history of samples into one verdict per resource: processor, memory and swap, always in
    /// that order. An empty history gives Unknown for every resource - nothing has been read. The history
    /// is put in timeline order before it is judged, so a caller that hands samples out of order still gets
    /// the verdict the timeline describes.
    /// </summary>
    public static ResourcePressureVerdict Evaluate(IReadOnlyList<TimelineSample> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count == 0)
        {
            const string nothing = "no sample has been taken yet";
            return new ResourcePressureVerdict(
            [
                new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Unknown, nothing),
                new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Unknown, nothing),
                new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Unknown, nothing),
            ]);
        }

        // OrderBy is a stable sort, so samples sharing a time keep the order the caller gave them.
        var ordered = history.OrderBy(sample => sample.Timeline).ToList();
        return new ResourcePressureVerdict([Cpu(ordered), Memory(ordered[^1].Sample), Swap(ordered)]);
    }

    /// <summary>Map the existing memory level onto the shared ladder, unchanged.</summary>
    public static ResourceLevel FromMemoryLevel(MemoryPressureLevel level) => level switch
    {
        MemoryPressureLevel.Normal => ResourceLevel.Normal,
        MemoryPressureLevel.Tight => ResourceLevel.Tight,
        MemoryPressureLevel.Critical => ResourceLevel.Critical,
        _ => ResourceLevel.Unknown,
    };

    // ---------------------------------------------------------------------------------------------
    // Processor: a limit held for the whole sustained window
    // ---------------------------------------------------------------------------------------------

    private static ResourceVerdict Cpu(List<TimelineSample> ordered)
    {
        var newestPoint = ordered[^1];
        var newest = newestPoint.Sample;
        if (!newest.Cpu.CouldRead)
            return new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Unknown,
                $"the processor cannot be read: {newest.Cpu.UnreadableReason}");

        var newestPercent = newest.Cpu.TotalBusyPercent;
        var windowStart = newestPoint.Timeline - SustainedWindow;

        // The level held through the window is set by its LOWEST readable sample: one sample under a
        // limit holds the whole window under it. This is a minimum over real readings, not a clamp.
        var lowest = newestPercent;
        var reachedBack = false;
        var oldestCounted = newestPoint.Timeline;
        for (var index = ordered.Count - 1; index >= 0; index--)
        {
            var point = ordered[index];
            var sample = point.Sample;
            if (!sample.Cpu.CouldRead) continue;

            lowest = Math.Min(lowest, sample.Cpu.TotalBusyPercent);
            oldestCounted = point.Timeline;
            if (point.Timeline <= windowStart)
            {
                reachedBack = true;
                break;
            }
        }

        var now = Percent(newestPercent);
        if (!reachedBack)
            return new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Normal,
                $"the processor is {now} busy; only {Seconds(newestPoint.Timeline - oldestCounted)} of readings so far, a limit counts after {Seconds(SustainedWindow)}");

        var span = Seconds(newestPoint.Timeline - oldestCounted);
        if (lowest > CpuCriticalPercent)
            return new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Critical,
                $"the processor is {now} busy and has been over {Percent(CpuCriticalPercent)} for {span}");
        if (lowest > CpuTightPercent)
            return new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Tight,
                $"the processor is {now} busy and has been over {Percent(CpuTightPercent)} for {span}");

        return new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Normal,
            $"the processor is {now} busy; it has not stayed over {Percent(CpuTightPercent)} for {Seconds(SustainedWindow)}");
    }

    // ---------------------------------------------------------------------------------------------
    // Memory: the existing, tested levels on the newest sample
    // ---------------------------------------------------------------------------------------------

    private static ResourceVerdict Memory(ResourceSample newest)
    {
        var memoryLevel = MemoryPressure.Level(newest.Memory);
        var level = FromMemoryLevel(memoryLevel);
        var evidence = level == ResourceLevel.Unknown
            ? newest.Memory.CouldRead
                ? $"the memory reading is not believable ({newest.Memory})"
                : $"memory cannot be read: {newest.Memory.UnreadableReason}"
            : $"{newest.Memory}; {MemoryPressure.Describe(memoryLevel)}";
        return new ResourceVerdict(ResourceKind.Memory, level, evidence);
    }

    // ---------------------------------------------------------------------------------------------
    // Swap: red on the share used now, amber on fast growth
    // ---------------------------------------------------------------------------------------------

    private static ResourceVerdict Swap(List<TimelineSample> ordered)
    {
        var newestPoint = ordered[^1];
        var newest = newestPoint.Sample;
        var swap = newest.Swap;
        if (!swap.CouldRead)
            return new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Unknown, $"swap cannot be read: {swap.UnreadableReason}");

        // Checked before "none configured": swap in use beyond its own size - including in use with no
        // swap configured at all - is a reading that cannot be true, not a machine with no swap.
        if (swap.UsedBytes > swap.TotalBytes)
            return new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Unknown, $"the swap reading is not believable ({swap})");

        if (swap.TotalBytes == 0)
            return new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal, "no swap is configured on this machine");

        var usedPercent = 100.0 * swap.UsedBytes / swap.TotalBytes;
        if (usedPercent > SwapUsedCriticalPercent)
            return new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Critical,
                $"{swap}, {Percent(usedPercent)} used, over the {Percent(SwapUsedCriticalPercent)} limit");

        // Growth is the newest figure against the lowest readable figure inside the window, so swap that
        // climbed and is still climbing is caught whichever sample it started from.
        var windowStart = newestPoint.Timeline - SwapGrowthWindow;
        var lowestUsed = swap.UsedBytes;
        foreach (var point in ordered)
        {
            var sample = point.Sample;
            if (point.Timeline < windowStart) continue;
            if (sample.Swap is { CouldRead: true } earlier && earlier.UsedBytes < lowestUsed) lowestUsed = earlier.UsedBytes;
        }

        var grewBy = swap.UsedBytes - lowestUsed;
        if (grewBy > SwapGrowthTightBytes)
            return new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Tight,
                string.Create(CultureInfo.InvariantCulture,
                    $"{swap}; it grew by {grewBy / BytesPerGigabyte:F1} GB in the last {SwapGrowthWindow.TotalMinutes:F0} minutes"));

        return new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal,
            $"{swap}, {Percent(usedPercent)} used and not growing fast");
    }

    private static string Percent(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:F0}%");

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds < 120
            ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:F0} seconds")
            : string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:F0} minutes");
}
