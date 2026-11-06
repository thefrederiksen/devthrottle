using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// Where a <see cref="StallReading"/> came from. The platforms measure stalls in different ways, and the
/// diagnosis must know which figures it can trust as direct and which are proxies.
/// </summary>
public enum StallSource
{
    /// <summary>No stall reading was taken.</summary>
    None = 0,

    /// <summary>Linux pressure stall information, <c>/proc/pressure/{cpu,memory,io}</c> - a direct measure.</summary>
    LinuxPressureStall = 1,

    /// <summary>
    /// Windows performance counters - processor queue length and hard page faults per second. Proxies.
    /// The per-disk timings the Windows diagnosis also reads are NOT here: they live on
    /// <see cref="DiskEvidence"/>, because they are per disk and this reading is whole-machine.
    /// </summary>
    WindowsCounters = 2,

    /// <summary>
    /// macOS kernel memory-pressure level and the one-minute load average. Proxies. As with Windows, the per-disk
    /// figures live on <see cref="DiskEvidence"/> rather than here.
    /// </summary>
    MacOsSignals = 3,
}

/// <summary>
/// How much programs were frozen waiting on each resource - the reading at the heart of "why is it slow".
///
/// A busy figure says a resource is in use; a stall figure says programs are WAITING for it, which is
/// what the owner feels as slowness. On devlinux on 2026-10-15 the processor and memory looked healthy,
/// yet programs were stalled on the disk 32% of the time; only a stall reading shows that.
///
/// Every figure is nullable because each platform offers a different subset: Linux gives the three
/// stalled percentages directly; Windows and macOS give the proxies. Null means "not offered on this
/// platform", never "failed" - a failed read is <see cref="CouldRead"/> false.
///
/// THE DISK STALL FIGURE IS WHY THIS SURVIVED THE NARROWING. The owner cut the disk meter on 2026-11-06
/// but kept the disk in the diagnosis, and mission section 5c-i rules what each platform may read and
/// what it may then CLAIM:
/// <list type="bullet">
/// <item>ON LINUX the stall figure decides. <see cref="IoStalledPercent"/> is measured directly, so a
/// Linux diagnosis may say "programs were stalled waiting on the disk 32 per cent of the time".</item>
/// <item>WHERE THE PLATFORM PUBLISHES NO STALL FIGURE - macOS has none for any resource, and Windows has
/// only performance-counter proxies - the diagnosis decides from each disk's LATENCY
/// (<see cref="DiskEvidence.MillisecondsPerRead"/> and
/// <see cref="DiskEvidence.MillisecondsPerWrite"/>) and says "the disk is answering slowly". It must
/// never be worded as though a stall percentage had been measured.</item>
/// <item>A BUSY PERCENTAGE NEVER DECIDES, on any platform. See
/// <see cref="DiskEvidence.BusyPercent"/>: it is evidence printed beside the verdict and nothing
/// more.</item>
/// <item>A PLATFORM THAT CANNOT TELL says <see cref="SlownessDiagnosis.CouldDiagnose"/> false with the
/// reason, which means NOT KNOWN and never "fine".</item>
/// </list>
/// Saying the stall figure were the only source would make a disk verdict impossible on macOS and
/// Windows, which is the devlinux failure of 2026-10-15 reproduced on two more platforms.
/// </summary>
/// <param name="CouldRead">False when no stall figure could be read; every figure is then null.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
/// <param name="Source">Which platform mechanism the figures came from. <see cref="StallSource.None"/> when unreadable.</param>
/// <param name="CpuStalledPercent">Share of the LAST TEN SECONDS some program was waiting for a processor, 0 to 100 (Linux "some avg10").</param>
/// <param name="MemoryStalledPercent">Share of the LAST TEN SECONDS some program was waiting on memory, 0 to 100 (Linux "some avg10").</param>
/// <param name="IoStalledPercent">Share of the LAST TEN SECONDS some program was waiting on the disk, 0 to 100 (Linux "some avg10").</param>
/// <param name="CpuStalledPercentOneMinute">
/// The same share over the LAST MINUTE, as the kernel itself averages it (Linux "some avg60"). It is a
/// different quantity from <paramref name="CpuStalledPercent"/>: averaging a few ten-second figures does
/// not give it, because they cover only the seconds they were read in. The slowness diagnosis judges a
/// minute, so this is the figure it reads where the kernel offers one.
/// </param>
/// <param name="MemoryStalledPercentOneMinute">The memory stall share over the last minute (Linux "some avg60").</param>
/// <param name="IoStalledPercentOneMinute">The disk stall share over the last minute (Linux "some avg60").</param>
/// <param name="RunQueueLength">
/// The INSTANTANEOUS number of threads ready to run at the moment of reading. On Linux that is the
/// kernel's runnable count from <c>/proc/loadavg</c>: the threads running on a processor now PLUS those
/// waiting for one, the reader itself included - so on a 32-core machine it can read 47 with only 15
/// actually waiting. The Windows probe will fill it from its processor queue length counter, once what
/// that counter counts (waiting only, or waiting plus running) has been measured. macOS leaves it null: it publishes no
/// instantaneous count. It is NOT interchangeable with <paramref name="LoadAverageOneMinute"/>, and
/// nothing may use one in place of the other. Read it against the logical core count: 47 on 32 cores is
/// not 47 on 4.
/// </param>
/// <param name="LoadAverageOneMinute">
/// The kernel's ONE-MINUTE LOAD AVERAGE: an exponentially decaying average of the runnable count (and,
/// on Linux, of tasks in an uninterruptible wait on the disk as well, which is how devlinux showed a load
/// of 19 to 23 while 80 per cent idle). Linux reads it from <c>/proc/loadavg</c>, macOS from
/// <c>vm.loadavg</c>. Windows leaves it null: it has no load average. It is NOT interchangeable with
/// <paramref name="RunQueueLength"/> - an average over a minute is a different quantity from a count at
/// one moment, and a threshold written for one reads the other wrongly. Read it against the logical
/// core count.
/// </param>
/// <param name="HardPageFaultsPerSecond">Memory pages fetched back from disk per second.</param>
/// <param name="MacOsMemoryPressureLevel">The macOS kernel's own memory-pressure level, as the kernel numbers it.</param>
/// <param name="ProcessesWaitingOnDisk">
/// Tasks blocked waiting on input and output right now. On Linux this is the kernel's own
/// <c>procs_blocked</c>, which counts every THREAD, not only each process's main thread.
/// </param>
public readonly record struct StallReading(
    bool CouldRead,
    string? UnreadableReason,
    StallSource Source,
    double? CpuStalledPercent,
    double? MemoryStalledPercent,
    double? IoStalledPercent,
    double? CpuStalledPercentOneMinute,
    double? MemoryStalledPercentOneMinute,
    double? IoStalledPercentOneMinute,
    double? RunQueueLength,
    double? LoadAverageOneMinute,
    double? HardPageFaultsPerSecond,
    int? MacOsMemoryPressureLevel,
    int? ProcessesWaitingOnDisk)
{
    /// <summary>
    /// The macOS kernel's level, read through the ONE mapping function rather than a second
    /// <c>switch</c>. <see cref="MacOsMemoryPressureLevel"/> is the kernel's raw number, which is a bit
    /// flag and not a ladder; this is what it means. Unknown on every other platform, because no level
    /// was offered - and Unknown is never a synonym for Normal.
    ///
    /// The macOS probe, its tests and the Gateway's display fold all read this. None of them maps the
    /// number itself: see <see cref="MacOsKernelPressure"/> for why that matters when a platform
    /// publishes only two stall signals in total.
    /// </summary>
    public KernelMemoryPressureLevel MacOsMemoryPressure => MacOsKernelPressure.Read(MacOsMemoryPressureLevel);

    /// <summary>
    /// A reading that succeeded. Pass only the figures the platform offers; the rest stay null.
    /// </summary>
    public static StallReading Read(
        StallSource source,
        double? cpuStalledPercent = null,
        double? memoryStalledPercent = null,
        double? ioStalledPercent = null,
        double? cpuStalledPercentOneMinute = null,
        double? memoryStalledPercentOneMinute = null,
        double? ioStalledPercentOneMinute = null,
        double? runQueueLength = null,
        double? loadAverageOneMinute = null,
        double? hardPageFaultsPerSecond = null,
        int? macOsMemoryPressureLevel = null,
        int? processesWaitingOnDisk = null)
    {
        if (source == StallSource.None)
            throw new ArgumentException("A stall reading that succeeded must name where it came from.", nameof(source));
        return new(CouldRead: true, UnreadableReason: null, source, cpuStalledPercent, memoryStalledPercent,
            ioStalledPercent, cpuStalledPercentOneMinute, memoryStalledPercentOneMinute, ioStalledPercentOneMinute,
            runQueueLength, loadAverageOneMinute, hardPageFaultsPerSecond, macOsMemoryPressureLevel,
            processesWaitingOnDisk);
    }

    /// <summary>A reading that failed, carrying why. Every figure is null.</summary>
    public static StallReading Unreadable(string reason) =>
        new(CouldRead: false, ResourceReadingGuard.Reason(reason), StallSource.None,
            null, null, null, null, null, null, null, null, null, null, null);

    /// <summary>Short, log-safe description listing only the figures that were offered. Prints no figure when the reading failed.</summary>
    public override string ToString()
    {
        if (!CouldRead) return $"stalls unreadable ({UnreadableReason})";

        var parts = new List<string>();
        if (CpuStalledPercent is { } cpu) parts.Add(string.Create(CultureInfo.InvariantCulture, $"processor {cpu:F1}%"));
        if (MemoryStalledPercent is { } memory) parts.Add(string.Create(CultureInfo.InvariantCulture, $"memory {memory:F1}%"));
        if (IoStalledPercent is { } io) parts.Add(string.Create(CultureInfo.InvariantCulture, $"disk {io:F1}%"));
        if (CpuStalledPercentOneMinute is { } cpuMinute) parts.Add(string.Create(CultureInfo.InvariantCulture, $"processor {cpuMinute:F1}% over a minute"));
        if (MemoryStalledPercentOneMinute is { } memoryMinute) parts.Add(string.Create(CultureInfo.InvariantCulture, $"memory {memoryMinute:F1}% over a minute"));
        if (IoStalledPercentOneMinute is { } ioMinute) parts.Add(string.Create(CultureInfo.InvariantCulture, $"disk {ioMinute:F1}% over a minute"));
        if (RunQueueLength is { } queue) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{queue:F0} threads runnable now"));
        if (LoadAverageOneMinute is { } load) parts.Add(string.Create(CultureInfo.InvariantCulture, $"load average {load:F2} over one minute"));
        if (HardPageFaultsPerSecond is { } faults) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{faults:F0} hard page faults/s"));
        if (MacOsMemoryPressureLevel is { } level) parts.Add($"macOS memory pressure level {level}");
        if (ProcessesWaitingOnDisk is { } waiting) parts.Add($"{waiting} tasks waiting on disk");

        return parts.Count == 0
            ? $"stalls ({Source}): no figures offered"
            : $"stalls ({Source}): {string.Join(", ", parts)}";
    }
}
