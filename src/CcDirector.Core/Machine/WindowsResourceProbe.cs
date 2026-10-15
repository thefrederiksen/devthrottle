using System.Diagnostics;
using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE WHOLE WINDOWS MACHINE, READ DIRECTLY (Resource Monitor mission, phase 1).
///
/// Processor, memory, page file, physical disks, volumes, network, stalls and every process, in one
/// <see cref="Read"/>. Built the way <see cref="MachineMemoryProbe"/> is: NO SUBPROCESS - every figure is
/// a direct call (see <see cref="WindowsResourceNative"/>), every structure is decoded by a pure function
/// (<see cref="WindowsResourceParsing"/>), and this class only makes the calls and does the arithmetic
/// between two cumulative readings, which is itself pure and static so it is tested without Windows.
///
/// RATES NEED TWO READINGS. The first <see cref="Read"/> has nothing to subtract from, so its processor,
/// disk activity, network, stall and process figures are unreadable with
/// <see cref="ResourceUnreadableReasons.FirstSample"/>. Memory, page file and volume space are levels,
/// not rates, and read on the first call.
///
/// INTERVALS ARE TIMED ON THE MONOTONIC CLOCK (<see cref="Stopwatch"/>), never the wall clock, so a
/// clock that jumps - a time sync, a daylight saving change, a laptop waking - cannot produce a negative
/// or enormous rate. The processor and the disks carry their own time base and need neither.
///
/// WHAT WINDOWS DOES NOT OFFER is null, never zero: the three stalled percentages (Linux pressure stall
/// information has no Windows equivalent) and the count of processes waiting on the disk.
///
/// Never throws. Each part of the sample is read on its own, so one part that cannot be read comes back
/// unreadable with its reason and never hides the others; each such part is logged once.
/// </summary>
public sealed class WindowsResourceProbe : IResourceProbe
{
    /// <summary>How long a sample is reused. The same reasoning as <see cref="MachineMemoryProbe.CacheFor"/>.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private readonly IMachineMemoryProbe _memory;
    private readonly HashSet<string> _loggedUnreadable = new(StringComparer.Ordinal);
    private readonly Dictionary<int, DiskKind> _diskKinds = new();

    private ResourceSample? _cached;
    private CpuTimes? _previousSystemTimes;
    private IReadOnlyList<CpuTimes>? _previousCores;
    private readonly Dictionary<int, DiskCounters> _previousDisks = new();
    private InterfaceSnapshot? _previousInterfaces;
    private ProcessSnapshot? _previousProcesses;
    private PagingSnapshot? _previousPaging;
    private int _processListSize = 1 << 20;

    /// <summary>
    /// Creates a probe. Memory is read by the existing probe from issue #2818, unchanged. The clock only
    /// ages the cache; it is injectable so a test can do that without sleeping.
    /// </summary>
    public WindowsResourceProbe(IMachineMemoryProbe? memory = null, Func<DateTime>? now = null)
    {
        _memory = memory ?? MachineMemoryProbe.Shared;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc />
    public ResourceSample Read()
    {
        // One lock around the whole read: the previous cumulative readings are what the next rates are
        // computed from, so two threads interleaving here would each compute a rate over the other's
        // half-updated state. A read costs milliseconds and is cached for a second; nobody waits long.
        lock (_gate)
        {
            var now = _now();

            // A cached sample from the FUTURE means the wall clock went backwards. It is not reused.
            if (_cached is not null && now >= _cached.TakenAtUtc && now - _cached.TakenAtUtc < CacheFor)
                return _cached;

            var sample = ReadUncached(now);
            _cached = sample;
            LogUnreadableParts(sample);
            return sample;
        }
    }

    private ResourceSample ReadUncached(DateTime now)
    {
        var memory = _memory.Read();

        var platformProblem = PlatformProblem();
        if (platformProblem is not null)
        {
            return new ResourceSample(now, CpuReading.Unreadable(platformProblem), memory,
                SwapReading.Unreadable(platformProblem), DiskSetReading.Unreadable(platformProblem),
                Array.Empty<VolumeReading>(), NetworkReading.Unreadable(platformProblem),
                StallReading.Unreadable(platformProblem), ProcessSetReading.Unreadable(platformProblem));
        }

        var (processes, stall) = ReadProcessesAndStall();

        return new ResourceSample(now, ReadCpu(), memory, ReadSwap(), ReadDisks(), ReadVolumes(), ReadNetwork(), stall, processes);
    }

    /// <summary>Why this process cannot read Windows at all, or null when it can.</summary>
    private static string? PlatformProblem()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"the Windows probe was asked to read {RuntimeInformation.OSDescription}";
        if (!Environment.Is64BitProcess)
            return "the Windows probe decodes 64-bit structures and this process is 32-bit";
        return null;
    }

