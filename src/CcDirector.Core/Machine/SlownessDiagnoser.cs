using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE ANSWER TO "WHY IS IT SLOW?" (mission sections 2, 5a and 5i): not slow, or slow because programs
/// are waiting on the processor, memory, the disk, or a combination - with the figures, the culprit and a
/// plain next step.
///
/// A pure function of the samples it is handed: no clock, no platform call, no state. TIME COMES FROM THE
/// MONOTONIC TIMELINE EACH SAMPLE SITS ON (<see cref="TimelineSample.Timeline"/>), NEVER FROM SUBTRACTING
/// TWO <see cref="ResourceSample.TakenAtUtc"/> STAMPS - that stamp is for printing, and subtracting a pair
/// of them is the defect <see cref="ResourceSample"/> was reshaped to remove (issue #4686). Until that
/// issue this method ordered its samples, chose its minute and printed its span from those stamps, so a
/// routine time correction or a machine waking from sleep could reorder the readings and judge the wrong
/// minute with no change in load at all. It judges the last minute, averaged
/// (<see cref="Window"/>): a diagnosis is an explanation, not a warning, so it speaks at once rather than
/// waiting a full minute as <see cref="ResourcePressure"/> does, and averaging keeps one burst from being
/// named as the cause.
///
/// A STALL SHARE IS READ OVER THE WINDOW IT CLAIMS. Linux publishes each stall share twice: over the last
/// ten seconds and over the last minute. The diagnosis judges a minute, so it reads the kernel's own
/// one-minute figure where one is offered. Averaging a few ten-second figures is NOT a minute: the
/// recorded devlinux minute averages 13 per cent from its two ten-second readings while the kernel's
/// one-minute figure says 23. Where only ten-second figures exist, the sentence says so.
///
/// WHAT DECIDES, PER PLATFORM (mission section 5c-i). The platforms do not measure the same thing, so
/// they may not claim the same thing:
/// <list type="bullet">
/// <item>WHERE THE KERNEL MEASURES STALL DIRECTLY (<see cref="StallSource.LinuxPressureStall"/>) the stall
/// figures decide, and the diagnosis may say "programs were stalled waiting on the disk 13 per cent of the
/// time".</item>
/// <item>WHERE IT DOES NOT (macOS, Windows) the verdict is built from proxies, and says what it read: the
/// disk "is answering slowly", from milliseconds per operation. It NEVER claims a stall percentage it did
/// not measure.</item>
/// <item>A DISK'S BUSY PERCENTAGE NEVER DECIDES, anywhere. It is printed beside the verdict as evidence.
/// The disk to name is the one with the deepest queue - the operations programs are waiting behind.</item>
/// <item>"CANNOT TELL" IS NEVER "NOT SLOW". The machine is called not slow only when the processor, memory
/// and the disk could all be judged and none was stalled; see <see cref="SlownessDiagnosis.CouldDiagnose"/>.</item>
/// </list>
///
/// THE DEFECT TO HUNT IS THE BELIEVABLE NUMBER. No figure here is clamped into range; an unknown is never
/// read as zero - not in an average, not in a ranking; and no threshold reads a quantity it was not written
/// for: the run queue and the load average are different quantities with their own handling, each read
/// against the logical core count.
///
/// THE THRESHOLDS FOR PLATFORMS WITHOUT A STALL FIGURE ARE PROPOSALS. They rest on one Mac with one
/// solid-state disk and no spinning disk ever measured on a Mac; the live load check must confirm them,
/// and the quality assurance report records them as proposals.
/// </summary>
public static class SlownessDiagnoser
{
    /// <summary>How far back from the newest sample the figures are averaged: one minute.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Linux: some program waiting for a processor more than this share of the time means the processor
    /// is a cause: 20 per cent. A busy machine that keeps up sits in single figures; at one second in five
    /// a program is ready to run and cannot, which a person at the keyboard feels as lag.
    /// </summary>
    public const double CpuStalledSlowPercent = 20.0;

    /// <summary>
    /// Linux: some program waiting on memory more than this share of the time means memory is a cause:
    /// 10 per cent. A memory stall is the kernel reclaiming or reading pages back from disk, which a
    /// healthy machine almost never does.
    /// </summary>
    public const double MemoryStalledSlowPercent = 10.0;

