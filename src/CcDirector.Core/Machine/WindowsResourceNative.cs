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
/// NtQuerySystemInformation for processors, processes, paging activity and page files, IOCTL_DISK_PERFORMANCE for each
/// physical disk (the same cumulative counters the PhysicalDisk performance counters are computed
/// from). Performance counter names are translated on non-English
/// Windows; none of these calls are.
///
/// A failing call throws <see cref="Win32Exception"/> carrying the error code. The probe catches it at
/// the boundary and turns it into a reading that says it could not be read.
/// </summary>
internal static class WindowsResourceNative
{
    private const int SystemPerformanceInformation = 2;
    private const int SystemProcessInformation = 5;
    private const int SystemProcessorPerformanceInformation = 8;
    private const int SystemPageFileInformation = 18;

    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    private const uint IoctlDiskPerformance = 0x00070020;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

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

    private const ushort AllProcessorGroups = 0xFFFF;

    /// <summary>
    /// Every logical processor the machine has, in every processor group. Not
    /// <see cref="Environment.ProcessorCount"/>, which counts only the processors THIS process may run
    /// on: under an affinity mask or a job object's processor limit it is smaller, and a per-process
    /// share computed over it would no longer be a share of the whole machine.
    /// </summary>
    internal static int ReadLogicalProcessorCount()
    {
        var count = GetActiveProcessorCount(AllProcessorGroups);
        if (count == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetActiveProcessorCount failed");
        return (int)count;
    }

    /// <summary>The most logical processors one processor group can hold.</summary>
    private const int MaxProcessorsInOneGroup = 64;

    /// <summary>
    /// One entry per logical processor in this process's processor group. The buffer is sized for a full
    /// group, NOT from <see cref="Environment.ProcessorCount"/>: the kernel fills only as many entries as
    /// fit and reports success, so a buffer sized from a count shrunk by an affinity mask or a job limit
    /// would silently leave processors out of the busiest-core scan. The returned length decides the count.
    /// </summary>
    internal static byte[] ReadProcessorPerformance()
    {
        var size = MaxProcessorsInOneGroup * WindowsResourceParsing.ProcessorPerformanceSize;
        return QuerySystemInformation(SystemProcessorPerformanceInformation, ref size, out _);
    }

    /// <summary>
    /// The process chain, and the address its buffer was at, because image names point into it.
    ///
    /// <paramref name="sizeHint"/> is the size the list needed last time, kept by the caller and updated
    /// here. On a machine with 800 processes and 12,000 threads the list is about 1.4 MB, and the kernel
    /// spends around 10 milliseconds filling it; guessing too small doubles that with a call that fails.
    /// </summary>
    internal static byte[] ReadProcessInformation(ref int sizeHint, out long bufferAddress) =>
        QuerySystemInformation(SystemProcessInformation, ref sizeHint, out bufferAddress);

    internal static byte[] ReadSystemPerformance()
    {
        var size = 1024;
        return QuerySystemInformation(SystemPerformanceInformation, ref size, out _);
    }

    internal static byte[] ReadPageFileInformation()
    {
        var size = 4096;
        return QuerySystemInformation(SystemPageFileInformation, ref size, out _);
    }

    /// <summary>
    /// Calls NtQuerySystemInformation, growing the buffer until the structure fits. The process list
    /// can grow between the size query and the read, which is why this loops rather than asking once.
    /// <paramref name="size"/> comes back as the size that fitted plus a quarter, so the next call for a
    /// list that has grown a little still fits first time.
    /// The copy out of unmanaged memory is what lets the parser, and a captured fixture, see plain bytes.
    /// </summary>
    private static byte[] QuerySystemInformation(int informationClass, ref int size, out long bufferAddress)
    {
        size = Math.Max(size, 64);
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
                size = returned + returned / 4;
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
    /// Opens \\.\PhysicalDriveN with NO access rights - enough for the performance and seek-penalty
    /// queries, all of which are FILE_ANY_ACCESS, and so no administrator is needed. Null when
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

    /// <summary>
    /// VOLUME_DISK_EXTENTS for a volume. Asked first with room for 32 extents; a volume spread across more
    /// answers with only part of the array but the full count, so it is asked again at the size that count
    /// needs rather than read as a volume whose disks are unknown.
    /// </summary>
    internal static byte[] ReadVolumeDiskExtents(SafeFileHandle volume)
    {
        var bytes = DeviceIoControlBytes(volume, IoctlVolumeGetVolumeDiskExtents, null, 8 + 24 * 32);
        if (bytes.Length < 4) return bytes;
        var needed = 8 + 24 * (long)BitConverter.ToUInt32(bytes, 0);
        if (needed <= bytes.Length || needed > int.MaxValue) return bytes;
        return DeviceIoControlBytes(volume, IoctlVolumeGetVolumeDiskExtents, null, (int)needed);
    }

    internal static byte[] ReadDiskPerformance(SafeFileHandle disk) =>
        DeviceIoControlBytes(disk, IoctlDiskPerformance, null, WindowsResourceParsing.DiskPerformanceSize);

    internal static byte[] ReadSeekPenalty(SafeFileHandle disk) =>
        DeviceIoControlBytes(disk, IoctlStorageQueryProperty, StoragePropertyQuery(StorageDeviceSeekPenaltyProperty), 12);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
