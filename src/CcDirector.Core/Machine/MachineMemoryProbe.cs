using System.Globalization;
using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE MACHINE'S MEMORY, READ DIRECTLY (issue #2818).
///
/// Before this existed, nothing in the Director read the machine's memory at all - searching the
/// source for <c>GlobalMemoryStatusEx</c>, <c>MemAvailable</c>, <c>MemoryLoad</c> or
/// <c>PerformanceCounter</c> returned nothing outside the Gateway's load-test metrics. Every deadline
/// in the Director was therefore fixed, and none of them could know the machine was the reason it was
/// missed. Three of those deadlines were failing together on a machine running eighteen sessions: the
/// tunnel keep-alive, the roster re-push, and the composer echo that decides whether the owner's typed
/// words survive.
///
/// NO SUBPROCESS IS SPAWNED. This is called on hot paths and on timers; shelling out to
/// <c>vm_stat</c> or <c>free</c> every few seconds on a machine that is ALREADY short of memory would
/// be its own small contribution to the problem. Every platform here is a direct call.
///
/// READINGS ARE CACHED for <see cref="CacheFor"/>, because several callers ask within the same moment
/// and the answer cannot meaningfully change between them.
/// </summary>
public sealed class MachineMemoryProbe : IMachineMemoryProbe
{
    /// <summary>
    /// The shared probe. A single instance so the cache is shared across every caller, and so the
    /// "cannot read this platform" line is logged once rather than once per caller.
    /// </summary>
    public static readonly MachineMemoryProbe Shared = new();

    /// <summary>
    /// How long a reading is reused. Short enough that a machine falling off a cliff is noticed within
    /// a second, long enough that a burst of callers costs one system call rather than twenty.
    /// </summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private MachineMemoryReading _cached;
    private bool _haveCached;
    private bool _loggedUnreadable;

