using System.Globalization;
using System.Text;

namespace CcDirector.Core.Machine;

/// <summary>Cumulative clock ticks for one CPU line of <c>/proc/stat</c>: time spent working, and all time.</summary>
internal readonly record struct LinuxCpuTicks(ulong Busy, ulong Total);

/// <summary>
/// The parts of <c>/proc/stat</c> the probe needs: the whole machine's ticks, each logical core's
/// ticks, and the boot time every process start time is measured from.
/// </summary>
internal sealed record LinuxCpuTimes(LinuxCpuTicks Machine, IReadOnlyList<LinuxCpuTicks> Cores, long? BootTimeUnixSeconds);

/// <summary>
/// One line of <c>/proc/diskstats</c>, cumulative since boot. Sector counts are always in units of
/// 512 bytes in this file, whatever the device's own sector size.
/// </summary>
internal sealed record LinuxDiskCounters(
    int Major,
    int Minor,
    string Name,
    ulong ReadsCompleted,
    ulong SectorsRead,
    ulong MillisecondsReading,
    ulong WritesCompleted,
    ulong SectorsWritten,
    ulong MillisecondsWriting,
    ulong InFlight,
    ulong MillisecondsBusy,
    ulong WeightedMillisecondsBusy);

/// <summary>One interface line of <c>/proc/net/dev</c>, cumulative since the interface came up.</summary>
internal sealed record LinuxNetworkCounters(string Name, ulong ReceivedBytes, ulong SentBytes);

/// <summary>One line of <c>/proc/self/mountinfo</c>: which device is mounted where, and as what.</summary>
internal sealed record LinuxMount(int Major, int Minor, string MountPoint, string FileSystemType, string Source);

/// <summary>The fields of <c>/proc/&lt;pid&gt;/stat</c> the probe needs.</summary>
internal sealed record LinuxProcessStat(int ProcessId, string Name, char State, int ParentProcessId, ulong CpuTicks, ulong StartTimeTicks);

/// <summary>A mount that is a real volume: where it is mounted, its file system, and the block device under it.</summary>
internal sealed record LinuxVolumeSource(string MountPoint, string FileSystemType, string DeviceName);

/// <summary>What <c>/sys/block/&lt;device&gt;</c> says about one whole block device.</summary>
/// <param name="Name">The device name, for example <c>sdc</c> or <c>dm-0</c>.</param>
/// <param name="Rotational">The <c>queue/rotational</c> flag: 1 spinning, 0 solid-state, null when the file is missing.</param>
/// <param name="Slaves">The devices this one is built on (device-mapper, md, logical volumes). Empty for a real disk.</param>
/// <param name="Partitions">The partitions carved out of this device, for example <c>sdc1</c>.</param>
internal sealed record LinuxBlockDevice(string Name, int? Rotational, IReadOnlyList<string> Slaves, IReadOnlyList<string> Partitions);

/// <summary>
/// THE TEXT OF /proc AND /sys, TURNED INTO NUMBERS - and nothing else.
///
/// Every function here is pure: it takes text the kernel wrote and returns values, so each one is unit
/// tested from files captured on a real machine rather than only on the one machine that has them. The
/// probe (<c>LinuxResourceProbe</c>) does the reading and the arithmetic between two readings;
/// none of that lives here.
///
/// A line that cannot be parsed is skipped, never guessed at. A file whose required content is missing
/// gives null, which the probe turns into a reading that cannot be read, with the reason.
/// </summary>
internal static class LinuxProcParsing
{
    /// <summary>
    /// Parse <c>/proc/stat</c>. Busy time is everything except idle and iowait - iowait is a CPU with
    /// nothing to run, waiting on a disk, which is not the CPU being busy. Guest time is already counted
    /// inside user and nice, so it is left out of the total to avoid counting it twice.
    /// Returns null when the file carries no aggregate "cpu" line.
    /// </summary>
    public static LinuxCpuTimes? ParseCpuTimes(IEnumerable<string> lines)
    {
        LinuxCpuTicks? machine = null;
        var cores = new List<LinuxCpuTicks>();
        long? bootTime = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                machine = ParseCpuLine(line);
            }
            else if (line.StartsWith("cpu", StringComparison.Ordinal))
            {
                var ticks = ParseCpuLine(line);
                if (ticks is { } core) cores.Add(core);
            }
            else if (line.StartsWith("btime ", StringComparison.Ordinal)
                     && long.TryParse(line.AsSpan(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                bootTime = seconds;
            }
        }

        return machine is { } total ? new LinuxCpuTimes(total, cores, bootTime) : null;
    }