    // ---------------------------------------------------------------------------------------------
    // The platform reads. Each one is a boundary to the operating system and catches there: a call that
    // fails, or a structure that does not decode, comes back as that part unreadable with the reason.
    // ---------------------------------------------------------------------------------------------

    private CpuReading ReadCpu()
    {
        try
        {
            var system = WindowsResourceNative.ReadSystemTimes();
            var cores = WindowsResourceParsing.ParseProcessorPerformance(WindowsResourceNative.ReadProcessorPerformance());

            var reading = CpuBetween(_previousSystemTimes, system, _previousCores, cores);
            _previousSystemTimes = system;
            _previousCores = cores;
            return reading;
        }
        catch (Exception ex)
        {
            return CpuReading.Unreadable(Describe(ex));
        }
    }

    private SwapReading ReadSwap()
    {
        try
        {
            var totals = WindowsResourceParsing.ParsePageFileInformation(
                WindowsResourceNative.ReadPageFileInformation(), (ulong)Environment.SystemPageSize);
            return SwapReading.Read(totals.TotalBytes, totals.InUseBytes);
        }
        catch (Exception ex)
        {
            return SwapReading.Unreadable(Describe(ex));
        }
    }

    private DiskSetReading ReadDisks()
    {
        try
        {
            var disks = new List<PhysicalDiskReading>();
            var seen = new HashSet<int>();

            for (var number = 0; number <= WindowsResourceNative.MaxPhysicalDiskNumber; number++)
            {
                var name = DiskName(number);
                Microsoft.Win32.SafeHandles.SafeFileHandle? handle;
                try
                {
                    handle = WindowsResourceNative.OpenPhysicalDisk(number);
                }
                catch (Exception ex)
                {
                    disks.Add(PhysicalDiskReading.Unreadable(name, DiskKind.Unknown, Describe(ex)));
                    continue;
                }
                if (handle is null) continue;

                using (handle)
                {
                    seen.Add(number);
                    var kind = KindOf(number, handle);
                    try
                    {
                        var counters = WindowsResourceParsing.ParseDiskPerformance(WindowsResourceNative.ReadDiskPerformance(handle));
                        disks.Add(DiskBetween(name, kind, _previousDisks.TryGetValue(number, out var earlier) ? earlier : null, counters));
                        _previousDisks[number] = counters;
                    }
                    catch (Exception ex)
                    {
                        disks.Add(PhysicalDiskReading.Unreadable(name, kind, Describe(ex)));
                    }
                }
            }

            // A disk that has gone (unplugged) must not leave its last counters behind for a different
            // disk that is later given the same number.
            foreach (var gone in _previousDisks.Keys.Where(n => !seen.Contains(n)).ToList())
            {
                _previousDisks.Remove(gone);
                _diskKinds.Remove(gone);
            }

            return DiskSetReading.Read(disks);
        }
        catch (Exception ex)
        {
            return DiskSetReading.Unreadable(Describe(ex));
        }
    }