    /// <summary>
    /// Linux: some program waiting on the disk more than this share of the last minute means the disk is a
    /// cause: 20 per cent. A healthy machine running builds stays in single figures; devlinux on 2026-10-15,
    /// unusable, read 23 per cent over the minute in the recording (the kernel's own "some avg60"), and 32
    /// per cent at another moment that day (mission section 2).
    /// </summary>
    public const double IoStalledSlowPercent = 20.0;

    /// <summary>
    /// Without a stall figure: the whole machine's processor busy above this, averaged over the window,
    /// means the processor is a cause: 90 per cent. Below the 95 per cent red limit of
    /// <see cref="ResourcePressure"/>, because a diagnosis explains a slowness felt now. A proposal.
    /// </summary>
    public const double CpuBusySlowPercent = 90.0;

    /// <summary>
    /// Without a stall figure: a one-minute load average above this PER LOGICAL CORE means the processor
    /// is a cause: 1.0. A load average counts running and waiting threads, so more than one per core is
    /// every core taken with more queued. Written for the load average only - never applied to
    /// <see cref="StallReading.RunQueueLength"/>, a different quantity. A proposal.
    /// </summary>
    public const double LoadAveragePerCoreSlow = 1.0;

    /// <summary>Windows: memory pages read back from disk per second above this means memory is a cause: 1,000. A proposal.</summary>
    public const double HardPageFaultsPerSecondSlow = 1000.0;

    /// <summary>
    /// Without a stall figure: a SOLID-STATE disk taking longer than this per operation, averaged over the
    /// window, is answering slowly: 10 milliseconds. One healthy writer on the Mac mini's solid-state disk
    /// took 1.1 milliseconds and four contended writers 15.8. A proposal.
    /// </summary>
    public const double SolidStateSlowMilliseconds = 10.0;

    /// <summary>
    /// Without a stall figure: a SPINNING disk taking longer than this per operation is answering slowly:
    /// 25 milliseconds. A healthy hard drive answers in about ten; devlinux's sdc was at 26 when the
    /// machine was unusable. Never measured on a Mac. A proposal.
    /// </summary>
    public const double SpinningSlowMilliseconds = 25.0;

    /// <summary>Without a stall figure: a disk of unknown kind taking longer than this is answering slowly: 20 milliseconds. A proposal.</summary>
    public const double UnknownKindSlowMilliseconds = 20.0;