    private static LinuxCpuTicks? ParseCpuLine(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // cpu user nice system idle iowait irq softirq steal [guest guest_nice]
        if (fields.Length < 9) return null;

        var values = new ulong[8];
        for (var i = 0; i < 8; i++)
        {
            if (!ulong.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return null;
        }

        var total = values.Aggregate(0UL, (sum, value) => sum + value);
        var idle = values[3] + values[4];
        return new LinuxCpuTicks(total - idle, total);
    }

    /// <summary>
    /// Swap total and used, in bytes, from the lines of <c>/proc/meminfo</c> (SwapTotal minus SwapFree),
    /// read with the memory probe's own kilobyte parsing. A machine with no swap gives a total of zero,
    /// which is a fact about the machine. Null when either line is missing.
    /// </summary>
    public static (ulong TotalBytes, ulong UsedBytes)? ParseSwap(IEnumerable<string> memInfoLines)
    {
        ulong? total = null, free = null;
        foreach (var line in memInfoLines)
        {
            if (MachineMemoryProbe.TryReadKilobytes(line, "SwapTotal:", out var t)) total = t;
            else if (MachineMemoryProbe.TryReadKilobytes(line, "SwapFree:", out var f)) free = f;
        }

        if (total is not { } totalBytes || free is not { } freeBytes) return null;
        return (totalBytes, freeBytes >= totalBytes ? 0 : totalBytes - freeBytes);
    }

    /// <summary>Parse <c>/proc/diskstats</c>. Lines with fewer than the fourteen classic fields are skipped.</summary>
    public static IReadOnlyList<LinuxDiskCounters> ParseDiskStats(IEnumerable<string> lines)
    {
        var result = new List<LinuxDiskCounters>();
        foreach (var line in lines)
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 14) continue;
            if (!int.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) continue;
            if (!int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) continue;

            var n = new ulong[11];
            var ok = true;
            for (var i = 0; i < 11 && ok; i++)
                ok = ulong.TryParse(f[i + 3], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]);
            if (!ok) continue;

