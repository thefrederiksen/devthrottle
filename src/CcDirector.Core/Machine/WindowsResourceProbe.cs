using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;
using Microsoft.Win32.SafeHandles;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE WHOLE WINDOWS MACHINE, READ DIRECTLY (Resource Monitor mission, phase 1).
///
/// Processor, memory, page file, the stall proxies, the physical disks the slowness diagnosis reads, and
/// every process, in one <see cref="Read"/>. Built the way <see cref="MachineMemoryProbe"/> is: NO
/// SUBPROCESS - every figure is a direct call (see <see cref="WindowsResourceNative"/>), every structure
/// is decoded by a pure function (<see cref="WindowsResourceParsing"/>), and this class only makes the
/// calls and does the arithmetic between two cumulative readings, which is itself pure and static so it
/// is tested without Windows.
///
/// RATES NEED TWO READINGS. The first <see cref="Read"/> has nothing to subtract from, so its processor,
/// disk and stall figures are unreadable with <see cref="ResourceUnreadableReasons.FirstSample"/>. The
/// process list is NOT: every process is listed and named on the first read, with its memory, and only
/// its processor share and input/output rates null. Memory and the page file are levels and read at once.
///
/// INTERVALS ARE TIMED ON THE MONOTONIC CLOCK of the <see cref="TimeProvider"/>, never the wall clock,
/// so a clock that jumps - a time sync, a daylight saving change, a laptop waking - cannot produce a
/// negative or enormous rate, and cannot freeze the cache. The processor and the disks carry their own
/// kernel time base and need neither.
///
/// WHAT WINDOWS DOES NOT OFFER is null, never zero: the three stalled percentages (Linux pressure stall
/// information has no Windows equivalent) and the count of processes waiting on the disk. The stall
/// figures Windows does give - the run queue and hard page faults - are PROXIES, and the disk side of
/// the diagnosis is the per-disk latency in <see cref="DiskEvidenceSet"/>, never a stall percentage.
///
/// Never throws. Each part of the sample is read on its own, so one part that cannot be read comes back
/// unreadable with its reason and never hides the others; each such part is logged once.
/// </summary>
public sealed class WindowsResourceProbe : IResourceProbe
{
    /// <summary>How long a sample is reused. The same reasoning as <see cref="MachineMemoryProbe.CacheFor"/>.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly IMachineMemoryProbe _memory;
    private readonly HashSet<string> _loggedUnreadable = new(StringComparer.Ordinal);
    private readonly Dictionary<int, DiskKind> _diskKinds = new();

    private ResourceSample? _cached;
    private long _cachedAtTimestamp;
    private CpuTimes? _previousSystemTimes;
    private IReadOnlyList<CpuTimes>? _previousCores;
    private readonly Dictionary<int, DiskCounters> _previousDisks = new();
    private ProcessSnapshot? _previousProcesses;
    private PagingSnapshot? _previousPaging;
    private IReadOnlySet<int>? _systemVolumeDisks;
    private bool _systemVolumeDisksAsked;
    private int _processListSize = 1 << 20;

    /// <summary>
    /// Creates a probe. Memory is read by the existing probe from issue #2818, unchanged. The clock is
    /// injectable so a test can age the cache and set an interval without sleeping.
    /// </summary>
    public WindowsResourceProbe(TimeProvider? time = null, IMachineMemoryProbe? memory = null)
    {
        _time = time ?? TimeProvider.System;
        _memory = memory ?? MachineMemoryProbe.Shared;
    }

    /// <inheritdoc />
    public ResourceSample Read()
    {
        // One lock around the whole read: the previous cumulative readings are what the next rates are
        // computed from, so two threads interleaving here would each compute a rate over the other's
        // half-updated state. A read costs milliseconds and is cached for a second; nobody waits long.
        lock (_gate)
        {
            var timestamp = _time.GetTimestamp();
            if (_cached is not null && _time.GetElapsedTime(_cachedAtTimestamp, timestamp) < CacheFor)
                return _cached;

            var sample = ReadUncached(_time.GetUtcNow().UtcDateTime, timestamp);
            _cached = sample;
            _cachedAtTimestamp = timestamp;
            LogUnreadableParts(sample);
            return sample;
        }
    }