    /// <summary>
    /// Spinning or solid-state, asked once per disk and remembered: it does not change while the disk is
    /// attached. A disk that cannot answer the seek-penalty question - some USB bridges and virtual
    /// disks cannot - is Unknown, and logged once, never assumed to be either.
    /// </summary>
    private DiskKind KindOf(int number, Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (_diskKinds.TryGetValue(number, out var known)) return known;

        DiskKind kind;
        try
        {
            kind = WindowsResourceParsing.ParseIncursSeekPenalty(WindowsResourceNative.ReadSeekPenalty(handle))
                ? DiskKind.Spinning
                : DiskKind.SolidState;
        }
        catch (Exception ex)
        {
            LogOnce($"disk-kind:{number}", $"{DiskName(number)} cannot say whether it is spinning or solid-state, so its kind is Unknown: {Describe(ex)}");
            kind = DiskKind.Unknown;
        }

        _diskKinds[number] = kind;
        return kind;
    }

    private IReadOnlyList<VolumeReading> ReadVolumes()
    {
        IReadOnlyList<char> letters;
        try
        {
            letters = WindowsResourceNative.FixedDriveLetters();
        }
        catch (Exception ex)
        {
            LogOnce("volumes", $"the fixed volumes could not be listed, so no volume is shown: {Describe(ex)}");
            return Array.Empty<VolumeReading>();
        }

        var volumes = new List<VolumeReading>(letters.Count);
        foreach (var letter in letters)
        {
            var mountPoint = $"{letter}:\\";
            try
            {
                var (free, total) = WindowsResourceNative.ReadVolumeSpace(mountPoint);
                volumes.Add(VolumeReading.Read(mountPoint, total, free, DisksUnder(letter)));
            }
            catch (Exception ex)
            {
                volumes.Add(VolumeReading.Unreadable(mountPoint, Describe(ex)));
            }
        }
        return volumes;
    }

    /// <summary>
    /// The physical disks a volume sits on. Empty when the volume cannot say - a volume the extents
    /// query does not support - which the type defines as "not known", and logged once.
    /// </summary>
    private IReadOnlyList<string> DisksUnder(char letter)
    {
        try
        {
            using var volume = WindowsResourceNative.OpenVolume(letter);
            return WindowsResourceParsing.ParseVolumeDiskNumbers(WindowsResourceNative.ReadVolumeDiskExtents(volume))
                .Select(DiskName)
                .ToList();
        }
        catch (Exception ex)
        {
            LogOnce($"volume-extents:{letter}", $"volume {letter}: cannot say which physical disks it sits on: {Describe(ex)}");
            return Array.Empty<string>();
        }
    }

    private NetworkReading ReadNetwork()
    {
        try
        {
            var rows = WindowsResourceParsing.ParseInterfaceTable(WindowsResourceNative.ReadInterfaceTable());
            var current = new InterfaceSnapshot(Stopwatch.GetTimestamp(), rows.Where(WindowsResourceParsing.IsPhysicalAndUp).ToList());

            var reading = NetworkBetween(_previousInterfaces, current);
            _previousInterfaces = current;
            return reading;
        }
        catch (Exception ex)
        {
            return NetworkReading.Unreadable(Describe(ex));
        }
    }