            // Fields after the name: reads, reads merged, sectors read, ms reading, writes, writes merged,
            // sectors written, ms writing, in flight, ms doing input and output, weighted ms.
            result.Add(new LinuxDiskCounters(major, minor, f[2],
                ReadsCompleted: n[0], SectorsRead: n[2], MillisecondsReading: n[3],
                WritesCompleted: n[4], SectorsWritten: n[6], MillisecondsWriting: n[7],
                InFlight: n[8], MillisecondsBusy: n[9], WeightedMillisecondsBusy: n[10]));
        }

        return result;
    }

    /// <summary>
    /// The "some avg10" figure of a <c>/proc/pressure/*</c> file: the percentage of the last ten
    /// seconds in which at least one task was stalled on this resource. Null when the line is missing.
    /// </summary>
    public static double? ParsePressureSomeAverage10(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("some ", StringComparison.Ordinal)) continue;
            foreach (var field in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (field.StartsWith("avg10=", StringComparison.Ordinal)
                    && double.TryParse(field.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return value;
            }
        }

        return null;
    }

    /// <summary>
    /// The running-task count from <c>/proc/loadavg</c> ("14.11 20.19 18.21 4/3921 4044667" gives 4).
    /// That count includes the task reading the file. Null when the field is missing.
    /// </summary>
    public static int? ParseLoadAverageRunning(string text)
    {
        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4) return null;
        var slash = fields[3].IndexOf('/');
        if (slash <= 0) return null;
        return int.TryParse(fields[3].AsSpan(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out var running) ? running : null;
    }

    /// <summary>Parse <c>/proc/net/dev</c>, leaving out the loopback interface, which never leaves the machine.</summary>
    public static IReadOnlyList<LinuxNetworkCounters> ParseNetworkDevices(IEnumerable<string> lines)
    {
        var result = new List<LinuxNetworkCounters>();
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon].Trim();
            if (name.Length == 0 || name == "lo") continue;

            var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Receive: bytes packets errs drop fifo frame compressed multicast; then transmit bytes first.
            if (f.Length < 9) continue;
            if (!ulong.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var received)) continue;
            if (!ulong.TryParse(f[8], NumberStyles.None, CultureInfo.InvariantCulture, out var sent)) continue;
            result.Add(new LinuxNetworkCounters(name, received, sent));
        }

        return result;
    }

    /// <summary>
    /// Parse <c>/proc/self/mountinfo</c>. The mount point is decoded from the kernel's octal escapes
    /// ("\040" for a space). The fields after the optional-fields separator " - " are the file system
    /// type and the mount source.
    /// </summary>
    public static IReadOnlyList<LinuxMount> ParseMountInfo(IEnumerable<string> lines)
    {
        var result = new List<LinuxMount>();
        foreach (var line in lines)
        {
            var f = line.Split(' ');
            if (f.Length < 10) continue;
            var deviceNumber = f[2].Split(':');
            if (deviceNumber.Length != 2) continue;
            if (!int.TryParse(deviceNumber[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) continue;
            if (!int.TryParse(deviceNumber[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) continue;

            var separator = Array.IndexOf(f, "-", 6);
            if (separator < 0 || separator + 2 >= f.Length) continue;

            result.Add(new LinuxMount(major, minor, DecodeOctalEscapes(f[4]), f[separator + 1], DecodeOctalEscapes(f[separator + 2])));
        }

        return result;
    }

    private static string DecodeOctalEscapes(string value)
    {
        if (!value.Contains('\\')) return value;
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length
                && IsOctal(value[i + 1]) && IsOctal(value[i + 2]) && IsOctal(value[i + 3]))
            {
                bytes.Add((byte)(((value[i + 1] - '0') << 6) | ((value[i + 2] - '0') << 3) | (value[i + 3] - '0')));
                i += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsOctal(char c) => c is >= '0' and <= '7';

    /// <summary>
    /// Parse <c>/proc/&lt;pid&gt;/stat</c>. The name is in parentheses and may itself contain spaces
    /// and parentheses ("npm exec astro "), so the fields are read from after the LAST closing
    /// parenthesis. Returns null for text that does not have that shape.
    /// </summary>
    public static LinuxProcessStat? ParseProcessStat(string text)
    {
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open <= 0 || close < open) return null;
        if (!int.TryParse(text.AsSpan(0, open).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return null;

        var name = text[(open + 1)..close];
        // After the name: state(3) ppid(4) pgrp session tty tpgid flags minflt cminflt majflt cmajflt
        // utime(14) stime(15) cutime cstime priority nice threads itrealvalue starttime(22).
        var f = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 20 || f[0].Length != 1) return null;
        if (!int.TryParse(f[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parent)) return null;
        if (!ulong.TryParse(f[11], NumberStyles.None, CultureInfo.InvariantCulture, out var user)) return null;
        if (!ulong.TryParse(f[12], NumberStyles.None, CultureInfo.InvariantCulture, out var system)) return null;
        if (!ulong.TryParse(f[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start)) return null;

        return new LinuxProcessStat(pid, name, f[0][0], parent, user + system, start);
    }

    /// <summary>The resident page count, the second field of <c>/proc/&lt;pid&gt;/statm</c>. Null when unreadable.</summary>
    public static ulong? ParseStatmResidentPages(string text)
    {
        var f = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 2) return null;
        return ulong.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pages) ? pages : null;
    }

    /// <summary>
    /// The bytes a process has caused to be read from and written to storage, from
    /// <c>/proc/&lt;pid&gt;/io</c> (read_bytes and write_bytes, not rchar and wchar, which count
    /// reads served from cache). Null when either line is missing.
    /// </summary>
    public static (ulong ReadBytes, ulong WriteBytes)? ParseProcessIo(IEnumerable<string> lines)
    {
        ulong? read = null, write = null;
        foreach (var line in lines)
        {
            if (TryReadKeyedValue(line, "read_bytes:", out var r)) read = r;
            else if (TryReadKeyedValue(line, "write_bytes:", out var w)) write = w;
        }

        return read is { } readBytes && write is { } writeBytes ? (readBytes, writeBytes) : null;
    }

    private static bool TryReadKeyedValue(string line, string key, out ulong value)
    {
        value = 0;
        return line.StartsWith(key, StringComparison.Ordinal)
               && ulong.TryParse(line.AsSpan(key.Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// The mounts that are volumes a person stores work on: each mount whose device is a block device
    /// the kernel counts in <c>/proc/diskstats</c>, leaving out loop devices (snap packages, which are
    /// read-only and always full, and would raise a "disk full" warning forever) and RAM disks. A device
    /// mounted more than once (a bind mount, a container's view) is one volume, under its first mount.
    ///
    /// The device is found by its major and minor number. Btrfs reports an anonymous number (major 0)
    /// for every mount, so for those the mount source's own name ("/dev/nvme0n1p2" gives "nvme0n1p2") is
    /// used when the kernel counts a device of that name. A mount that matches neither way is not a
    /// volume this probe can attribute, and is left out rather than guessed at.
    /// </summary>
    public static IReadOnlyList<LinuxVolumeSource> SelectVolumes(IReadOnlyList<LinuxMount> mounts, IReadOnlyList<LinuxDiskCounters> disks)
    {
        var byNumber = new Dictionary<(int, int), string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var disk in disks)
        {
            byNumber.TryAdd((disk.Major, disk.Minor), disk.Name);
            names.Add(disk.Name);
        }

        var result = new List<LinuxVolumeSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            string? device = null;
            if (mount.Major != 0) byNumber.TryGetValue((mount.Major, mount.Minor), out device);
            else if (mount.Source.StartsWith("/dev/", StringComparison.Ordinal) && names.Contains(mount.Source[5..])) device = mount.Source[5..];

            if (device is null) continue;
            if (device.StartsWith("loop", StringComparison.Ordinal)
                || device.StartsWith("ram", StringComparison.Ordinal)
                || device.StartsWith("zram", StringComparison.Ordinal)) continue;
            if (!seen.Add(device)) continue;

            result.Add(new LinuxVolumeSource(mount.MountPoint, mount.FileSystemType, device));
        }

        return result;
    }

    /// <summary>
    /// The physical disks a block device sits on, following device-mapper, md and logical volumes
    /// down through their slaves, and partitions up to the disk that holds them. On devlinux the root
    /// logical volume dm-0 sits on sda1, sdb1 and sdc3, which gives sda, sdb and sdc.
    ///
    /// A loop device, a RAM disk or a device <paramref name="devices"/> does not know gives nothing:
    /// it is not a physical disk and none can be named for it. A cycle in the slaves cannot happen in
    /// a real kernel, but a visited set guards against one anyway rather than recursing forever.
    /// </summary>
    public static IReadOnlyList<string> ResolvePhysicalDisks(string deviceName, IReadOnlyDictionary<string, LinuxBlockDevice> devices)
    {
        var result = new List<string>();
        Resolve(deviceName, devices, result, new HashSet<string>(StringComparer.Ordinal));
        return result;
    }

    private static void Resolve(string name, IReadOnlyDictionary<string, LinuxBlockDevice> devices, List<string> result, HashSet<string> visited)
    {
        if (!visited.Add(name)) return;

        var device = devices.TryGetValue(name, out var whole)
            ? whole
            : devices.Values.FirstOrDefault(d => d.Partitions.Contains(name, StringComparer.Ordinal));
        if (device is null) return;

        if (device.Slaves.Count > 0)
        {
            foreach (var slave in device.Slaves) Resolve(slave, devices, result, visited);
            return;
        }

        if (IsPhysicalDisk(device) && !result.Contains(device.Name, StringComparer.Ordinal)) result.Add(device.Name);
    }

    /// <summary>
    /// Whether a whole block device is a physical disk: not a loop device, a RAM disk or zram, an optical drive (sr) or a floppy (fd), and
    /// not built on other devices (device-mapper and md have slaves). Partitions never reach here -
    /// they are not entries of <c>/sys/block</c>.
    /// </summary>
    public static bool IsPhysicalDisk(LinuxBlockDevice device) =>
        device.Slaves.Count == 0
        && !device.Name.StartsWith("loop", StringComparison.Ordinal)
        && !device.Name.StartsWith("ram", StringComparison.Ordinal)
        && !device.Name.StartsWith("zram", StringComparison.Ordinal)
        && !device.Name.StartsWith("dm-", StringComparison.Ordinal)
        && !device.Name.StartsWith("md", StringComparison.Ordinal)
        && !device.Name.StartsWith("sr", StringComparison.Ordinal)
        && !device.Name.StartsWith("fd", StringComparison.Ordinal);

    /// <summary>
    /// Read <c>/sys/block</c> (or a captured copy of it under <paramref name="sysBlockRoot"/>): each
    /// whole block device, its rotational flag, its slaves, and its partitions. A partition is a
    /// sub-directory carrying a <c>dev</c> file and named after the device ("sdc" holds "sdc1";
    /// "nvme0n1" holds "nvme0n1p1"). A device whose rotational file is missing or unreadable gets null,
    /// which the probe reports as an unknown disk kind rather than guessing one.
    /// </summary>
    public static IReadOnlyDictionary<string, LinuxBlockDevice> ReadBlockDevices(string sysBlockRoot)
    {
        var result = new Dictionary<string, LinuxBlockDevice>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateDirectories(sysBlockRoot))
        {
            var name = Path.GetFileName(path);
            int? rotational = null;
            var rotationalFile = Path.Combine(path, "queue", "rotational");
            if (File.Exists(rotationalFile)
                && int.TryParse(File.ReadAllText(rotationalFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var flag))
                rotational = flag;

            var slavesDirectory = Path.Combine(path, "slaves");
            var slaves = Directory.Exists(slavesDirectory)
                ? Directory.EnumerateFileSystemEntries(slavesDirectory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
                : [];

            var partitions = Directory.EnumerateDirectories(path)
                .Where(d => Path.GetFileName(d).StartsWith(name, StringComparison.Ordinal) && File.Exists(Path.Combine(d, "dev")))
                .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList();

            result[name] = new LinuxBlockDevice(name, rotational, slaves, partitions);
        }

        return result;
    }
}
