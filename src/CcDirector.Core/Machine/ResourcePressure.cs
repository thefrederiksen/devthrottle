using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE ONE PLACE A HISTORY OF RESOURCE SAMPLES BECOMES A VERDICT PER RESOURCE (mission section 5b).
///
/// Built the same way as <see cref="MemoryPressure"/>: a pure function of the samples it is handed - no
/// clock, no platform call, no state - so it is tested exhaustively in <c>CcDirector.Core.UnitTests</c>,
/// the assembly that forbids wall-clock dependence. Time is read only from each sample's
/// <see cref="ResourceSample.TakenAtUtc"/>, never from the machine.
///
/// SUSTAINED MEANS EVERY SAMPLE IN THE WINDOW. The processor and disk activity limits count only when
/// held for <see cref="SustainedWindow"/>: every sample from the newest back to, and including, the
/// first one taken at least that long before the newest must be over the limit. One quiet sample inside
/// the window and the resource is not over the limit. A machine that has not yet recorded that much
/// history is Normal for that resource - nothing has been held long enough to count - and only becomes
/// Unknown when the readings themselves cannot be read.
///
/// UNKNOWN IS NEVER NORMAL. A resource whose newest reading cannot be read is Unknown. An unreadable
/// sample further back inside the window is skipped rather than allowed to break or prove the run, so a
/// single failed read does not reset a real warning; the level is then decided by the readable samples,
/// which must still reach back the whole window.
///
/// A FIGURE THE PLATFORM DOES NOT OFFER IS NEVER ZERO. macOS gives no disk a busy percentage, so there
/// disk activity is judged by milliseconds per operation, against its own limits.
///
/// THE BUSIEST SINGLE CORE NEVER RAISES A LEVEL. One stuck core of thirty-two is not a machine out of
/// processor; it is shown in the detail screen and nowhere else.
/// </summary>
public static class ResourcePressure
{
    /// <summary>
    /// How long the processor or disk activity must stay over a limit to count: 60 seconds (mission
    /// section 5b). Long enough that a compile burst or a test run's start-up does not warn; short enough
    /// that a machine pinned for a minute is shown as pinned within the 70 seconds the mission promises.
    /// </summary>
    public static readonly TimeSpan SustainedWindow = TimeSpan.FromSeconds(60);

    /// <summary>Above this whole-machine processor busy percent, held for 60 seconds, the processor is Tight (amber).</summary>
    public const double CpuTightPercent = 85.0;

    /// <summary>Above this whole-machine processor busy percent, held for 60 seconds, the processor is Critical (red).</summary>
    public const double CpuCriticalPercent = 95.0;

    /// <summary>
    /// Above this disk activity percent, held for 60 seconds, disk activity is Tight (amber). Disk activity
    /// is the busiest physical disk's busy percent or the Linux disk stall percent, whichever is higher.
    /// </summary>
    public const double DiskActivityTightPercent = 80.0;

    /// <summary>Above this disk activity percent, held for 60 seconds, disk activity is Critical (red).</summary>
    public const double DiskActivityCriticalPercent = 95.0;

    /// <summary>
    /// WHERE NO DISK OFFERS A BUSY PERCENTAGE (macOS), disk activity is judged on how long one operation
    /// takes, and above this many milliseconds per operation, held for 60 seconds, it is Tight (amber): 10.
    ///
    /// A PROPOSAL THE QUALITY ASSURANCE RUN MUST CONFIRM. It is chosen from what was measured: on an M4
    /// Mac a light load wrote in 0.06 ms, one saturating writer in 1.1 ms and four writers in 19 ms, reads
    /// took 0.27 ms idle and 2.6 ms saturated; devlinux's struggling hard drive sdc took 26 ms per write.
    /// Ten milliseconds is well above anything a solid-state drive does when it is keeping up, and below
    /// the four-writer pile-up that a person at the keyboard notices.
    /// </summary>
    public const double DiskLatencyTightMilliseconds = 10.0;