    private (ProcessSetReading Processes, StallReading Stall) ReadProcessesAndStall()
    {
        try
        {
            var buffer = WindowsResourceNative.ReadProcessInformation(ref _processListSize, out var address);
            var current = new ProcessSnapshot(Stopwatch.GetTimestamp(), WindowsResourceParsing.ParseProcessInformation(buffer, address));

            var paging = new PagingSnapshot(Stopwatch.GetTimestamp(),
                WindowsResourceParsing.ParsePageReadCount(WindowsResourceNative.ReadSystemPerformance()));

            var processes = ProcessesBetween(_previousProcesses, current, Environment.ProcessorCount);
            var stall = StallBetween(current, _previousPaging, paging);
            _previousProcesses = current;
            _previousPaging = paging;
            return (processes, stall);
        }
        catch (Exception ex)
        {
            var reason = Describe(ex);
            return (ProcessSetReading.Unreadable(reason), StallReading.Unreadable(reason));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The arithmetic between two readings. Pure and static: tested without Windows.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The operating system's own name for physical disk N, as Disk Management and diskpart show it.</summary>
    internal static string DiskName(int number) => $"PhysicalDrive{number}";

    /// <summary>
    /// The processor from two readings: the whole machine from GetSystemTimes, the busiest core from the
    /// per-processor times. Null previous readings mean this is the first sample.
    ///
    /// The per-processor list covers the processor group the Director runs in, which is every processor
    /// on any machine with 64 or fewer; the whole-machine figure from GetSystemTimes covers all of them
    /// regardless.
    /// </summary>
    internal static CpuReading CpuBetween(CpuTimes? earlierSystem, CpuTimes laterSystem, IReadOnlyList<CpuTimes>? earlierCores, IReadOnlyList<CpuTimes> laterCores)
    {
        if (earlierSystem is null || earlierCores is null) return CpuReading.Unreadable(ResourceUnreadableReasons.FirstSample);

        var total = WindowsResourceParsing.BusyFraction(earlierSystem.Value, laterSystem);
        if (total is null) return CpuReading.Unreadable("no processor time passed between the two readings");

        // Processors that appear in both readings. One added while running is counted from its second
        // reading on; until then there is nothing to subtract from.
        double? busiest = null;
        for (var i = 0; i < Math.Min(earlierCores.Count, laterCores.Count); i++)
        {
            var core = WindowsResourceParsing.BusyFraction(earlierCores[i], laterCores[i]);
            if (core is { } value && (busiest is null || value > busiest)) busiest = value;
        }
        if (busiest is null) return CpuReading.Unreadable("no processor reported time passing between the two readings");

        return CpuReading.Read(total.Value * 100, busiest.Value * 100, laterCores.Count);
    }

    /// <summary>One physical disk from two readings of its counters. A null previous reading means first sample.</summary>
    internal static PhysicalDiskReading DiskBetween(string name, DiskKind kind, DiskCounters? earlier, DiskCounters later)
    {
        if (earlier is null) return PhysicalDiskReading.Unreadable(name, kind, ResourceUnreadableReasons.FirstSample);

        var rates = WindowsResourceParsing.DiskRatesBetween(earlier.Value, later);
        if (rates.BusyFraction is not { } busy || rates.ReadBytesPerSecond is not { } read || rates.WriteBytesPerSecond is not { } write)
            return PhysicalDiskReading.Unreadable(name, kind, "no time passed between the two readings of this disk");

        return PhysicalDiskReading.Read(name, kind, busy * 100, read, write,
            rates.SecondsPerRead * 1000, rates.SecondsPerWrite * 1000, rates.AverageQueueLength);
    }

    /// <summary>
    /// The network from two readings: bytes in and out per second on the PHYSICAL interfaces only (see
    /// <see cref="WindowsResourceParsing.IsPhysicalAndUp"/>). Counting every non-loopback interface
    /// would count the same bytes several times over - once on the adapter, again on each filter driver
    /// row bound to it, and again on any virtual switch or tunnel whose traffic leaves through it.
    ///
    /// An interface counts only when it is in both readings and its counters did not go backwards (an
    /// adapter reset restarts them). A machine with no physical interface up has, truthfully, no network
    /// traffic, and reads as zero.
    /// </summary>
    internal static NetworkReading NetworkBetween(InterfaceSnapshot? earlier, InterfaceSnapshot later)
    {
        if (earlier is null) return NetworkReading.Unreadable(ResourceUnreadableReasons.FirstSample);

        var seconds = (later.Timestamp - earlier.Timestamp) / (double)Stopwatch.Frequency;
        if (seconds <= 0) return NetworkReading.Unreadable("no time passed between the two readings of the network");

        ulong received = 0, sent = 0;
        var before = earlier.Interfaces.ToDictionary(r => r.Index);
        foreach (var row in later.Interfaces)
        {
            if (!before.TryGetValue(row.Index, out var then)) continue;
            if (row.InOctets < then.InOctets || row.OutOctets < then.OutOctets) continue;
            received += row.InOctets - then.InOctets;
            sent += row.OutOctets - then.OutOctets;
        }

        return NetworkReading.Read(received / seconds, sent / seconds);
    }

    /// <summary>
    /// Every process, with its processor share, memory and input/output rates over the interval.
    ///
    /// A process is the same process only when its id AND its creation time match: ids are reused, and
    /// a new process under an old id must not be charged the difference between the two. A process new
    /// since the last reading was created during the interval, so everything it has used, it used in
    /// the interval - that is its figure, not a guess.
    ///
    /// The System Idle Process (id 0) is left out: its "processor time" is the time nothing ran, and
    /// listed as a process it would top every list of what is using the processor.
    ///
    /// <paramref name="logicalCores"/> is every logical processor the machine has, so a share is of the
    /// WHOLE machine: one process spinning one core of 24 reads about 4%.
    ///
    /// ON WINDOWS THE INPUT/OUTPUT FIGURES ARE EVERY READ AND WRITE THE PROCESS MAKES - files, devices
    /// and the network, including reads served from the file cache - not only bytes that reached a
    /// physical disk. Windows keeps no per-process count of physical disk bytes. They answer "which
    /// process is moving the most data", which is what naming a culprit needs.
    /// </summary>
    internal static ProcessSetReading ProcessesBetween(ProcessSnapshot? earlier, ProcessSnapshot later, int logicalCores)
    {
        if (earlier is null) return ProcessSetReading.Unreadable(ResourceUnreadableReasons.FirstSample);

        var seconds = (later.Timestamp - earlier.Timestamp) / (double)Stopwatch.Frequency;
        if (seconds <= 0) return ProcessSetReading.Unreadable("no time passed between the two readings of the processes");
        if (logicalCores <= 0) return ProcessSetReading.Unreadable("the number of logical processors is not known");

        var before = earlier.ByIdentity();
        var capacity = seconds * 1e7 * logicalCores;
        var readings = new List<ProcessReading>(later.Processes.Count);

        foreach (var p in later.Processes)
        {
            if (p.ProcessId == 0) continue;

            before.TryGetValue((p.ProcessId, p.CreateTime), out var then);
            var cpu = (p.KernelTime + p.UserTime) - (then is null ? 0 : then.KernelTime + then.UserTime);

            readings.Add(new ProcessReading(
                Pid: p.ProcessId,
                ParentPid: p.ParentProcessId,
                Name: p.Name ?? $"process {p.ProcessId}",
                StartTimeUtc: p.CreateTime > 0 ? DateTime.FromFileTimeUtc(p.CreateTime) : null,
                CpuPercent: Math.Clamp(cpu / capacity * 100, 0, 100),
                MemoryBytes: p.WorkingSetBytes,
                DiskReadBytesPerSecond: Growth(then?.ReadBytes ?? 0, p.ReadBytes) / seconds,
                DiskWriteBytesPerSecond: Growth(then?.WriteBytes ?? 0, p.WriteBytes) / seconds));
        }

        return ProcessSetReading.Read(readings);
    }

    /// <summary>How far a cumulative counter grew. A counter that went backwards was reset, and grew by nothing we can know.</summary>
    private static ulong Growth(ulong earlier, ulong later) => later >= earlier ? later - earlier : 0;

    /// <summary>
    /// The stall proxies Windows offers:
    ///
    /// RUN QUEUE: threads in the Ready or Deferred Ready state - runnable, waiting only for a processor -
    /// in the latest process list. This is what the "System\Processor Queue Length" performance counter
    /// counts, and like it, it is a moment, not an average.
    ///
    /// HARD PAGE FAULTS PER SECOND: pages the whole machine read back from disk to satisfy hard faults,
    /// over the interval - the arithmetic of "Memory\Pages Input/sec", on the same kernel counter.
    ///
    /// The stalled percentages and the count of processes waiting on the disk are not offered by Windows
    /// and stay null. The disk side of the diagnosis reads each disk's busy share, time per transfer and
    /// queue length from <see cref="DiskSetReading"/>.
    /// </summary>
    internal static StallReading StallBetween(ProcessSnapshot processes, PagingSnapshot? earlierPaging, PagingSnapshot laterPaging)
    {
        if (earlierPaging is null) return StallReading.Unreadable(ResourceUnreadableReasons.FirstSample);

        var seconds = (laterPaging.Timestamp - earlierPaging.Timestamp) / (double)Stopwatch.Frequency;
        if (seconds <= 0) return StallReading.Unreadable("no time passed between the two readings of the paging counters");

        // A 32-bit counter: subtracted unchecked, so a wrap past four billion still gives the growth.
        var pagesRead = unchecked(laterPaging.PageReadCount - earlierPaging.PageReadCount);

        return StallReading.Read(StallSource.WindowsCounters,
            runQueueLength: processes.Processes.Sum(p => p.ThreadsWaitingOnCpu),
            hardPageFaultsPerSecond: pagesRead / seconds);
    }

    // ---------------------------------------------------------------------------------------------
    // Logging
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Logs each part of the sample that could not be read, ONCE per part, so a machine that will never
    /// let the probe read its disks says so in the log once rather than every five seconds. The first
    /// sample's "no interval yet" is expected and is not logged.
    /// </summary>
    private void LogUnreadableParts(ResourceSample sample)
    {
        LogIfUnreadable("cpu", sample.Cpu.CouldRead, sample.Cpu.UnreadableReason);
        LogIfUnreadable("swap", sample.Swap.CouldRead, sample.Swap.UnreadableReason);
        LogIfUnreadable("disks", sample.Disks.CouldRead, sample.Disks.UnreadableReason);
        foreach (var disk in sample.Disks.Disks) LogIfUnreadable($"disk:{disk.Name}", disk.CouldRead, disk.UnreadableReason);
        foreach (var volume in sample.Volumes) LogIfUnreadable($"volume:{volume.MountPoint}", volume.CouldRead, volume.UnreadableReason);
        LogIfUnreadable("network", sample.Network.CouldRead, sample.Network.UnreadableReason);
        LogIfUnreadable("stall", sample.Stall.CouldRead, sample.Stall.UnreadableReason);
        LogIfUnreadable("processes", sample.Processes.CouldRead, sample.Processes.UnreadableReason);
    }

    private void LogIfUnreadable(string part, bool couldRead, string? reason)
    {
        if (couldRead || reason == ResourceUnreadableReasons.FirstSample) return;
        LogOnce(part, $"{part} could not be read: {reason}");
    }

    private void LogOnce(string key, string message)
    {
        if (!_loggedUnreadable.Add(key)) return;
        FileLog.Write($"[WindowsResourceProbe] Read FAILED, and will not be reported again: {message}");
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}

/// <summary>The physical interfaces at one moment, with the monotonic time they were read.</summary>
internal sealed record InterfaceSnapshot(long Timestamp, IReadOnlyList<RawInterface> Interfaces);

/// <summary>The machine's cumulative pages-read-from-disk count at one moment, with the monotonic time it was read.</summary>
internal sealed record PagingSnapshot(long Timestamp, uint PageReadCount);

/// <summary>Every process at one moment, with the monotonic time the list was read.</summary>
internal sealed record ProcessSnapshot(long Timestamp, IReadOnlyList<RawProcess> Processes)
{
    /// <summary>
    /// The processes keyed by id AND creation time - the pair that identifies one process, since ids are
    /// reused. Built on demand; a duplicate pair (which Windows does not produce) keeps the first.
    /// </summary>
    public Dictionary<(int Pid, long CreateTime), RawProcess> ByIdentity()
    {
        var map = new Dictionary<(int, long), RawProcess>(Processes.Count);
        foreach (var p in Processes) map.TryAdd((p.ProcessId, p.CreateTime), p);
        return map;
    }
}
