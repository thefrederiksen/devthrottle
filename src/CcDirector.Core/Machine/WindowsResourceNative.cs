using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CcDirector.Core.Machine;

/// <summary>
/// The Windows calls behind <see cref="WindowsResourceProbe"/>, and nothing else. Every method here
/// asks the operating system for one structure and hands back its RAW BYTES; decoding them is
/// <see cref="WindowsResourceParsing"/>'s job, so the decoding is tested from captured bytes.
///
/// NO SUBPROCESS, AND NO PERFORMANCE COUNTER LIBRARY. Every figure comes from a direct call:
/// NtQuerySystemInformation for processors, processes and page files, IOCTL_DISK_PERFORMANCE for each
/// physical disk (the same cumulative counters the PhysicalDisk performance counters are computed
/// from), and GetIfTable2 for the network. Performance counter names are translated on non-English
/// Windows; none of these calls are.
///
/// A failing call throws <see cref="Win32Exception"/> carrying the error code. The probe catches it at
/// the boundary and turns it into a reading that says it could not be read.
/// </summary>
internal static class WindowsResourceNative
{
    private const int SystemProcessInformation = 5;
    private const int SystemProcessorPerformanceInformation = 8;
    private const int SystemPageFileInformation = 18;

    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    private const uint IoctlDiskPerformance = 0x00070020;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const int ErrorFileNotFound = 2;
    private const int ErrorMoreData = 234;

    /// <summary>The most physical disks looked for. PhysicalDrive numbers are allotted from zero, with gaps after a removal.</summary>
    internal const int MaxPhysicalDiskNumber = 63;

    /// <summary>GetSystemTimes: whole-machine idle, kernel and user time, in 100-nanosecond units. Kernel includes idle.</summary>
    internal static CpuTimes ReadSystemTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemTimes failed");
        return new CpuTimes(idle, kernel, user);
    }

    internal static byte[] ReadProcessorPerformance() =>
        QuerySystemInformation(SystemProcessorPerformanceInformation, Environment.ProcessorCount * WindowsResourceParsing.ProcessorPerformanceSize, out _);

    /// <summary>The process chain, and the address its buffer was at, because image names point into it.</summary>
    internal static byte[] ReadProcessInformation(out long bufferAddress) =>
        QuerySystemInformation(SystemProcessInformation, 1 << 20, out bufferAddress);

    internal static byte[] ReadPageFileInformation() =>
        QuerySystemInformation(SystemPageFileInformation, 4096, out _);

    /// <summary>
    /// Calls NtQuerySystemInformation, growing the buffer until the structure fits. The process list
    /// can grow between the size query and the read, which is why this loops rather than asking once.
    /// The copy out of unmanaged memory is what lets the parser, and a captured fixture, see plain bytes.
    /// </summary>
    private static byte[] QuerySystemInformation(int informationClass, int initialSize, out long bufferAddress)
    {
        var size = Math.Max(initialSize, 64);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(informationClass, buffer, size, out var returned);
                if (status is StatusInfoLengthMismatch or StatusBufferTooSmall)
                {
                    size = Math.Max(size * 2, returned + 64 * 1024);
                    continue;
                }
                if (status != 0)
                    throw new Win32Exception($"NtQuerySystemInformation class {informationClass} returned status 0x{status:X8}");

                var bytes = new byte[returned];
                Marshal.Copy(buffer, bytes, 0, returned);
                bufferAddress = buffer.ToInt64();
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new Win32Exception($"NtQuerySystemInformation class {informationClass} still did not fit after growing to {size} bytes");
    }

    /// <summary>
    /// Opens \\.\PhysicalDriveN with NO access rights - enough for the performance, geometry and
    /// property queries, all of which are FILE_ANY_ACCESS, and so no administrator is needed. Null when
    /// that disk number does not exist.
    /// </summary>
    internal static SafeFileHandle? OpenPhysicalDisk(int number)
    {
        var handle = CreateFileW($@"\\.\PhysicalDrive{number}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error == ErrorFileNotFound) return null;
        throw new Win32Exception(error, $"opening PhysicalDrive{number} failed");
    }

    /// <summary>Opens \\.\C: (for example) with no access rights, for the disk-extents query.</summary>
    internal static SafeFileHandle OpenVolume(char driveLetter)
    {
        var handle = CreateFileW($@"\\.\{driveLetter}:", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, $"opening volume {driveLetter}: failed");
    }

    internal static byte[] ReadDiskPerformance(SafeFileHandle disk) =>
        DeviceIoControlBytes(disk, IoctlDiskPerformance, null, WindowsResourceParsing.DiskPerformanceSize);

    internal static byte[] ReadDiskGeometry(SafeFileHandle disk) =>
        DeviceIoControlBytes(disk, IoctlDiskGetDriveGeometryEx, null, 256);

    internal static byte[] ReadStorageDescriptor(SafeFileHandle disk) =>
        DeviceIoControlBytes(disk, IoctlStorageQueryProperty, StoragePropertyQuery(StorageDeviceProperty), 1024);

    internal static byte[] ReadSeekPenalty(SafeFileHandle disk) =>
        DeviceIoControlBytes(disk, IoctlStorageQueryProperty, StoragePropertyQuery(StorageDeviceSeekPenaltyProperty), 12);

    internal static byte[] ReadVolumeDiskExtents(SafeFileHandle volume) =>
        DeviceIoControlBytes(volume, IoctlVolumeGetVolumeDiskExtents, null, 8 + 24 * 32);

    /// <summary>STORAGE_PROPERTY_QUERY: PropertyId, QueryType (PropertyStandardQuery = 0), one AdditionalParameters byte.</summary>
    private static byte[] StoragePropertyQuery(int propertyId)
    {
        var query = new byte[12];
        BitConverter.TryWriteBytes(query.AsSpan(0, 4), propertyId);
        return query;
    }

    private static byte[] DeviceIoControlBytes(SafeFileHandle device, uint code, byte[]? input, int outputSize)
    {
        var output = new byte[outputSize];
        if (!DeviceIoControl(device, code, input, input?.Length ?? 0, output, output.Length, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorMoreData)
                throw new Win32Exception(error, $"DeviceIoControl 0x{code:X8} failed");
        }
        return output.AsSpan(0, returned).ToArray();
    }

    /// <summary>Free and total bytes on a volume, as the caller would see them.</summary>
    internal static (ulong Free, ulong Total) ReadVolumeSpace(string root)
    {
        if (!GetDiskFreeSpaceExW(root, out var freeToCaller, out var total, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetDiskFreeSpaceEx {root} failed");
        return (freeToCaller, total);
    }

    /// <summary>The fixed (non-removable, non-network) drive letters on the machine.</summary>
    internal static IReadOnlyList<char> FixedDriveLetters()
    {
        var mask = GetLogicalDrives();
        if (mask == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetLogicalDrives failed");

        var letters = new List<char>();
        for (var i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            var letter = (char)('A' + i);
            if (GetDriveTypeW($"{letter}:\\") == DriveFixed) letters.Add(letter);
        }
        return letters;
    }

    private const uint DriveFixed = 3;

    /// <summary>GetIfTable2, copied out as MIB_IF_TABLE2 bytes and the table freed.</summary>
    internal static byte[] ReadInterfaceTable()
    {
        var error = GetIfTable2(out var table);
        if (error != 0) throw new Win32Exception(error, "GetIfTable2 failed");
        try
        {
            var count = Marshal.ReadInt32(table);
            var bytes = new byte[8 + count * WindowsResourceParsing.InterfaceRowSize];
            Marshal.Copy(table, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string root, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);
}
