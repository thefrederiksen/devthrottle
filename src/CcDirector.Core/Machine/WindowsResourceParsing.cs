using System.Buffers.Binary;
using System.Text;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE WINDOWS STRUCTURES, PARSED FROM BYTES (Resource Monitor mission, phase 1).
///
/// Every structure the Windows probe reads from the operating system comes back as a block of bytes,
/// and every one of them is decoded here, by pure functions that take the captured bytes and return
/// values. That is what lets the decoding be tested on any machine from bytes captured on a real one
/// (src/CcDirector.Core.Tests/TestData/resource-monitor/windows/), rather than only on the machine
/// that happens to run it.
///
/// THE OFFSETS ARE THE 64-BIT LAYOUTS. The Director ships as a 64-bit process only (x64 and arm64),
/// and the probe refuses to read on a 32-bit one rather than decode the wrong layout. Each offset is
/// written next to the field it reads, from the Windows SDK headers (winternl.h, winioctl.h,
/// netioapi.h) and, for the two process structures the SDK only partly documents, from their
/// long-stable public layouts.
///
/// Nothing here allocates a handle, starts a process or touches the operating system.
/// </summary>
internal static class WindowsResourceParsing
{
    // ---------------------------------------------------------------------------------------------
    // CPU
    // ---------------------------------------------------------------------------------------------

    /// <summary>Size of SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION: five 64-bit times, a 32-bit count, padded to 8.</summary>
    internal const int ProcessorPerformanceSize = 48;

    /// <summary>
    /// Decodes the array <c>NtQuerySystemInformation(SystemProcessorPerformanceInformation)</c> returns,
    /// one entry per logical processor. Times are in 100-nanosecond units, cumulative since boot, and
    /// - as with GetSystemTimes - KERNEL TIME INCLUDES IDLE TIME.
    /// </summary>
    internal static IReadOnlyList<CpuTimes> ParseProcessorPerformance(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length % ProcessorPerformanceSize != 0)
            throw new FormatException($"processor performance buffer is {buffer.Length} bytes, not a multiple of {ProcessorPerformanceSize}");

