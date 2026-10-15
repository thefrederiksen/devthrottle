using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE ANSWER TO "WHY IS IT SLOW?" (mission sections 2 and 5): not slow, or slow because programs are
/// stalled waiting on the processor, memory, the disk, or a combination - with the figures, the culprit
/// and a plain next step.
///
/// A pure function of the samples it is handed, like <see cref="MemoryPressure"/>: no clock, no
/// platform call, no state. Time is read only from each sample's <see cref="ResourceSample.TakenAtUtc"/>.
///
/// IT JUDGES THE LAST MINUTE, AVERAGED. The figures are averaged over the samples taken in the
/// <see cref="Window"/> before the newest one. A diagnosis is an explanation, not a warning, so unlike
/// <see cref="ResourcePressure"/> it does not wait for a full minute of history before it speaks; the
/// Linux stall figures it prefers are already ten-second averages, and averaging a minute of them keeps
/// one burst from being named as the cause.
///
/// DIRECT FIGURES FIRST, PROXIES ONLY WHERE THERE ARE NONE. On Linux the kernel says how much of the
/// time programs waited on each resource (<see cref="StallSource.LinuxPressureStall"/>), and that is
/// used alone: the Linux load average counts programs waiting on the disk as well as the processor,
/// which is exactly how devlinux showed a load of 19 to 23 while 80% idle. Elsewhere the verdict is built
/// from proxies - the run queue against the core count, hard page faults, the macOS memory-pressure
/// level, and how busy and how slow each disk is.
///
/// "CANNOT TELL" IS NEVER "NOT SLOW". The machine is called not slow only when the processor, memory
/// and the disk could all be judged and none was stalled. When nothing could be read the diagnosis says
/// it cannot tell. When some resource is stalled it is named even if another could not be read, and the
/// unreadable one is listed in the evidence.
/// </summary>
public static class SlownessDiagnoser
{
    /// <summary>How far back from the newest sample the figures are averaged: one minute.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Linux: programs waiting for a processor more than this share of the time means the processor is
    /// the cause: 20 percent. A healthy busy machine sits in single figures; at one second in five some
    /// program is ready to run and cannot, which a person at the keyboard feels as lag.
    /// </summary>
    public const double CpuStalledSlowPercent = 20.0;

    /// <summary>
    /// Linux: programs waiting on memory more than this share of the time means memory is the cause:
    /// 10 percent. Lower than the processor and disk limits because a memory stall is the kernel
    /// reclaiming or reading pages back from disk, which healthy machines almost never do at all.
    /// </summary>
    public const double MemoryStalledSlowPercent = 10.0;

    /// <summary>
    /// Linux: programs waiting on the disk more than this share of the time means the disk is the cause:
    /// 20 percent. devlinux on 2026-10-15 was at about 32 percent and was "super slow"; an idle machine
    /// with normal builds stays in single figures.
    /// </summary>
    public const double IoStalledSlowPercent = 20.0;

    /// <summary>
    /// Windows: processor queue length per logical core above this means the processor is the cause: 2.
    /// The Windows queue counts only threads WAITING, not those running, and two waiting per core held
    /// over time is the long-standing Windows performance guidance for a processor bottleneck.
    /// </summary>
    public const double WindowsRunQueuePerCoreSlow = 2.0;

    /// <summary>
    /// macOS: run-queue load per logical core above this means the processor is the cause: 1. The macOS
    /// load counts running AND waiting programs, so one per core is every core taken with more queued.
    /// </summary>
    public const double MacOsRunQueuePerCoreSlow = 1.0;

    /// <summary>
    /// Any platform without direct stall figures: the whole machine's processor busy above this, averaged
    /// over the minute, means the processor is the cause: 90 percent. Kept below the 95 percent red limit
    /// of <see cref="ResourcePressure"/> because a diagnosis explains a slowness felt now, before the meter
    /// has held red for a minute.
    /// </summary>
    public const double CpuBusySlowPercent = 90.0;

    /// <summary>
    /// Windows: hard page faults per second above this means memory is the cause: 1,000. Each is a page
    /// read back from disk; a thousand a second is the long-standing Windows guidance for a machine that
    /// is paging rather than working.
    /// </summary>
    public const double HardPageFaultsPerSecondSlow = 1000.0;

    /// <summary>
    /// macOS: the kernel's memory-pressure level at or above this means memory is the cause: 2, which the
    /// kernel names "warn" (1 is normal, 4 is critical).
    /// </summary>
    public const int MacOsMemoryPressureLevelSlow = 2;

