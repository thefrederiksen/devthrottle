using System.Runtime.InteropServices;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE SYSTEM-CALL HALF OF THE macOS RESOURCE PROBE.
///
/// This file does the talking to the operating system and nothing else: it fills the raw captures in
/// <see cref="MacOsRawReadings"/> with exactly what the kernel handed back, and leaves every
/// interpretation of those figures to <see cref="MacOsResourceParsing"/>, which is pure and tested.
///
/// NO SUBPROCESS IS STARTED HERE. Not <c>iostat</c>, not <c>ps</c>, not <c>sysctl</c> the command -
/// only <c>sysctlbyname</c> the system call. See <see cref="MacOsNative"/> for why.
///
/// EVERY HANDLE IS RELEASED IN A FINALLY. IOKit objects, Core Foundation objects, Mach send rights
/// and memory the kernel allocated into this task are all owned by the caller once handed over. The
/// probe behind this runs every five seconds for weeks at a time, so one missed release is not a leak
/// that shows up in a test - it is a leak that shows up as a Director that has to be restarted.
/// </summary>
internal static class MacOsPlatformReader
{
    /// <summary>kIOMainPortDefault. Zero asks IOKit for the default port rather than naming one.</summary>
    private const uint MainPortDefault = 0;

    /// <summary>The IOKit registry class every physical disk's driver is an instance of.</summary>
    private const string BlockStorageDriverClass = "IOBlockStorageDriver";

    /// <summary>The registry property holding the cumulative read and write counters.</summary>
    private const string StatisticsProperty = "Statistics";

    /// <summary>The registry property holding the medium type, found on the device ABOVE the driver.</summary>
    private const string DeviceCharacteristicsProperty = "Device Characteristics";

    /// <summary>The registry property holding the device name, found on the media BELOW the driver.</summary>
    private const string BsdNameProperty = "BSD Name";

    // ---------------------------------------------------------------------------------------------
    // Disks
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Captures every physical disk the machine has, as the three pieces of registry the probe needs.
    ///
    /// WHY THREE SEPARATE REGISTRY LOOKUPS PER DISK. The figures are not all on one entry, and on
    /// this platform they never have been. Measured on an Apple M4 on 2026-10-15, the statistics are
    /// on the <c>IOBlockStorageDriver</c> entry itself; the device name "disk0" is on the
    /// <c>IOMedia</c> entry BELOW it; and the medium type is inside "Device Characteristics" on the
    /// <c>IOBlockStorageDevice</c> entry ABOVE it. So the statistics are read from the entry, the
    /// name is searched for downwards, and the characteristics are searched for upwards.
    /// </summary>
    internal static List<MacOsRawReadings.DiskCapture> ReadDisks()
    {
        var disks = new List<MacOsRawReadings.DiskCapture>();

        // IOServiceGetMatchingServices CONSUMES a reference on the matching dictionary whether it
        // succeeds or fails, so the dictionary is never released here. Releasing it as well would be
        // an over-release of a kernel object.
        var matching = MacOsNative.IOServiceMatching(BlockStorageDriverClass);
        if (matching == IntPtr.Zero) return disks;

        if (MacOsNative.IOServiceGetMatchingServices(MainPortDefault, matching, out var iterator) != 0)
            return disks;

        try
        {
            uint entry;
            while ((entry = MacOsNative.IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    disks.Add(ReadOneDisk(entry));
                }
                finally
                {
                    MacOsNative.IOObjectRelease(entry);
                }
            }
        }
        finally
        {
            MacOsNative.IOObjectRelease(iterator);
        }

        return disks;
    }

