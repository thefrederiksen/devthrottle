using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE MACHINE'S RESOURCES ON macOS, READ DIRECTLY (issue #2811).
///
/// Memory, the processor, swap, the stall signals, every process, and the per-disk evidence the
/// slowness diagnosis needs, read from the operating system with NO SUBPROCESS - the same rule and the
/// same reason as
/// <see cref="MachineMemoryProbe"/>, which this uses for the memory reading rather than measuring it
/// twice. A machine that is already short of processor time or disk time must not be charged for a
/// process launch so that we can tell it is busy; starting <c>iostat</c> or <c>ps</c> every five
/// seconds would make the measurement part of the problem it is measuring.
///
/// THE WORK IS SPLIT THREE WAYS, and the split is the design:
///   * <see cref="MacOsNative"/> declares the system calls.
///   * <see cref="MacOsPlatformReader"/> makes them and captures exactly what came back.
///   * <see cref="MacOsResourceParsing"/> is pure and turns captures into figures, driven in tests
///     from output captured on a real Mac.
/// This class is the thin layer left over: it holds the previous reading, does the arithmetic between
/// two of them, caches the result and never throws.
///
/// WHAT THIS PROBE DOES NOT READ, AND WHY. The owner narrowed the Resource Monitor on 2026-11-06 to
/// memory and the processor: there is no disk bar and no network figure in the toolbar, the detail
/// tiles, the Gateway rows or the Cockpit. So this probe reads no network counters at all, no per-volume
/// figures and no disk throughput. The disk survives in one place only - <see cref="DiskEvidence"/>,
/// which exists to fill in the words of a slowness diagnosis and is never drawn as a meter.
///
/// MOST FIGURES HERE ARE RATES, SO THEY NEED TWO READINGS. The processor, the per-disk timings and
/// each process's processor and disk use are all cumulative counters, and a rate is the difference
/// between two of them over the time between. On the FIRST call there is no previous reading, so
/// those readings come back unreadable with <see cref="ResourceUnreadableReasons.FirstSample"/> -
/// which is the honest answer, and emphatically not zero. Memory, swap and the stall signals are
/// figures for right now and are reported on the first call like any other.
///
/// A READING THAT CANNOT BE TAKEN IS <c>CouldRead = false</c> WITH A REASON, NEVER A MADE-UP VALUE,
/// and a figure this platform does not offer is null. The most important case is the disk busy
/// percentage: macOS does not publish one. IOKit's disk-time counters measure concurrency-weighted
/// service time, which is the average number of operations in flight - measured on an Apple M4 it
/// read 234 percent while one program copied a file at full speed on an entirely healthy machine, and
/// 2454 percent under four contended writers. So <see cref="DiskEvidence.BusyPercent"/> is
/// null here and <see cref="DiskEvidence.AverageQueueDepth"/> carries what the counter honestly
/// measures. The evidence is in missions/resource-monitor/phase-1/macos-disk-busy-risk.txt.
/// </summary>
public sealed class MacOsResourceProbe : IResourceProbe
{
    /// <summary>
    /// The shared probe. One instance so the cache and the previous reading are shared across callers,
    /// and so an unreadable resource is logged once rather than once per caller.
    /// </summary>
    public static readonly MacOsResourceProbe Shared = new();

    /// <summary>
    /// How long a sample is reused. The same reasoning as <see cref="MachineMemoryProbe.CacheFor"/>:
    /// short enough that a machine falling off a cliff is noticed within a second, long enough that a
    /// burst of callers costs one pass over the machine rather than twenty.
    /// </summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private readonly IMachineMemoryProbe _memory;

    /// <summary>
    /// The conversion between Mach absolute time units and nanoseconds. Fixed for the life of the
    /// machine, so it is read once rather than on every pass over 400 processes.
    ///
    /// READ IN THE CONSTRUCTOR BEHIND A PLATFORM CHECK, NOT IN A FIELD INITIALISER. As a field
    /// initialiser this called <c>mach_timebase_info</c> the moment the class was touched, which on
    /// Windows or Linux throws because the entry point is not there - and because
    /// <see cref="Shared"/> is static, it threw during class initialisation, before the platform
    /// check in <see cref="ReadUncached"/> could answer "this is the macOS probe and the machine is
    /// not a Mac". A probe whose contract is that it never throws must not throw merely by existing.
    /// </summary>
    private readonly MacOsResourceParsing.MachTimebase _timebase;

