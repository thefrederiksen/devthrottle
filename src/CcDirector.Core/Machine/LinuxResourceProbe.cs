using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE WHOLE LINUX MACHINE, READ FROM /proc AND /sys (the Resource Monitor mission, issue #2811).
///
/// NO SUBPROCESS IS STARTED. Every figure comes from a file the kernel writes - <c>/proc/stat</c>,
/// <c>/proc/meminfo</c>, <c>/proc/diskstats</c>, <c>/proc/pressure/*</c>, <c>/proc/loadavg</c>,
/// <c>/proc/net/dev</c>, <c>/proc/self/mountinfo</c>, <c>/proc/&lt;pid&gt;/*</c> and <c>/sys/block</c> -
/// plus the file system's own free-space call. This is read every five seconds, and often on a machine
/// that is already struggling; starting <c>iostat</c> or <c>ps</c> would add to the very load it measures.
///
/// THE TEXT PARSING IS NOT HERE. It is in <see cref="LinuxProcParsing"/>, pure functions tested from
/// files captured on devlinux. This class does only the reading and the arithmetic between two
/// cumulative readings: rates, percentages and averages.
///
/// EACH PART FAILS ON ITS OWN. A kernel without <c>/proc/pressure</c> still has a readable processor; a
/// process whose <c>io</c> file is closed to us still has its processor and memory. A part that cannot
/// be read says so with a plain-English reason and is logged once - never zero, never a guess.
///
/// THE FIRST READ HAS NO INTERVAL. Rates need two readings, so the first sample's processor, disk,
/// network and process figures are unreadable with <see cref="ResourceUnreadableReasons.FirstSample"/>.
/// Intervals are measured on a monotonic clock, so a wall clock that jumps cannot produce a negative or
/// enormous rate.
/// </summary>
public sealed class LinuxResourceProbe : IResourceProbe
{
    /// <summary>
    /// The shared probe. One instance, so every caller shares the one-second cache and the previous
    /// counters the rates are computed against, and so each "cannot read" line is logged once.
    /// </summary>
    public static readonly LinuxResourceProbe Shared = new();

    /// <summary>How long a sample is reused. The same reasoning as <see cref="MachineMemoryProbe.CacheFor"/>.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(1);

    /// <summary>Every count of sectors in <c>/proc/diskstats</c> is in 512-byte units, whatever the disk's own sector size.</summary>
    private const double BytesPerDiskStatsSector = 512;

    private readonly string _procRoot;
    private readonly string _sysBlockRoot;
    private readonly Func<DateTime> _now;
    private readonly Func<long> _monotonicMilliseconds;
    private readonly Func<string, (ulong TotalBytes, ulong FreeBytes)> _volumeSpace;
    private readonly Func<long> _clockTicksPerSecond;
    private readonly long _pageSize;

    // One read at a time: each read moves the previous counters on, so two reads interleaving would
    // compute rates against each other's halves. A second caller waits and then gets the cached sample.
    private readonly object _gate = new();
    private readonly HashSet<string> _loggedUnreadable = new(StringComparer.Ordinal);
    private ResourceSample? _cached;
    private long _cachedAtMilliseconds;

    private (LinuxCpuTimes Times, long AtMilliseconds)? _previousCpu;
    private (Dictionary<string, LinuxDiskCounters> Disks, long AtMilliseconds)? _previousDisks;
    private (Dictionary<string, LinuxNetworkCounters> Interfaces, long AtMilliseconds)? _previousNetwork;
    private (Dictionary<(int Pid, ulong StartTicks), ProcessCounters> Processes, long AtMilliseconds, DateTime AtUtc)? _previousProcesses;

    private readonly record struct ProcessCounters(ulong CpuTicks, (ulong ReadBytes, ulong WriteBytes)? Io);

    /// <summary>Creates a probe of this machine.</summary>
    public LinuxResourceProbe()
        : this("/proc", "/sys/block", now: null, monotonicMilliseconds: null, volumeSpace: null, clockTicksPerSecond: null, pageSize: null)
    {
    }

    /// <summary>
    /// Creates a probe over any copy of <c>/proc</c> and <c>/sys/block</c>, so the whole probe - rates,
    /// first sample, unreadable parts - is tested against files captured on a real machine. The clocks,
    /// the free-space call, the clock tick rate and the page size are injectable for the same reason.
    /// </summary>
    internal LinuxResourceProbe(
        string procRoot,
        string sysBlockRoot,
        Func<DateTime>? now,
        Func<long>? monotonicMilliseconds,
        Func<string, (ulong TotalBytes, ulong FreeBytes)>? volumeSpace,
        Func<long>? clockTicksPerSecond,
        long? pageSize)
    {
        _procRoot = procRoot;
        _sysBlockRoot = sysBlockRoot;
        _now = now ?? (() => DateTime.UtcNow);
        _monotonicMilliseconds = monotonicMilliseconds ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
        _volumeSpace = volumeSpace ?? ReadVolumeSpace;
        _clockTicksPerSecond = clockTicksPerSecond ?? ReadClockTicksPerSecond;
        _pageSize = pageSize ?? Environment.SystemPageSize;
    }

    /// <inheritdoc />
    public ResourceSample Read()
    {
        lock (_gate)
        {
            var milliseconds = _monotonicMilliseconds();
            if (_cached is { } cached && milliseconds - _cachedAtMilliseconds < CacheFor.TotalMilliseconds) return cached;

            var sample = ReadUncached(_now(), milliseconds);
            _cached = sample;
            _cachedAtMilliseconds = milliseconds;
            LogUnreadableOnce(sample);
            return sample;
        }
    }

    private ResourceSample ReadUncached(DateTime now, long milliseconds)
    {
        var (cpu, cpuTimes) = Guard("processor", () => ReadCpu(milliseconds), r => (CpuReading.Unreadable(r), null));
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
        var (disks, volumes) = Guard("disks", () => ReadDisksAndVolumes(milliseconds),
            r => (DiskSetReading.Unreadable(r), (IReadOnlyList<VolumeReading>)Array.Empty<VolumeReading>()));
        var network = Guard("network", () => ReadNetwork(milliseconds), NetworkReading.Unreadable);
        var (processes, waitingOnDisk) = Guard("processes", () => ReadProcesses(cpuTimes, now, milliseconds),
            r => (ProcessSetReading.Unreadable(r), (int?)null));
        var stall = Guard("stalls", () => ReadStall(waitingOnDisk), StallReading.Unreadable);

        return new ResourceSample(now, cpu, memory, swap, disks, volumes, network, stall, processes);
    }

    /// <summary>
    /// The boundary to the operating system. A file that is not there, a permission refused, or a
    /// structure whose shape has changed under us comes back as an unreadable part, never an exception
    /// out of <see cref="Read"/>.
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

    private (CpuReading, LinuxCpuTimes?) ReadCpu(long milliseconds)
    {
        var path = ProcPath("stat");
        var times = LinuxProcParsing.ParseCpuTimes(File.ReadAllLines(path));
        if (times is null) return (CpuReading.Unreadable($"{path} carried no aggregate cpu line"), null);

        var previous = _previousCpu;
        _previousCpu = (times, milliseconds);
        if (previous is not { } before) return (CpuReading.Unreadable(ResourceUnreadableReasons.FirstSample), times);

        var total = BusyPercent(times.Machine, before.Times.Machine);
        if (total is not { } totalPercent)
            return (CpuReading.Unreadable("no processor time passed since the previous sample"), times);

        if (times.Cores.Count != before.Times.Cores.Count)
            return (CpuReading.Unreadable("the number of logical cores changed since the previous sample"), times);

        var busiest = 0.0;
        for (var i = 0; i < times.Cores.Count; i++)
        {
            if (BusyPercent(times.Cores[i], before.Times.Cores[i]) is { } core && core > busiest) busiest = core;
        }

        return (CpuReading.Read(totalPercent, busiest, times.Cores.Count), times);
    }

    /// <summary>
    /// Busy share of the ticks that passed between two readings, 0 to 100. Computed in doubles and
    /// clamped, because the kernel's per-core iowait counter is documented to step backwards now and
    /// then, which would wrap an unsigned subtraction. Null when no ticks passed.
    /// </summary>
    private static double? BusyPercent(LinuxCpuTicks now, LinuxCpuTicks before)
    {
        var total = (double)now.Total - before.Total;
        if (total <= 0) return null;
        return Math.Clamp(((double)now.Busy - before.Busy) / total * 100, 0, 100);
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
    // Physical disks and volumes
    // ---------------------------------------------------------------------------------------------

    private (DiskSetReading, IReadOnlyList<VolumeReading>) ReadDisksAndVolumes(long milliseconds)
    {
        var devices = LinuxProcParsing.ReadBlockDevices(_sysBlockRoot);
        var counters = LinuxProcParsing.ParseDiskStats(File.ReadAllLines(ProcPath("diskstats")));
        var byName = new Dictionary<string, LinuxDiskCounters>(StringComparer.Ordinal);
        foreach (var counter in counters) byName.TryAdd(counter.Name, counter);

        var previous = _previousDisks;
        _previousDisks = (byName, milliseconds);

        var disks = devices.Values
            .Where(LinuxProcParsing.IsPhysicalDisk)
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(device => ReadPhysicalDisk(device, byName, previous, milliseconds))
            .ToList();

        // A volume list that cannot be read at all is empty - the sample has no set-level flag for it -
        // so the reason goes to the log rather than being lost.
        var volumes = Guard("volumes", () => ReadVolumes(counters, devices), reason =>
        {
            LogOnce("volumes", couldRead: false, reason);
            return (IReadOnlyList<VolumeReading>)Array.Empty<VolumeReading>();
        });
        return (DiskSetReading.Read(disks), volumes);
    }

    private PhysicalDiskReading ReadPhysicalDisk(
        LinuxBlockDevice device,
        Dictionary<string, LinuxDiskCounters> current,
        (Dictionary<string, LinuxDiskCounters> Disks, long AtMilliseconds)? previous,
        long milliseconds)
    {
        var kind = device.Rotational switch
        {
            1 => DiskKind.Spinning,
            0 => DiskKind.SolidState,
            _ => DiskKind.Unknown,
        };

        if (!current.TryGetValue(device.Name, out var now))
            return PhysicalDiskReading.Unreadable(device.Name, kind, $"{device.Name} is not listed in {ProcPath("diskstats")}");
        if (previous is not { } before || !before.Disks.TryGetValue(device.Name, out var then))
            return PhysicalDiskReading.Unreadable(device.Name, kind, ResourceUnreadableReasons.FirstSample);

        var elapsedMilliseconds = (double)(milliseconds - before.AtMilliseconds);
        if (elapsedMilliseconds <= 0)
            return PhysicalDiskReading.Unreadable(device.Name, kind, "no time passed since the previous sample");

        if (now.MillisecondsBusy < then.MillisecondsBusy || now.SectorsRead < then.SectorsRead || now.SectorsWritten < then.SectorsWritten
            || now.ReadsCompleted < then.ReadsCompleted || now.WritesCompleted < then.WritesCompleted)
            return PhysicalDiskReading.Unreadable(device.Name, kind,
                "its counters went backwards since the previous sample (the disk was removed and added again)");

        var seconds = elapsedMilliseconds / 1000;
        var reads = now.ReadsCompleted - then.ReadsCompleted;
        var writes = now.WritesCompleted - then.WritesCompleted;

        // A disk that completed no reads in the interval has no time per read - there was nothing to
        // average. That is null, not zero: zero would claim reads that took no time at all.
        return PhysicalDiskReading.Read(
            device.Name,
            kind,
            busyPercent: Math.Clamp((now.MillisecondsBusy - then.MillisecondsBusy) / elapsedMilliseconds * 100, 0, 100),
            readBytesPerSecond: (now.SectorsRead - then.SectorsRead) * BytesPerDiskStatsSector / seconds,
            writeBytesPerSecond: (now.SectorsWritten - then.SectorsWritten) * BytesPerDiskStatsSector / seconds,
            millisecondsPerRead: reads > 0 ? (double)(now.MillisecondsReading - then.MillisecondsReading) / reads : null,
            millisecondsPerWrite: writes > 0 ? (double)(now.MillisecondsWriting - then.MillisecondsWriting) / writes : null,
            // The average number of requests in the disk's queue over the interval - the same figure
            // iostat prints as aqu-sz - from the weighted time spent doing input and output.
            queueLength: Math.Max(0, ((double)now.WeightedMillisecondsBusy - then.WeightedMillisecondsBusy) / elapsedMilliseconds));
    }

    private IReadOnlyList<VolumeReading> ReadVolumes(IReadOnlyList<LinuxDiskCounters> counters, IReadOnlyDictionary<string, LinuxBlockDevice> devices)
    {
        var mounts = LinuxProcParsing.ParseMountInfo(File.ReadAllLines(ProcPath("self", "mountinfo")));
        var result = new List<VolumeReading>();
        foreach (var volume in LinuxProcParsing.SelectVolumes(mounts, counters))
        {
            var physical = LinuxProcParsing.ResolvePhysicalDisks(volume.DeviceName, devices);
            result.Add(Guard($"volume {volume.MountPoint}",
                () =>
                {
                    var (total, free) = _volumeSpace(volume.MountPoint);
                    return VolumeReading.Read(volume.MountPoint, total, free, physical);
                },
                reason => VolumeReading.Unreadable(volume.MountPoint, reason)));
        }

        return result;
    }

    /// <summary>The file system's own free-space figures (statvfs underneath), through .NET rather than a P/Invoke of our own.</summary>
    private static (ulong TotalBytes, ulong FreeBytes) ReadVolumeSpace(string mountPoint)
    {
        var drive = new DriveInfo(mountPoint);
        return ((ulong)drive.TotalSize, (ulong)drive.AvailableFreeSpace);
    }

    // ---------------------------------------------------------------------------------------------
    // Network
    // ---------------------------------------------------------------------------------------------

    private NetworkReading ReadNetwork(long milliseconds)
    {
        var interfaces = LinuxProcParsing.ParseNetworkDevices(File.ReadAllLines(ProcPath("net", "dev")))
            .GroupBy(i => i.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var previous = _previousNetwork;
        _previousNetwork = (interfaces, milliseconds);
        if (previous is not { } before) return NetworkReading.Unreadable(ResourceUnreadableReasons.FirstSample);

        var seconds = (milliseconds - before.AtMilliseconds) / 1000.0;
        if (seconds <= 0) return NetworkReading.Unreadable("no time passed since the previous sample");

        // Only interfaces present in both readings, and only counters that moved forward: an interface
        // that came up, went away or was reset in between has no honest difference to contribute.
        double received = 0, sent = 0;
        foreach (var (name, now) in interfaces)
        {
            if (!before.Interfaces.TryGetValue(name, out var then)) continue;
            if (now.ReceivedBytes >= then.ReceivedBytes) received += now.ReceivedBytes - then.ReceivedBytes;
            if (now.SentBytes >= then.SentBytes) sent += now.SentBytes - then.SentBytes;
        }

        return NetworkReading.Read(received / seconds, sent / seconds);
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
        var running = LinuxProcParsing.ParseLoadAverageRunning(File.ReadAllText(loadAverage));
        if (running is null) return StallReading.Unreadable($"{loadAverage} carried no running-task count");

        // The running count is the kernel's "currently runnable" figure, which includes tasks on a
        // processor now as well as those waiting for one, and includes this reader itself.
        return StallReading.Read(
            StallSource.LinuxPressureStall,
            cpuStalledPercent: cpu,
            memoryStalledPercent: memory,
            ioStalledPercent: io,
            runQueueLength: running,
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

    private (ProcessSetReading, int?) ReadProcesses(LinuxCpuTimes? cpuTimes, DateTime now, long milliseconds)
    {
        var ticksPerSecond = _clockTicksPerSecond();
        if (ticksPerSecond <= 0)
            return (ProcessSetReading.Unreadable($"the clock tick rate read as {ticksPerSecond}"), null);
        if (cpuTimes is null || cpuTimes.Cores.Count == 0)
            return (ProcessSetReading.Unreadable($"the logical core count could not be read from {ProcPath("stat")}"), null);

        var bootTime = cpuTimes.BootTimeUnixSeconds is { } boot ? DateTime.UnixEpoch.AddSeconds(boot) : (DateTime?)null;
        var previous = _previousProcesses;
        var elapsedSeconds = previous is { } p ? (milliseconds - p.AtMilliseconds) / 1000.0 : 0;
        var counters = new Dictionary<(int, ulong), ProcessCounters>();
        var readings = new List<ProcessReading>();
        var waitingOnDisk = 0;

        foreach (var directory in Directory.EnumerateDirectories(_procRoot))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
            if (ReadOneProcess(directory) is not { } process) continue;

            var (stat, residentPages, io) = process;
            if (stat.State == 'D') waitingOnDisk++;

            var key = (stat.ProcessId, stat.StartTimeTicks);
            counters[key] = new ProcessCounters(stat.CpuTicks, io);
            if (previous is not { } before || elapsedSeconds <= 0) continue;

            var startedAt = bootTime?.AddSeconds((double)stat.StartTimeTicks / ticksPerSecond);
            ProcessCounters then;
            if (before.Processes.TryGetValue(key, out var seen))
            {
                then = seen;
            }
            else if (startedAt is { } started && started >= before.AtUtc)
            {
                // Started since the previous sample: everything it has done happened in this interval.
                then = new ProcessCounters(0, io is null ? null : (0, 0));
            }
            else
            {
                // Was running last time but could not be read then. Its processor time since is not
                // known, so it waits for the next sample rather than being shown with a guess.
                continue;
            }

            var cpuTicks = stat.CpuTicks >= then.CpuTicks ? stat.CpuTicks - then.CpuTicks : 0;
            double? readRate = null, writeRate = null;
            if (io is { } nowIo && then.Io is { } thenIo)
            {
                readRate = nowIo.ReadBytes >= thenIo.ReadBytes ? (nowIo.ReadBytes - thenIo.ReadBytes) / elapsedSeconds : 0;
                writeRate = nowIo.WriteBytes >= thenIo.WriteBytes ? (nowIo.WriteBytes - thenIo.WriteBytes) / elapsedSeconds : 0;
            }

            readings.Add(new ProcessReading(
                stat.ProcessId,
                stat.ParentProcessId,
                stat.Name,
                startedAt,
                CpuPercent: Math.Clamp(cpuTicks / (double)ticksPerSecond / elapsedSeconds / cpuTimes.Cores.Count * 100, 0, 100),
                MemoryBytes: residentPages * (ulong)_pageSize,
                readRate,
                writeRate));
        }

        _previousProcesses = (counters, milliseconds, now);

        if (previous is null) return (ProcessSetReading.Unreadable(ResourceUnreadableReasons.FirstSample), waitingOnDisk);
        if (elapsedSeconds <= 0) return (ProcessSetReading.Unreadable("no time passed since the previous sample"), waitingOnDisk);
        return (ProcessSetReading.Read(readings), waitingOnDisk);
    }

    /// <summary>
    /// One process's stat, resident pages and disk counters. A process that exits between listing and
    /// reading is normal on a busy machine, so a stat or statm that cannot be read leaves the process
    /// out (null). The io file is closed to us for other users' processes, so it alone gives null
    /// disk counters - the process is still listed with its processor and memory.
    /// </summary>
    private static (LinuxProcessStat Stat, ulong ResidentPages, (ulong ReadBytes, ulong WriteBytes)? Io)? ReadOneProcess(string directory)
    {
        LinuxProcessStat? stat;
        ulong? residentPages;
        try
        {
            stat = LinuxProcParsing.ParseProcessStat(File.ReadAllText(Path.Combine(directory, "stat")));
            residentPages = LinuxProcParsing.ParseStatmResidentPages(File.ReadAllText(Path.Combine(directory, "statm")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (stat is null || residentPages is not { } pages) return null;

        (ulong, ulong)? io;
        try
        {
            io = LinuxProcParsing.ParseProcessIo(File.ReadAllLines(Path.Combine(directory, "io")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            io = null;
        }

        return (stat, pages, io);
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
        LogOnce("disks", sample.Disks.CouldRead, sample.Disks.UnreadableReason);
        foreach (var disk in sample.Disks.Disks) LogOnce($"disk {disk.Name}", disk.CouldRead, disk.UnreadableReason);
        foreach (var volume in sample.Volumes) LogOnce($"volume {volume.MountPoint}", volume.CouldRead, volume.UnreadableReason);
        LogOnce("network", sample.Network.CouldRead, sample.Network.UnreadableReason);
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
