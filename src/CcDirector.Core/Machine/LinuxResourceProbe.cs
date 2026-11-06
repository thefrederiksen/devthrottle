using System.Globalization;
using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE LINUX MACHINE, READ FROM /proc AND /sys (the Resource Monitor mission, issue #2811).
///
/// What it reads: the processor (the whole machine and the busiest core), memory and swap, the stall
/// readings from the kernel's own pressure files, each physical disk's evidence for the slowness
/// diagnosis, and every process. It reads no disk meter, no free space and no network throughput: the
/// owner narrowed the meter to memory and the processor on 2026-11-06, and the disk survives only as
/// evidence the diagnosis puts into a sentence.
///
/// NO SUBPROCESS IS STARTED. Every figure comes from a file the kernel writes - <c>/proc/stat</c>,
/// <c>/proc/meminfo</c>, <c>/proc/diskstats</c>, <c>/proc/pressure/*</c>, <c>/proc/loadavg</c>,
/// <c>/proc/self/mountinfo</c>, <c>/proc/&lt;pid&gt;/*</c> and <c>/sys/block</c>. This is read every
/// five seconds, and often on a machine that is already struggling; starting <c>iostat</c> or <c>ps</c>
/// would add to the very load it measures.
///
/// THE STALL FIGURES ARE A DIRECT MEASURE HERE. <c>/proc/pressure/io</c> says how much of the time some
/// program was frozen waiting on the disk. It is a figure for the whole machine, so it cannot say WHICH
/// disk; the per-disk evidence (busy, milliseconds per operation, queue depth, and whether the disk backs
/// the root volume) is what lets the diagnosis name one.
///
/// THE TEXT PARSING IS NOT HERE. It is in <see cref="LinuxProcParsing"/>, pure functions tested from
/// files captured on devlinux. This class does the reading, and every rate goes through
/// <see cref="ResourceRate"/>, so a counter that steps backwards gives null, never zero.
///
/// EACH PART FAILS ON ITS OWN. A kernel without <c>/proc/pressure</c> still has a readable processor; a
/// process whose <c>io</c> file is closed to us is still listed with its processor and memory. A part
/// that cannot be read says so with a plain-English reason and is logged once - never zero, never a guess.
///
/// THE FIRST READ HAS NO INTERVAL. The processor and the disk figures are unreadable with
/// <see cref="ResourceUnreadableReasons.FirstSample"/>; the process list is still returned, every process
/// named, with its processor share and disk rates null. Every interval is measured on the monotonic clock
/// of the <see cref="TimeProvider"/>; wall-clock time only stamps the sample.
///
/// LOGGING: <see cref="Read"/> runs every five seconds, so - like <see cref="MachineMemoryProbe"/> - it
/// logs each part that cannot be read once, with its reason, rather than an entry and exit line per read.
/// </summary>
public sealed class LinuxResourceProbe : IResourceProbe
{
    /// <summary>
    /// The shared probe. One instance, so every caller shares the one-second cache and the previous
    /// counters the rates are computed against, and so each "cannot read" line is logged once.
    /// </summary>
    public static readonly LinuxResourceProbe Shared = new();

    /// <summary>How long a sample is reused. The same reasoning as <see cref="MachineMemoryProbe.CacheFor"/>.</summary>
    public static readonly TimeSpan CacheFor = MachineMemoryProbe.CacheFor;

    private readonly string _procRoot;
    private readonly string _sysBlockRoot;
    private readonly TimeProvider _time;
    private readonly Func<long> _clockTicksPerSecond;
    private readonly long _pageSize;
    private readonly Func<string, string> _readProcessFile;

    // One read at a time: each read moves the previous counters on, so two reads interleaving would
    // compute rates against each other's halves. A second caller waits and then gets the cached sample.
    private readonly object _gate = new();
    private readonly HashSet<string> _loggedUnreadable = new(StringComparer.Ordinal);
    private ResourceSample? _cached;
    private long _cachedAtTimestamp;

    private LinuxCpuTimes? _previousCpu;
    private (Dictionary<string, LinuxDiskCounters> Disks, long AtTimestamp)? _previousDisks;
    private (Dictionary<(int Pid, ulong StartTicks), ProcessCounters> Processes, long AtTimestamp)? _previousProcesses;

    private readonly record struct ProcessCounters(ulong CpuTicks, (ulong ReadBytes, ulong WriteBytes)? Io);

    /// <summary>Creates a probe of this machine.</summary>
    public LinuxResourceProbe()
        : this("/proc", "/sys/block", time: null, clockTicksPerSecond: null, pageSize: null, readProcessFile: null)
    {
    }

    /// <summary>
    /// Creates a probe over any copy of <c>/proc</c> and <c>/sys/block</c>, so the whole probe - rates,
    /// first sample, unreadable parts - is tested against files captured on a real machine. The clock,
    /// the clock tick rate and the page size are injectable for the same reason, and so is the read of
    /// one process's files, so a process the kernel REFUSES (a /proc mounted with hidepid) can be tested
    /// without a real permission.
    /// </summary>
    internal LinuxResourceProbe(
        string procRoot,
        string sysBlockRoot,
        TimeProvider? time,
        Func<long>? clockTicksPerSecond,
        long? pageSize,
        Func<string, string>? readProcessFile = null)
    {
        _procRoot = procRoot;
        _sysBlockRoot = sysBlockRoot;
        _time = time ?? TimeProvider.System;
        _clockTicksPerSecond = clockTicksPerSecond ?? ReadClockTicksPerSecond;
        _pageSize = pageSize ?? Environment.SystemPageSize;
        _readProcessFile = readProcessFile ?? File.ReadAllText;
    }

    /// <inheritdoc />
    public ResourceSample Read()
    {
        lock (_gate)
        {
            var timestamp = _time.GetTimestamp();
            if (_cached is { } cached && _time.GetElapsedTime(_cachedAtTimestamp, timestamp) < CacheFor) return cached;

            var sample = ReadUncached(_time.GetUtcNow().UtcDateTime, timestamp);
            _cached = sample;
            _cachedAtTimestamp = timestamp;
            LogUnreadableOnce(sample);
            return sample;
        }
    }

    private ResourceSample ReadUncached(DateTime now, long timestamp)
    {
        var (cpu, cpuTimes) = Guard("processor", ReadCpu, r => (CpuReading.Unreadable(r), null));
        var coreCount = cpuTimes?.Cores.Count is > 0 and var count ? count : (int?)null;

        // Memory and swap come from the one file, read once. Memory is parsed by the existing memory
        // probe's own parser, so the meter and every memory-aware deadline agree on the figure.
        var memInfo = Guard("memory", () => (Lines: File.ReadAllLines(ProcPath("meminfo")), Error: (string?)null),
            r => (Lines: Array.Empty<string>(), Error: r));
        var memory = memInfo.Error is { } memoryError
            ? MachineMemoryReading.Unreadable(memoryError, now)
            : MachineMemoryProbe.ParseProcMemInfo(memInfo.Lines, now);
        var swap = memInfo.Error is { } swapError
            ? SwapReading.Unreadable(swapError)
            : Guard("swap", () => ReadSwap(memInfo.Lines), SwapReading.Unreadable);

        var disks = Guard("disks", ReadDisks, DiskEvidenceSet.Unreadable);
        var processes = Guard("processes", () => ReadProcesses(cpuTimes, coreCount), ProcessSetReading.Unreadable);
        // The kernel's own count of every task - every thread - blocked on input and output, from
        // /proc/stat. Counting processes whose main thread is in state D would miss the worker threads
        // of exactly the multi-threaded programs the diagnosis exists to name.
        var stall = Guard("stalls", () => ReadStall(cpuTimes?.ProcessesBlocked), StallReading.Unreadable);

        return new ResourceSample(now, cpu, memory, swap, stall, disks, processes);
    }

    /// <summary>
    /// The boundary to the operating system. A file that is not there, a permission refused, or a
    /// structure whose shape has changed under us comes back as an unreadable part, never an exception
    /// out of <see cref="Read"/> - the interface promises it never throws.
    /// </summary>
    private static T Guard<T>(string part, Func<T> read, Func<string, T> unreadable)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return unreadable($"reading the {part} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string ProcPath(params string[] parts) => Path.Combine([_procRoot, .. parts]);

    // ---------------------------------------------------------------------------------------------
    // Processor
    // ---------------------------------------------------------------------------------------------

    private (CpuReading, LinuxCpuTimes?) ReadCpu()
    {
        var path = ProcPath("stat");
        var times = LinuxProcParsing.ParseCpuTimes(File.ReadAllLines(path));
        if (times is null) return (CpuReading.Unreadable($"{path} carried no aggregate cpu line"), null);

        var cores = times.Cores.Count > 0 ? times.Cores.Count : (int?)null;
        var ticksPerSecond = _clockTicksPerSecond();
        if (ticksPerSecond <= 0) return (CpuReading.Unreadable($"the clock tick rate read as {ticksPerSecond}", cores), times);
        var previous = _previousCpu;
        _previousCpu = times;
        if (previous is null) return (CpuReading.Unreadable(ResourceUnreadableReasons.FirstSample, cores), times);

        if (BusyPercent(previous.Machine, times.Machine, ticksPerSecond) is not { } total)
            return (CpuReading.Unreadable(
                "the processor's tick counters did not move forward since the previous sample", cores), times);

        // The busiest core is only offered when every core lines up with the previous sample: a core
        // brought online or taken offline in between has no honest difference to compare.
        double? busiest = null;
        if (times.Cores.Count == previous.Cores.Count)
        {
            for (var i = 0; i < times.Cores.Count; i++)
            {
                if (BusyPercent(previous.Cores[i], times.Cores[i], ticksPerSecond) is { } core && (busiest is null || core > busiest)) busiest = core;
            }
        }

        return (CpuReading.Read(total, busiest, cores), times);
    }

    /// <summary>
    /// The busy share of the ticks that passed between two readings, 0 to 100, through
    /// <see cref="ResourceRate.BusyPercent"/>: ticks spent working over all ticks that passed. Null when
    /// the busy counter stepped backwards or no ticks passed. Only the DIFFERENCE in ticks is turned into
    /// time, divided by the tick rate: the whole machine's total grows by the tick rate for every logical
    /// core every second, and on a large machine up for months the raw count, handed over as seconds,
    /// would overflow a TimeSpan.
    /// </summary>
    private static double? BusyPercent(LinuxCpuTicks before, LinuxCpuTicks now, long ticksPerSecond)
    {
        // The kernel's per-core iowait counter is documented to step backwards now and then, which can
        // pull the total below the previous one: there is then no interval to divide by.
        if (now.Total <= before.Total) return null;
        if (now.Busy < before.Busy) return null; // a counter that steps backwards: null, never zero
        // Subtract the whole tick counts first and convert only the difference: the totals are large
        // enough that converting each to seconds and then subtracting would lose precision.
        return ResourceRate.BusyPercent(
            TimeSpan.Zero, TicksToTime(now.Busy - before.Busy, ticksPerSecond), TicksToTime(now.Total - before.Total, ticksPerSecond));
    }

    // ---------------------------------------------------------------------------------------------
    // Swap
    // ---------------------------------------------------------------------------------------------

    private SwapReading ReadSwap(string[] memInfoLines)
    {
        var swap = LinuxProcParsing.ParseSwap(memInfoLines);
        return swap is { } read
            ? SwapReading.Read(read.TotalBytes, read.UsedBytes)
            : SwapReading.Unreadable($"{ProcPath("meminfo")} carried no readable SwapTotal and SwapFree");
    }

    // ---------------------------------------------------------------------------------------------
    // Disk evidence, for the slowness diagnosis only
    // ---------------------------------------------------------------------------------------------

    private DiskEvidenceSet ReadDisks()
    {
        var devices = LinuxProcParsing.ReadBlockDevices(_sysBlockRoot);
        var counters = LinuxProcParsing.ParseDiskStats(File.ReadAllLines(ProcPath("diskstats")));
        // Stamped beside the read of the counters it measures, not at the start of the sample: the
        // processor and the /sys walk before it take a varying time on a loaded machine.
        var timestamp = _time.GetTimestamp();
        var byName = new Dictionary<string, LinuxDiskCounters>(StringComparer.Ordinal);
        foreach (var counter in counters) byName.TryAdd(counter.Name, counter);

        var previous = _previousDisks;
        _previousDisks = (byName, timestamp);
        var rootDisks = Guard("system volume", () => ReadRootDisks(devices), reason =>
        {
            LogOnce("system volume", couldRead: false, reason);
            return null;
        });

        var disks = devices.Values
            .Where(LinuxProcParsing.IsPhysicalDisk)
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(device => ReadDiskEvidence(device, rootDisks, byName, previous, timestamp))
            .ToList();

        return DiskEvidenceSet.Read(disks);
    }

    /// <summary>
    /// The physical disks behind <c>/</c>, or null when that cannot be told - which makes every disk's
    /// place in the system volume null, never false. The reason is logged once.
    /// </summary>
    private IReadOnlyList<string>? ReadRootDisks(IReadOnlyDictionary<string, LinuxBlockDevice> devices)
    {
        var path = ProcPath("self", "mountinfo");
        var root = LinuxProcParsing.ParseRootMount(File.ReadAllLines(path));
        if (root is null)
        {
            LogOnce("system volume", couldRead: false, $"{path} has no mount at /");
            return null;
        }

        var disks = LinuxProcParsing.ResolveRootDisks(root, devices);
        if (disks is null)
            LogOnce("system volume", couldRead: false,
                $"the device mounted at / ({root.DeviceNumber.Major}:{root.DeviceNumber.Minor}, {root.Source}) is not in {_sysBlockRoot}");
        return disks;
    }

    private DiskEvidence ReadDiskEvidence(
        LinuxBlockDevice device,
        IReadOnlyList<string>? rootDisks,
        Dictionary<string, LinuxDiskCounters> current,
        (Dictionary<string, LinuxDiskCounters> Disks, long AtTimestamp)? previous,
        long timestamp)
    {
        var kind = device.Rotational switch
        {
            1 => DiskKind.Spinning,
            0 => DiskKind.SolidState,
            _ => DiskKind.Unknown,
        };
        bool? inSystemVolume = rootDisks is null ? null : rootDisks.Contains(device.Name, StringComparer.Ordinal);

        if (!current.TryGetValue(device.Name, out var now))
            return DiskEvidence.Unreadable(device.Name, kind, inSystemVolume, $"{device.Name} is not listed in {ProcPath("diskstats")}");
        if (previous is not { } before || !before.Disks.TryGetValue(device.Name, out var then))
            return DiskEvidence.Unreadable(device.Name, kind, inSystemVolume, ResourceUnreadableReasons.FirstSample);

        var interval = _time.GetElapsedTime(before.AtTimestamp, timestamp);
        if (interval <= TimeSpan.Zero)
            return DiskEvidence.Unreadable(device.Name, kind, inSystemVolume, "no time passed since the previous sample");

        // A disk removed and added again restarts every counter at zero. Nothing about this interval can
        // then be told honestly, so the whole disk says so rather than offering partial figures.
        if (now.MillisecondsBusy < then.MillisecondsBusy || now.ReadsCompleted < then.ReadsCompleted
            || now.WritesCompleted < then.WritesCompleted || now.MillisecondsReading < then.MillisecondsReading
            || now.MillisecondsWriting < then.MillisecondsWriting || now.WeightedMillisecondsBusy < then.WeightedMillisecondsBusy)
            return DiskEvidence.Unreadable(device.Name, kind, inSystemVolume,
                "its counters went backwards since the previous sample: a counter wrapped (the kernel keeps the " +
                "millisecond counters in 32 bits, so they wrap every few days on a busy disk) or the disk was replaced");

        // A disk that completed no reads in the interval has no time per read - there was nothing to
        // average. That is null, not zero: zero would claim reads that took no time at all.
        return DiskEvidence.Read(
            device.Name,
            kind,
            inSystemVolume,
            busyPercent: ResourceRate.BusyPercent(
                TimeSpan.FromMilliseconds(then.MillisecondsBusy), TimeSpan.FromMilliseconds(now.MillisecondsBusy), interval),
            millisecondsPerRead: ResourceRate.PerOperation(then.MillisecondsReading, now.MillisecondsReading, then.ReadsCompleted, now.ReadsCompleted),
            millisecondsPerWrite: ResourceRate.PerOperation(then.MillisecondsWriting, now.MillisecondsWriting, then.WritesCompleted, now.WritesCompleted),
            // The average number of requests in the disk's queue over the interval - the figure iostat
            // prints as aqu-sz - from the weighted milliseconds spent doing input and output.
            averageQueueDepth: ResourceRate.PerSecond(then.WeightedMillisecondsBusy, now.WeightedMillisecondsBusy, interval) / 1000);
    }

    // ---------------------------------------------------------------------------------------------
    // Stalls
    // ---------------------------------------------------------------------------------------------

    private StallReading ReadStall(int? processesWaitingOnDisk)
    {
        var pressure = ProcPath("pressure");
        if (!Directory.Exists(pressure))
            return StallReading.Unreadable(
                $"{pressure} does not exist: this kernel offers no pressure stall information " +
                "(Linux older than 4.20, a kernel built without it, or a container that hides it)");

        var cpu = ReadPressure(Path.Combine(pressure, "cpu"));
        var memory = ReadPressure(Path.Combine(pressure, "memory"));
        var io = ReadPressure(Path.Combine(pressure, "io"));

        var loadAverage = ProcPath("loadavg");
        var loadText = File.ReadAllText(loadAverage);
        var running = LinuxProcParsing.ParseLoadAverageRunning(loadText);
        var oneMinute = LinuxProcParsing.ParseLoadAverageOneMinute(loadText);
        if (running is null || oneMinute is null)
            return StallReading.Unreadable($"{loadAverage} carried no running-task count or no one-minute load average");

        // The running count is the kernel's "currently runnable" figure, which includes tasks on a
        // processor now as well as those waiting for one, and includes this reader itself.
        return StallReading.Read(
            StallSource.LinuxPressureStall,
            cpuStalledPercent: cpu,
            memoryStalledPercent: memory,
            ioStalledPercent: io,
            runQueueLength: running,
            loadAverageOneMinute: oneMinute,
            processesWaitingOnDisk: processesWaitingOnDisk);
    }

    /// <summary>
    /// One pressure file's "some avg10". A file that is there but cannot be read means pressure stall
    /// information is compiled in and switched off (the psi=0 boot option), which the reason says.
    /// </summary>
    private static double ReadPressure(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new IOException($"{path} could not be read ({ex.Message}); pressure stall information may be switched off on this kernel (psi=0)", ex);
        }

        return LinuxProcParsing.ParsePressureSomeAverage10(text)
               ?? throw new InvalidDataException($"{path} carried no 'some avg10' figure");
    }

    // ---------------------------------------------------------------------------------------------
    // Processes
    // ---------------------------------------------------------------------------------------------

    private ProcessSetReading ReadProcesses(LinuxCpuTimes? cpuTimes, int? coreCount)
    {
        var ticksPerSecond = _clockTicksPerSecond();
        if (ticksPerSecond <= 0) return ProcessSetReading.Unreadable($"the clock tick rate read as {ticksPerSecond}");

        var bootTime = cpuTimes?.BootTimeUnixSeconds is { } boot ? DateTime.UnixEpoch.AddSeconds(boot) : (DateTime?)null;
        var directories = Directory.EnumerateDirectories(_procRoot);
        // Stamped where the enumeration starts, beside the counters it measures rather than at the start
        // of the sample, so the time spent on the processor, memory and disks first is not in the interval.
        var timestamp = _time.GetTimestamp();
        var previous = _previousProcesses;
        var interval = previous is { } p ? _time.GetElapsedTime(p.AtTimestamp, timestamp) : TimeSpan.Zero;
        var counters = new Dictionary<(int, ulong), ProcessCounters>();
        var readings = new List<ProcessReading>();
        var refused = 0;

        foreach (var directory in directories)
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            var process = ReadOneProcess(directory);
            if (process.Exited) continue;

            if (process.Stat is not { } stat)
            {
                // RUNNING, BUT THE KERNEL REFUSED ITS STAT - /proc mounted with hidepid=1, as on hardened
                // servers and some container runtimes, hides other users' processes this way. It is
                // listed by its id with every figure null - never dropped, never given a guess - and the
                // count is logged, so the diagnosis can at least say the machine holds processes it cannot see.
                refused++;
                readings.Add(new ProcessReading(pid, null, $"process {pid} (refused by the system)", null, null, null, null, null));
                continue;
            }

            // A process is its id AND its start time: ids are reused, and without the start time a new
            // process would inherit a dead one's counters.
            var key = (stat.ProcessId, stat.StartTimeTicks);
            counters[key] = new ProcessCounters(stat.CpuTicks, process.Io);

            // Rates need this same process in the previous sample. One that is new since then - started
            // during the interval, or not readable last time - is still listed and named, with its rates
            // null: never left out, and never given a guessed figure.
            double? cpuPercent = null, readRate = null, writeRate = null;
            if (previous is { } before && before.Processes.TryGetValue(key, out var then))
            {
                // The difference in whole ticks, converted alone; a counter that stepped backwards is null.
                cpuPercent = stat.CpuTicks < then.CpuTicks
                    ? null
                    : ResourceRate.ShareOfMachine(TimeSpan.Zero, TicksToTime(stat.CpuTicks - then.CpuTicks, ticksPerSecond), interval, coreCount);
                if (process.Io is { } nowIo && then.Io is { } thenIo)
                {
                    readRate = ResourceRate.PerSecond(thenIo.ReadBytes, nowIo.ReadBytes, interval);
                    writeRate = ResourceRate.PerSecond(thenIo.WriteBytes, nowIo.WriteBytes, interval);
                }
            }

            readings.Add(new ProcessReading(
                stat.ProcessId,
                stat.ParentProcessId,
                stat.Name,
                bootTime?.AddSeconds((double)stat.StartTimeTicks / ticksPerSecond),
                cpuPercent,
                process.ResidentPages is { } pages ? pages * (ulong)_pageSize : null,
                readRate,
                writeRate));
        }

        _previousProcesses = (counters, timestamp);
        if (refused > 0)
            LogOnce("processes refused", couldRead: false,
                $"the system refused to show {refused} running processes (is /proc mounted with hidepid?); they are listed by id with no figures");
        return ProcessSetReading.Read(readings);
    }

    private static TimeSpan TicksToTime(ulong ticks, long ticksPerSecond) => TimeSpan.FromSeconds((double)ticks / ticksPerSecond);

    /// <summary>What could be read of one process directory.</summary>
    /// <param name="Exited">The process exited between listing and reading - normal on a busy machine. Nothing to list.</param>
    /// <param name="Stat">Its stat, or null when the kernel refused it (the process is running but hidden from us).</param>
    /// <param name="ResidentPages">Its resident pages, or null when statm could not be read.</param>
    /// <param name="Io">Its disk counters, or null when the io file is closed to us (another user's process).</param>
    private readonly record struct OneProcess(bool Exited, LinuxProcessStat? Stat, ulong? ResidentPages, (ulong ReadBytes, ulong WriteBytes)? Io);

    /// <summary>
    /// One process's stat, resident pages and disk counters. The two failures of the stat read mean
    /// different things and are kept apart: a file or directory that is gone, or a read the kernel
    /// ends with "no such process", means the process EXITED, and it is left out; a refused read
    /// means it is RUNNING but hidden from us, and it is listed. A statm that cannot be read gives null
    /// memory, and an io file closed to us gives null disk counters; the process is still listed.
    /// </summary>
    private OneProcess ReadOneProcess(string directory)
    {
        LinuxProcessStat? stat;
        try
        {
            stat = LinuxProcParsing.ParseProcessStat(_readProcessFile(Path.Combine(directory, "stat")));
        }
        catch (UnauthorizedAccessException)
        {
            return new OneProcess(Exited: false, Stat: null, ResidentPages: null, Io: null);
        }
        catch (IOException)
        {
            return new OneProcess(Exited: true, null, null, null);
        }

        // A stat that cannot be parsed is not a process we can name; it is treated as gone rather than
        // listed with a made-up name.
        if (stat is null) return new OneProcess(Exited: true, null, null, null);

        ulong? residentPages;
        try
        {
            residentPages = LinuxProcParsing.ParseStatmResidentPages(_readProcessFile(Path.Combine(directory, "statm")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            residentPages = null;
        }

        (ulong, ulong)? io;
        try
        {
            io = LinuxProcParsing.ParseProcessIo(_readProcessFile(Path.Combine(directory, "io")).Split('\n'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            io = null;
        }

        return new OneProcess(Exited: false, stat, residentPages, io);
    }

    /// <summary>
    /// The kernel's clock tick rate (USER_HZ), which every tick count in <c>/proc/&lt;pid&gt;/stat</c> is
    /// in. Almost always 100, but read rather than assumed.
    /// </summary>
    private static long ReadClockTicksPerSecond() => sysconf(ScClockTicks);

    /// <summary>_SC_CLK_TCK, the same value in glibc and musl.</summary>
    private const int ScClockTicks = 2;

    [DllImport("libc", SetLastError = true)]
    private static extern long sysconf(int name);

    // ---------------------------------------------------------------------------------------------
    // Logging
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Each part that cannot be read is logged the first time only, with its reason. The first sample's
    /// missing rates are expected and not logged.
    /// </summary>
    private void LogUnreadableOnce(ResourceSample sample)
    {
        LogOnce("processor", sample.Cpu.CouldRead, sample.Cpu.UnreadableReason);
        LogOnce("memory", sample.Memory.CouldRead, sample.Memory.UnreadableReason);
        LogOnce("swap", sample.Swap.CouldRead, sample.Swap.UnreadableReason);
        LogOnce("disks", sample.DiskEvidence.CouldRead, sample.DiskEvidence.UnreadableReason);
        foreach (var disk in sample.DiskEvidence.Disks) LogOnce($"disk {disk.Name}", disk.CouldRead, disk.UnreadableReason);
        LogOnce("stalls", sample.Stall.CouldRead, sample.Stall.UnreadableReason);
        LogOnce("processes", sample.Processes.CouldRead, sample.Processes.UnreadableReason);
    }

    private void LogOnce(string part, bool couldRead, string? reason)
    {
        if (couldRead || reason == ResourceUnreadableReasons.FirstSample) return;
        if (!_loggedUnreadable.Add(part)) return;
        FileLog.Write($"[LinuxResourceProbe] Read FAILED for {part}, and will not be reported again: {reason}");
    }
}