        var cores = new CpuTimes[buffer.Length / ProcessorPerformanceSize];
        for (var i = 0; i < cores.Length; i++)
        {
            var entry = buffer.Slice(i * ProcessorPerformanceSize, ProcessorPerformanceSize);
            cores[i] = new CpuTimes(
                Idle: ReadInt64(entry, 0),
                Kernel: ReadInt64(entry, 8),
                User: ReadInt64(entry, 16));
        }
        return cores;
    }

    /// <summary>
    /// The fraction of the interval a processor was busy, from two cumulative readings, 0.0 to 1.0.
    /// Null when no time passed between them, or a counter went backwards (a reading out of order), so
    /// a caller never divides by an interval that was never measured.
    /// </summary>
    internal static double? BusyFraction(CpuTimes earlier, CpuTimes later)
    {
        var idle = later.Idle - earlier.Idle;
        var total = (later.Kernel - earlier.Kernel) + (later.User - earlier.User);
        if (total <= 0 || idle < 0 || idle > total) return null;
        return (double)(total - idle) / total;
    }

    // ---------------------------------------------------------------------------------------------
    // Processes
    // ---------------------------------------------------------------------------------------------

    /// <summary>Size of SYSTEM_PROCESS_INFORMATION on 64-bit Windows, before its thread array.</summary>
    internal const int ProcessInformationSize = 256;

    /// <summary>Size of SYSTEM_THREAD_INFORMATION on 64-bit Windows.</summary>
    internal const int ThreadInformationSize = 80;

    /// <summary>KTHREAD_STATE Ready: runnable, and waiting only for a processor.</summary>
    internal const uint ThreadStateReady = 1;

    /// <summary>KTHREAD_STATE DeferredReady: chosen to run, not yet given a processor. Also waiting on the CPU.</summary>
    internal const uint ThreadStateDeferredReady = 7;

    /// <summary>
    /// Decodes the chain <c>NtQuerySystemInformation(SystemProcessInformation)</c> returns: one entry per
    /// process, each followed by an entry per thread, linked by NextEntryOffset.
    ///
    /// ONE CALL, EVERY PROCESS, NO HANDLES. This structure carries each process's parent, creation
    /// time, processor time, memory, input/output and hard fault count, so the probe never opens a
    /// process - and therefore never meets "access denied" on a protected or another user's process.
    ///
    /// THE IMAGE NAME IS A POINTER INTO THIS SAME BUFFER, so <paramref name="bufferAddress"/> is the
    /// address the buffer was at when the kernel filled it. A name pointing outside the buffer is read
    /// as no name rather than out of range.
    /// </summary>
    internal static IReadOnlyList<RawProcess> ParseProcessInformation(ReadOnlySpan<byte> buffer, long bufferAddress)
    {
        var processes = new List<RawProcess>();
        var offset = 0;

        while (true)
        {
            if (offset < 0 || offset + ProcessInformationSize > buffer.Length)
                throw new FormatException($"process entry at offset {offset} runs past the {buffer.Length}-byte buffer");

            var entry = buffer[offset..];
            var next = (int)ReadUInt32(entry, 0);
            var threadCount = (int)ReadUInt32(entry, 4);

            var threadsEnd = ProcessInformationSize + (long)threadCount * ThreadInformationSize;
            if (threadsEnd > entry.Length)
                throw new FormatException($"process entry at offset {offset} claims {threadCount} threads, which runs past the buffer");

            var waitingOnCpu = 0;
            for (var t = 0; t < threadCount; t++)
            {
                var state = ReadUInt32(entry, ProcessInformationSize + t * ThreadInformationSize + 68);
                if (state is ThreadStateReady or ThreadStateDeferredReady) waitingOnCpu++;
            }

            processes.Add(new RawProcess(
                ProcessId: (int)ReadInt64(entry, 80),
                ParentProcessId: (int)ReadInt64(entry, 88),
                Name: ReadUnicodeString(buffer, offset + 56, bufferAddress),
                CreateTime: ReadInt64(entry, 32),
                UserTime: ReadInt64(entry, 40),
                KernelTime: ReadInt64(entry, 48),
                HardFaultCount: ReadUInt32(entry, 16),
                ThreadCount: threadCount,
                ThreadsWaitingOnCpu: waitingOnCpu,
                WorkingSetBytes: ReadUInt64(entry, 144),
                PrivateBytes: ReadUInt64(entry, 200),
                ReadBytes: ReadUInt64(entry, 232),
                WriteBytes: ReadUInt64(entry, 240)));

            if (next == 0) break;
            offset += next;
        }

        return processes;
    }

    /// <summary>
    /// Reads a UNICODE_STRING (16-bit Length in bytes, 16-bit MaximumLength, padding, 64-bit Buffer)
    /// whose text lives inside the same captured buffer. The System Idle Process has a null name.
    /// </summary>
    private static string? ReadUnicodeString(ReadOnlySpan<byte> buffer, int at, long bufferAddress)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer[at..]);
        var pointer = ReadInt64(buffer, at + 8);
        if (length == 0 || pointer == 0) return null;

        var start = pointer - bufferAddress;
        if (start < 0 || start + length > buffer.Length) return null;
        return Encoding.Unicode.GetString(buffer.Slice((int)start, length));
    }

    // ---------------------------------------------------------------------------------------------
    // Page file
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Decodes <c>NtQuerySystemInformation(SystemPageFileInformation)</c>: one entry per page file,
    /// linked by NextEntryOffset, sizes in PAGES. An empty buffer is a machine with no page file, which
    /// is a real configuration and reads as zero total, not as a failure.
    /// </summary>
    internal static PageFileTotals ParsePageFileInformation(ReadOnlySpan<byte> buffer, ulong pageSize)
    {
        ulong totalPages = 0, inUsePages = 0;
        var count = 0;
        var offset = 0;

        while (buffer.Length >= offset + 16)
        {
            var entry = buffer[offset..];
            var next = (int)ReadUInt32(entry, 0);
            totalPages += ReadUInt32(entry, 4);
            inUsePages += ReadUInt32(entry, 8);
            count++;
            if (next == 0) break;
            offset += next;
        }

        return new PageFileTotals(count, totalPages * pageSize, inUsePages * pageSize);
    }

    // ---------------------------------------------------------------------------------------------
    // Physical disks
    // ---------------------------------------------------------------------------------------------

    /// <summary>Size of DISK_PERFORMANCE on 64-bit Windows.</summary>
    internal const int DiskPerformanceSize = 88;

    /// <summary>
    /// Decodes DISK_PERFORMANCE from <c>IOCTL_DISK_PERFORMANCE</c>: cumulative bytes, counts and times
    /// for one physical disk, the same counters the PhysicalDisk performance counters are computed
    /// from. Times are in 100-nanosecond units.
    /// </summary>
    internal static DiskCounters ParseDiskPerformance(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 68)
            throw new FormatException($"DISK_PERFORMANCE is {buffer.Length} bytes, expected at least 68");

        return new DiskCounters(
            BytesRead: ReadInt64(buffer, 0),
            BytesWritten: ReadInt64(buffer, 8),
            ReadTime: ReadInt64(buffer, 16),
            WriteTime: ReadInt64(buffer, 24),
            IdleTime: ReadInt64(buffer, 32),
            ReadCount: ReadUInt32(buffer, 40),
            WriteCount: ReadUInt32(buffer, 44),
            QueueDepth: ReadUInt32(buffer, 48),
            QueryTime: ReadInt64(buffer, 56));
    }

    /// <summary>
    /// One disk's activity between two cumulative readings: how busy it was, how fast it moved bytes,
    /// how long a read and a write took on average, and how many requests were in flight on average.
    /// The last is the time every request spent at the disk, added up, over the interval - the same
    /// arithmetic as the "Avg. Disk Queue Length" performance counter, rather than the instantaneous
    /// depth, which on a 5-second sample is one moment's luck. A field is null when the interval cannot
    /// answer it - no time passed, or no read happened so there is no time per read - never zero.
    /// </summary>
    internal static DiskRates DiskRatesBetween(DiskCounters earlier, DiskCounters later)
    {
        var interval = later.QueryTime - earlier.QueryTime;
        if (interval <= 0) return new DiskRates(null, null, null, null, null, null);

        var seconds = interval / 1e7;
        var idle = later.IdleTime - earlier.IdleTime;

        // The idle clock and the query clock are read a moment apart, so idle can run a hair past the
        // interval on a disk that did nothing. Clamped to the range a fraction can have, not trusted
        // to stay inside it.
        var busy = Math.Clamp(1.0 - (double)idle / interval, 0.0, 1.0);

        var reads = unchecked(later.ReadCount - earlier.ReadCount);
        var writes = unchecked(later.WriteCount - earlier.WriteCount);

        return new DiskRates(
            BusyFraction: busy,
            ReadBytesPerSecond: Math.Max(0, later.BytesRead - earlier.BytesRead) / seconds,
            WriteBytesPerSecond: Math.Max(0, later.BytesWritten - earlier.BytesWritten) / seconds,
            SecondsPerRead: reads == 0 ? null : (later.ReadTime - earlier.ReadTime) / 1e7 / reads,
            SecondsPerWrite: writes == 0 ? null : (later.WriteTime - earlier.WriteTime) / 1e7 / writes,
            AverageQueueLength: Math.Max(0, (later.ReadTime - earlier.ReadTime) + (later.WriteTime - earlier.WriteTime)) / (double)interval);
    }

    /// <summary>
    /// Decodes DEVICE_SEEK_PENALTY_DESCRIPTOR from <c>IOCTL_STORAGE_QUERY_PROPERTY(StorageDeviceSeekPenaltyProperty)</c>.
    /// A disk that incurs a seek penalty has a head to move: it is a spinning hard drive.
    /// </summary>
    internal static bool ParseIncursSeekPenalty(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 9)
            throw new FormatException($"DEVICE_SEEK_PENALTY_DESCRIPTOR is {buffer.Length} bytes, expected at least 9");
        return buffer[8] != 0;
    }

    // ---------------------------------------------------------------------------------------------
    // Volumes
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Decodes VOLUME_DISK_EXTENTS from <c>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS</c>: which physical
    /// disks a volume sits on. A volume spanned or mirrored across disks has one extent per disk, so
    /// the answer is a set, returned in disk order without duplicates.
    /// </summary>
    internal static IReadOnlyList<int> ParseVolumeDiskNumbers(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
            throw new FormatException($"VOLUME_DISK_EXTENTS is {buffer.Length} bytes, expected at least 8");

        var count = (int)ReadUInt32(buffer, 0);
        if (8 + (long)count * 24 > buffer.Length)
            throw new FormatException($"VOLUME_DISK_EXTENTS claims {count} extents, which runs past its {buffer.Length} bytes");

        var disks = new SortedSet<int>();
        for (var i = 0; i < count; i++) disks.Add((int)ReadUInt32(buffer, 8 + i * 24));
        return disks.ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // Network
    // ---------------------------------------------------------------------------------------------

    /// <summary>Size of MIB_IF_ROW2 on 64-bit Windows.</summary>
    internal const int InterfaceRowSize = 1352;

    /// <summary>IF_TYPE_SOFTWARE_LOOPBACK.</summary>
    internal const uint InterfaceTypeLoopback = 24;

    /// <summary>IfOperStatusUp.</summary>
    internal const uint InterfaceOperStatusUp = 1;

    /// <summary>
    /// Decodes MIB_IF_TABLE2 from <c>GetIfTable2</c>: a count, padded to 8, then one MIB_IF_ROW2 per
    /// interface. Every interface is returned; which ones count as the machine's network is
    /// <see cref="IsPhysicalAndUp"/>.
    /// </summary>
    internal static IReadOnlyList<RawInterface> ParseInterfaceTable(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
            throw new FormatException($"MIB_IF_TABLE2 is {buffer.Length} bytes, expected at least 8");

        var count = (int)ReadUInt32(buffer, 0);
        if (8 + (long)count * InterfaceRowSize > buffer.Length)
            throw new FormatException($"MIB_IF_TABLE2 claims {count} rows, which runs past its {buffer.Length} bytes");

        var rows = new RawInterface[count];
        for (var i = 0; i < count; i++)
        {
            var row = buffer.Slice(8 + i * InterfaceRowSize, InterfaceRowSize);
            rows[i] = new RawInterface(
                Index: ReadUInt32(row, 8),
                Alias: ReadFixedUtf16(row.Slice(28, 514)),
                Description: ReadFixedUtf16(row.Slice(542, 514)),
                Type: ReadUInt32(row, 1128),
                IsHardware: (row[1152] & 0x01) != 0,
                OperStatus: ReadUInt32(row, 1156),
                ReceiveLinkSpeed: ReadUInt64(row, 1200),
                InOctets: ReadUInt64(row, 1208),
                OutOctets: ReadUInt64(row, 1280));
        }
        return rows;
    }

    /// <summary>
    /// The interfaces that are the machine's real network: hardware (not a virtual switch, a filter
    /// or a tunnel), up, and not the loopback. Windows lists each physical adapter several times -
    /// once per filter driver bound to it - and only the hardware row carries the adapter itself, so
    /// counting every row would count the same bytes three or four times.
    /// </summary>
    internal static bool IsPhysicalAndUp(RawInterface row) =>
        row.IsHardware && row.OperStatus == InterfaceOperStatusUp && row.Type != InterfaceTypeLoopback;

    // ---------------------------------------------------------------------------------------------
    // Little-endian readers
    // ---------------------------------------------------------------------------------------------

    private static long ReadInt64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt64LittleEndian(b[at..]);
    private static ulong ReadUInt64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b[at..]);
    private static uint ReadUInt32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);

    private static string ReadFixedUtf16(ReadOnlySpan<byte> field)
    {
        var text = Encoding.Unicode.GetString(field);
        var end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }
}

