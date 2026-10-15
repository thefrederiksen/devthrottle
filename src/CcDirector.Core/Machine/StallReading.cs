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

    /// <summary>Windows performance counters - processor queue, disk timings, hard page faults. Proxies.</summary>
    WindowsCounters = 2,

    /// <summary>macOS kernel memory-pressure level, run-queue load and disk statistics. Proxies.</summary>
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
/// </summary>
/// <param name="CouldRead">False when no stall figure could be read; every figure is then null.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
/// <param name="Source">Which platform mechanism the figures came from. <see cref="StallSource.None"/> when unreadable.</param>
/// <param name="CpuStalledPercent">Share of time some program was waiting for a processor, 0 to 100 (Linux "some avg10").</param>
/// <param name="MemoryStalledPercent">Share of time some program was waiting on memory, 0 to 100 (Linux "some avg10").</param>
/// <param name="IoStalledPercent">Share of time some program was waiting on the disk, 0 to 100 (Linux "some avg10").</param>
/// <param name="RunQueueLength">Programs ready to run and waiting for a processor.</param>
/// <param name="HardPageFaultsPerSecond">Memory pages fetched back from disk per second.</param>
/// <param name="MacOsMemoryPressureLevel">The macOS kernel's own memory-pressure level, as the kernel numbers it.</param>
/// <param name="ProcessesWaitingOnDisk">Processes currently blocked waiting on the disk.</param>
public readonly record struct StallReading(
    bool CouldRead,
    string? UnreadableReason,
    StallSource Source,
    double? CpuStalledPercent,
    double? MemoryStalledPercent,
    double? IoStalledPercent,
    double? RunQueueLength,
    double? HardPageFaultsPerSecond,
    int? MacOsMemoryPressureLevel,
    int? ProcessesWaitingOnDisk)
{
    /// <summary>
    /// A reading that succeeded. Pass only the figures the platform offers; the rest stay null.
    /// </summary>
    public static StallReading Read(
        StallSource source,
        double? cpuStalledPercent = null,
        double? memoryStalledPercent = null,
        double? ioStalledPercent = null,
        double? runQueueLength = null,
        double? hardPageFaultsPerSecond = null,
        int? macOsMemoryPressureLevel = null,
        int? processesWaitingOnDisk = null)
    {
        if (source == StallSource.None)
            throw new ArgumentException("A stall reading that succeeded must name where it came from.", nameof(source));
        return new(CouldRead: true, UnreadableReason: null, source, cpuStalledPercent, memoryStalledPercent,
            ioStalledPercent, runQueueLength, hardPageFaultsPerSecond, macOsMemoryPressureLevel, processesWaitingOnDisk);
    }

    /// <summary>A reading that failed, carrying why. Every figure is null.</summary>
    public static StallReading Unreadable(string reason) =>
        new(CouldRead: false, ResourceReadingGuard.Reason(reason), StallSource.None,
            null, null, null, null, null, null, null);

    /// <summary>Short, log-safe description listing only the figures that were offered. Prints no figure when the reading failed.</summary>
    public override string ToString()
    {
        if (!CouldRead) return $"stalls unreadable ({UnreadableReason})";

        var parts = new List<string>();
        if (CpuStalledPercent is { } cpu) parts.Add(string.Create(CultureInfo.InvariantCulture, $"processor {cpu:F1}%"));
        if (MemoryStalledPercent is { } memory) parts.Add(string.Create(CultureInfo.InvariantCulture, $"memory {memory:F1}%"));
        if (IoStalledPercent is { } io) parts.Add(string.Create(CultureInfo.InvariantCulture, $"disk {io:F1}%"));
        if (RunQueueLength is { } queue) parts.Add(string.Create(CultureInfo.InvariantCulture, $"run queue {queue:F1}"));
        if (HardPageFaultsPerSecond is { } faults) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{faults:F0} hard page faults/s"));
        if (MacOsMemoryPressureLevel is { } level) parts.Add($"macOS memory pressure level {level}");
        if (ProcessesWaitingOnDisk is { } waiting) parts.Add($"{waiting} processes waiting on disk");

        return parts.Count == 0
            ? $"stalls ({Source}): no figures offered"
            : $"stalls ({Source}): {string.Join(", ", parts)}";
    }
}