    /// <summary>
    /// Diagnose a history of samples.
    /// </summary>
    /// <param name="history">
    /// The samples with their positions on a monotonic timeline, in any order; they are put in timeline
    /// order before they are judged. The caller owns the clock: live, the sampler's time since it started;
    /// replayed, the recording's own <c>recordingSeconds</c>. See <see cref="TimelineSample"/>.
    /// </param>
    /// <param name="membership">Which session each live process belongs to, to name the culprit's session. <see cref="SessionProcessMembership.Empty"/> names none.</param>
    /// <param name="sessionNameById">Each session's display name, for the sentence. Null when names are not known.</param>
    public static SlownessDiagnosis Diagnose(
        IReadOnlyList<TimelineSample> history,
        SessionProcessMembership membership,
        IReadOnlyDictionary<string, string>? sessionNameById = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(membership);
        if (history.Count == 0) return SlownessDiagnosis.CannotDiagnose("no sample has been taken yet");

        // ALL THREE OF THESE READ THE TIMELINE, NOT THE WALL CLOCK, AND THEY FAIL DIFFERENTLY, WHICH IS WHY
        // EACH IS PROVED BY ITS OWN TEST:
        //   ORDER decides which sample is the newest, and so which minute is judged at all. A backward step
        //     makes an older reading hold the largest stamp, and the newest reading is then not last.
        //   MEMBERSHIP decides which samples are averaged. A step pulls readings from outside the minute in,
        //     or pushes real ones out, and every figure in the verdict moves with no change in load.
        //   THE SPAN is only printed ("averaged over 60 seconds"); it is a string, never a divisor, so a
        //     wrong one mis-states the window in the evidence line without moving a single figure. It is
        //     the mildest of the three and it is still a sentence that is not true.
        // OrderBy is a stable sort, so samples sharing a position keep the order the caller gave them.
        var ordered = history.OrderBy(sample => sample.Timeline).ToList();
        var newest = ordered[^1];
        var inWindow = ordered.Where(sample => sample.Timeline >= newest.Timeline - Window).ToList();
        var span = Seconds(newest.Timeline - inWindow[0].Timeline);
        var window = inWindow.ConvertAll(sample => sample.Sample);

        var stallMeasured = window.Any(sample => sample.Stall is { CouldRead: true, Source: StallSource.LinuxPressureStall });
        var cores = window.LastOrDefault(sample => sample.Cpu.LogicalCoreCount is > 0)?.Cpu.LogicalCoreCount;
        var disk = DeepestQueueDisk(window);

        var cpu = stallMeasured ? CpuFromStall(window, span) : CpuFromProxies(window, span, cores);
        var memory = stallMeasured ? MemoryFromStall(window, span) : MemoryFromProxies(window, span);
        var diskJudgement = stallMeasured ? DiskFromStall(window, span, disk) : DiskFromLatency(window, span);
        var judgements = new[] { cpu, memory, diskJudgement };

        var cause = SlownessCause.None;
        if (cpu.Slow) cause |= SlownessCause.Cpu;
        if (memory.Slow) cause |= SlownessCause.Memory;
        if (diskJudgement.Slow) cause |= SlownessCause.Disk;

        var evidence = new List<string>
        {
            stallMeasured
                ? "Measured directly: the Linux kernel's own figures for how much of the time programs were stalled waiting."
                : "This platform publishes no figure for how long programs were stalled, so the verdict is judged from how busy the processor is, how memory stands and how long each disk takes to answer.",
        };
        foreach (var judgement in judgements) evidence.AddRange(judgement.Evidence);
        if (disk is { } namedDisk && !diskJudgement.Slow) evidence.Add($"the disk with the deepest queue is {DiskDescription(namedDisk.Disk)}");

        var unjudged = judgements.Where(judgement => !judgement.CouldJudge).Select(judgement => judgement.Name).ToList();
        var whyUnjudged = string.Join("; ", judgements.Where(judgement => !judgement.CouldJudge).Select(judgement => judgement.UnreadableReason));
        if (cause == SlownessCause.None)
        {
            if (unjudged.Count == judgements.Length)
                return SlownessDiagnosis.CannotDiagnose(
                    $"the processor, memory and disk figures could not be read ({string.Join("; ", judgements.Select(judgement => judgement.UnreadableReason))})");

            if (unjudged.Count > 0)
                return new SlownessDiagnosis(
                    CouldDiagnose: false, IsSlow: false, SlownessCause.None, evidence, Culprit: null,
                    NextStep: "Check the Director log for why the machine could not be read.",
                    Summary: $"Cannot tell whether the machine is slow: {Join(unjudged)} could not be read ({whyUnjudged}), and nothing else is stalled");

            return new SlownessDiagnosis(
                CouldDiagnose: true, IsSlow: false, SlownessCause.None, evidence, Culprit: null,
                NextStep: "Nothing to do: programs are not waiting on the processor, memory or the disk.",
                Summary: "Not slow: programs are not waiting on the processor, memory or the disk");
        }

        if (unjudged.Count > 0) evidence.Add($"{Join(unjudged)} could not be read ({whyUnjudged}), so it is not ruled out");

        var slowDisk = cause.HasFlag(SlownessCause.Disk) ? diskJudgement.Disk ?? disk?.Disk : null;
        var culprit = Culprit(cause, window, slowDisk, membership, sessionNameById);
        return new SlownessDiagnosis(
            CouldDiagnose: true, IsSlow: true, cause, evidence, culprit,
            NextStep(cause, culprit),
            Summary(cause, cpu, memory, diskJudgement, culprit));
    }

    // ---------------------------------------------------------------------------------------------
    // One judgement per resource
    // ---------------------------------------------------------------------------------------------

    /// <summary>What was decided about one resource, the figures behind it, and the phrase for the summary.</summary>
    private sealed record Judgement(
        string Name, bool CouldJudge, bool Slow, IReadOnlyList<string> Evidence, string? UnreadableReason, string? Headline,
        DiskEvidence? Disk = null)
    {
        public static Judgement Unreadable(string name, string reason) => new(name, false, false, [], reason, null);
    }

    private static Judgement CpuFromStall(List<ResourceSample> window, string span)
    {
        if (StallShare(window, stall => stall.CpuStalledPercentOneMinute, stall => stall.CpuStalledPercent, span) is not { } share)
            return Judgement.Unreadable("the processor", "no processor stall figure");

        var evidence = new List<string> { $"programs were stalled waiting for a processor {Percent1(share.Value)} {share.Of} ({share.Source})" };
        if (Average(window, sample => sample.Cpu.CouldRead ? sample.Cpu.TotalBusyPercent : null) is { } busy)
            evidence.Add($"the processor was {Percent(busy)} busy");
        return new Judgement("the processor", true, share.Value > CpuStalledSlowPercent, evidence, null,
            $"programs were stalled waiting for a processor {Percent1(share.Value)} {share.Of}");
    }