    private ResourceSample? _cached;
    private Previous? _previous;
    private readonly HashSet<string> _loggedUnreadable = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a probe. The clock is injectable so a test can age the cache without sleeping, and the
    /// memory probe so a test can pin the memory reading; both default to the real thing.
    /// </summary>
    public MacOsResourceProbe(Func<DateTime>? now = null, IMachineMemoryProbe? memory = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _memory = memory ?? MachineMemoryProbe.Shared;
        _timebase = ReadTimebaseSafely();
    }

    /// <summary>
    /// The machine's timebase, or an unusable one on a machine that has no such notion. Constructing
    /// this probe must not throw anywhere, including on the platforms it cannot read - see the remarks
    /// on <see cref="_timebase"/>.
    /// </summary>
    private static MacOsResourceParsing.MachTimebase ReadTimebaseSafely()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return default;

        try
        {
            return MacOsPlatformReader.ReadMachTimebase();
        }
        catch (Exception)
        {
            // An unusable timebase, which makes the process figures unreadable with a stated reason
            // rather than wrong. Nothing is logged here: the probe has not been asked for a reading
            // yet, and Read reports it on the first call.
            return default;
        }
    }

    /// <inheritdoc />
    ///
    /// <remarks>
    /// WHY THIS DOES NOT LOG ITS OWN ENTRY AND EXIT, which the coding standards otherwise ask of every
    /// public method. It is called every five seconds for as long as the Director runs, so an entry and
    /// an exit line would be about thirty-five thousand lines a day that say nothing happened, and they
    /// would bury the lines that matter - including this probe's own report that a resource has become
    /// unreadable. <see cref="MachineMemoryProbe"/> is on the same timer and settled this the same way:
    /// it logs a failure, once, and nothing else. Deviating from the standard here is deliberate and
    /// consistent with that precedent rather than an omission, and it is written down so the next
    /// reader does not add the lines back.
    ///
    /// What IS logged is each resource the FIRST time it becomes unreadable, with the reason - see
    /// <see cref="LogAnythingNewlyUnreadable"/>. A failure is reported once per resource rather than
    /// once per sample for the same arithmetic.
    /// </remarks>
    public ResourceSample Read()
    {
        ResourceSample sample;
        var fresh = false;

        // THE CACHE IS CHECKED AND FILLED UNDER ONE LOCK, not two. With two, both callers of a
        // simultaneous first read missed the cache, and the second then made its own full pass a
        // fraction of a millisecond after the first - which set the interval for every rate to that
        // fraction. One lock means the second caller finds the sample the first just took. The pass
        // itself costs about a millisecond and a half, so a caller that waits for it waits for that.
        lock (_gate)
        {
            var now = _now();

            if (_cached is { } cached && now - cached.TakenAtUtc < CacheFor)
            {
                sample = cached;
            }
            else
            {
                sample = ReadUncached(now);
                _cached = sample;
                fresh = true;
            }
        }

        // Outside the lock: writing to the log is file work, and no other caller should queue behind
        // it. Only a fresh sample can have become unreadable, so a cached one is not re-examined.
        if (fresh) LogAnythingNewlyUnreadable(sample);

        return sample;
    }

    /// <summary>
    /// The pass over the machine. This is a boundary to the operating system, so it catches: a system
    /// call that is not present, a structure whose shape has changed under us, or a registry that
    /// answered something unexpected must come back as a sample of unreadable readings, never take the
    /// Director down.
    /// </summary>
    private ResourceSample ReadUncached(DateTime now)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var wrongPlatform = $"this is the macOS probe and the machine is {RuntimeInformation.OSDescription}";
            return UnreadableSample(now, wrongPlatform);
        }

        try
        {
            return ReadEverything(now);
        }
        catch (Exception ex)
        {
            return UnreadableSample(now, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// One pass over the machine. Called with <see cref="_gate"/> ALREADY HELD, which is what keeps
    /// two callers from each taking a pass and giving the second one an interval of a fraction of a
    /// millisecond.
    /// </summary>
    private ResourceSample ReadEverything(DateTime now)
    {
        var previous = _previous;
        var since = previous is null ? TimeSpan.Zero : now - previous.TakenAtUtc;

        var cores = ReadCores(out var coresReason);

        // READ ONCE. Enumerating the block storage drivers means an IOKit registry walk and three
        // property-list serialisations per disk, and this used to be done twice a sample: once to
        // collect the counters and again to collect the names and kinds. The captures are the same
        // either way, so they are taken once and handed to both.
        var diskCaptures = MacOsPlatformReader.ReadDisks();
        var disks = ReadDiskCounters(diskCaptures, out var disksWhyNot);
        var systemVolumeDisks = ReadSystemVolumeDisks();
        var processes = ReadProcessSnapshots(_timebase);

        var sample = new ResourceSample(
            TakenAtUtc: now,
            Cpu: FoldCpu(cores, coresReason, previous, since),
            Memory: _memory.Read(),
            Swap: FoldSwap(),
            Stall: FoldStall(),
            DiskEvidence: FoldDiskEvidence(diskCaptures, disks, disksWhyNot, systemVolumeDisks, previous, since),
            Processes: FoldProcesses(processes, previous, since, cores.Count, now));

        _previous = new Previous(now, cores, disks, processes);
        return sample;
    }

    // =============================================================================================
    // Processor
    // =============================================================================================

    private static IReadOnlyList<MacOsResourceParsing.CoreTicks> ReadCores(out string? reason)
    {
        var ticks = MacOsPlatformReader.ReadProcessorTicks(out var coreCount, out reason);
        if (ticks is null) return Array.Empty<MacOsResourceParsing.CoreTicks>();

        return MacOsResourceParsing.TryReadCoreTicks(ticks, coreCount, out var cores, out reason)
            ? cores
            : Array.Empty<MacOsResourceParsing.CoreTicks>();
    }

    private static CpuReading FoldCpu(
        IReadOnlyList<MacOsResourceParsing.CoreTicks> cores,
        string? reason,
        Previous? previous,
        TimeSpan since)
    {
        if (cores.Count == 0)
            return CpuReading.Unreadable(reason ?? "the processor tick counters could not be read");

        if (previous is null || since <= TimeSpan.Zero)
            return CpuReading.Unreadable(ResourceUnreadableReasons.FirstSample);

        if (previous.Cores.Count != cores.Count)
            return CpuReading.Unreadable(
                $"the machine reported {previous.Cores.Count} cores and then {cores.Count}, " +
                "so the two readings cannot be compared");

        // The whole machine's figure is built from the TOTALS across every core rather than from an
        // average of the per-core percentages. Averaging percentages weights a core that was barely
        // scheduled the same as one that was saturated, which on a machine with efficiency cores and
        // performance cores is a real difference, not a rounding one.
        ulong busy = 0;
        ulong total = 0;
        var busiest = 0.0;
        var anyCoreComparable = false;

        for (var index = 0; index < cores.Count; index++)
        {
            var before = previous.Cores[index];
            var after = cores[index];

            if (after.Total < before.Total || after.Busy < before.Busy) continue;

            busy += after.Busy - before.Busy;
            total += after.Total - before.Total;

            if (MacOsResourceParsing.BusyPercent(before, after) is { } percent)
            {
                busiest = Math.Max(busiest, percent);
                anyCoreComparable = true;
            }
        }

        if (total == 0 || !anyCoreComparable)
            return CpuReading.Unreadable(
                "no processor ticks passed between the two readings, so no percentage can be worked out");

        return CpuReading.Read(
            totalBusyPercent: busy / (double)total * 100.0,
            busiestCorePercent: busiest,
            logicalCoreCount: cores.Count);
    }

    // =============================================================================================
    // Swap
    // =============================================================================================

    private static SwapReading FoldSwap()
    {
        var bytes = MacOsPlatformReader.ReadSysctlBytes("vm.swapusage");
        if (bytes is null) return SwapReading.Unreadable("sysctl vm.swapusage could not be read");

        if (!MacOsResourceParsing.TryReadSwapUsage(bytes, out var usage, out var reason))
            return SwapReading.Unreadable(reason ?? "sysctl vm.swapusage could not be read");

        // EVERY FIGURE BEING ZERO IS A SUCCESSFUL READING HERE, not a failure. macOS writes no swap
        // file until it needs one, so a healthy Apple Silicon machine legitimately reports zero of
        // everything, and reporting that as unreadable would put a "cannot read" on the meter of a
        // machine in perfect health.
        return SwapReading.Read(usage.TotalBytes, usage.UsedBytes);
    }

    // =============================================================================================
    // Disks
    // =============================================================================================

    private static Dictionary<string, MacOsResourceParsing.DiskCounters> ReadDiskCounters(
        IReadOnlyList<MacOsRawReadings.DiskCapture> captures,
        out Dictionary<string, string> whyNot)
    {
        var counters = new Dictionary<string, MacOsResourceParsing.DiskCounters>(StringComparer.Ordinal);

        // WHY A DISK HAD NO COUNTERS IS WORTH KEEPING. The parser works out which statistics key was
        // missing and this used to throw that away, so a disk with one absent key was reported with
        // the generic "could not be read" and whoever read the log had nothing to go on.
        whyNot = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var disk in captures)
        {
            var name = MacOsResourceParsing.ReadPropertyListString(disk.BsdNamePropertyList ?? "");
            if (string.IsNullOrEmpty(name)) continue;

            var statistics = MacOsResourceParsing.ReadPropertyListDictionary(disk.StatisticsPropertyList ?? "");
            if (MacOsResourceParsing.TryReadDiskCounters(statistics, out var read, out var why))
                counters[name] = read;
            else if (why is not null)
                whyNot[name] = why;
        }

        return counters;
    }

    /// <summary>
    /// Which physical disks the volume the machine runs from is stored on, or null when that could not
    /// be worked out. Null is not an empty set: the empty set would say "no disk backs the system
    /// volume", which is never true, and would make <see cref="DiskEvidence.IsInSystemVolume"/> read
    /// false for every disk on the machine.
    /// </summary>
    private static HashSet<string>? ReadSystemVolumeDisks()
    {
        var device = MacOsPlatformReader.ReadSystemVolumeDevice();
        if (device is null) return null;

        var ancestors = MacOsPlatformReader.ReadAncestorDeviceNames(device);
        return ancestors.Count == 0 ? null : new HashSet<string>(ancestors, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every physical disk, as evidence for the slowness diagnosis and for nothing else.
    ///
    /// The owner narrowed this mission on 2026-11-06: there is no disk bar and no disk throughput
    /// figure anywhere he looks. What survives is what fills in the approved sentence - the disk's name,
    /// whether it spins, whether the machine runs from it, how long an operation takes and how many are
    /// in flight. No byte rate is computed here at all, because none is reported.
    /// </summary>
    private static DiskEvidenceSet FoldDiskEvidence(
        IReadOnlyList<MacOsRawReadings.DiskCapture> disks,
        Dictionary<string, MacOsResourceParsing.DiskCounters> counters,
        Dictionary<string, string> whyNot,
        HashSet<string>? systemVolumeDisks,
        Previous? previous,
        TimeSpan since)
    {
        if (disks.Count == 0)
            return DiskEvidenceSet.Unreadable("IOKit reported no block storage drivers, so no physical disk could be found");

        var readings = new List<DiskEvidence>(disks.Count);

        foreach (var disk in disks)
        {
            var name = MacOsResourceParsing.ReadPropertyListString(disk.BsdNamePropertyList ?? "");
            var kind = DiskKindOf(disk);

            if (string.IsNullOrEmpty(name))
            {
                readings.Add(DiskEvidence.Unreadable(
                    "unnamed disk", kind, isInSystemVolume: null,
                    "IOKit gave this disk no device name, so its figures cannot be attributed"));
                continue;
            }

            // NULL WHEN THE WALK FAILED, not false. "This disk does not back the system volume" and "we
            // could not find out which disks do" are different answers, and the second must not be
            // allowed to read as the first - that is how a diagnosis drops the one fact that made a slow
            // disk matter.
            var inSystemVolume = systemVolumeDisks is null ? (bool?)null : systemVolumeDisks.Contains(name);

            if (!counters.TryGetValue(name, out var after))
            {
                readings.Add(DiskEvidence.Unreadable(
                    name,
                    kind,
                    inSystemVolume,
                    whyNot.TryGetValue(name, out var why)
                        ? why
                        : "the IOKit statistics for this disk could not be read"));
                continue;
            }

            if (previous is null || since <= TimeSpan.Zero || !previous.Disks.TryGetValue(name, out var before))
            {
                readings.Add(DiskEvidence.Unreadable(name, kind, inSystemVolume, ResourceUnreadableReasons.FirstSample));
                continue;
            }

            readings.Add(FoldOneDisk(name, kind, inSystemVolume, before, after, since));
        }

        return DiskEvidenceSet.Read(readings);
    }

    private static DiskEvidence FoldOneDisk(
        string name,
        DiskKind kind,
        bool? inSystemVolume,
        MacOsResourceParsing.DiskCounters before,
        MacOsResourceParsing.DiskCounters after,
        TimeSpan since)
    {
        // A counter that went backwards means the driver detached and reattached between the two
        // readings, and the difference is meaningless. Reporting it would show a wildly negative rate.
        if (after.OperationsRead < before.OperationsRead ||
            after.OperationsWritten < before.OperationsWritten ||
            after.TotalTimeReadNanoseconds < before.TotalTimeReadNanoseconds ||
            after.TotalTimeWrittenNanoseconds < before.TotalTimeWrittenNanoseconds)
        {
            return DiskEvidence.Unreadable(
                name, kind, inSystemVolume,
                "this disk's counters went backwards between two readings, which means it was " +
                "detached and reattached, so no rate can be worked out across them");
        }

        var seconds = since.TotalSeconds;
        var reads = after.OperationsRead - before.OperationsRead;
        var writes = after.OperationsWritten - before.OperationsWritten;
        var timeRead = after.TotalTimeReadNanoseconds - before.TotalTimeReadNanoseconds;
        var timeWritten = after.TotalTimeWrittenNanoseconds - before.TotalTimeWrittenNanoseconds;

        return DiskEvidence.Read(
            name: name,
            kind: kind,
            isInSystemVolume: inSystemVolume,
            // NULL, BECAUSE macOS DOES NOT PUBLISH A DISK UTILISATION FIGURE. See this class's remarks
            // and the proof in missions/resource-monitor/phase-1/macos-disk-busy-risk.txt. Dividing the
            // disk-time counters by the interval gives average concurrency, not a share of the
            // interval, and it crosses 100 on an idle-but-streaming machine.
            busyPercent: null,
            // Null rather than zero when nothing happened: a disk that served no reads in the interval
            // has no time per read, and zero would read as an instant one.
            millisecondsPerRead: reads == 0 ? null : timeRead / 1e6 / reads,
            millisecondsPerWrite: writes == 0 ? null : timeWritten / 1e6 / writes,
            // What the disk-time counters honestly measure: the average number of operations in flight
            // over the interval.
            averageQueueDepth: (timeRead + timeWritten) / (seconds * 1_000_000_000.0));
    }

    private static DiskKind DiskKindOf(MacOsRawReadings.DiskCapture disk)
    {
        if (disk.DeviceCharacteristicsPropertyList is null) return DiskKind.Unknown;

        var characteristics = MacOsResourceParsing.ReadPropertyListDictionary(disk.DeviceCharacteristicsPropertyList);
        if (!characteristics.TryGetValue(MacOsResourceParsing.DeviceCharacteristicsMediumType, out var medium))
            return DiskKind.Unknown;

        // Anything that is not one of IOKit's two known words is Unknown, and is NOT guessed at. The
        // slowness diagnosis says "spinning hard drive" in the owner's warning text, and saying that
        // about a disk we could not identify would be worse than saying nothing.
        return medium switch
        {
            MacOsResourceParsing.MediumTypeSolidState => DiskKind.SolidState,
            MacOsResourceParsing.MediumTypeRotational => DiskKind.Spinning,
            _ => DiskKind.Unknown,
        };
    }

    // =============================================================================================
    // Stall signals
    // =============================================================================================

    private static StallReading FoldStall()
    {
        var loadBytes = MacOsPlatformReader.ReadSysctlBytes("vm.loadavg");
        double? runQueue = null;
        if (loadBytes is not null &&
            MacOsResourceParsing.TryReadLoadAverage(loadBytes, out var load, out _))
        {
            runQueue = load.OneMinute;
        }

        var level = MacOsPlatformReader.ReadSysctlInt32("kern.memorystatus_vm_pressure_level");

        if (runQueue is null && level is null)
            return StallReading.Unreadable("neither vm.loadavg nor the kernel memory-pressure level could be read");

        // WHAT THIS PLATFORM DOES NOT OFFER IS NULL, NOT ZERO. Linux publishes, in
        // /proc/pressure/{cpu,memory,io}, the share of time programs spent frozen waiting on each
        // resource. macOS publishes nothing equivalent: there is no stalled-percent figure for the
        // processor, for memory or for the disk, and zero would read as "nothing was ever stalled",
        // which is the opposite of "we cannot tell". So the three percentages are null and the verdict
        // is built from the run queue, the kernel's own pressure level and the per-disk times instead.
        //
        // ProcessesWaitingOnDisk is null for the same reason: the process status this platform reports
        // distinguishes running, sleeping, stopped and zombie, with no separate state for a process
        // held in an uninterruptible wait on the disk, so the count cannot be taken honestly here.
        return StallReading.Read(
            source: StallSource.MacOsSignals,
            cpuStalledPercent: null,
            memoryStalledPercent: null,
            ioStalledPercent: null,
            runQueueLength: runQueue,
            hardPageFaultsPerSecond: null,
            macOsMemoryPressureLevel: level,
            processesWaitingOnDisk: null);
    }

    // =============================================================================================
    // Processes
    // =============================================================================================

    private static Dictionary<int, ProcessSnapshot> ReadProcessSnapshots(
        MacOsResourceParsing.MachTimebase timebase)
    {
        var snapshots = new Dictionary<int, ProcessSnapshot>();

        // THE LIST COMES FROM THE KERNEL'S PROCESS TABLE, NOT FROM THE PER-PROCESS CALLS. An ordinary
        // user is refused the per-process call for another user's process, so building the list from
        // it dropped roughly three hundred of this machine's seven hundred processes - absent, not
        // null, out of a set reporting itself as read without trouble. The two busiest processes on
        // the machine were among them. The table below is readable for every process.
        var tableBytes = MacOsPlatformReader.ReadKernelProcessTable();
        if (tableBytes is null) return snapshots;

        var entries = MacOsResourceParsing.ReadKernelProcessTable(tableBytes);
        if (entries.Count == 0) return snapshots;

        // WITHOUT A USABLE TIMEBASE THERE ARE NO PROCESSOR FIGURES, rather than wrong ones. The
        // processor times are Mach absolute time units and the timebase is the only thing that turns
        // them into time; a zero numerator would turn every one of them into nothing and report a
        // flat-out machine as completely idle. The processes are still listed and named - only their
        // processor figures are withheld.
        var canReadProcessorTime = timebase.IsUsable;

        foreach (var entry in entries)
        {
            var (taskInfoBytes, bsdInfoBytes, rusageBytes) = MacOsPlatformReader.ReadProcess(entry.Pid);

            ulong? cpuNanoseconds = null;
            ulong? residentBytes = null;
            var figuresRefused = true;

            if (taskInfoBytes is not null &&
                MacOsResourceParsing.TryReadProcessTaskInfo(taskInfoBytes, out var task))
            {
                figuresRefused = false;
                residentBytes = task.ResidentBytes;
                if (canReadProcessorTime)
                    cpuNanoseconds = MacOsResourceParsing.MachAbsoluteToNanoseconds(task.TotalMachTime, timebase);
            }

            ulong? diskRead = null;
            ulong? diskWritten = null;
            if (rusageBytes is not null &&
                MacOsResourceParsing.TryReadProcessDiskUsage(rusageBytes, out var usage))
            {
                diskRead = usage.BytesRead;
                diskWritten = usage.BytesWritten;
            }

            // THE LONGER NAME WHEN IT IS OFFERED, THE TABLE'S SHORT NAME OTHERWISE. The per-process
            // call carries a thirty-two character name and the table only sixteen, and a truncated
            // name is a worse culprit than a full one - but it is a far better culprit than no entry
            // at all, which is what a refused process used to get.
            var name = entry.Name;
            if (bsdInfoBytes is not null &&
                MacOsResourceParsing.TryReadProcessBsdInfo(bsdInfoBytes, out var bsd) &&
                bsd.Name.Length > 0)
            {
                name = bsd.Name;
            }

            snapshots[entry.Pid] = new ProcessSnapshot(
                ParentPid: entry.ParentPid,
                Name: name,
                // ALWAYS THE TABLE'S START TIME, even when the per-process call also offers one. It is
                // the key that tells a reused process identifier from the same process one tick later,
                // so it has to come from one source or two samples could disagree about the same
                // process and every rate for it would be thrown away.
                StartedAtUtc: entry.StartedAtUtc,
                CpuNanoseconds: cpuNanoseconds,
                ResidentBytes: residentBytes,
                DiskBytesRead: diskRead,
                DiskBytesWritten: diskWritten,
                FiguresRefused: figuresRefused);
        }

        return snapshots;
    }

    private static ProcessSetReading FoldProcesses(
        Dictionary<int, ProcessSnapshot> snapshots,
        Previous? previous,
        TimeSpan since,
        int logicalCoreCount,
        DateTime now)
    {
        if (snapshots.Count == 0)
            return ProcessSetReading.Unreadable(
                "the kernel's process table could not be read, so no process on the machine could be listed");

        var readings = new List<ProcessReading>(snapshots.Count);

        foreach (var (pid, nowProcess) in snapshots)
        {
            ProcessSnapshot? before = null;
            if (previous is not null &&
                since > TimeSpan.Zero &&
                previous.Processes.TryGetValue(pid, out var found) &&
                // THE START TIME IS CHECKED, NOT JUST THE PROCESS IDENTIFIER. macOS reuses identifiers,
                // and on a machine starting and stopping sessions all day it reuses them often. Without
                // this, a new process inherits the processor time of the dead one that held its number
                // and is reported as having used years of processor time in five seconds.
                found.StartedAtUtc == nowProcess.StartedAtUtc)
            {
                before = found;
            }

            readings.Add(new ProcessReading(
                Pid: pid,
                ParentPid: nowProcess.ParentPid == 0 ? null : nowProcess.ParentPid,
                Name: nowProcess.Name,
                StartTimeUtc: nowProcess.StartedAtUtc,
                CpuPercent: ProcessPercent(before, nowProcess, since, logicalCoreCount, now),
                MemoryBytes: nowProcess.ResidentBytes,
                DiskReadBytesPerSecond: RatePerSecond(before?.DiskBytesRead, nowProcess.DiskBytesRead, since),
                DiskWriteBytesPerSecond: RatePerSecond(before?.DiskBytesWritten, nowProcess.DiskBytesWritten, since)));
        }

        return ProcessSetReading.Read(readings);
    }

    /// <summary>
    /// One process's share of the whole machine, or null when there is honestly no figure.
    ///
    /// NOTHING HERE IS ALLOWED TO BECOME A ZERO. There used to be a <c>?? 0</c> at the end of this,
    /// and it reported two different things as nought percent: a process the platform refused, and a
    /// process that started during the interval and so had nothing to subtract from. Measured: a child
    /// spawned after the first sample, doing nothing but spinning, had used sixty-five milliseconds of
    /// processor time by the second sample and was written down as completely idle, inside a set that
    /// said it had read fine. For five whole seconds a freshly started agent burning a core looked
    /// asleep. Now a refused process is null, and a brand new one is measured over its own life.
    /// </summary>
    private static double? ProcessPercent(
        ProcessSnapshot? before,
        ProcessSnapshot now,
        TimeSpan since,
        int logicalCoreCount,
        DateTime takenAtUtc)
    {
        // The platform refused this process, or the machine gave no usable timebase.
        if (now.CpuNanoseconds is not { } used) return null;

        if (before is { CpuNanoseconds: { } usedBefore })
            return MacOsResourceParsing.ProcessMachinePercent(usedBefore, used, since, logicalCoreCount);

        // No earlier reading for this process: its whole life IS its interval.
        return MacOsResourceParsing.ProcessLifetimeMachinePercent(
            used, takenAtUtc - now.StartedAtUtc, logicalCoreCount);
    }

    /// <summary>
    /// A rate from two cumulative byte figures, or null when there is no interval, no previous
    /// reading, or the figure is not offered for this process at all.
    /// </summary>
    private static double? RatePerSecond(ulong? before, ulong? after, TimeSpan since)
    {
        if (before is null || after is null || since <= TimeSpan.Zero) return null;
        if (after.Value < before.Value) return null;
        return (after.Value - before.Value) / since.TotalSeconds;
    }

    // =============================================================================================
    // Everything unreadable, and the logging
    // =============================================================================================

    private static ResourceSample UnreadableSample(DateTime now, string reason) =>
        new(
            TakenAtUtc: now,
            Cpu: CpuReading.Unreadable(reason),
            Memory: MachineMemoryReading.Unreadable(reason, now),
            Swap: SwapReading.Unreadable(reason),
            Stall: StallReading.Unreadable(reason),
            DiskEvidence: DiskEvidenceSet.Unreadable(reason),
            Processes: ProcessSetReading.Unreadable(reason));

    /// <summary>
    /// Writes one line for each resource that has become unreadable, and NOT AGAIN for the same one.
    ///
    /// Logged once per resource rather than once per sample, because this runs every five seconds: a
    /// machine with one unreadable resource would otherwise write seventeen thousand identical lines a
    /// day and bury everything else in the log. The first sample's "no interval yet" reason is never
    /// logged at all - it is the ordinary state of a probe that has just started, not a fault.
    /// </summary>
    private void LogAnythingNewlyUnreadable(ResourceSample sample)
    {
        Report("processor", sample.Cpu.CouldRead, sample.Cpu.UnreadableReason);
        Report("swap", sample.Swap.CouldRead, sample.Swap.UnreadableReason);
        Report("stall signals", sample.Stall.CouldRead, sample.Stall.UnreadableReason);
        Report("processes", sample.Processes.CouldRead, sample.Processes.UnreadableReason);
        Report("disk evidence", sample.DiskEvidence.CouldRead, sample.DiskEvidence.UnreadableReason);

        foreach (var disk in sample.DiskEvidence.Disks)
            Report($"disk {disk.Name}", disk.CouldRead, disk.UnreadableReason);
    }

    private void Report(string resource, bool couldRead, string? reason)
    {
        if (couldRead || reason is null) return;
        if (string.Equals(reason, ResourceUnreadableReasons.FirstSample, StringComparison.Ordinal)) return;

        bool shouldLog;
        lock (_gate)
        {
            shouldLog = _loggedUnreadable.Add(resource);
        }

        if (shouldLog)
            FileLog.Write($"[MacOsResourceProbe] Read: {resource} is unreadable and will not be " +
                          $"reported again: {reason}");
    }

    // =============================================================================================
    // The previous reading
    // =============================================================================================

    /// <summary>
    /// The last pass over the machine, kept so the next one can work out rates. Holds only what the
    /// operating system reported, never a derived figure.
    /// </summary>
    private sealed record Previous(
        DateTime TakenAtUtc,
        IReadOnlyList<MacOsResourceParsing.CoreTicks> Cores,
        IReadOnlyDictionary<string, MacOsResourceParsing.DiskCounters> Disks,
        IReadOnlyDictionary<int, ProcessSnapshot> Processes);

    /// <summary>
    /// One process as the operating system reported it, with no rate worked out yet.
    ///
    /// The processor time and the resident memory are nullable because an ordinary user is REFUSED
    /// them for a process belonging to another user, while the process itself is still named from the
    /// kernel's process table. That is the difference between a list that says "this process is here
    /// and I may not see what it costs" and one that silently leaves out a third of the machine.
    /// </summary>
    private readonly record struct ProcessSnapshot(
        int ParentPid,
        string Name,
        DateTime StartedAtUtc,
        ulong? CpuNanoseconds,
        ulong? ResidentBytes,
        ulong? DiskBytesRead,
        ulong? DiskBytesWritten,
        bool FiguresRefused);
}
