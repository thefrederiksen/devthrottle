using System.Buffers.Binary;
using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The Windows probe's decoding, tested from bytes captured on a real Windows 11 machine (SOREN_NORTH,
/// 24 logical processors, two NVMe and SATA solid-state disks, 2026-10-15). The fixtures and how they
/// were captured and scrubbed are in TestData/resource-monitor/windows/README.md. These run on every
/// platform: decoding a byte layout needs no Windows.
///
/// The expected figures are the real ones from the capture, not figures recomputed by the code under
/// test, so a wrong offset fails here rather than agreeing with itself.
/// </summary>
[Trait("Category", "Resources")]
public class WindowsResourceParsingTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "resource-monitor", "windows", name));

    private static long ProcessBufferAddress() =>
        long.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "resource-monitor", "windows", "process-information.address.txt")).Trim());

    // ---------------------------------------------------------------------------------------------
    // CPU
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParseProcessorPerformance_RealCapture_OneEntryPerLogicalProcessor()
    {
        var cores = WindowsResourceParsing.ParseProcessorPerformance(Fixture("processor-performance-1.bin"));

        Assert.Equal(24, cores.Count);
        Assert.Equal(new CpuTimes(Idle: 902163437500, Kernel: 1226880000000, User: 331047187500), cores[0]);
        Assert.Equal(new CpuTimes(Idle: 858904843750, Kernel: 1099977812500, User: 457946875000), cores[23]);
    }

    [Fact]
    public void BusyFraction_TwoRealCaptures_GivesEachCoresBusyShare()
    {
        var earlier = WindowsResourceParsing.ParseProcessorPerformance(Fixture("processor-performance-1.bin"));
        var later = WindowsResourceParsing.ParseProcessorPerformance(Fixture("processor-performance-2.bin"));

        // Core 0: idle 6,718,750 of 10,000,000 (kernel 8,906,250 + user 1,093,750), so busy 32.8125%.
        Assert.Equal(0.328125, WindowsResourceParsing.BusyFraction(earlier[0], later[0])!.Value, 9);
        // Core 23: idle 8,750,000 of 10,781,250, so busy 2,031,250 / 10,781,250.
        Assert.Equal(2031250.0 / 10781250.0, WindowsResourceParsing.BusyFraction(earlier[23], later[23])!.Value, 9);

        for (var i = 0; i < earlier.Count; i++)
        {
            var busy = WindowsResourceParsing.BusyFraction(earlier[i], later[i]);
            Assert.NotNull(busy);
            Assert.InRange(busy.Value, 0.0, 1.0);
        }
    }

    [Fact]
    public void BusyFraction_RealGetSystemTimesPair_KernelIncludesIdle()
    {
        // The two GetSystemTimes readings from the same capture (capture-notes.txt). Kernel time counts
        // idle time, so busy is (kernel + user - idle) over (kernel + user): 70,937,500 of 243,281,250.
        // Treating kernel as busy time on its own would report 87%, not 29%.
        var earlier = new CpuTimes(Idle: 21479250781250, Kernel: 27843232187500, User: 9546977500000);
        var later = new CpuTimes(Idle: 21479423125000, Kernel: 27843443281250, User: 9547009687500);

        Assert.Equal(70937500.0 / 243281250.0, WindowsResourceParsing.BusyFraction(earlier, later)!.Value, 9);
    }

    [Fact]
    public void BusyFraction_NoTimePassed_IsNullNotZero()
    {
        var same = new CpuTimes(100, 200, 50);
        Assert.Null(WindowsResourceParsing.BusyFraction(same, same));
    }

    [Fact]
    public void BusyFraction_ReadingsOutOfOrder_IsNull()
    {
        var earlier = new CpuTimes(Idle: 500, Kernel: 1000, User: 400);
        var later = new CpuTimes(Idle: 400, Kernel: 900, User: 300);
        Assert.Null(WindowsResourceParsing.BusyFraction(earlier, later));
    }

    [Fact]
    public void ParseProcessorPerformance_LengthNotAWholeNumberOfEntries_Throws()
    {
        Assert.Throws<FormatException>(() => WindowsResourceParsing.ParseProcessorPerformance(new byte[47]));
    }

    // ---------------------------------------------------------------------------------------------
    // Processes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParseProcessInformation_RealCapture_ReadsEveryEntryInTheChain()
    {
        var processes = WindowsResourceParsing.ParseProcessInformation(Fixture("process-information.bin"), ProcessBufferAddress());

        Assert.Equal(41, processes.Count);

        // The System Idle Process: id 0, and the kernel gives it no name.
        Assert.Equal(0, processes[0].ProcessId);
        Assert.Null(processes[0].Name);

        var system = processes[1];
        Assert.Equal(4, system.ProcessId);
        Assert.Equal("System", system.Name);
        Assert.Equal(534, system.ThreadCount);
    }

    [Fact]
    public void ParseProcessInformation_RealCapture_ReadsParentsAndCreationTimes()
    {
        var processes = WindowsResourceParsing.ParseProcessInformation(Fixture("process-information.bin"), ProcessBufferAddress());

        var services = Assert.Single(processes, p => p.Name == "services.exe");
        Assert.Equal(1816, services.ProcessId);
        Assert.Equal(1676, services.ParentProcessId);
        Assert.Equal("wininit.exe", Assert.Single(processes, p => p.ProcessId == 1676).Name);

        var smss = Assert.Single(processes, p => p.Name == "smss.exe");
        Assert.Equal(new DateTime(2026, 10, 13, 18, 10, 18, DateTimeKind.Utc).AddTicks(2623737), DateTime.FromFileTimeUtc(smss.CreateTime));
    }

    [Fact]
    public void ParseProcessInformation_RealCapture_ReadsMemoryAndInputOutput()
    {
        var processes = WindowsResourceParsing.ParseProcessInformation(Fixture("process-information.bin"), ProcessBufferAddress());

        var lsass = Assert.Single(processes, p => p.Name == "lsass.exe");
        Assert.Equal(35926016UL, lsass.WorkingSetBytes);
        Assert.Equal(35221504UL, lsass.PrivateBytes);
        Assert.Equal(52810615UL, lsass.ReadBytes);
        Assert.Equal(30832433UL, lsass.WriteBytes);

        var compression = Assert.Single(processes, p => p.Name == "Memory Compression");
        Assert.Equal(685561U, compression.HardFaultCount);
    }

    [Fact]
    public void ParseProcessInformation_RealCaptureUnderLoad_CountsThreadsWaitingOnAProcessor()
    {
        // The capture tool ran 48 spinning threads on 24 processors while it read the list, so about
        // half of its own threads were Ready - runnable, waiting for a processor. This is the figure
        // the processor queue length is built from.
        var processes = WindowsResourceParsing.ParseProcessInformation(Fixture("process-information.bin"), ProcessBufferAddress());

        var capture = Assert.Single(processes, p => p.Name == "Capture.exe");
        Assert.Equal(57092, capture.ProcessId);
        Assert.Equal(47756, capture.ParentProcessId);
        Assert.Equal(56, capture.ThreadCount);
        Assert.Equal(25, capture.ThreadsWaitingOnCpu);

        // An idle service has threads, none of them waiting on a processor.
        Assert.Equal(0, Assert.Single(processes, p => p.Name == "lsass.exe").ThreadsWaitingOnCpu);
    }

    [Fact]
    public void ParseProcessInformation_NamePointerOutsideTheBuffer_ReadsAsNoName()
    {
        // The same real bytes, decoded as if the buffer had been at a different address: every name
        // pointer now points outside it. The entries still decode; only the names are absent.
        var processes = WindowsResourceParsing.ParseProcessInformation(Fixture("process-information.bin"), ProcessBufferAddress() + (1L << 40));

        Assert.Equal(41, processes.Count);
        Assert.All(processes, p => Assert.Null(p.Name));
        Assert.Equal(1676, Assert.Single(processes, p => p.ProcessId == 1816).ParentProcessId);
    }

    [Fact]
    public void ParseProcessInformation_ThreadCountRunsPastTheBuffer_Throws()
    {
        var bytes = new byte[WindowsResourceParsing.ProcessInformationSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 5);

        Assert.Throws<FormatException>(() => WindowsResourceParsing.ParseProcessInformation(bytes, 0));
    }

    [Fact]
    public void ParseProcessInformation_LinkRunsPastTheBuffer_Throws()
    {
        var bytes = new byte[WindowsResourceParsing.ProcessInformationSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 4096);

        Assert.Throws<FormatException>(() => WindowsResourceParsing.ParseProcessInformation(bytes, 0));
    }

    // ---------------------------------------------------------------------------------------------
    // Page file
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsePageFileInformation_RealCapture_SumsPagesIntoBytes()
    {
        var totals = WindowsResourceParsing.ParsePageFileInformation(Fixture("page-file.bin"), pageSize: 4096);

        Assert.Equal(1, totals.Count);
        Assert.Equal(51539607552UL, totals.TotalBytes);
        Assert.Equal(3187798016UL, totals.InUseBytes);
    }

    [Fact]
    public void ParsePageFileInformation_NoPageFile_IsZeroFilesNotAFailure()
    {
        var totals = WindowsResourceParsing.ParsePageFileInformation(ReadOnlySpan<byte>.Empty, pageSize: 4096);

        Assert.Equal(new PageFileTotals(0, 0, 0), totals);
    }

    // ---------------------------------------------------------------------------------------------
    // Physical disks
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParseDiskPerformance_RealCapture_ReadsTheCumulativeCounters()
    {
        var counters = WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-1.bin"));

        Assert.Equal(815873592320, counters.BytesRead);
        Assert.Equal(4181894214656, counters.BytesWritten);
        Assert.Equal(737159415896, counters.IdleTime);
        Assert.Equal(63863699U, counters.ReadCount);
        Assert.Equal(222851442U, counters.WriteCount);
        Assert.Equal(0U, counters.QueueDepth);
        Assert.Equal(134365444012057930, counters.QueryTime);
    }

    [Fact]
    public void DiskRatesBetween_TwoRealCaptures_GivesBusyThroughputAndLatency()
    {
        var rates = WindowsResourceParsing.DiskRatesBetween(
            WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-1.bin")),
            WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-2.bin")));

        // 1.0113157 seconds apart, idle for 0.8656723 of them.
        Assert.Equal(1 - 8656723.0 / 10113157.0, rates.BusyFraction!.Value, 9);
        // 65,536 bytes read and 19,536,384 written in that time.
        Assert.Equal(65536 / 1.0113157, rates.ReadBytesPerSecond!.Value, 3);
        Assert.Equal(19536384 / 1.0113157, rates.WriteBytesPerSecond!.Value, 3);
        // One read taking 3.5 milliseconds; 2,662 writes taking 0.1438397 seconds between them.
        Assert.Equal(0.0035207, rates.SecondsPerRead!.Value, 9);
        Assert.Equal(0.1438397 / 2662, rates.SecondsPerWrite!.Value, 12);
        Assert.Equal(0U, rates.QueueDepth);
    }

    [Fact]
    public void DiskRatesBetween_NoReadsInTheInterval_TimePerReadIsNullNotZero()
    {
        var earlier = new DiskCounters(0, 0, ReadTime: 50, WriteTime: 0, IdleTime: 0, ReadCount: 7, WriteCount: 0, QueueDepth: 0, QueryTime: 0);
        var later = earlier with { IdleTime = 9_000_000, QueryTime = 10_000_000 };

        var rates = WindowsResourceParsing.DiskRatesBetween(earlier, later);

        Assert.Null(rates.SecondsPerRead);
        Assert.Null(rates.SecondsPerWrite);
        Assert.Equal(0.1, rates.BusyFraction!.Value, 9);
    }

    [Fact]
    public void DiskRatesBetween_NoTimePassed_EveryRateIsNull()
    {
        var counters = WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-1.bin"));

        var rates = WindowsResourceParsing.DiskRatesBetween(counters, counters);

        Assert.Null(rates.BusyFraction);
        Assert.Null(rates.ReadBytesPerSecond);
        Assert.Null(rates.WriteBytesPerSecond);
        Assert.Null(rates.SecondsPerRead);
    }

    [Fact]
    public void DiskRatesBetween_CountWrapsPastItsThirtyTwoBits_StillCountsTheReads()
    {
        // ReadCount is a 32-bit counter, and a busy disk passes four billion reads.
        var earlier = new DiskCounters(0, 0, ReadTime: 0, WriteTime: 0, IdleTime: 0, ReadCount: uint.MaxValue - 1, WriteCount: 0, QueueDepth: 0, QueryTime: 0);
        var later = earlier with { ReadCount = 2, ReadTime = 40_000, QueryTime = 10_000_000 };

        var rates = WindowsResourceParsing.DiskRatesBetween(earlier, later);

        // Four reads (max-1 -> max -> 0 -> 1 -> 2), one millisecond each.
        Assert.Equal(0.001, rates.SecondsPerRead!.Value, 12);
    }

    [Fact]
    public void DiskRatesBetween_IdleRunsAHairPastTheInterval_BusyIsZeroNotNegative()
    {
        var earlier = new DiskCounters(0, 0, 0, 0, IdleTime: 0, 0, 0, 0, QueryTime: 0);
        var later = earlier with { IdleTime = 10_000_050, QueryTime = 10_000_000 };

        Assert.Equal(0.0, WindowsResourceParsing.DiskRatesBetween(earlier, later).BusyFraction);
    }

    [Theory]
    [InlineData("disk0-descriptor.bin", "Fanxiang S101 4TB", 11)] // STORAGE_BUS_TYPE BusTypeSata
    [InlineData("disk1-descriptor.bin", "WD_BLACK SN770 1TB", 17)] // STORAGE_BUS_TYPE BusTypeNvme
    public void ParseStorageDeviceDescriptor_RealCapture_ReadsModelAndBus(string fixture, string model, int busType)
    {
        var descriptor = WindowsResourceParsing.ParseStorageDeviceDescriptor(Fixture(fixture));

        Assert.Equal(model, descriptor.Model);
        Assert.Equal(busType, descriptor.BusType);
        Assert.False(descriptor.Removable);
    }

    [Theory]
    [InlineData("disk0-seek-penalty.bin")]
    [InlineData("disk1-seek-penalty.bin")]
    public void ParseIncursSeekPenalty_RealSolidStateCapture_IsFalse(string fixture)
    {
        Assert.False(WindowsResourceParsing.ParseIncursSeekPenalty(Fixture(fixture)));
    }

    [Fact]
    public void ParseIncursSeekPenalty_SpinningDisk_IsTrue()
    {
        // This machine has no spinning disk, so the true case is the real solid-state bytes with the
        // one flag a hard drive sets: DEVICE_SEEK_PENALTY_DESCRIPTOR.IncursSeekPenalty at offset 8.
        var bytes = Fixture("disk0-seek-penalty.bin");
        bytes[8] = 1;

        Assert.True(WindowsResourceParsing.ParseIncursSeekPenalty(bytes));
    }

    [Theory]
    [InlineData("disk0-geometry.bin", 4096805658624UL)]
    [InlineData("disk1-geometry.bin", 1000204886016UL)]
    public void ParseDiskSize_RealCapture_ReadsBytes(string fixture, ulong size)
    {
        Assert.Equal(size, WindowsResourceParsing.ParseDiskSize(Fixture(fixture)));
    }

    // ---------------------------------------------------------------------------------------------
    // Volumes
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("volume-C-extents.bin", 1)]
    [InlineData("volume-D-extents.bin", 0)]
    public void ParseVolumeDiskNumbers_RealCapture_NamesThePhysicalDisk(string fixture, int disk)
    {
        Assert.Equal(new[] { disk }, WindowsResourceParsing.ParseVolumeDiskNumbers(Fixture(fixture)));
    }

    [Fact]
    public void ParseVolumeDiskNumbers_SpannedVolume_NamesEachDiskOnce()
    {
        // Three extents: disk 2, disk 0, disk 2 again. A volume spanned across disks names the set.
        var bytes = new byte[8 + 3 * 24];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), 2);

        Assert.Equal(new[] { 0, 2 }, WindowsResourceParsing.ParseVolumeDiskNumbers(bytes));
    }

    [Fact]
    public void ParseVolumeDiskNumbers_CountRunsPastTheBuffer_Throws()
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);

        Assert.Throws<FormatException>(() => WindowsResourceParsing.ParseVolumeDiskNumbers(bytes));
    }

    // ---------------------------------------------------------------------------------------------
    // Network
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParseInterfaceTable_RealCapture_ReadsEveryRow()
    {
        var rows = WindowsResourceParsing.ParseInterfaceTable(Fixture("interface-table.bin"));

        Assert.Equal(7, rows.Count);
        var ethernet = Assert.Single(rows, r => r.Alias == "Ethernet");
        Assert.Equal(19U, ethernet.Index);
        Assert.Equal("Realtek Gaming 2.5GbE Family Controller", ethernet.Description);
        Assert.Equal(96390806574UL, ethernet.InOctets);
        Assert.Equal(42206131110UL, ethernet.OutOctets);
    }

    [Fact]
    public void IsPhysicalAndUp_RealCapture_CountsTheAdapterOnceNotOncePerFilter()
    {
        // The Ethernet adapter appears four times: itself and three filter drivers bound to it, all
        // with the same byte counts. Wi-Fi is hardware but disconnected; the loopback and the
        // virtual switch are not hardware. Exactly one row is the machine's network.
        var rows = WindowsResourceParsing.ParseInterfaceTable(Fixture("interface-table.bin"));

        Assert.Equal(4, rows.Count(r => r.InOctets == 96390806574UL));
        var counted = Assert.Single(rows, WindowsResourceParsing.IsPhysicalAndUp);
        Assert.Equal("Ethernet", counted.Alias);

        var wifi = Assert.Single(rows, r => r.Alias == "Wi-Fi");
        Assert.True(wifi.IsHardware);
        Assert.False(WindowsResourceParsing.IsPhysicalAndUp(wifi));
    }

    [Fact]
    public void ParseInterfaceTable_CountRunsPastTheBuffer_Throws()
    {
        var bytes = new byte[8 + WindowsResourceParsing.InterfaceRowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);

        Assert.Throws<FormatException>(() => WindowsResourceParsing.ParseInterfaceTable(bytes));
    }
}