    private static MacOsRawReadings.DiskCapture ReadOneDisk(uint driverEntry)
    {
        var statistics = CopyPropertyAsText(driverEntry, StatisticsProperty, options: 0);

        var bsdNameText = CopyPropertyAsText(
            driverEntry, BsdNameProperty, MacOsNative.RegistryIterateRecursively);

        var characteristics = CopyPropertyAsText(
            driverEntry,
            DeviceCharacteristicsProperty,
            MacOsNative.RegistryIterateRecursively | MacOsNative.RegistryIterateParents);

        return new MacOsRawReadings.DiskCapture(
            BsdNamePropertyList: bsdNameText,
            DeviceCharacteristicsPropertyList: characteristics,
            StatisticsPropertyList: statistics);
    }

    /// <summary>
    /// Finds one registry property and hands it back as the operating system's own property list
    /// text. Returns null when the property is not there, which is an answer the caller must handle
    /// rather than a failure.
    /// </summary>
    private static string? CopyPropertyAsText(uint entry, string key, uint options)
    {
        var cfKey = MacOsNative.CFStringCreateWithCString(
            IntPtr.Zero, key, MacOsNative.StringEncodingUtf8);
        if (cfKey == IntPtr.Zero) return null;

        IntPtr property;
        try
        {
            property = MacOsNative.IORegistryEntrySearchCFProperty(
                entry, MacOsNative.IoServicePlane, cfKey, IntPtr.Zero, options);
        }
        finally
        {
            MacOsNative.CFRelease(cfKey);
        }

        if (property == IntPtr.Zero) return null;

        try
        {
            return CopyAsPropertyListText(property);
        }
        finally
        {
            MacOsNative.CFRelease(property);
        }
    }