    /// <summary>
    /// Where no disk offers a busy percentage: above this many milliseconds per operation, held for 60
    /// seconds, disk activity is Critical (red): 25. A proposal the quality assurance run must confirm;
    /// it sits beyond the M4's four-writer 19 ms and at devlinux's 26 ms, the disk that made the machine
    /// "super slow".
    /// </summary>
    public const double DiskLatencyCriticalMilliseconds = 25.0;

    /// <summary>
    /// The window swap growth is measured over: 5 minutes (mission section 5b). Swap growing fast is the
    /// late sign of a machine running out of memory, and it is the rate, not the size, that says so.
    /// </summary>
    public static readonly TimeSpan SwapGrowthWindow = TimeSpan.FromMinutes(5);

    /// <summary>Swap that grew by more than this inside <see cref="SwapGrowthWindow"/> is Tight (amber): 1 GB.</summary>
    public const ulong SwapGrowthTightBytes = 1024UL * 1024 * 1024;

    /// <summary>Swap more than this share used is Critical (red): 75 percent.</summary>
    public const double SwapUsedCriticalPercent = 75.0;

    /// <summary>A volume with less than this share free is Tight (amber): 15 percent.</summary>
    public const double DiskFreeTightPercent = 15.0;

    /// <summary>
    /// A volume with less than this much free is Tight (amber): 20 GB. The absolute floor catches a large
    /// volume whose percentage still looks comfortable; the percentage catches a small one.
    /// </summary>
    public const ulong DiskFreeTightBytes = 20UL * 1024 * 1024 * 1024;

    /// <summary>A volume with less than this share free is Critical (red): 5 percent.</summary>
    public const double DiskFreeCriticalPercent = 5.0;

    /// <summary>A volume with less than this much free is Critical (red): 5 GB - one build's output from full.</summary>
    public const ulong DiskFreeCriticalBytes = 5UL * 1024 * 1024 * 1024;

    private const double BytesPerGigabyte = 1024.0 * 1024 * 1024;