    private ResourceSample ReadUncached(DateTime now, long timestamp)
    {
        var memory = ReadMemory(now);

        var platformProblem = PlatformProblem();
        if (platformProblem is not null)
        {
            return new ResourceSample(now, CpuReading.Unreadable(platformProblem), memory,
                SwapReading.Unreadable(platformProblem), StallReading.Unreadable(platformProblem),
                DiskEvidenceSet.Unreadable(platformProblem), ProcessSetReading.Unreadable(platformProblem));
        }

        var cores = ReadLogicalCoreCount();
        var (processes, stall) = ReadProcessesAndStall(timestamp, cores);

        return new ResourceSample(now, ReadCpu(cores), memory, ReadSwap(), stall, ReadDiskEvidence(), processes);
    }

    /// <summary>
    /// The memory reading from <see cref="IMachineMemoryProbe"/>. The shipped probe never throws, but an
    /// injected one might, and a throw here would break this probe's own promise never to throw.
    /// </summary>
    private MachineMemoryReading ReadMemory(DateTime now)
    {
        try
        {
            return _memory.Read();
        }
        catch (Exception ex)
        {
            return MachineMemoryReading.Unreadable(Describe(ex), now);
        }
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

    /// <summary>Every logical processor in every group, or null - logged once - when Windows will not say.</summary>
    private int? ReadLogicalCoreCount()
    {
        try
        {
            return WindowsResourceNative.ReadLogicalProcessorCount();
        }
        catch (Exception ex)
        {
            LogFailureOnce("cores", $"the logical processor count could not be read, so no processor share can be given: {Describe(ex)}");
            return null;
        }
    }

    private CpuReading ReadCpu(int? cores)
    {
        try
        {
            var system = WindowsResourceNative.ReadSystemTimes();
            var perCore = WindowsResourceParsing.ParseProcessorPerformance(WindowsResourceNative.ReadProcessorPerformance());

            var reading = CpuBetween(_previousSystemTimes, system, _previousCores, perCore, cores);
            _previousSystemTimes = system;
            _previousCores = perCore;
            return reading;
        }
        catch (Exception ex)
        {
            return CpuReading.Unreadable(Describe(ex), cores);
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

    private DiskEvidenceSet ReadDiskEvidence()
    {
        try
        {
            var systemDisks = SystemVolumeDisks();
            var disks = new List<DiskEvidence>();
            var seen = new HashSet<int>();

            for (var number = 0; number <= WindowsResourceNative.MaxPhysicalDiskNumber; number++)
            {
                var name = DiskName(number);
                bool? inSystemVolume = systemDisks is null ? null : systemDisks.Contains(number);
                SafeFileHandle? handle;
                try
                {
                    handle = WindowsResourceNative.OpenPhysicalDisk(number);
                }
                catch (Exception ex)
                {
                    disks.Add(DiskEvidence.Unreadable(name, DiskKind.Unknown, inSystemVolume, Describe(ex)));
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
                        disks.Add(DiskBetween(name, kind, inSystemVolume, _previousDisks.TryGetValue(number, out var earlier) ? earlier : null, counters));
                        _previousDisks[number] = counters;
                    }
                    catch (Exception ex)
                    {
                        disks.Add(DiskEvidence.Unreadable(name, kind, inSystemVolume, Describe(ex)));
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

            return DiskEvidenceSet.Read(disks);
        }
        catch (Exception ex)
        {
            return DiskEvidenceSet.Unreadable(Describe(ex));
        }
    }

    /// <summary>
    /// The physical disks under the volume Windows runs from, asked once: a disk does not move between
    /// volumes while the machine runs. Null - "not known", never "no" - when the volume cannot say, which
    /// is logged once. This is what lets a diagnosis say a slow disk is the one the machine runs FROM.
    /// </summary>
    private IReadOnlySet<int>? SystemVolumeDisks()
    {
        if (_systemVolumeDisksAsked) return _systemVolumeDisks;
        _systemVolumeDisksAsked = true;

        var root = Path.GetPathRoot(Environment.SystemDirectory);
        try
        {
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
                throw new InvalidOperationException($"the system directory {Environment.SystemDirectory} is not on a lettered volume");

            using var volume = WindowsResourceNative.OpenVolume(root[0]);
            _systemVolumeDisks = WindowsResourceParsing.ParseVolumeDiskNumbers(WindowsResourceNative.ReadVolumeDiskExtents(volume)).ToHashSet();
        }
        catch (Exception ex)
        {
            LogNoteOnce("system-volume", $"cannot say which physical disks hold the system volume {root}, so no disk is marked as holding it: {Describe(ex)}");
            _systemVolumeDisks = null;
        }
        return _systemVolumeDisks;
    }

    /// <summary>
    /// Spinning or solid-state, asked once per disk and remembered: it does not change while the disk is
    /// attached. A disk that cannot answer the seek-penalty question - some USB bridges and virtual
    /// disks cannot - is Unknown, and logged once, never assumed to be either.
    /// </summary>
    private DiskKind KindOf(int number, SafeFileHandle handle)
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
            LogNoteOnce($"disk-kind:{number}", $"{DiskName(number)} cannot say whether it is spinning or solid-state, so its kind is Unknown: {Describe(ex)}");
            kind = DiskKind.Unknown;
        }

        _diskKinds[number] = kind;
        return kind;
    }

    /// <summary>
    /// The process list and the paging counter, read SEPARATELY so one failing never hides the other: a
    /// paging counter that cannot be read leaves the process list readable, and a process list that
    /// cannot be read still gives the hard page fault rate, with the run queue - which is counted from the
    /// process list - null.
    /// </summary>
    private (ProcessSetReading Processes, StallReading Stall) ReadProcessesAndStall(long timestamp, int? cores)
    {
        ProcessSnapshot? current = null;
        ProcessSetReading processes;
        try
        {
            var buffer = WindowsResourceNative.ReadProcessInformation(ref _processListSize, out var address);
            current = new ProcessSnapshot(timestamp, WindowsResourceParsing.ParseProcessInformation(buffer, address));
            var interval = _previousProcesses is null ? (TimeSpan?)null : _time.GetElapsedTime(_previousProcesses.Timestamp, timestamp);
            processes = ProcessesBetween(_previousProcesses, current, interval, cores);
        }
        catch (Exception ex)
        {
            processes = ProcessSetReading.Unreadable(Describe(ex));
        }
        // A list that could not be read leaves no earlier reading, so the next good one is a first sample
        // again rather than a rate over two lists read a long way apart.
        _previousProcesses = current;

        StallReading stall;
        PagingSnapshot? paging = null;
        try
        {
            paging = new PagingSnapshot(timestamp,
                WindowsResourceParsing.ParsePageReadCount(WindowsResourceNative.ReadSystemPerformance()));
            var pagingInterval = _previousPaging is null ? (TimeSpan?)null : _time.GetElapsedTime(_previousPaging.Timestamp, timestamp);
            stall = StallBetween(current, _previousPaging, paging, pagingInterval);
        }
        catch (Exception ex)
        {
            stall = StallReading.Unreadable(Describe(ex));
        }
        _previousPaging = paging;

        return (processes, stall);
    }

    // ---------------------------------------------------------------------------------------------
    // The arithmetic between two readings. Pure and static: tested without Windows.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The operating system's own name for physical disk N, as Disk Management and diskpart show it.</summary>
    internal static string DiskName(int number) => $"PhysicalDrive{number}";

    /// <summary>
    /// The processor from two readings: the whole machine from GetSystemTimes, the busiest core from the
    /// per-processor times. Null previous readings mean this is the first sample, which still carries
    /// the core count because that is a static fact, not a rate.
    ///
    /// The per-processor list covers the processor group the Director runs in, which is every processor
    /// on any machine with 64 or fewer; the whole-machine figure from GetSystemTimes and the core count
    /// cover all of them regardless.
    /// </summary>
    internal static CpuReading CpuBetween(CpuTimes? earlierSystem, CpuTimes laterSystem, IReadOnlyList<CpuTimes>? earlierCores, IReadOnlyList<CpuTimes> laterCores, int? logicalCoreCount)
    {
        if (earlierSystem is null || earlierCores is null) return CpuReading.Unreadable(ResourceUnreadableReasons.FirstSample, logicalCoreCount);

        var total = WindowsResourceParsing.BusyFraction(earlierSystem.Value, laterSystem);
        if (total is null) return CpuReading.Unreadable("no processor time passed between the two readings, or a counter went backwards", logicalCoreCount);

        // Processors that appear in both readings. One added while running is counted from its second
        // reading on; until then there is nothing to subtract from.
        double? busiest = null;
        for (var i = 0; i < Math.Min(earlierCores.Count, laterCores.Count); i++)
        {
            var core = WindowsResourceParsing.BusyFraction(earlierCores[i], laterCores[i]);
            if (core is { } value && (busiest is null || value > busiest)) busiest = value;
        }

        return CpuReading.Read(total.Value * 100, busiest * 100, logicalCoreCount);
    }

    /// <summary>One physical disk from two readings of its counters. A null previous reading means first sample.</summary>
    internal static DiskEvidence DiskBetween(string name, DiskKind kind, bool? isInSystemVolume, DiskCounters? earlier, DiskCounters later)
    {
        if (earlier is null) return DiskEvidence.Unreadable(name, kind, isInSystemVolume, ResourceUnreadableReasons.FirstSample);

        var rates = WindowsResourceParsing.DiskRatesBetween(earlier.Value, later);
        if (rates is null)
            return DiskEvidence.Unreadable(name, kind, isInSystemVolume, "no time passed between the two readings of this disk, or its counters went backwards");

        var r = rates.Value;
        return DiskEvidence.Read(name, kind, isInSystemVolume, r.BusyFraction * 100,
            r.SecondsPerRead * 1000, r.SecondsPerWrite * 1000, r.AverageQueueLength);
    }

    /// <summary>
    /// Every process, named, with its memory - and, once there is an interval, its processor share and
    /// input/output rates.
    ///
    /// THE FIRST READ IS STILL A LIST. A share is a rate and has no interval yet, but a process's name,
    /// parent, start time and memory are readable now, so <paramref name="earlier"/> null gives every
    /// process with those and the rates null.
    ///
    /// A process is the same process only when its id AND its creation time match: ids are reused, and a
    /// new process under an old id must not be charged the difference between the two. A process that is
    /// not in the earlier list started during the interval and, as <see cref="ProcessReading.CpuPercent"/>
    /// requires, has its rates null: it has no earlier reading to compare against. A counter that went
    /// backwards gives null, never zero.
    ///
    /// The System Idle Process (id 0) is left out: its "processor time" is the time nothing ran, and
    /// listed as a process it would top every list of what is using the processor.
    ///
    /// THE SHARE IS OF THE WHOLE MACHINE (<see cref="ResourceRate.ShareOfMachine"/>), over
    /// <paramref name="logicalCoreCount"/> - every processor in every group - so one process spinning one
    /// core of 24 reads about 4%.
    ///
    /// ON WINDOWS THE INPUT/OUTPUT FIGURES ARE EVERY READ AND WRITE THE PROCESS MAKES - files, devices and
    /// the network, including reads served from the file cache - not only bytes that reached a physical
    /// disk. Windows keeps no per-process count of physical disk bytes.
    /// </summary>
    internal static ProcessSetReading ProcessesBetween(ProcessSnapshot? earlier, ProcessSnapshot later, TimeSpan? interval, int? logicalCoreCount)
    {
        var before = earlier?.ByIdentity();
        var readings = new List<ProcessReading>(later.Processes.Count);

        foreach (var p in later.Processes)
        {
            if (p.ProcessId == 0) continue;

            double? cpu = null, read = null, write = null;
            if (before is not null && interval is { } span && before.TryGetValue((p.ProcessId, p.CreateTime), out var then))
            {
                cpu = ResourceRate.ShareOfMachine(
                    TimeSpan.FromTicks(then.KernelTime + then.UserTime),
                    TimeSpan.FromTicks(p.KernelTime + p.UserTime),
                    span, logicalCoreCount);
                read = ResourceRate.PerSecond(then.ReadBytes, p.ReadBytes, span);
                write = ResourceRate.PerSecond(then.WriteBytes, p.WriteBytes, span);
            }

            readings.Add(new ProcessReading(
                Pid: p.ProcessId,
                ParentPid: p.ParentProcessId,
                Name: p.Name ?? $"process {p.ProcessId}",
                StartTimeUtc: p.CreateTime > 0 ? DateTime.FromFileTimeUtc(p.CreateTime) : null,
                CpuPercent: cpu,
                MemoryBytes: p.WorkingSetBytes,
                DiskReadBytesPerSecond: read,
                DiskWriteBytesPerSecond: write));
        }

        return ProcessSetReading.Read(readings);
    }

    /// <summary>
    /// The stall proxies Windows offers. NEITHER IS A MEASURE OF HOW LONG PROGRAMS WERE STALLED, which is
    /// what Linux pressure stall information gives directly:
    ///
    /// RUN QUEUE: the threads ON a processor now PLUS those waiting for one - the quantity
    /// <see cref="StallReading.RunQueueLength"/> is defined as, so that it means the same thing here as
    /// the figure Linux reads from <c>/proc/loadavg</c>. See
    /// <see cref="WindowsResourceParsing.WantsAProcessor"/> for the thread states and the measurement
    /// behind them. It is NOT what the "System\Processor Queue Length" performance counter counts - that
    /// counter is documented as ready threads only - and this probe does not read that counter at all.
    /// It is ONE MOMENT, not an average: on a five-second sample it is that moment's luck, so it can only
    /// support a processor verdict alongside a busy processor, never make one alone. Null when the process
    /// list could not be read (<paramref name="processes"/> null).
    ///
    /// THE SYSTEM IDLE PROCESS (id 0) IS LEFT OUT, the same rule and for the same reason as
    /// <see cref="ProcessesBetween"/>: an idle thread is "Running" on a processor exactly when nothing
    /// else is, which is the OPPOSITE of demand for one, and Linux's runnable count never includes the
    /// idle task. In the capture committed with this probe's tests, process id 0 held 23 Running and 25
    /// Standby threads out of a machine total of 107 - so counting it would have reported 107 where the
    /// real figure is 59, and would have made a completely idle 24-processor machine read one to two
    /// threads of demand per core for ever.
    ///
    /// HARD PAGE FAULTS PER SECOND: pages the whole machine read back from disk to satisfy hard faults,
    /// over the interval - the arithmetic of "Memory\Pages Input/sec", on the same kernel counter. It
    /// also counts ordinary reads of memory-mapped files (a program starting), so it says memory is
    /// short only alongside little available memory.
    ///
    /// The stalled percentages and the count of processes waiting on the disk are not offered by Windows
    /// and stay null. The disk side of the diagnosis reads each disk's time per transfer and queue depth
    /// from <see cref="DiskEvidenceSet"/>.
    /// </summary>
    internal static StallReading StallBetween(ProcessSnapshot? processes, PagingSnapshot? earlierPaging, PagingSnapshot laterPaging, TimeSpan? interval)
    {
        if (earlierPaging is null || interval is null) return StallReading.Unreadable(ResourceUnreadableReasons.FirstSample);
        if (interval.Value <= TimeSpan.Zero) return StallReading.Unreadable("no time passed between the two readings of the paging counters");

        // A 32-bit counter that only grows: subtracted unchecked, so a wrap past four billion still gives
        // the true growth. Unlike a reset, a wrap is not a step backwards.
        var pagesRead = unchecked(laterPaging.PageReadCount - earlierPaging.PageReadCount);

        return StallReading.Read(StallSource.WindowsCounters,
            runQueueLength: processes?.Processes
                .Where(p => p.ProcessId != 0)
                .Sum(p => p.ThreadsWantingAProcessor),
            hardPageFaultsPerSecond: pagesRead / interval.Value.TotalSeconds);
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
        LogIfUnreadable("memory", sample.Memory.CouldRead, sample.Memory.UnreadableReason);
        LogIfUnreadable("cpu", sample.Cpu.CouldRead, sample.Cpu.UnreadableReason);
        LogIfUnreadable("swap", sample.Swap.CouldRead, sample.Swap.UnreadableReason);
        LogIfUnreadable("disks", sample.DiskEvidence.CouldRead, sample.DiskEvidence.UnreadableReason);
        foreach (var disk in sample.DiskEvidence.Disks) LogIfUnreadable($"disk:{disk.Name}", disk.CouldRead, disk.UnreadableReason);
        LogIfUnreadable("stall", sample.Stall.CouldRead, sample.Stall.UnreadableReason);
        LogIfUnreadable("processes", sample.Processes.CouldRead, sample.Processes.UnreadableReason);
    }

    private void LogIfUnreadable(string part, bool couldRead, string? reason)
    {
        if (couldRead || reason == ResourceUnreadableReasons.FirstSample) return;
        LogFailureOnce(part, $"{part} could not be read: {reason}");
    }

    /// <summary>A reading that should have worked and did not. Written as a failure, so it is reported.</summary>
    private void LogFailureOnce(string key, string message)
    {
        if (!_loggedUnreadable.Add(key)) return;
        FileLog.Write($"[WindowsResourceProbe] Read FAILED, and will not be reported again: {message}");
    }

    /// <summary>
    /// A fact some machines simply do not offer - a USB bridge or a virtual disk that cannot say whether it
    /// spins, a system volume the extents query does not cover. Expected, so written WITHOUT the failure
    /// word: on such a machine it would otherwise file an error report every time the Director starts.
    /// </summary>
    private void LogNoteOnce(string key, string message)
    {
        if (!_loggedUnreadable.Add(key)) return;
        FileLog.Write($"[WindowsResourceProbe] Read: not offered on this machine, and will not be repeated: {message}");
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}

/// <summary>The machine's cumulative pages-read-from-disk count at one moment, with the monotonic timestamp it was read at.</summary>
internal sealed record PagingSnapshot(long Timestamp, uint PageReadCount);

/// <summary>Every process at one moment, with the monotonic timestamp the list was read at.</summary>
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