    /// <summary>Creates a probe. The clock is injectable so a test can age the cache without sleeping.</summary>
    public MachineMemoryProbe(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <inheritdoc />
    public MachineMemoryReading Read()
    {
        var now = _now();
        lock (_gate)
        {
            if (_haveCached && now - _cached.TakenAtUtc < CacheFor) return _cached;
        }

        var reading = ReadUncached(now);
        bool shouldLog;

        lock (_gate)
        {
            _cached = reading;
            _haveCached = true;

            // Logged ONCE, and the decision to log is taken under the same lock that sets the flag so
            // two threads racing on a starved machine cannot both write the line.
            shouldLog = !reading.CouldRead && !_loggedUnreadable;
            if (shouldLog) _loggedUnreadable = true;
        }

        if (shouldLog)
            FileLog.Write($"[MachineMemoryProbe] Read FAILED, and will not be reported again: {reading.UnreadableReason}. " +
                          "Every memory-aware deadline stays at its healthy-machine value.");

        return reading;
    }

    /// <summary>
    /// The platform read itself. This is a boundary to the operating system, so it catches: a P/Invoke
    /// that is not present, a file that is not there, or a structure whose shape has changed under us
    /// must come back as an unreadable reading, never take the Director down.
    /// </summary>
    private static MachineMemoryReading ReadUncached(DateTime now)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return ReadWindows(now);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return ReadLinux(now);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return ReadMacOs(now);
            return MachineMemoryReading.Unreadable($"unsupported platform {RuntimeInformation.OSDescription}", now);
        }
        catch (Exception ex)
        {
            return MachineMemoryReading.Unreadable($"{ex.GetType().Name}: {ex.Message}", now);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Windows
    // ---------------------------------------------------------------------------------------------

    private static MachineMemoryReading ReadWindows(DateTime now)
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return MachineMemoryReading.Unreadable(
                $"GlobalMemoryStatusEx failed with error {Marshal.GetLastWin32Error()}", now);

        if (status.ullTotalPhys == 0)
            return MachineMemoryReading.Unreadable("GlobalMemoryStatusEx reported zero total physical memory", now);

        return MachineMemoryReading.Read(status.ullTotalPhys, status.ullAvailPhys, now);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    // ---------------------------------------------------------------------------------------------
    // Linux
    // ---------------------------------------------------------------------------------------------

    private const string ProcMemInfo = "/proc/meminfo";

    private static MachineMemoryReading ReadLinux(DateTime now)
    {
        if (!File.Exists(ProcMemInfo))
            return MachineMemoryReading.Unreadable($"{ProcMemInfo} does not exist", now);

        return ParseProcMemInfo(File.ReadAllLines(ProcMemInfo), now);
    }

    /// <summary>
    /// Parse the lines of <c>/proc/meminfo</c>. Split out and internal so the parser is unit tested
    /// against real captured text on every platform, rather than only on the one machine that has the
    /// file.
    ///
    /// MemAvailable IS THE FIGURE, not MemFree. MemFree on a healthy Linux box is small by design -
    /// the kernel spends free memory on cache it will surrender instantly - so reading it would report
    /// permanent Critical pressure on a machine with plenty of room. MemAvailable is the kernel's own
    /// estimate of what a new process could actually get, and it has been present since Linux 3.14.
    /// </summary>
    internal static MachineMemoryReading ParseProcMemInfo(IEnumerable<string> lines, DateTime now)
    {
        ulong total = 0, available = 0;
        var haveTotal = false;
        var haveAvailable = false;

        foreach (var line in lines)
        {
            if (!haveTotal && TryReadKilobytes(line, "MemTotal:", out total)) { haveTotal = true; continue; }
            if (!haveAvailable && TryReadKilobytes(line, "MemAvailable:", out available)) haveAvailable = true;
            if (haveTotal && haveAvailable) break;
        }

        if (!haveTotal) return MachineMemoryReading.Unreadable($"{ProcMemInfo} carried no readable MemTotal", now);
        if (!haveAvailable) return MachineMemoryReading.Unreadable($"{ProcMemInfo} carried no readable MemAvailable", now);

        return MachineMemoryReading.Read(total, available, now);
    }

    /// <summary>Reads "MemTotal:       16316576 kB" into bytes. Every value in the file is in kilobytes.</summary>
    private static bool TryReadKilobytes(string line, string key, out ulong bytes)
    {
        bytes = 0;
        if (!line.StartsWith(key, StringComparison.Ordinal)) return false;

        var rest = line.AsSpan(key.Length).Trim();
        var space = rest.IndexOf(' ');
        var number = space < 0 ? rest : rest[..space];

        if (!ulong.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var kilobytes)) return false;

        bytes = kilobytes * 1024;
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // macOS
    // ---------------------------------------------------------------------------------------------

    /// <summary>HOST_VM_INFO64, the flavour of host_statistics64 that returns vm_statistics64_data_t.</summary>
    private const int HostVmInfo64 = 4;

    /// <summary>HOST_VM_INFO64_COUNT - the size of vm_statistics64_data_t in 32-bit words.</summary>
    private const int HostVmInfo64Count = 38;

    private static MachineMemoryReading ReadMacOs(DateTime now)
    {
        if (!TryReadSysctlUInt64("hw.memsize", out var total) || total == 0)
            return MachineMemoryReading.Unreadable("sysctl hw.memsize could not be read", now);

        var count = (uint)HostVmInfo64Count;
        var stats = new uint[HostVmInfo64Count];
        var result = host_statistics64(mach_host_self(), HostVmInfo64, stats, ref count);
        if (result != 0)
            return MachineMemoryReading.Unreadable($"host_statistics64 returned {result}", now);

        var pageSize = (ulong)Environment.SystemPageSize;
        if (pageSize == 0)
            return MachineMemoryReading.Unreadable("the system page size read as zero", now);

        // Reclaimable memory, in the same spirit as Linux's MemAvailable: pages that are free now plus
        // pages the kernel will surrender on demand. The indices are the natural_t fields of
        // vm_statistics64_data_t - free_count at 0, inactive_count at 2, purgeable_count at 22 - where
        // each uint64 field ahead of them occupies two 32-bit slots.
        //
        // THIS IS AN APPROXIMATION AND IS DOCUMENTED AS ONE. macOS publishes no single figure equivalent
        // to MemAvailable, and its memory compressor means no arithmetic over these counters is exact.
        // What matters here is that it is a real measurement of real counters rather than an invention,
        // and that the thresholds it feeds are deliberately coarse - three levels, not a percentage
        // anybody reads off a screen.
        var freePages = (ulong)stats[0];
        var inactivePages = (ulong)stats[2];
        var purgeablePages = (ulong)stats[22];
        var available = (freePages + inactivePages + purgeablePages) * pageSize;

        return MachineMemoryReading.Read(total, Math.Min(available, total), now);
    }

    private static bool TryReadSysctlUInt64(string name, out ulong value)
    {
        value = 0;
        var length = (IntPtr)sizeof(ulong);
        var buffer = Marshal.AllocHGlobal(sizeof(ulong));
        try
        {
            if (sysctlbyname(name, buffer, ref length, IntPtr.Zero, IntPtr.Zero) != 0) return false;
            value = (ulong)Marshal.ReadInt64(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sysctlbyname(string name, IntPtr oldp, ref IntPtr oldlenp, IntPtr newp, IntPtr newlen);

    [DllImport("libc")]
    private static extern IntPtr mach_host_self();

    [DllImport("libc")]
    private static extern int host_statistics64(IntPtr hostPriv, int flavor, uint[] hostInfoOut, ref uint hostInfoOutCnt);
}