    /// <summary>
    /// Fold a history of samples, oldest first and newest last, into one verdict per resource:
    /// processor, memory, disk activity, swap and disk free, always in that order.
    ///
    /// An empty history gives Unknown for every resource - nothing has been read. The history is put in
    /// <see cref="ResourceSample.TakenAtUtc"/> order before it is judged, so a caller that hands samples
    /// out of order still gets the verdict the times describe.
    /// </summary>
    public static ResourcePressureVerdict Evaluate(IReadOnlyList<ResourceSample> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count == 0)
        {
            return new ResourcePressureVerdict(new[]
            {
                new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Unknown, "no sample has been taken yet"),
                new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Unknown, "no sample has been taken yet"),
                new ResourceVerdict(ResourceKind.DiskActivity, ResourceLevel.Unknown, "no sample has been taken yet"),
                new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Unknown, "no sample has been taken yet"),
                new ResourceVerdict(ResourceKind.DiskFree, ResourceLevel.Unknown, "no sample has been taken yet"),
            });
        }

        var ordered = InTimeOrder(history);

        return new ResourcePressureVerdict(new[]
        {
            Cpu(ordered),
            Memory(ordered[^1]),
            DiskActivity(ordered),
            Swap(ordered),
            DiskFree(ordered[^1]),
        });
    }

    /// <summary>
    /// The disk activity figure for one sample: the busiest readable physical disk's busy percent, or the
    /// Linux disk stall percent, whichever is higher. Null when neither is offered - on macOS, where no
    /// disk has a true busy figure, disk activity is judged by <see cref="SlowestDiskMilliseconds"/> instead.
    ///
    /// The stall percent counts only when it came from Linux pressure stall information, because only
    /// there is it a direct "share of time programs waited on the disk" on the same 0 to 100 scale as busy.
    /// </summary>
    public static double? DiskActivityPercent(ResourceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        double? busiest = BusiestDisk(sample)?.BusyPercent;
        double? stalled = sample.Stall is { CouldRead: true, Source: StallSource.LinuxPressureStall, IoStalledPercent: { } io }
            ? io
            : null;

        if (busiest is null) return stalled;
        if (stalled is null) return busiest;
        return Math.Max(busiest.Value, stalled.Value);
    }

    /// <summary>
    /// The busiest physical disk that offers a busy percentage, or null when none does. A disk whose busy
    /// figure is not offered (macOS) is never ranked as if it were idle.
    /// </summary>
    public static PhysicalDiskReading? BusiestDisk(ResourceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!sample.Disks.CouldRead) return null;

        PhysicalDiskReading? busiest = null;
        foreach (var disk in sample.Disks.Disks)
        {
            if (!disk.CouldRead || disk.BusyPercent is not { } busy) continue;
            if (busiest is null || busy > busiest.Value.BusyPercent) busiest = disk;
        }
        return busiest;
    }

    /// <summary>
    /// How long one operation took on the slowest disk, in milliseconds: the slower of its read and write
    /// times, over the readable disks that did any work in the interval. Null when no disk offers a time.
    ///
    /// A disk that moved no bytes is left out: its time per operation describes nothing it did, and an
    /// idle disk must not be named slow off a figure with no operations behind it.
    /// </summary>
    public static double? SlowestDiskMilliseconds(ResourceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!sample.Disks.CouldRead) return null;

        double? slowest = null;
        foreach (var disk in sample.Disks.Disks)
        {
            if (!disk.CouldRead || disk.ReadBytesPerSecond + disk.WriteBytesPerSecond <= 0) continue;
            if (MillisecondsPerOperation(disk) is not { } milliseconds) continue;
            if (slowest is null || milliseconds > slowest) slowest = milliseconds;
        }
        return slowest;
    }

    /// <summary>The slower of a disk's read and write times, or null when it offers neither.</summary>
    public static double? MillisecondsPerOperation(PhysicalDiskReading disk) =>
        disk.MillisecondsPerRead is { } read
            ? disk.MillisecondsPerWrite is { } write ? Math.Max(read, write) : read
            : disk.MillisecondsPerWrite;

    /// <summary>Map the existing memory level onto the shared ladder, unchanged.</summary>
    public static ResourceLevel FromMemoryLevel(MemoryPressureLevel level) => level switch
    {
        MemoryPressureLevel.Normal => ResourceLevel.Normal,
        MemoryPressureLevel.Tight => ResourceLevel.Tight,
        MemoryPressureLevel.Critical => ResourceLevel.Critical,
        _ => ResourceLevel.Unknown,
    };

    internal static List<ResourceSample> InTimeOrder(IReadOnlyList<ResourceSample> history)
    {
        // OrderBy is a stable sort, so samples sharing a time keep the order the caller gave them.
        return history.OrderBy(sample => sample.TakenAtUtc).ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // Processor and disk activity: a limit held for the whole sustained window
    // ---------------------------------------------------------------------------------------------

    private static ResourceVerdict Cpu(List<ResourceSample> ordered)
    {
        var newest = ordered[^1];
        if (!newest.Cpu.CouldRead)
            return new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Unknown,
                $"the processor cannot be read: {newest.Cpu.UnreadableReason}");

        return Sustained(ResourceKind.Cpu, ordered,
            sample => sample.Cpu.CouldRead ? sample.Cpu.TotalBusyPercent : null,
            CpuTightPercent, CpuCriticalPercent, "the processor is", Percent, "busy");
    }

    private static ResourceVerdict DiskActivity(List<ResourceSample> ordered)
    {
        var newest = ordered[^1];
        if (DiskActivityPercent(newest) is not null)
            return Sustained(ResourceKind.DiskActivity, ordered, DiskActivityPercent,
                DiskActivityTightPercent, DiskActivityCriticalPercent, "the disk is", Percent, "busy");

        // No busy figure and no Linux stall figure - macOS. Judge how long one operation takes instead.
        // The mode is chosen from the newest sample and kept for the whole window, so a percentage and a
        // number of milliseconds are never compared with each other.
        if (SlowestDiskMilliseconds(newest) is not null)
            return Sustained(ResourceKind.DiskActivity, ordered, SlowestDiskMilliseconds,
                DiskLatencyTightMilliseconds, DiskLatencyCriticalMilliseconds, "the slowest disk is taking", Milliseconds, "per operation");

        return new ResourceVerdict(ResourceKind.DiskActivity, ResourceLevel.Unknown,
            $"disk activity cannot be read: {DiskUnreadableReason(newest)}");
    }

    /// <summary>
    /// The level a percentage has held for the whole window. The newest sample is known to be readable.
    /// The window is the readable samples from the newest back to the first taken at least
    /// <see cref="SustainedWindow"/> before it. The level is the lowest any of them reached, so one sample
    /// under a limit holds the whole window under it.
    /// </summary>
    private static ResourceVerdict Sustained(
        ResourceKind kind,
        List<ResourceSample> ordered,
        Func<ResourceSample, double?> percentOf,
        double tightPercent,
        double criticalPercent,
        string subject,
        Func<double, string> format,
        string unit)
    {
        var newest = ordered[^1];
        var newestPercent = percentOf(newest) ?? throw new InvalidOperationException("The newest sample must be readable here.");
        var windowStart = newest.TakenAtUtc - SustainedWindow;

        var lowest = newestPercent;
        var reachedBack = false;
        var oldestCounted = newest.TakenAtUtc;
        for (var index = ordered.Count - 1; index >= 0; index--)
        {
            var sample = ordered[index];
            if (percentOf(sample) is not { } percent) continue;

            lowest = Math.Min(lowest, percent);
            oldestCounted = sample.TakenAtUtc;
            if (sample.TakenAtUtc <= windowStart)
            {
                reachedBack = true;
                break;
            }
        }

        var now = format(newestPercent);
        if (!reachedBack)
        {
            var heldFor = newest.TakenAtUtc - oldestCounted;
            return new ResourceVerdict(kind, ResourceLevel.Normal,
                $"{subject} {now} {unit}; only {Seconds(heldFor)} of readings so far, a limit counts after {Seconds(SustainedWindow)}");
        }

        var span = Seconds(newest.TakenAtUtc - oldestCounted);
        if (lowest > criticalPercent)
            return new ResourceVerdict(kind, ResourceLevel.Critical,
                $"{subject} {now} {unit} and has been over {format(criticalPercent)} for {span}");
        if (lowest > tightPercent)
            return new ResourceVerdict(kind, ResourceLevel.Tight,
                $"{subject} {now} {unit} and has been over {format(tightPercent)} for {span}");

        return new ResourceVerdict(kind, ResourceLevel.Normal,
            $"{subject} {now} {unit}; it has not stayed over {format(tightPercent)} for {Seconds(SustainedWindow)}");
    }

    private static string DiskUnreadableReason(ResourceSample sample)
    {
        if (!sample.Disks.CouldRead) return sample.Disks.UnreadableReason ?? "the disks could not be listed";

        foreach (var disk in sample.Disks.Disks)
        {
            if (!disk.CouldRead) return $"disk {disk.Name}: {disk.UnreadableReason}";
        }
        return "no disk offers a busy figure or a time per operation, and no disk stall figure is offered";
    }

    // ---------------------------------------------------------------------------------------------
    // Memory: the existing, tested levels on the newest sample
    // ---------------------------------------------------------------------------------------------

    private static ResourceVerdict Memory(ResourceSample newest)
    {
        var level = FromMemoryLevel(MemoryPressure.Level(newest.Memory));
        var evidence = level == ResourceLevel.Unknown
            ? newest.Memory.CouldRead
                ? $"memory reading is not believable ({newest.Memory})"
                : $"memory cannot be read: {newest.Memory.UnreadableReason}"
            : $"{newest.Memory}; {MemoryPressure.Describe(MemoryPressure.Level(newest.Memory))}";
        return new ResourceVerdict(ResourceKind.Memory, level, evidence);
    }

    // ---------------------------------------------------------------------------------------------
    // Swap: red on the share used now, amber on fast growth
    // ---------------------------------------------------------------------------------------------

    private static ResourceVerdict Swap(List<ResourceSample> ordered)
    {
        var newest = ordered[^1];
        var swap = newest.Swap;
        if (!swap.CouldRead)
            return new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Unknown, $"swap cannot be read: {swap.UnreadableReason}");

        if (swap.TotalBytes == 0)
            return new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal, "no swap is configured on this machine");

        if (swap.UsedBytes > swap.TotalBytes)
            return new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Unknown, $"swap reading is not believable ({swap})");

        var usedPercent = 100.0 * swap.UsedBytes / swap.TotalBytes;
        if (usedPercent > SwapUsedCriticalPercent)
            return new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Critical,
                $"{swap}, {Percent(usedPercent)} used, over the {Percent(SwapUsedCriticalPercent)} limit");

        // Growth is the newest figure against the lowest readable figure inside the window, so swap that
        // climbed and is still climbing is caught whichever sample it started from.
        var windowStart = newest.TakenAtUtc - SwapGrowthWindow;
        var lowestUsed = swap.UsedBytes;
        foreach (var sample in ordered)
        {
            if (sample.TakenAtUtc < windowStart) continue;
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

    // ---------------------------------------------------------------------------------------------
    // Disk free: every readable volume on the newest sample, the worst wins
    // ---------------------------------------------------------------------------------------------

    private static ResourceVerdict DiskFree(ResourceSample newest)
    {
        if (newest.Volumes.Count == 0)
            return new ResourceVerdict(ResourceKind.DiskFree, ResourceLevel.Unknown, "no volume could be listed");

        var worstLevel = ResourceLevel.Unknown;
        VolumeReading? worstVolume = null;
        var unreadable = new List<string>();
        foreach (var volume in newest.Volumes)
        {
            var level = VolumeLevel(volume);
            if (level == ResourceLevel.Unknown)
            {
                unreadable.Add(volume.CouldRead ? $"{volume.MountPoint} (size reads as zero)" : $"{volume.MountPoint} ({volume.UnreadableReason})");
                continue;
            }

            if (worstVolume is null || level > worstLevel || (level == worstLevel && volume.FreeBytes < worstVolume.FreeBytes))
            {
                worstLevel = level;
                worstVolume = volume;
            }
        }

        if (worstVolume is null)
            return new ResourceVerdict(ResourceKind.DiskFree, ResourceLevel.Unknown,
                $"no volume's free space could be read: {string.Join("; ", unreadable)}");

        var freePercent = 100.0 * worstVolume.FreeBytes / worstVolume.TotalBytes;
        var evidence = string.Create(CultureInfo.InvariantCulture,
            $"{(worstLevel == ResourceLevel.Normal ? "the fullest volume is " : "")}{worstVolume.MountPoint} with {worstVolume.FreeBytes / BytesPerGigabyte:F1} GB free of {worstVolume.TotalBytes / BytesPerGigabyte:F1} GB ({freePercent:F0}%)");
        if (unreadable.Count > 0) evidence += $"; cannot read {string.Join("; ", unreadable)}";

        return new ResourceVerdict(ResourceKind.DiskFree, worstLevel, evidence);
    }

    /// <summary>
    /// One volume's level. The percentage OR the absolute floor raises it, whichever is worse: a small
    /// volume is caught by its percentage, a large one by its gigabytes. Unknown when its space cannot be
    /// read, or reads as a zero-sized volume, or reads as more free than its size.
    /// </summary>
    public static ResourceLevel VolumeLevel(VolumeReading volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (!volume.CouldRead || volume.TotalBytes == 0 || volume.FreeBytes > volume.TotalBytes) return ResourceLevel.Unknown;

        var freePercent = 100.0 * volume.FreeBytes / volume.TotalBytes;
        if (freePercent < DiskFreeCriticalPercent || volume.FreeBytes < DiskFreeCriticalBytes) return ResourceLevel.Critical;
        if (freePercent < DiskFreeTightPercent || volume.FreeBytes < DiskFreeTightBytes) return ResourceLevel.Tight;
        return ResourceLevel.Normal;
    }

    private static string Percent(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:F0}%");

    private static string Milliseconds(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:0.#} ms");

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds >= 120
            ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:F0} minutes")
            : string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:F0} seconds");
}