/// <summary>One processor's cumulative times, in 100-nanosecond units. Kernel includes idle.</summary>
internal readonly record struct CpuTimes(long Idle, long Kernel, long User);

/// <summary>One process as SystemProcessInformation reports it. Times in 100-nanosecond units, cumulative.</summary>
internal sealed record RawProcess(
    int ProcessId,
    int ParentProcessId,
    string? Name,
    long CreateTime,
    long UserTime,
    long KernelTime,
    uint HardFaultCount,
    int ThreadCount,
    int ThreadsWaitingOnCpu,
    ulong WorkingSetBytes,
    ulong PrivateBytes,
    ulong ReadBytes,
    ulong WriteBytes);

/// <summary>Every page file added together. <see cref="Count"/> is zero on a machine with none.</summary>
internal readonly record struct PageFileTotals(int Count, ulong TotalBytes, ulong InUseBytes);

/// <summary>DISK_PERFORMANCE: one physical disk's cumulative counters.</summary>
internal readonly record struct DiskCounters(
    long BytesRead,
    long BytesWritten,
    long ReadTime,
    long WriteTime,
    long IdleTime,
    uint ReadCount,
    uint WriteCount,
    uint QueueDepth,
    long QueryTime);

/// <summary>One disk's activity over an interval. Null means the interval could not answer it.</summary>
internal readonly record struct DiskRates(
    double? BusyFraction,
    double? ReadBytesPerSecond,
    double? WriteBytesPerSecond,
    double? SecondsPerRead,
    double? SecondsPerWrite,
    double? AverageQueueLength);

/// <summary>One row of MIB_IF_TABLE2.</summary>
internal sealed record RawInterface(
    uint Index,
    string Alias,
    string Description,
    uint Type,
    bool IsHardware,
    uint OperStatus,
    ulong ReceiveLinkSpeed,
    ulong InOctets,
    ulong OutOctets);