    /// <summary>
    /// Any platform without direct stall figures: a disk busier than this AND slower than
    /// <see cref="MillisecondsPerOperationSlow"/> per read or write means the disk is the cause: 60 percent.
    /// Busy alone is not enough, because a fast solid-state drive can be busy and still answer at once.
    /// </summary>
    public const double DiskBusySlowPercent = 60.0;

    /// <summary>
    /// A disk operation taking longer than this, on average, is slow: 20 milliseconds. A healthy hard
    /// drive answers in about ten and a solid-state drive in under one; above twenty, requests are
    /// queueing (devlinux's sdc was at 26). This is also the Windows guidance for disk seconds per transfer.
    /// </summary>
    public const double MillisecondsPerOperationSlow = 20.0;

    /// <summary>
    /// Any platform without direct stall figures: a disk busier than this means the disk is the cause
    /// whatever its timings, or when the platform offers no timings: 90 percent.
    /// </summary>
    public const double DiskBusySaturatedPercent = 90.0;

    private const double BytesPerGigabyte = 1024.0 * 1024 * 1024;

    /// <summary>
    /// Diagnose a history of samples, oldest first and newest last.
    /// </summary>
    /// <param name="history">The samples; put in time order before they are judged.</param>
    /// <param name="sessionIdByRootPid">
    /// Each running session's id, keyed by the process id of the process it started. Used to name the
    /// session the culprit process belongs to. Null when sessions are not known; no session is then named.
    /// </param>
    public static SlownessDiagnosis Diagnose(
        IReadOnlyList<ResourceSample> history,
        IReadOnlyDictionary<int, string>? sessionIdByRootPid)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0) return SlownessDiagnosis.CannotDiagnose("no sample has been taken yet");

        var ordered = ResourcePressure.InTimeOrder(history);
        var newest = ordered[^1];
        var window = ordered.Where(sample => sample.TakenAtUtc >= newest.TakenAtUtc - Window).ToList();
        var seconds = Seconds(newest.TakenAtUtc - window[0].TakenAtUtc);

        var linux = window.Any(sample => sample.Stall is { CouldRead: true, Source: StallSource.LinuxPressureStall });

        var cpu = linux ? CpuFromStall(window, seconds) : CpuFromProxies(window, seconds);
        var memory = linux ? MemoryFromStall(window, seconds) : MemoryFromProxies(window, seconds);
        var disk = linux ? DiskFromStall(window, seconds) : DiskFromProxies(window, seconds);
        var busiestDisk = BusiestDiskOverWindow(window);

        var judgements = new[] { cpu, memory, disk };
        var cause = SlownessCause.None;
        if (cpu.Slow) cause |= SlownessCause.Cpu;
        if (memory.Slow) cause |= SlownessCause.Memory;
        if (disk.Slow) cause |= SlownessCause.Disk;

        var evidence = new List<string>();
        foreach (var judgement in judgements) evidence.AddRange(judgement.Evidence);
        if (busiestDisk is { } busy) evidence.Add(DiskLine(busy, newest));

        var unjudged = judgements.Where(judgement => !judgement.CouldJudge).Select(judgement => judgement.Name).ToList();

        if (cause == SlownessCause.None)
        {
            if (unjudged.Count == judgements.Length)
                return SlownessDiagnosis.CannotDiagnose(
                    $"the processor, memory and disk figures could not be read ({string.Join("; ", judgements.Select(judgement => judgement.UnreadableReason))})");

            if (unjudged.Count > 0)
                return new SlownessDiagnosis(
                    CouldDiagnose: false, IsSlow: false, SlownessCause.None, evidence, Culprit: null,
                    NextStep: "Check the Director log for why the machine could not be read.",
                    Summary: $"Cannot tell whether the machine is slow: {Join(unjudged)} could not be read, and nothing else is stalled");

            return new SlownessDiagnosis(
                CouldDiagnose: true, IsSlow: false, SlownessCause.None, evidence, Culprit: null,
                NextStep: "Nothing to do: programs are not waiting on the processor, memory or the disk.",
                Summary: "Not slow: programs are not waiting on the processor, memory or the disk");
        }

        if (unjudged.Count > 0) evidence.Add($"{Join(unjudged)} could not be read, so it is not ruled out");

        var culprit = Culprit(cause, window, busiestDisk, sessionIdByRootPid);
        return new SlownessDiagnosis(
            CouldDiagnose: true, IsSlow: true, cause, evidence, culprit,
            NextStep(cause, culprit),
            Summary(cause, cpu, memory, busiestDisk, culprit));
    }

    // ---------------------------------------------------------------------------------------------
    // One judgement per resource
    // ---------------------------------------------------------------------------------------------

    /// <summary>What was decided about one resource, and the figures behind it.</summary>
    private sealed record Judgement(string Name, bool CouldJudge, bool Slow, IReadOnlyList<string> Evidence, string? UnreadableReason, string? Headline)
    {
        public static Judgement Unreadable(string name, string reason) => new(name, false, false, Array.Empty<string>(), reason, null);
    }

    private static Judgement CpuFromStall(List<ResourceSample> window, string seconds)
    {
        if (Average(window, sample => LinuxStall(sample)?.CpuStalledPercent) is not { } stalled)
            return Judgement.Unreadable("the processor", "no processor stall figure");

        var line = $"programs waited for a processor {Percent1(stalled)} of the time (Linux pressure figures, averaged over {seconds})";
        var evidence = new List<string> { line };
        if (Average(window, sample => sample.Cpu.CouldRead ? sample.Cpu.TotalBusyPercent : null) is { } busy)
            evidence.Add($"the processor was {Percent(busy)} busy");
        var slow = stalled > CpuStalledSlowPercent;
        return new Judgement("the processor", true, slow, evidence, null,
            $"programs waited for a processor {Percent(stalled)} of the time");
    }

    private static Judgement MemoryFromStall(List<ResourceSample> window, string seconds)
    {
        if (Average(window, sample => LinuxStall(sample)?.MemoryStalledPercent) is not { } stalled)
            return Judgement.Unreadable("memory", "no memory stall figure");

        var evidence = new List<string>
        {
            $"programs waited on memory {Percent1(stalled)} of the time (Linux pressure figures, averaged over {seconds})",
        };
        var newestMemory = window.Last().Memory;
        if (newestMemory.CouldRead) evidence.Add(newestMemory.ToString());
        return new Judgement("memory", true, stalled > MemoryStalledSlowPercent, evidence, null,
            $"programs waited on memory {Percent(stalled)} of the time");
    }

    private static Judgement DiskFromStall(List<ResourceSample> window, string seconds)
    {
        if (Average(window, sample => LinuxStall(sample)?.IoStalledPercent) is not { } stalled)
            return Judgement.Unreadable("the disk", "no disk stall figure");

        var evidence = new List<string>
        {
            $"programs waited on the disk {Percent1(stalled)} of the time (Linux pressure figures, averaged over {seconds})",
        };
        if (window.Last().Stall.ProcessesWaitingOnDisk is { } waiting)
            evidence.Add($"{waiting} processes were waiting on the disk");
        return new Judgement("the disk", true, stalled > IoStalledSlowPercent, evidence, null,
            $"programs waited on the disk {Percent(stalled)} of the time");
    }

    private static Judgement CpuFromProxies(List<ResourceSample> window, string seconds)
    {
        var busy = Average(window, sample => sample.Cpu.CouldRead ? sample.Cpu.TotalBusyPercent : null);
        var cores = window.LastOrDefault(sample => sample.Cpu.CouldRead && sample.Cpu.LogicalCoreCount > 0)?.Cpu.LogicalCoreCount;
        var queue = Average(window, sample => sample.Stall.CouldRead ? sample.Stall.RunQueueLength : null);
        var source = window.LastOrDefault(sample => sample.Stall.CouldRead)?.Stall.Source ?? StallSource.None;

        if (busy is null && (queue is null || cores is null))
            return Judgement.Unreadable("the processor", ReasonOf(window, sample => sample.Cpu.CouldRead ? null : sample.Cpu.UnreadableReason, "no processor figure"));

        var evidence = new List<string>();
        var slow = false;
        string? headline = null;
        if (busy is { } busyPercent)
        {
            evidence.Add($"the processor was {Percent(busyPercent)} busy (averaged over {seconds})");
            if (busyPercent > CpuBusySlowPercent)
            {
                slow = true;
                headline = $"the processor is {Percent(busyPercent)} busy";
            }
        }

        if (queue is { } queueLength && cores is { } coreCount)
        {
            var perCore = queueLength / coreCount;
            var limit = source == StallSource.MacOsSignals ? MacOsRunQueuePerCoreSlow : WindowsRunQueuePerCoreSlow;
            evidence.Add(string.Create(CultureInfo.InvariantCulture,
                $"{queueLength:F1} programs were queued for {coreCount} cores ({perCore:F1} per core)"));
            if (perCore > limit)
            {
                slow = true;
                headline ??= string.Create(CultureInfo.InvariantCulture, $"{perCore:F1} programs are queued per core");
            }
        }

        return new Judgement("the processor", true, slow, evidence, null, headline);
    }

    private static Judgement MemoryFromProxies(List<ResourceSample> window, string seconds)
    {
        var newestMemory = window.Last().Memory;
        var level = MemoryPressure.Level(newestMemory);
        var faults = Average(window, sample => sample.Stall.CouldRead ? sample.Stall.HardPageFaultsPerSecond : null);
        var macLevel = window.LastOrDefault(sample => sample.Stall is { CouldRead: true, MacOsMemoryPressureLevel: not null })?.Stall.MacOsMemoryPressureLevel;

        if (level == MemoryPressureLevel.Unknown && faults is null && macLevel is null)
            return Judgement.Unreadable("memory", newestMemory.UnreadableReason ?? "no memory figure");

        var evidence = new List<string>();
        var slow = false;
        string? headline = null;

        if (level != MemoryPressureLevel.Unknown)
        {
            evidence.Add($"{newestMemory}; {MemoryPressure.Describe(level)}");
            if (level == MemoryPressureLevel.Critical)
            {
                slow = true;
                headline = string.Create(CultureInfo.InvariantCulture,
                    $"only {newestMemory.AvailableBytes / BytesPerGigabyte:F1} GB of memory is available");
            }
        }

        if (faults is { } faultsPerSecond)
        {
            evidence.Add(string.Create(CultureInfo.InvariantCulture,
                $"{faultsPerSecond:F0} memory pages per second were read back from disk (averaged over {seconds})"));
            if (faultsPerSecond > HardPageFaultsPerSecondSlow)
            {
                slow = true;
                headline ??= string.Create(CultureInfo.InvariantCulture, $"{faultsPerSecond:F0} memory pages a second are read back from disk");
            }
        }

        if (macLevel is { } kernelLevel)
        {
            evidence.Add($"the macOS memory-pressure level is {kernelLevel} ({MacOsLevelName(kernelLevel)})");
            if (kernelLevel >= MacOsMemoryPressureLevelSlow)
            {
                slow = true;
                headline ??= $"macOS reports memory pressure \"{MacOsLevelName(kernelLevel)}\"";
            }
        }

        return new Judgement("memory", true, slow, evidence, null, headline);
    }

    private static Judgement DiskFromProxies(List<ResourceSample> window, string seconds)
    {
        var names = window.SelectMany(sample => sample.Disks.CouldRead ? sample.Disks.Disks.Where(disk => disk.CouldRead).Select(disk => disk.Name) : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
            return Judgement.Unreadable("the disk", ReasonOf(window, sample => sample.Disks.CouldRead ? null : sample.Disks.UnreadableReason, "no physical disk could be read"));

        var slow = false;
        string? headline = null;
        var evidence = new List<string>();
        foreach (var name in names)
        {
            var busy = Average(window, sample => DiskNamed(sample, name)?.BusyPercent) ?? 0;
            var perOperation = SlowerOf(
                Average(window, sample => DiskNamed(sample, name)?.MillisecondsPerRead),
                Average(window, sample => DiskNamed(sample, name)?.MillisecondsPerWrite));

            if (busy > DiskBusySaturatedPercent || (busy > DiskBusySlowPercent && perOperation > MillisecondsPerOperationSlow))
            {
                slow = true;
                headline ??= $"disk {name} is {Percent(busy)} busy";
                evidence.Add(string.Create(CultureInfo.InvariantCulture,
                    $"disk {name} was {Percent(busy)} busy{(perOperation is { } ms ? $", {ms:F0} ms per operation" : "")} (averaged over {seconds})"));
            }
        }

        if (!slow) evidence.Add($"no disk was both busy and slow (averaged over {seconds})");
        return new Judgement("the disk", true, slow, evidence, null, headline);
    }

    // ---------------------------------------------------------------------------------------------
    // The culprit
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The disk that was busiest on average over the window, as its newest readable reading. Null when no
    /// disk could be read.
    /// </summary>
    private static PhysicalDiskReading? BusiestDiskOverWindow(List<ResourceSample> window)
    {
        PhysicalDiskReading? best = null;
        var bestBusy = double.MinValue;
        var names = window.SelectMany(sample => sample.Disks.CouldRead ? sample.Disks.Disks.Where(disk => disk.CouldRead).Select(disk => disk.Name) : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var busy = Average(window, sample => DiskNamed(sample, name)?.BusyPercent) ?? 0;
            if (busy <= bestBusy) continue;

            var latest = window.Select(sample => DiskNamed(sample, name)).Last(disk => disk is not null);
            if (latest is not { } reading) continue;

            // The averaged figures stand in for the latest ones, so the summary describes the minute.
            best = reading with
            {
                BusyPercent = busy,
                MillisecondsPerRead = Average(window, sample => DiskNamed(sample, name)?.MillisecondsPerRead),
                MillisecondsPerWrite = Average(window, sample => DiskNamed(sample, name)?.MillisecondsPerWrite),
            };
            bestBusy = busy;
        }
        return best;
    }

    private static SlownessCulprit? Culprit(
        SlownessCause cause,
        List<ResourceSample> window,
        PhysicalDiskReading? busiestDisk,
        IReadOnlyDictionary<int, string>? sessionIdByRootPid)
    {
        var processes = window.LastOrDefault(sample => sample.Processes.CouldRead)?.Processes;

        // Memory first: memory short with the disk busy is almost always swap, and the memory user is the
        // cause the disk is suffering. Then the disk, then the processor.
        ProcessReading? process = null;
        if (processes is not null)
        {
            if (cause.HasFlag(SlownessCause.Memory))
                process = processes.Processes.MaxBy(candidate => candidate.MemoryBytes);
            else if (cause.HasFlag(SlownessCause.Disk))
                process = processes.Processes
                    .Where(candidate => candidate.DiskReadBytesPerSecond is not null || candidate.DiskWriteBytesPerSecond is not null)
                    .MaxBy(candidate => (candidate.DiskReadBytesPerSecond ?? 0) + (candidate.DiskWriteBytesPerSecond ?? 0));
            else
                process = processes.Processes.MaxBy(candidate => candidate.CpuPercent);
        }

        string? sessionId = null;
        if (process is not null && processes is not null && sessionIdByRootPid is { Count: > 0 })
        {
            var rootPidBySessionId = new Dictionary<string, int>();
            foreach (var (rootPid, id) in sessionIdByRootPid) rootPidBySessionId.TryAdd(id, rootPid);
            sessionId = ProcessTreeUsage.BySession(processes, rootPidBySessionId)
                .FirstOrDefault(usage => usage.Pids.Contains(process.Pid))?.SessionId;
        }

        var disk = cause.HasFlag(SlownessCause.Disk) ? busiestDisk : null;
        if (process is null && disk is null) return null;

        return new SlownessCulprit(
            ProcessName: process?.Name,
            Pid: process?.Pid,
            SessionId: sessionId,
            SessionName: null,
            DiskName: disk?.Name,
            DiskKind: disk?.Kind);
    }

    // ---------------------------------------------------------------------------------------------
    // The words
    // ---------------------------------------------------------------------------------------------

    private static string Summary(SlownessCause cause, Judgement cpu, Judgement memory, PhysicalDiskReading? disk, SlownessCulprit? culprit)
    {
        var reasons = new List<string>();
        if (cause.HasFlag(SlownessCause.Cpu)) reasons.Add("the processor");
        if (cause.HasFlag(SlownessCause.Memory)) reasons.Add("memory");
        if (cause.HasFlag(SlownessCause.Disk)) reasons.Add("the disk");

        var details = new List<string>();
        if (cause.HasFlag(SlownessCause.Disk) && disk is { } busy) details.Add(DiskPhrase(busy));
        if (cause.HasFlag(SlownessCause.Memory) && memory.Headline is { } memoryHeadline) details.Add(memoryHeadline);
        if (cause.HasFlag(SlownessCause.Cpu) && cpu.Headline is { } cpuHeadline) details.Add(cpuHeadline);

        var text = $"Slow because of {Join(reasons)}";
        if (details.Count > 0) text += ": " + string.Join("; ", details);
        if (culprit?.ProcessName is { } name)
        {
            text += $"; mostly {name} (process {culprit.Pid})";
            if (culprit.SessionId is { } session) text += $" in session {session}";
        }
        return text;
    }

    private static string NextStep(SlownessCause cause, SlownessCulprit? culprit)
    {
        var who = culprit?.ProcessName is { } name
            ? culprit.SessionId is { } session ? $"{name} in session {session}" : name
            : null;

        if (cause.HasFlag(SlownessCause.Memory))
            return who is null
                ? "Close the sessions or programs using the most memory."
                : $"Close the sessions or programs using the most memory, starting with {who}.";

        if (cause.HasFlag(SlownessCause.Disk))
        {
            var start = culprit is { DiskKind: DiskKind.Spinning, DiskName: { } diskName }
                ? $"Move the busy work off the hard drive {diskName} onto a solid-state drive, or let the heavy disk work finish"
                : "Let the heavy disk work finish";
            return who is null ? $"{start}." : $"{start}; {who} is doing the most of it.";
        }

        return who is null
            ? "Pause or close the busiest work, or move sessions to a machine with room."
            : $"Pause or close the busiest work, starting with {who}, or move sessions to a machine with room.";
    }

    private static string DiskPhrase(PhysicalDiskReading disk)
    {
        var text = $"the {KindNoun(disk.Kind)} {disk.Name} is {Percent(disk.BusyPercent)} busy";
        if (disk.MillisecondsPerWrite is { } perWrite)
            text += string.Create(CultureInfo.InvariantCulture, $", {perWrite:F0} ms per write");
        return text;
    }

    private static string DiskLine(PhysicalDiskReading disk, ResourceSample newest)
    {
        var line = $"the busiest physical disk is the {KindNoun(disk.Kind)} {disk.Name}, {Percent(disk.BusyPercent)} busy";
        if (disk.MillisecondsPerWrite is { } perWrite)
            line += string.Create(CultureInfo.InvariantCulture, $", {perWrite:F0} ms per write");
        var volumes = newest.Volumes.Where(volume => volume.PhysicalDiskNames.Contains(disk.Name, StringComparer.Ordinal))
            .Select(volume => volume.MountPoint).ToList();
        if (volumes.Count > 0) line += $", part of the volume{(volumes.Count > 1 ? "s" : "")} {string.Join(", ", volumes)}";
        return line;
    }

    /// <summary>What to call a disk of this kind in a sentence for the owner.</summary>
    public static string KindNoun(DiskKind kind) => kind switch
    {
        DiskKind.Spinning => "hard drive",
        DiskKind.SolidState => "solid-state drive",
        _ => "disk",
    };

    private static string MacOsLevelName(int level) => level switch
    {
        1 => "normal",
        2 => "warn",
        4 => "critical",
        _ => "not a level the kernel documents",
    };

    // ---------------------------------------------------------------------------------------------
    // Arithmetic over the window
    // ---------------------------------------------------------------------------------------------

    private static StallReading? LinuxStall(ResourceSample sample) =>
        sample.Stall is { CouldRead: true, Source: StallSource.LinuxPressureStall } stall ? stall : null;

    private static PhysicalDiskReading? DiskNamed(ResourceSample sample, string name)
    {
        if (!sample.Disks.CouldRead) return null;
        foreach (var disk in sample.Disks.Disks)
        {
            if (disk.CouldRead && string.Equals(disk.Name, name, StringComparison.Ordinal)) return disk;
        }
        return null;
    }

    /// <summary>The average of the figures that were offered, or null when none was.</summary>
    private static double? Average(List<ResourceSample> window, Func<ResourceSample, double?> figureOf)
    {
        double sum = 0;
        var count = 0;
        foreach (var sample in window)
        {
            if (figureOf(sample) is not { } figure) continue;
            sum += figure;
            count++;
        }
        return count == 0 ? null : sum / count;
    }

    private static double? SlowerOf(double? first, double? second) =>
        first is null ? second : second is null ? first : Math.Max(first.Value, second.Value);

    private static string ReasonOf(List<ResourceSample> window, Func<ResourceSample, string?> reasonOf, string otherwise) =>
        window.Select(reasonOf).LastOrDefault(reason => reason is not null) ?? otherwise;

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}",
    };

    private static string Percent(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:F0}%");

    /// <summary>
    /// A stall share, with as many decimals as it needs to be read: "32%", "4.5%", "0.06%". A small
    /// stall must not print as "0%", which reads as "never" when it is "rarely".
    /// </summary>
    private static string Percent1(double value) => value switch
    {
        >= 10 => string.Create(CultureInfo.InvariantCulture, $"{value:F0}%"),
        >= 1 => string.Create(CultureInfo.InvariantCulture, $"{value:0.#}%"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value:0.##}%"),
    };

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds < 1
            ? "one reading"
            : string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:F0} seconds");
}