    /// <summary>
    /// Serialises a Core Foundation value to property list text. This is the probe's ONE way of
    /// getting a value out of Core Foundation, so that the reading of it is done by pure functions a
    /// test can drive on any platform.
    /// </summary>
    private static string? CopyAsPropertyListText(IntPtr propertyList)
    {
        var data = MacOsNative.CFPropertyListCreateData(
            IntPtr.Zero, propertyList, MacOsNative.PropertyListXmlFormat, 0, IntPtr.Zero);
        if (data == IntPtr.Zero) return null;

        try
        {
            var length = (long)MacOsNative.CFDataGetLength(data);
            if (length <= 0) return null;

            var bytes = MacOsNative.CFDataGetBytePtr(data);
            if (bytes == IntPtr.Zero) return null;

            var buffer = new byte[length];
            Marshal.Copy(bytes, buffer, 0, (int)length);
            return System.Text.Encoding.UTF8.GetString(buffer);
        }
        finally
        {
            MacOsNative.CFRelease(data);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // sysctl
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads a sysctl value as the bytes the kernel wrote, or null when it could not be read.
    ///
    /// The kernel is asked for the SIZE FIRST and the buffer is made to fit, rather than a buffer of a
    /// size this code believes in being passed in. A structure that grows in a later macOS then reads
    /// short and is reported as unreadable, instead of being read with its last field cut off.
    /// </summary>
    internal static byte[]? ReadSysctlBytes(string name)
    {
        var length = IntPtr.Zero;
        if (MacOsNative.sysctlbyname(name, IntPtr.Zero, ref length, IntPtr.Zero, IntPtr.Zero) != 0) return null;

        var size = (long)length;
        if (size <= 0 || size > 1 << 20) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var actual = (IntPtr)size;
            if (MacOsNative.sysctlbyname(name, buffer, ref actual, IntPtr.Zero, IntPtr.Zero) != 0) return null;

            var written = (long)actual;
            if (written <= 0) return null;

            var bytes = new byte[written];
            Marshal.Copy(buffer, bytes, 0, (int)written);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads a sysctl value that is a single whole number. Returns null when it cannot be read.</summary>
    internal static int? ReadSysctlInt32(string name)
    {
        var bytes = ReadSysctlBytes(name);
        if (bytes is null || bytes.Length < sizeof(int)) return null;
        return BitConverter.ToInt32(bytes, 0);
    }

    // ---------------------------------------------------------------------------------------------
    // Processor ticks
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Captures the per-core tick counters. Returns null when the kernel refused, with the reason.
    ///
    /// TWO THINGS HERE MUST BE GIVEN BACK and both are in a finally: the send right from
    /// <c>mach_host_self</c>, and the array the kernel allocated into this task. The second is the one
    /// that is easy to miss, because nothing fails when it is missed - the probe just grows a page
    /// every five seconds for as long as the Director runs.
    /// </summary>
    internal static uint[]? ReadProcessorTicks(out int coreCount, out string? reason)
    {
        coreCount = 0;
        reason = null;

        var host = MacOsNative.mach_host_self();
        try
        {
            var result = MacOsNative.host_processor_info(
                host, MacOsNative.ProcessorCpuLoadInfo, out var cores, out var info, out var count);

            if (result != 0)
            {
                reason = $"host_processor_info returned {result}";
                return null;
            }

            if (info == IntPtr.Zero)
            {
                reason = "host_processor_info returned no counters";
                return null;
            }

            try
            {
                var ticks = new uint[count];
                for (var index = 0; index < count; index++)
                    ticks[index] = unchecked((uint)Marshal.ReadInt32(info, index * sizeof(int)));

                coreCount = (int)cores;
                return ticks;
            }
            finally
            {
                MacOsNative.vm_deallocate(
                    MacOsNative.mach_task_self(), info, (IntPtr)(count * sizeof(int)));
            }
        }
        finally
        {
            MacOsNative.mach_port_deallocate(MacOsNative.mach_task_self(), host);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Volumes
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Captures the mounted file systems as the bytes <c>getfsstat</c> wrote, with the count it
    /// reported. Returns null when the call failed.
    ///
    /// The count is asked for first, and then a little ROOM TO SPARE is allowed for, because a volume
    /// can be mounted between the two calls. Without the spare room that race silently drops the
    /// newest volume; with it, the second call's own return value is still what decides how many
    /// structures are read.
    /// </summary>
    internal static (byte[] Buffer, int Count)? ReadVolumes()
    {
        var count = MacOsNative.getfsstat(IntPtr.Zero, 0, MacOsNative.MountNoWait);
        if (count <= 0) return null;

        var room = count + 8;
        var size = room * MacOsResourceParsing.StatfsSize;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var written = MacOsNative.getfsstat(buffer, size, MacOsNative.MountNoWait);
            if (written <= 0) return null;

            var bytes = new byte[Math.Min(written, room) * MacOsResourceParsing.StatfsSize];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return (bytes, Math.Min(written, room));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Network interfaces
    // ---------------------------------------------------------------------------------------------

    /// <summary>AF_LINK - the address family whose entry carries the interface's own counters.</summary>
    private const byte AddressFamilyLink = 18;

    private const int IfAddrsNextOffset = 0;
    private const int IfAddrsNameOffset = 8;
    private const int IfAddrsAddressOffset = 24;
    private const int IfAddrsDataOffset = 48;

    private const int SockAddrFamilyOffset = 1;

    /// <summary>
    /// Captures every network interface's link-level counters, as the name and the raw bytes of the
    /// <c>if_data</c> hanging off its AF_LINK address.
    ///
    /// ONLY THE AF_LINK ENTRY CARRIES COUNTERS. <c>getifaddrs</c> returns one entry per address, so a
    /// single interface appears several times - once for each network address and once for its own
    /// link-level address - and only the last of those has the byte and packet counters. Adding up all
    /// the entries for an interface would count its traffic once per address it holds.
    /// </summary>
    internal static List<(string Name, byte[] Data)> ReadInterfaces()
    {
        var interfaces = new List<(string Name, byte[] Data)>();

        if (MacOsNative.getifaddrs(out var head) != 0 || head == IntPtr.Zero) return interfaces;

        try
        {
            for (var entry = head; entry != IntPtr.Zero; entry = Marshal.ReadIntPtr(entry, IfAddrsNextOffset))
            {
                var address = Marshal.ReadIntPtr(entry, IfAddrsAddressOffset);
                if (address == IntPtr.Zero) continue;
                if (Marshal.ReadByte(address, SockAddrFamilyOffset) != AddressFamilyLink) continue;

                var data = Marshal.ReadIntPtr(entry, IfAddrsDataOffset);
                if (data == IntPtr.Zero) continue;

                var namePointer = Marshal.ReadIntPtr(entry, IfAddrsNameOffset);
                if (namePointer == IntPtr.Zero) continue;
                var name = Marshal.PtrToStringUTF8(namePointer);
                if (string.IsNullOrEmpty(name)) continue;

                var bytes = new byte[MacOsResourceParsing.InterfaceDataReadLength];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                interfaces.Add((name, bytes));
            }
        }
        finally
        {
            MacOsNative.freeifaddrs(head);
        }

        return interfaces;
    }

    // ---------------------------------------------------------------------------------------------
    // The Mach timebase
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The conversion between Mach absolute time units and nanoseconds. Fixed for the life of the
    /// machine, so it is read once and kept.
    /// </summary>
    internal static MacOsResourceParsing.MachTimebase ReadMachTimebase()
    {
        if (MacOsNative.mach_timebase_info(out var info) != 0) return default;
        return new MacOsResourceParsing.MachTimebase(info.numer, info.denom);
    }

    // ---------------------------------------------------------------------------------------------
    // Processes
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every process identifier on the machine, or null when the list could not be taken.
    ///
    /// <c>proc_listallpids</c> RETURNS A COUNT OF PROCESSES, NOT A COUNT OF BYTES, and it is widely
    /// documented the other way round. Reading its answer as bytes and dividing by four is the
    /// difference between 726 processes and 181: measured on the M4 Mac mini on 2026-10-15, where
    /// <c>ps</c> listed 725. A quarter of the machine's processes is not an obviously broken reading -
    /// it is a plausible one, and it would have quietly left the busiest process out of the list the
    /// detail screen names the culprit from.
    /// </summary>
    internal static int[]? ReadProcessIds()
    {
        var count = MacOsNative.proc_listallpids(IntPtr.Zero, 0);
        if (count <= 0) return null;

        // Room to spare, because processes start between the two calls.
        var room = count + 64;
        var size = room * sizeof(int);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var written = MacOsNative.proc_listallpids(buffer, size);
            if (written <= 0) return null;

            var pids = new int[Math.Min(written, room)];
            Marshal.Copy(buffer, pids, 0, pids.Length);
            return pids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Captures one process's three raw structures. Any of them can be null, which means the process
    /// has gone or this user may not read it - a reading of null figures for that one process, never a
    /// failure of the whole set.
    /// </summary>
    internal static (byte[]? TaskInfo, byte[]? BsdInfo, byte[]? Rusage) ReadProcess(int pid) =>
        (ReadProcessInfo(pid, MacOsNative.ProcPidTaskInfo, MacOsResourceParsing.ProcessTaskInfoSize),
         ReadProcessInfo(pid, MacOsNative.ProcPidTBsdInfo, MacOsResourceParsing.ProcessBsdInfoSize),
         ReadProcessRusage(pid));

    private static byte[]? ReadProcessInfo(int pid, int flavor, int size)
    {
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var written = MacOsNative.proc_pidinfo(pid, flavor, 0, buffer, size);
            if (written < size) return null;

            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[]? ReadProcessRusage(int pid)
    {
        var size = MacOsResourceParsing.RusageV2Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (MacOsNative.proc_pid_rusage(pid, MacOsNative.RusageInfoV2, buffer) != 0) return null;

            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }


    // ---------------------------------------------------------------------------------------------
    // Which physical disk a volume actually sits on
    // ---------------------------------------------------------------------------------------------

    /// <summary>How far up the registry to walk before giving up. The real depth on this platform is
    /// about six; the cap exists so a registry that loops cannot hang the sampler.</summary>
    private const int AncestorWalkLimit = 64;

    /// <summary>
    /// Every device name found above a volume's own device in the storage stack.
    ///
    /// WHY THIS IS NEEDED AT ALL. A volume's own device name is not the disk it is stored on. On a
    /// modern Mac the boot volume reports <c>/dev/disk3s5</c>, and <c>disk3</c> does not exist as
    /// hardware - it is an Apple file system container SYNTHESIZED on top of a partition of the real
    /// disk. Measured on the M4 Mac mini, the real chain is disk0, the hardware, then its partition
    /// disk0s2, then the synthesized container disk3, then the volume disk3s5. Reporting a volume as
    /// living on "disk3" would attribute its activity to a disk that has no statistics and no medium
    /// type, and the slowness diagnosis would never be able to name the hardware that was busy.
    ///
    /// SO THE WALK GOES UPWARDS AND RETURNS EVERYTHING IT FINDS, and the caller keeps the names that
    /// match a disk it has statistics for. That way this function never has to decide what counts as a
    /// physical disk, and a volume whose chain cannot be followed returns the names it did find rather
    /// than a guess.
    ///
    /// ALL PARENTS ARE WALKED, NOT THE FIRST. A container can be built on more than one physical
    /// store, and following only the first parent would report one of two disks with no sign that the
    /// other was missed.
    /// </summary>
    internal static List<string> ReadAncestorDeviceNames(string volumeDeviceName)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(volumeDeviceName)) return found;

        var matching = MacOsNative.IOBSDNameMatching(MainPortDefault, 0, volumeDeviceName);
        if (matching == IntPtr.Zero) return found;

        if (MacOsNative.IOServiceGetMatchingServices(MainPortDefault, matching, out var iterator) != 0)
            return found;

        uint start;
        try
        {
            start = MacOsNative.IOIteratorNext(iterator);
        }
        finally
        {
            MacOsNative.IOObjectRelease(iterator);
        }

        if (start == 0) return found;

        // Breadth-first over the parents, because the registry's parent links converge rather than
        // forming a tree: without the seen set the same branch is walked repeatedly and the same disk
        // name comes back several times.
        var seen = new HashSet<ulong>();
        var queue = new Queue<uint>();
        queue.Enqueue(start);
        var visited = 0;

        try
        {
            while (queue.Count > 0 && visited < AncestorWalkLimit)
            {
                var entry = queue.Dequeue();
                visited++;

                try
                {
                    if (MacOsNative.IORegistryEntryGetParentIterator(
                            entry, MacOsNative.IoServicePlane, out var parents) != 0)
                        continue;

                    try
                    {
                        uint parent;
                        while ((parent = MacOsNative.IOIteratorNext(parents)) != 0)
                        {
                            var keep = false;
                            try
                            {
                                if (MacOsNative.IORegistryEntryGetRegistryEntryID(parent, out var id) != 0) continue;
                                if (!seen.Add(id)) continue;

                                var nameText = CopyPropertyAsText(parent, BsdNameProperty, options: 0);
                                var name = nameText is null
                                    ? null
                                    : MacOsResourceParsing.ReadPropertyListString(nameText);
                                if (!string.IsNullOrEmpty(name)) found.Add(name);

                                queue.Enqueue(parent);
                                keep = true;
                            }
                            finally
                            {
                                // Kept alive only while it is still queued; released now otherwise.
                                if (!keep) MacOsNative.IOObjectRelease(parent);
                            }
                        }
                    }
                    finally
                    {
                        MacOsNative.IOObjectRelease(parents);
                    }
                }
                finally
                {
                    if (entry != start) MacOsNative.IOObjectRelease(entry);
                }
            }
        }
        finally
        {
            MacOsNative.IOObjectRelease(start);
            while (queue.Count > 0) MacOsNative.IOObjectRelease(queue.Dequeue());
        }

        return found;
    }

}