    private static Judgement MemoryFromStall(List<ResourceSample> window, string span)
    {
        if (StallShare(window, stall => stall.MemoryStalledPercentOneMinute, stall => stall.MemoryStalledPercent, span) is not { } share)
            return Judgement.Unreadable("memory", "no memory stall figure");

        var evidence = new List<string> { $"programs were stalled waiting on memory {Percent1(share.Value)} {share.Of} ({share.Source})" };
        if (window[^1].Memory is { CouldRead: true } memory) evidence.Add(memory.ToString());
        return new Judgement("memory", true, share.Value > MemoryStalledSlowPercent, evidence, null,
            $"programs were stalled waiting on memory {Percent1(share.Value)} {share.Of}");
    }

    private static Judgement DiskFromStall(List<ResourceSample> window, string span, (DiskEvidence Disk, double QueueDepth)? deepest)
    {
        if (StallShare(window, stall => stall.IoStalledPercentOneMinute, stall => stall.IoStalledPercent, span) is not { } share)
            return Judgement.Unreadable("the disk", "no disk stall figure");

        var evidence = new List<string> { $"programs were stalled waiting on the disk {Percent1(share.Value)} {share.Of} ({share.Source})" };
        if (window[^1].Stall.ProcessesWaitingOnDisk is { } waiting) evidence.Add($"{waiting} tasks were waiting on the disk");
        var slow = share.Value > IoStalledSlowPercent;
        if (slow && deepest is { } disk) evidence.Add($"the disk with the deepest queue is {DiskDescription(disk.Disk)}");
        return new Judgement("the disk", true, slow, evidence, null,
            $"programs were stalled waiting on the disk {Percent1(share.Value)} {share.Of}", deepest?.Disk);
    }

    private static Judgement CpuFromProxies(List<ResourceSample> window, string span, int? cores)
    {
        var busy = Average(window, sample => sample.Cpu.CouldRead ? sample.Cpu.TotalBusyPercent : null);
        // The one-minute load average is ALREADY a minute's average, so the newest one is read as it is;
        // averaging it again over the window would report a figure the kernel never gave, a minute late.
        var load = window.Select(sample => sample.Stall.CouldRead ? sample.Stall.LoadAverageOneMinute : null).LastOrDefault(value => value is not null);
        var queue = Average(window, sample => sample.Stall.CouldRead ? sample.Stall.RunQueueLength : null);

        if (busy is null && (load is null || cores is null))
            return Judgement.Unreadable("the processor",
                window.Select(sample => sample.Cpu.CouldRead ? null : sample.Cpu.UnreadableReason).LastOrDefault(reason => reason is not null)
                ?? "no processor figure");

        var evidence = new List<string>();
        var slow = false;
        string? headline = null;
        if (busy is { } busyPercent)
        {
            evidence.Add($"the processor was {Percent(busyPercent)} busy (averaged over {span})");
            if (busyPercent > CpuBusySlowPercent)
            {
                slow = true;
                headline = $"the processor is {Percent(busyPercent)} busy";
            }
        }

        if (load is { } loadAverage && cores is { } coreCount)
        {
            var perCore = loadAverage / coreCount;
            evidence.Add(string.Create(CultureInfo.InvariantCulture,
                $"the one-minute load average is {loadAverage:F2} on {coreCount} cores ({perCore:F2} per core)"));
            if (perCore > LoadAveragePerCoreSlow)
            {
                slow = true;
                headline ??= string.Create(CultureInfo.InvariantCulture, $"the load average is {perCore:F1} per core");
            }
        }

        // The instantaneous run queue is shown but decides nothing. What it counts IS now measured and
        // the same on both platforms that offer it (running plus waiting; see StallReading.RunQueueLength),
        // so the old reason for holding off has gone - but two better ones remain. It is one moment every
        // five seconds of a figure measured swinging between 0 and 92 on a real machine, so a windowed
        // average of a handful of spot readings is a weak estimate; and a saturated processor is already
        // caught by busy-by-time, which is measured over the interval rather than sampled. Whether it
        // should set a threshold on WINDOWS specifically - where there is no load average, so this is the
        // only sustained-queueing signal - needs the load harness to show how the windowed average behaves
        // under known loads first. Not guessed.
        if (queue is { } queueLength && cores is { } queueCores)
            evidence.Add(string.Create(CultureInfo.InvariantCulture,
                $"on average {queueLength:F0} threads were on a processor or waiting for one, on {queueCores} cores"));

        return new Judgement("the processor", true, slow, evidence, null, headline);
    }

