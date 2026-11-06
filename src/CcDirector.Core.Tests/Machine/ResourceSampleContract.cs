using CcDirector.Core.Machine;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE RULES EVERY <see cref="IResourceProbe"/> MUST KEEP, WRITTEN ONCE.
///
/// Three Developers write three probes - Linux, Windows and macOS - against one interface, and the
/// interface's doc comments are not enforcement. These are. The same checks run against each platform's
/// real sample, so a rule one probe keeps and another quietly breaks fails in the suite rather than in
/// the Cockpit six weeks later, where a per-core figure on one machine and a per-machine figure on
/// another would make every comparison between machines nonsense.
///
/// IT RETURNS VIOLATIONS RATHER THAN ASSERTING. A live probe sample can break several rules at once, and
/// a Developer debugging a new platform wants the whole list, not the first one xunit happened to reach.
/// It also lets the checks themselves be tested - see the unit tests, which feed this a sample that
/// reports per-core figures and require that it is REJECTED. A checker nobody has watched reject
/// anything is not known to work.
/// </summary>
public static class ResourceSampleContract
{
    /// <summary>
    /// Every rule <paramref name="sample"/> breaks, in plain English, or an empty list when it keeps all
    /// of them. The sample's own platform is not inspected: these are the rules that hold everywhere.
    /// </summary>
    public static IReadOnlyList<string> Violations(ResourceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var broken = new List<string>();

        CheckUnreadableCarriesItsReason(sample, broken);
        CheckProcessor(sample.Cpu, broken);
        CheckProcesses(sample, broken);
        CheckDiskEvidence(sample.DiskEvidence, broken);
        CheckStalls(sample.Stall, broken);

        return broken;
    }

    /// <summary>
    /// An unreadable part says why, and a readable one does not pretend to. This is the rule the whole
    /// interface is built on: a reading that cannot be taken says so, never a zero and never a guess.
    /// </summary>
    private static void CheckUnreadableCarriesItsReason(ResourceSample sample, List<string> broken)
    {
        Reason("the processor", sample.Cpu.CouldRead, sample.Cpu.UnreadableReason, broken);
        Reason("memory", sample.Memory.CouldRead, sample.Memory.UnreadableReason, broken);
        Reason("swap", sample.Swap.CouldRead, sample.Swap.UnreadableReason, broken);
        Reason("the stall figures", sample.Stall.CouldRead, sample.Stall.UnreadableReason, broken);
        Reason("the disk listing", sample.DiskEvidence.CouldRead, sample.DiskEvidence.UnreadableReason, broken);
        Reason("the process list", sample.Processes.CouldRead, sample.Processes.UnreadableReason, broken);

        foreach (var disk in sample.DiskEvidence.Disks)
            Reason($"disk {disk.Name}", disk.CouldRead, disk.UnreadableReason, broken);
    }

    private static void Reason(string what, bool couldRead, string? reason, List<string> broken)
    {
        if (!couldRead && string.IsNullOrWhiteSpace(reason))
            broken.Add($"{what} could not be read but gave no reason - the owner would see \"cannot read ()\".");
        if (couldRead && !string.IsNullOrWhiteSpace(reason))
            broken.Add($"{what} was read but still carries an unreadable reason (\"{reason}\"), so the two disagree.");
    }

    private static void CheckProcessor(CpuReading cpu, List<string> broken)
    {
        if (!cpu.CouldRead)
        {
            if (cpu.TotalBusyPercent != 0 || cpu.BusiestCorePercent != 0 || cpu.LogicalCoreCount != 0)
                broken.Add("the processor could not be read but carries figures, which a screen would show as real.");
            return;
        }

        Percentage("the processor's whole-machine busy figure", cpu.TotalBusyPercent, broken);
        Percentage("the busiest core's figure", cpu.BusiestCorePercent, broken);

        if (cpu.LogicalCoreCount <= 0)
            broken.Add($"the processor was read but reports {cpu.LogicalCoreCount} logical cores, which cannot be true.");

        if (cpu.BusiestCorePercent + 0.5 < cpu.TotalBusyPercent)
            broken.Add(FormattableString.Invariant(
                $"the busiest core ({cpu.BusiestCorePercent:F1}%) reads lower than the whole machine ({cpu.TotalBusyPercent:F1}%), so one of the two is measured against the wrong base."));
    }

