using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE MACHINE'S RESOURCES ON macOS, READ DIRECTLY (issue #2811).
///
/// Processor, swap, physical disks, volumes, network and every process, read from the operating
/// system with NO SUBPROCESS - the same rule and the same reason as
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
/// MOST FIGURES HERE ARE RATES, SO THEY NEED TWO READINGS. The processor, the disks, the network and
/// each process's processor and disk use are all cumulative counters, and a rate is the difference
/// between two of them over the time between. On the FIRST call there is no previous reading, so
/// those readings come back unreadable with <see cref="ResourceUnreadableReasons.FirstSample"/> -
/// which is the honest answer, and emphatically not zero. Memory, swap, the volumes and the stall
/// signals are figures for right now and are reported on the first call like any other.
///
/// A READING THAT CANNOT BE TAKEN IS <c>CouldRead = false</c> WITH A REASON, NEVER A MADE-UP VALUE,
/// and a figure this platform does not offer is null. The most important case is the disk busy
/// percentage: macOS does not publish one. IOKit's disk-time counters measure concurrency-weighted
/// service time, which is the average number of operations in flight - measured on an Apple M4 it
/// read 234 percent while one program copied a file at full speed on an entirely healthy machine, and
/// 2454 percent under four contended writers. So <see cref="PhysicalDiskReading.BusyPercent"/> is
/// null here and <see cref="PhysicalDiskReading.AverageQueueDepth"/> carries what the counter honestly
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
    private VolumeDiskMap? _volumeDisks;

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
        var interfaces = ReadInterfaceCounters();
        var processes = ReadProcessSnapshots(_timebase);

        var sample = new ResourceSample(
            TakenAtUtc: now,
            Cpu: FoldCpu(cores, coresReason, previous, since),
            Memory: _memory.Read(),
            Swap: FoldSwap(),
            Disks: FoldDisks(diskCaptures, disks, disksWhyNot, previous, since),
            Volumes: FoldVolumes(diskCaptures),
            Network: FoldNetwork(interfaces, previous, since),
            Stall: FoldStall(),
            Processes: FoldProcesses(processes, previous, since, cores.Count, now));

        _previous = new Previous(now, cores, disks, interfaces, processes);
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

    private static DiskSetReading FoldDisks(
        IReadOnlyList<MacOsRawReadings.DiskCapture> disks,
        Dictionary<string, MacOsResourceParsing.DiskCounters> counters,
        Dictionary<string, string> whyNot,
        Previous? previous,
        TimeSpan since)
    {
        if (disks.Count == 0)
            return DiskSetReading.Unreadable("IOKit reported no block storage drivers, so no physical disk could be found");

        var readings = new List<PhysicalDiskReading>(disks.Count);

        foreach (var disk in disks)
        {
            var name = MacOsResourceParsing.ReadPropertyListString(disk.BsdNamePropertyList ?? "");
            var kind = DiskKindOf(disk);

            if (string.IsNullOrEmpty(name))
            {
                readings.Add(PhysicalDiskReading.Unreadable(
                    "unnamed disk", kind, "IOKit gave this disk no device name, so its figures cannot be attributed"));
                continue;
            }

            if (!counters.TryGetValue(name, out var after))
            {
                readings.Add(PhysicalDiskReading.Unreadable(
                    name,
                    kind,
                    whyNot.TryGetValue(name, out var why)
                        ? why
                        : "the IOKit statistics for this disk could not be read"));
                continue;
            }

            if (previous is null || since <= TimeSpan.Zero || !previous.Disks.TryGetValue(name, out var before))
            {
                readings.Add(PhysicalDiskReading.Unreadable(name, kind, ResourceUnreadableReasons.FirstSample));
                continue;
            }

            readings.Add(FoldOneDisk(name, kind, before, after, since));
        }

        return DiskSetReading.Read(readings);
    }

    private static PhysicalDiskReading FoldOneDisk(
        string name,
        DiskKind kind,
        MacOsResourceParsing.DiskCounters before,
        MacOsResourceParsing.DiskCounters after,
        TimeSpan since)
    {
        // A counter that went backwards means the driver detached and reattached between the two
        // readings, and the difference is meaningless. Reporting it would show a wildly negative rate.
        if (after.BytesRead < before.BytesRead ||
            after.BytesWritten < before.BytesWritten ||
            after.OperationsRead < before.OperationsRead ||
            after.OperationsWritten < before.OperationsWritten ||
            after.TotalTimeReadNanoseconds < before.TotalTimeReadNanoseconds ||
            after.TotalTimeWrittenNanoseconds < before.TotalTimeWrittenNanoseconds)
        {
            return PhysicalDiskReading.Unreadable(
                name, kind,
                "this disk's counters went backwards between two readings, which means it was " +
                "detached and reattached, so no rate can be worked out across them");
        }

        var seconds = since.TotalSeconds;
        var reads = after.OperationsRead - before.OperationsRead;
        var writes = after.OperationsWritten - before.OperationsWritten;
        var timeRead = after.TotalTimeReadNanoseconds - before.TotalTimeReadNanoseconds;
        var timeWritten = after.TotalTimeWrittenNanoseconds - before.TotalTimeWrittenNanoseconds;

        return PhysicalDiskReading.Read(
            name: name,
            kind: kind,
            // NULL, BECAUSE macOS DOES NOT PUBLISH A DISK UTILISATION FIGURE. See this class's remarks
            // and the proof in missions/resource-monitor/phase-1/macos-disk-busy-risk.txt. Dividing the
            // disk-time counters by the interval gives average concurrency, not a share of the
            // interval, and it crosses 100 on an idle-but-streaming machine.
            busyPercent: null,
            readBytesPerSecond: (after.BytesRead - before.BytesRead) / seconds,
            writeBytesPerSecond: (after.BytesWritten - before.BytesWritten) / seconds,
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
    // Volumes
    // =============================================================================================

    private IReadOnlyList<VolumeReading> FoldVolumes(IReadOnlyList<MacOsRawReadings.DiskCapture> diskCaptures)
    {
        var captured = MacOsPlatformReader.ReadVolumes();
        if (captured is null)
        {
            // NO INVENTED VOLUME. This used to return one unreadable volume whose mount point was the
            // made-up string "(all volumes)", because the shared sample carries volumes as a bare list
            // with nowhere to put a reason for the whole set. A screen listing volumes would then show
            // one called "(all volumes)" - a name no machine has. An empty list and a logged reason
            // tell the truth about what happened; that an empty list cannot be told apart from a
            // machine with nothing mounted is a gap in the shared type, recorded in the review answer.
            Report("volumes", couldRead: false, reason: "the list of mounted file systems could not be read");
            return Array.Empty<VolumeReading>();
        }

        var volumes = MacOsResourceParsing.ReadVolumes(captured.Value.Buffer, captured.Value.Count);
        var map = VolumeDisksFor(volumes, diskCaptures);
        var readings = new List<VolumeReading>(volumes.Count);

        foreach (var volume in volumes)
        {
            // A volume whose device the walk could not reach names no disk. That is not the same as
            // having no disk behind it - the shared type has no way to tell those apart, which is
            // recorded in the review answer - and the synthetic mounts macOS makes, /dev and the
            // automounter map, genuinely have none.
            var names = volume.DeviceName is null
                ? Array.Empty<string>()
                : map.GetValueOrDefault(volume.DeviceName, Array.Empty<string>());

            readings.Add(VolumeReading.Read(
                mountPoint: volume.MountedOn,
                totalBytes: volume.TotalBytes,
                freeBytes: volume.AvailableBytes,
                physicalDiskNames: names));
        }

        return readings;
    }

    /// <summary>
    /// Which physical disks each volume sits on, CACHED until the set of mounted devices changes.
    ///
    /// Working this out means walking the IOKit registry upwards once per volume, and this machine has
    /// ten volumes. Doing that every five seconds would be the most expensive thing the probe does, to
    /// re-learn an answer that only changes when something is mounted or unmounted. So the map is kept
    /// and thrown away whole the moment the set of device names differs from the one it was built for.
    /// </summary>
    private Dictionary<string, IReadOnlyList<string>> VolumeDisksFor(
        IReadOnlyList<MacOsResourceParsing.Volume> volumes,
        IReadOnlyList<MacOsRawReadings.DiskCapture> diskCaptures)
    {
        var devices = volumes
            .Select(volume => volume.DeviceName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        if (_volumeDisks is { } cached && cached.Devices.SetEquals(devices)) return cached.Map;

        var physical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var disk in diskCaptures)
        {
            var name = MacOsResourceParsing.ReadPropertyListString(disk.BsdNamePropertyList ?? "");
            if (!string.IsNullOrEmpty(name)) physical.Add(name);
        }

        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            // The volume's OWN device counts when it is itself a physical disk - a volume written
            // straight onto a disk with no partition map. Otherwise the answer is whatever the walk
            // upwards finds that we hold statistics for.
            var names = new List<string>();
            if (physical.Contains(device)) names.Add(device);

            foreach (var ancestor in MacOsPlatformReader.ReadAncestorDeviceNames(device))
            {
                if (physical.Contains(ancestor) && !names.Contains(ancestor)) names.Add(ancestor);
            }

            map[device] = names;
        }

        _volumeDisks = new VolumeDiskMap(devices, map);
        return map;
    }

    // =============================================================================================
    // Network
    // =============================================================================================

    private static Dictionary<string, MacOsResourceParsing.InterfaceCounters> ReadInterfaceCounters()
    {
        var counters = new Dictionary<string, MacOsResourceParsing.InterfaceCounters>(StringComparer.Ordinal);

        foreach (var (name, flags, data) in MacOsPlatformReader.ReadInterfaces())
        {
            // ONLY THE REAL HARDWARE COUNTS. A tunnel or a bridge carries bytes that the interface
            // underneath it has already counted, so adding them reports traffic the machine never saw.
            // The interface type is the first byte of the counters the operating system just handed us.
            if (data.Length == 0) continue;
            if (!MacOsResourceParsing.IsPhysicalNetworkInterface(name, flags, data[0])) continue;

            if (MacOsResourceParsing.TryReadInterfaceCounters(name, data, out var read, out _))
                counters[name] = read;
        }

        return counters;
    }

    private static NetworkReading FoldNetwork(
        Dictionary<string, MacOsResourceParsing.InterfaceCounters> counters,
        Previous? previous,
        TimeSpan since)
    {
        if (counters.Count == 0)
            return NetworkReading.Unreadable("no network interface counters could be read");

        if (previous is null || since <= TimeSpan.Zero)
            return NetworkReading.Unreadable(ResourceUnreadableReasons.FirstSample);

        // EACH INTERFACE IS DIFFERENCED ON ITS OWN AND THEN ADDED UP, rather than the totals being
        // differenced. These counters are thirty-two bits wide, so each one wraps independently every
        // four gibibytes, and the wrap has to be allowed for per interface or an interface that
        // wrapped drags the whole machine's figure negative.
        var received = 0UL;
        var sent = 0UL;
        var comparable = 0;

        foreach (var (name, after) in counters)
        {
            if (!previous.Interfaces.TryGetValue(name, out var before)) continue;

            received += MacOsResourceParsing.DeltaOfWrappingCounter(before.BytesReceived, after.BytesReceived);
            sent += MacOsResourceParsing.DeltaOfWrappingCounter(before.BytesSent, after.BytesSent);
            comparable++;
        }

        if (comparable == 0)
            return NetworkReading.Unreadable(
                "every network interface appeared between the two readings, so no rate can be worked out");

        var seconds = since.TotalSeconds;
        return NetworkReading.Read(received / seconds, sent / seconds);
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
            Disks: DiskSetReading.Unreadable(reason),
            // Empty rather than one invented volume, for the reason in FoldVolumes. The sample as a
            // whole is already unreadable here, so the reason is never lost.
            Volumes: Array.Empty<VolumeReading>(),
            Network: NetworkReading.Unreadable(reason),
            Stall: StallReading.Unreadable(reason),
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
        Report("disks", sample.Disks.CouldRead, sample.Disks.UnreadableReason);
        Report("network", sample.Network.CouldRead, sample.Network.UnreadableReason);
        Report("stall signals", sample.Stall.CouldRead, sample.Stall.UnreadableReason);
        Report("processes", sample.Processes.CouldRead, sample.Processes.UnreadableReason);

        foreach (var disk in sample.Disks.Disks)
            Report($"disk {disk.Name}", disk.CouldRead, disk.UnreadableReason);

        foreach (var volume in sample.Volumes)
            Report($"volume {volume.MountPoint}", volume.CouldRead, volume.UnreadableReason);
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
        IReadOnlyDictionary<string, MacOsResourceParsing.InterfaceCounters> Interfaces,
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

    /// <summary>
    /// The volume-to-disk map, with the set of devices it was built for so it can be thrown away the
    /// moment something is mounted or unmounted.
    /// </summary>
    private sealed record VolumeDiskMap(
        HashSet<string> Devices,
        Dictionary<string, IReadOnlyList<string>> Map);
}