    private static Judgement MemoryFromProxies(List<ResourceSample> window, string span)
    {
        var newestMemory = window[^1].Memory;
        var level = MemoryPressure.Level(newestMemory);
        var faults = Average(window, sample => sample.Stall.CouldRead ? sample.Stall.HardPageFaultsPerSecond : null);
        // The kernel's own level, through the ONE mapping (MacOsKernelPressure), never a second switch.
        var kernel = window.Select(sample => sample.Stall.MacOsMemoryPressure).LastOrDefault(found => found != KernelMemoryPressureLevel.Unknown);

        if (level == MemoryPressureLevel.Unknown && faults is null && kernel == KernelMemoryPressureLevel.Unknown)
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
                headline = $"memory is nearly full ({newestMemory})";
            }
        }

        if (faults is { } faultsPerSecond)
        {
            evidence.Add(string.Create(CultureInfo.InvariantCulture,
                $"{faultsPerSecond:F0} memory pages a second were read back from disk (averaged over {span})"));
            if (faultsPerSecond > HardPageFaultsPerSecondSlow)
            {
                slow = true;
                headline ??= string.Create(CultureInfo.InvariantCulture, $"{faultsPerSecond:F0} memory pages a second are read back from disk");
            }
        }

        if (kernel != KernelMemoryPressureLevel.Unknown)
        {
            evidence.Add($"the macOS kernel reports memory pressure {KernelLevelName(kernel)}");
            if (kernel is KernelMemoryPressureLevel.Warning or KernelMemoryPressureLevel.Critical)
            {
                slow = true;
                headline ??= $"the macOS kernel reports memory pressure {KernelLevelName(kernel)}";
            }
        }

        return new Judgement("memory", true, slow, evidence, null, headline);
    }

    /// <summary>
    /// Without a stall figure, the disk is judged by how long it takes to answer: the slowest disk's
    /// milliseconds per operation, averaged over the window, against a limit for its kind. A disk that
    /// did no work has no time per operation (null) and is not judged - never read as a fast disk.
    ///
    /// A KNOWN LIMITATION, RECORDED BESIDE THE PROPOSED LIMITS: the figure is a mean of each sample's own
    /// mean, every five-second sample counted equally however many operations it completed, because
    /// <see cref="DiskEvidence"/> carries no operation counts. One very slow operation in an otherwise
    /// quiet sample - a spinning drive waking from sleep - weighs as much as a busy sample of hundreds,
    /// and can push the minute over the limit while the disk is healthy. Weighting by operations needs
    /// the counts on the shared shape; that is the Implementation Lead's decision (pull request 4653).
    /// </summary>
    private static Judgement DiskFromLatency(List<ResourceSample> window, string span)
    {
        var names = DiskNames(window);
        if (names.Count == 0)
            return Judgement.Unreadable("the disk",
                window.Select(sample => sample.DiskEvidence.CouldRead ? null : sample.DiskEvidence.UnreadableReason).LastOrDefault(reason => reason is not null)
                ?? "no physical disk could be read");

        DiskEvidence? slowest = null;
        double slowestRatio = 0;
        var evidence = new List<string>();
        foreach (var name in names)
        {
            if (Averaged(window, name) is not { } disk || SlowerOperation(disk) is not { } milliseconds) continue;
            var limit = SlowLimit(disk.Kind);
            if (milliseconds <= limit) continue;

            evidence.Add($"the disk is answering slowly: {DiskDescription(disk)} (averaged over {span})");
            var ratio = milliseconds / limit;
            if (slowest is null || ratio > slowestRatio)
            {
                slowest = disk;
                slowestRatio = ratio;
            }
        }

        if (slowest is null)
        {
            var disks = names.Select(name => Averaged(window, name)).OfType<DiskEvidence>().ToList();

            // A disk with operations in flight that completed none in the whole window may have stopped
            // answering - the slowest disk there is. Checked PER DISK and first, so a healthy system disk
            // cannot hide an external drive that has hung: the verdict is "cannot tell", never "fine".
            var stuck = disks.FirstOrDefault(disk => SlowerOperation(disk) is null && disk.AverageQueueDepth > 0);
            if (stuck.Name is { } stuckName)
                return Judgement.Unreadable("the disk",
                    string.Create(CultureInfo.InvariantCulture,
                        $"{stuckName} had {stuck.AverageQueueDepth:F1} operations in flight and completed none in {span}, so how long it takes to answer cannot be told"));

            if (disks.Any(disk => SlowerOperation(disk) is not null))
            {
                evidence.Add($"every disk that completed an operation answered within its limit (averaged over {span})");
                return new Judgement("the disk", true, false, evidence, null, null);
            }

            // No disk completed an operation, and none had any waiting: an idle machine.
            evidence.Add($"no disk completed an operation in {span}, and none had any waiting");
            return new Judgement("the disk", true, false, evidence, null, null);
        }

        return new Judgement("the disk", true, true, evidence, null, $"the disk is answering slowly: {DiskDescription(slowest.Value)}", slowest);
    }

    // ---------------------------------------------------------------------------------------------
    // The disk to name
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The disk programs are queued behind: the deepest average queue over the window. Where no disk
    /// offers a queue depth, the slowest to answer. Null when no disk could be read or none did any work.
    /// Busy is never the ranking: a disk can be busy and still answer at once.
    /// </summary>
    private static (DiskEvidence Disk, double QueueDepth)? DeepestQueueDisk(List<ResourceSample> window)
    {
        (DiskEvidence Disk, double Rank)? best = null;
        var byQueue = DiskNames(window).Select(name => Averaged(window, name)).OfType<DiskEvidence>().ToList();
        var anyQueue = byQueue.Any(disk => disk.AverageQueueDepth is not null);
        foreach (var disk in byQueue)
        {
            double? rank = anyQueue ? disk.AverageQueueDepth : SlowerOperation(disk);
            if (rank is not { } value || value <= 0) continue;
            if (best is null || value > best.Value.Rank) best = (disk, value);
        }

        return best is { } found ? (found.Disk, found.Rank) : null;
    }

    private static List<string> DiskNames(List<ResourceSample> window) =>
        window.SelectMany(sample => sample.DiskEvidence.CouldRead
                ? sample.DiskEvidence.Disks.Where(disk => disk.CouldRead).Select(disk => disk.Name)
                : [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// One disk's readings averaged over the window: each figure averaged over the samples that offered
    /// it, and null where none did. Null when the disk was never readable in the window.
    /// </summary>
    private static DiskEvidence? Averaged(List<ResourceSample> window, string name)
    {
        var readings = window.Select(sample => DiskNamed(sample, name)).OfType<DiskEvidence>().ToList();
        if (readings.Count == 0) return null;
        var latest = readings[^1];
        return latest with
        {
            BusyPercent = Mean(readings.Select(disk => disk.BusyPercent)),
            MillisecondsPerRead = Mean(readings.Select(disk => disk.MillisecondsPerRead)),
            MillisecondsPerWrite = Mean(readings.Select(disk => disk.MillisecondsPerWrite)),
            AverageQueueDepth = Mean(readings.Select(disk => disk.AverageQueueDepth)),
        };
    }

    private static DiskEvidence? DiskNamed(ResourceSample sample, string name)
    {
        if (!sample.DiskEvidence.CouldRead) return null;
        foreach (var disk in sample.DiskEvidence.Disks)
        {
            if (disk.CouldRead && string.Equals(disk.Name, name, StringComparison.Ordinal)) return disk;
        }
        return null;
    }

    /// <summary>The slower of a disk's read and write times, or null when it offers neither (it did no work).</summary>
    private static double? SlowerOperation(DiskEvidence disk) => (disk.MillisecondsPerRead, disk.MillisecondsPerWrite) switch
    {
        ({ } read, { } write) => Math.Max(read, write),
        ({ } read, null) => read,
        (null, { } write) => write,
        _ => null,
    };

    private static double SlowLimit(DiskKind kind) => kind switch
    {
        DiskKind.SolidState => SolidStateSlowMilliseconds,
        DiskKind.Spinning => SpinningSlowMilliseconds,
        _ => UnknownKindSlowMilliseconds,
    };

    // ---------------------------------------------------------------------------------------------
    // The culprit
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Who to name. Memory first - memory short with the disk busy is almost always paging, and the memory
    /// user is the cause the disk is suffering - then the disk, then the processor. Only a process whose
    /// figure is KNOWN can be ranked: one whose figure is null is not treated as using nothing.
    ///
    /// For a disk cause the process is the one reading and writing the most bytes, which is WEAK evidence:
    /// many small random writes stall a spinning disk at a low byte rate. It supports "mostly X" in the
    /// sentence and decides nothing.
    /// </summary>
    private static SlownessCulprit? Culprit(
        SlownessCause cause,
        List<ResourceSample> window,
        DiskEvidence? disk,
        SessionProcessMembership membership,
        IReadOnlyDictionary<string, string>? sessionNameById)
    {
        var processes = window.LastOrDefault(sample => sample.Processes.CouldRead)?.Processes.Processes;
        ProcessReading? process = null;
        if (processes is not null)
        {
            if (cause.HasFlag(SlownessCause.Memory))
                process = processes.Where(candidate => candidate.MemoryBytes is not null).MaxBy(candidate => candidate.MemoryBytes);
            else if (cause.HasFlag(SlownessCause.Disk))
                process = processes
                    .Where(candidate => candidate.DiskReadBytesPerSecond is not null && candidate.DiskWriteBytesPerSecond is not null)
                    .Where(candidate => candidate.DiskReadBytesPerSecond + candidate.DiskWriteBytesPerSecond > 0)
                    .MaxBy(candidate => candidate.DiskReadBytesPerSecond + candidate.DiskWriteBytesPerSecond);
            else
                process = processes.Where(candidate => candidate.CpuPercent is not null).MaxBy(candidate => candidate.CpuPercent);
        }

        var sessionId = process is null ? null : membership.SessionOf(ProcessIdentity.Of(process));
        string? sessionName = null;
        if (sessionId is not null) sessionNameById?.TryGetValue(sessionId, out sessionName);
        if (process is null && disk is null) return null;

        return new SlownessCulprit(
            ProcessName: process?.Name,
            Pid: process?.Pid,
            SessionId: sessionId,
            SessionName: sessionName,
            DiskName: disk?.Name,
            DiskKind: disk?.Kind);
    }

    // ---------------------------------------------------------------------------------------------
    // The words
    // ---------------------------------------------------------------------------------------------

    private static string Summary(SlownessCause cause, Judgement cpu, Judgement memory, Judgement disk, SlownessCulprit? culprit)
    {
        var reasons = new List<string>();
        if (cause.HasFlag(SlownessCause.Cpu)) reasons.Add("the processor");
        if (cause.HasFlag(SlownessCause.Memory)) reasons.Add("memory");
        if (cause.HasFlag(SlownessCause.Disk)) reasons.Add("the disk");

        var details = new List<string>();
        if (cause.HasFlag(SlownessCause.Disk))
        {
            if (disk.Headline is { } diskHeadline) details.Add(diskHeadline);
            if (disk.Disk is { } named && disk.Headline?.Contains(named.Name, StringComparison.Ordinal) != true)
                details.Add($"on {DiskDescription(named)}");
        }
        if (cause.HasFlag(SlownessCause.Memory) && memory.Headline is { } memoryHeadline) details.Add(memoryHeadline);
        if (cause.HasFlag(SlownessCause.Cpu) && cpu.Headline is { } cpuHeadline) details.Add(cpuHeadline);

        var text = $"Slow because of {Join(reasons)}";
        if (details.Count > 0) text += ": " + string.Join("; ", details);
        if (culprit?.ProcessName is { } name) text += $"; mostly {name} (process {culprit.Pid}){SessionPhrase(culprit)}";
        return text;
    }

    private static string NextStep(SlownessCause cause, SlownessCulprit? culprit)
    {
        var who = culprit?.ProcessName is { } name ? $"{name}{SessionPhrase(culprit)}" : null;

        if (cause.HasFlag(SlownessCause.Memory))
            return who is null
                ? "Close the sessions or programs using the most memory."
                : $"Close the sessions or programs using the most memory, starting with {who}.";

        if (cause.HasFlag(SlownessCause.Disk))
        {
            var start = culprit is { DiskKind: DiskKind.Spinning, DiskName: { } diskName }
                ? $"Move the busy work off the hard drive {diskName} onto a solid-state drive, or let the heavy disk work finish"
                : "Let the heavy disk work finish, or move it to another machine";
            return who is null ? $"{start}." : $"{start}; {who} is reading and writing the most.";
        }

        return who is null
            ? "Pause or close the busiest work, or move sessions to a machine with room."
            : $"Pause or close the busiest work, starting with {who}, or move sessions to a machine with room.";
    }

    private static string SessionPhrase(SlownessCulprit culprit) => culprit.SessionId switch
    {
        null => "",
        { } id when culprit.SessionName is { } name => $" in session {id} ({name})",
        { } id => $" in session {id}",
    };

    /// <summary>
    /// "the hard drive sdc, 88% busy, 92 ms per write, 16.6 operations in flight, in the system volume".
    /// Only the figures offered are printed; a missing one is never shown as zero.
    /// </summary>
    private static string DiskDescription(DiskEvidence disk)
    {
        var parts = new List<string> { $"the {KindNoun(disk.Kind)} {disk.Name}" };
        if (disk.BusyPercent is { } busy) parts.Add($"{Percent(busy)} busy");
        if (disk.MillisecondsPerWrite is { } perWrite) parts.Add($"{DiskEvidence.FormatMilliseconds(perWrite)} ms per write");
        if (disk.MillisecondsPerRead is { } perRead) parts.Add($"{DiskEvidence.FormatMilliseconds(perRead)} ms per read");
        if (disk.AverageQueueDepth is { } depth) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{depth:F1} operations in flight"));
        if (disk.IsInSystemVolume == true) parts.Add("in the system volume");
        return string.Join(", ", parts);
    }

    /// <summary>What to call a disk of this kind in a sentence for the owner.</summary>
    public static string KindNoun(DiskKind kind) => kind switch
    {
        DiskKind.Spinning => "hard drive",
        DiskKind.SolidState => "solid-state drive",
        _ => "disk",
    };

    private static string KernelLevelName(KernelMemoryPressureLevel level) => level switch
    {
        KernelMemoryPressureLevel.Normal => "normal",
        KernelMemoryPressureLevel.Warning => "warning",
        KernelMemoryPressureLevel.Critical => "critical",
        _ => "unknown",
    };

    // ---------------------------------------------------------------------------------------------
    // Arithmetic over the window
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One stall share and the window it truly covers: the newest sample's one-minute figure where the
    /// kernel offers one, because the diagnosis judges a minute; otherwise the ten-second figures averaged
    /// over the samples, worded as exactly that. Null when neither was offered.
    /// </summary>
    private static (double Value, string Of, string Source)? StallShare(
        List<ResourceSample> window, Func<StallReading, double?> oneMinute, Func<StallReading, double?> tenSeconds, string span)
    {
        if (window.Select(MeasuredStall).LastOrDefault(stall => stall is { } found && oneMinute(found) is not null) is { } newest
            && oneMinute(newest) is { } minute)
            return (minute, "of the last minute", "the kernel's own one-minute figure");

        if (Average(window, sample => MeasuredStall(sample) is { } stall ? tenSeconds(stall) : null) is { } averaged)
            return (averaged, "of the time", $"the kernel's ten-second figures, averaged over {span}");

        return null;
    }

    /// <summary>A stall reading the kernel measured directly, or null for every other kind.</summary>
    private static StallReading? MeasuredStall(ResourceSample sample) =>
        sample.Stall is { CouldRead: true, Source: StallSource.LinuxPressureStall } stall ? stall : null;

    /// <summary>The average of the figures that were offered, or null when none was. An absent figure is not a zero.</summary>
    private static double? Average(List<ResourceSample> window, Func<ResourceSample, double?> figureOf) =>
        Mean(window.Select(figureOf));

    private static double? Mean(IEnumerable<double?> figures)
    {
        double sum = 0;
        var count = 0;
        foreach (var figure in figures)
        {
            if (figure is not { } value) continue;
            sum += value;
            count++;
        }
        return count == 0 ? null : sum / count;
    }

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}",
    };

    private static string Percent(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:F0}%");

    /// <summary>
    /// A stall share, with as many decimals as it needs to be read: "32%", "4.5%", "0.06%". A small stall
    /// must not print as "0%", which reads as "never" when it is "rarely".
    /// </summary>
    private static string Percent1(double value) => value switch
    {
        >= 10 => string.Create(CultureInfo.InvariantCulture, $"{value:F0}%"),
        >= 1 => string.Create(CultureInfo.InvariantCulture, $"{value:0.#}%"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value:0.##}%"),
    };

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds < 1 ? "one reading" : string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:F0} seconds");
}