    /// <summary>
    /// THE RULE THAT MATTERS MOST ACROSS PLATFORMS: a process's processor figure is a share of the WHOLE
    /// machine. A process holding one core of ten reads 10, not 100. If the platforms disagree, no
    /// per-session total can be summed and no two machines can be compared.
    ///
    /// On an idle machine this check cannot tell the two conventions apart - everything is near zero
    /// either way - which is why the live contract test puts a known load on one core and checks the
    /// figure against the core count. This part catches only the unmissable cases.
    /// </summary>
    private static void CheckProcesses(ResourceSample sample, List<string> broken)
    {
        var processes = sample.Processes;

        if (!processes.CouldRead)
        {
            if (processes.Processes.Count != 0)
                broken.Add("the process list could not be read but is not empty.");
            return;
        }

        if (processes.Processes.Count == 0)
        {
            broken.Add("the process list was read and is empty, which cannot be true - this process is in it.");
            return;
        }

        var seen = new HashSet<int>();
        double knownTotal = 0;

        foreach (var process in processes.Processes)
        {
            if (string.IsNullOrWhiteSpace(process.Name))
                broken.Add($"process {process.Pid} has no name, so a diagnosis could not name it as the culprit.");

            if (!seen.Add(process.Pid))
                broken.Add($"process {process.Pid} appears more than once, so any per-session total double-counts it.");

            if (process.CpuPercent is { } percent)
            {
                Percentage($"process {process.Name} ({process.Pid})", percent, broken);
                knownTotal += percent;
            }

            if (process.ParentPid == process.Pid)
                broken.Add($"process {process.Pid} is its own parent, so walking a session's tree would not end.");
        }

        // A share of the whole machine cannot sum past one machine. The allowance is generous because the
        // processes are not all sampled in the same instant; a per-core convention on any multi-core
        // machine under load overshoots this by multiples, not by a margin.
        if (sample.Cpu.CouldRead && sample.Cpu.LogicalCoreCount > 1 && knownTotal > 150)
            broken.Add(FormattableString.Invariant(
                $"the processes' processor figures sum to {knownTotal:F0}%, past one whole machine - they look measured per core rather than as a share of all {sample.Cpu.LogicalCoreCount} cores."));
    }

    private static void CheckDiskEvidence(DiskEvidenceSet disks, List<string> broken)
    {
        if (!disks.CouldRead && disks.Disks.Count != 0)
            broken.Add("the disks could not be listed but the list is not empty.");

        foreach (var disk in disks.Disks)
        {
            if (string.IsNullOrWhiteSpace(disk.Name))
                broken.Add("a disk has no name, so a diagnosis could not name it.");

            if (!disk.CouldRead)
            {
                if (disk.BusyPercent is not null || disk.MillisecondsPerRead is not null
                    || disk.MillisecondsPerWrite is not null || disk.AverageQueueDepth is not null)
                    broken.Add($"disk {disk.Name} could not be read but carries a figure.");
                continue;
            }

            if (disk.BusyPercent is { } busy) Percentage($"disk {disk.Name}", busy, broken);

            foreach (var (what, value) in new[]
            {
                ("milliseconds per read", disk.MillisecondsPerRead),
                ("milliseconds per write", disk.MillisecondsPerWrite),
                ("average queue depth", disk.AverageQueueDepth),
            })
            {
                if (value < 0)
                    broken.Add(FormattableString.Invariant(
                        $"disk {disk.Name} reports {value:F2} for {what}, which cannot be negative."));
            }
        }
    }

    private static void CheckStalls(StallReading stall, List<string> broken)
    {
        if (!stall.CouldRead)
        {
            if (stall.Source != StallSource.None)
                broken.Add($"the stall figures could not be read but name {stall.Source} as their source.");
            return;
        }

        if (stall.Source == StallSource.None)
            broken.Add("the stall figures were read but name no source, so nobody can tell a direct measure from a proxy.");

        foreach (var (what, value) in new[]
        {
            ("the processor stall figure", stall.CpuStalledPercent),
            ("the memory stall figure", stall.MemoryStalledPercent),
            ("the disk stall figure", stall.IoStalledPercent),
        })
        {
            if (value is { } percent) Percentage(what, percent, broken);
        }

        if (stall.RunQueueLength < 0)
            broken.Add(FormattableString.Invariant(
                $"the run queue reads {stall.RunQueueLength:F1}, which cannot be negative."));

        if (stall.HardPageFaultsPerSecond < 0)
            broken.Add(FormattableString.Invariant(
                $"hard page faults read {stall.HardPageFaultsPerSecond:F0} per second, which cannot be negative."));

        if (stall.ProcessesWaitingOnDisk < 0)
            broken.Add($"{stall.ProcessesWaitingOnDisk} processes are waiting on the disk, which cannot be negative.");

        // The kernel's number is a bit flag, so only the mapping may be read as a ladder. A level the
        // mapping does not know must not be reported as Normal.
        if (stall.MacOsMemoryPressureLevel is { } level
            && MacOsKernelPressure.Read(level) == KernelMemoryPressureLevel.Unknown
            && stall.MacOsMemoryPressure != KernelMemoryPressureLevel.Unknown)
            broken.Add($"the kernel memory-pressure number {level} is not one this version knows, "
                       + "but it is being reported as a known level.");
    }

    private static void Percentage(string what, double value, List<string> broken)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            broken.Add($"{what} is not a number.");
            return;
        }

        if (value < 0 || value > 100)
            broken.Add(FormattableString.Invariant($"{what} reads {value:F1}%, outside nought to one hundred."));
    }
}
