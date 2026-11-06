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
    }

    [Fact]
    public void ParseProcessInformation_RealCaptureUnderLoad_CountsThreadsWaitingOnAProcessor()
    {
        // The capture tool ran 48 spinning threads on 24 processors while it read the list. Its own
        // threads came out 25 Ready and 24 Running - waiting for a processor, and on one - and the run
        // queue counts BOTH, because that is the quantity the shared contract defines and the figure
        // Linux reads from /proc/loadavg. Counting only the waiting ones would report a third of the
        // truth on this machine under a name that means something else.
        var processes = WindowsResourceParsing.ParseProcessInformation(Fixture("process-information.bin"), ProcessBufferAddress());

        var capture = Assert.Single(processes, p => p.Name == "Capture.exe");
        Assert.Equal(57092, capture.ProcessId);
        Assert.Equal(47756, capture.ParentProcessId);
        Assert.Equal(56, capture.ThreadCount);
        Assert.True(capture.ThreadsWantingAProcessor > 25,
            $"the capture tool shows {capture.ThreadsWantingAProcessor} threads wanting a processor, which "
            + "is the 25 WAITING for one and nothing else: the 24 that were ON a processor were left out. "
            + "That is a different quantity from the shared contract's and from the one Linux supplies.");
        Assert.Equal(49, capture.ThreadsWantingAProcessor);

        // An idle service has threads, none of them on a processor or wanting one.
        Assert.Equal(0, Assert.Single(processes, p => p.Name == "lsass.exe").ThreadsWantingAProcessor);
    }

    [Fact]
    public void ParseProcessInformation_TheRunQueue_CountsThreadsOnAProcessorAsWellAsThoseWaiting()
    {
        // THE REGRESSION THIS TEST EXISTS FOR. The run queue was once built from the waiting states
        // alone, which is a different quantity from the one the shared contract defines and from the one
        // Linux supplies - and the difference is invisible, because both are ordinary small integers.
        //
        // This test is on the PARSER, so it asserts the raw per-process tallies. Whole machine, from a
        // 24-processor machine with 48 threads spinning: Ready 35, Running 47, Standby 25, DeferredReady
        // 0, Initialized 24, and 874 Waiting on something other than a processor. The waiting states
        // alone give 35 and all four give 107 - but 48 of that 107 belongs to the System Idle Process,
        // whose threads are "Running" exactly when nothing else is, so the figure the probe reports is
        // 59. WindowsResourceProbe.StallBetween does that excluding and the arithmetic tests assert it;
        // the parser's job is to report what the bytes say.
        var processes = WindowsResourceParsing.ParseProcessInformation(
            Fixture("process-information.bin"), ProcessBufferAddress());

        var everyProcess = processes.Sum(p => p.ThreadsWantingAProcessor);
        var exceptIdle = processes.Where(p => p.ProcessId != 0).Sum(p => p.ThreadsWantingAProcessor);

        Assert.True(everyProcess > 35,
            $"every process sums to {everyProcess} threads wanting a processor. 35 means the count took "
            + "the WAITING states alone and left out the threads that were on a processor, which is a "
            + "different quantity from the shared contract's and from the one Linux supplies.");
        Assert.True(exceptIdle > 35,
            $"excluding the System Idle Process gives {exceptIdle}. 35 means the count took the WAITING "
            + "states alone; 107 means the idle process was counted in, and its threads are 'Running' "
            + "exactly when nothing else is, which is the opposite of demand for a processor.");
        Assert.Equal(107, everyProcess);
        Assert.Equal(59, exceptIdle);

        // The idle process's share of it, named so a future reader sees why the two figures differ.
        Assert.Equal(48, Assert.Single(processes, p => p.ProcessId == 0).ThreadsWantingAProcessor);

        // And it is not merely "everything": the threads waiting on something else are excluded, so the
        // figure is a run queue and not a thread count. This capture has 1,005 threads in all.
        Assert.Equal(1005, processes.Sum(p => p.ThreadCount));
        Assert.True(everyProcess < processes.Sum(p => p.ThreadCount),
            "the run queue counted every thread, so it is not a run queue");
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
    // Paging activity
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsePageReadCount_RealCaptures_ReadsTheCumulativePagesReadFromDisk()
    {
        Assert.Equal(694021597U, WindowsResourceParsing.ParsePageReadCount(Fixture("system-performance-1.bin")));
        Assert.Equal(694022508U, WindowsResourceParsing.ParsePageReadCount(Fixture("system-performance-2.bin")));
    }

    [Fact]
    public void SystemPerformanceLayout_RealCapture_AgreesWithGlobalMemoryStatusExReadAtTheSameMoment()
    {
        // The structure is only partly documented, so its offsets are proved against a documented call
        // made at the same capture (capture-notes.txt): GlobalMemoryStatusEx reported 20,584,345,600
        // bytes available and a commit limit of 120,024,301,568 bytes. If the fields at 44 and 52 are
        // right, PageReadCount at 80 is read from the same, correctly placed structure.
        var bytes = Fixture("system-performance-2.bin");

        Assert.Equal(120024301568UL, WindowsResourceParsing.ParseCommitLimitPages(bytes) * 4096UL);
        // Available memory moves from moment to moment; the two reads were microseconds apart.
        var available = WindowsResourceParsing.ParseAvailablePages(bytes) * 4096L;
        Assert.InRange(available, 20584345600L - 64 * 1024 * 1024, 20584345600L + 64 * 1024 * 1024);
    }

    [Fact]
    public void ParsePageReadCount_TooShortToHoldTheField_Throws()
    {
        Assert.Throws<FormatException>(() => WindowsResourceParsing.ParsePageReadCount(new byte[80]));
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
    public void DiskRatesBetween_TwoRealCaptures_GivesBusyAndLatency()
    {
        var rates = WindowsResourceParsing.DiskRatesBetween(
            WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-1.bin")),
            WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-2.bin")))!.Value;

        // 1.0113157 seconds apart, idle for 0.8656723 of them.
        Assert.Equal(1 - 8656723.0 / 10113157.0, rates.BusyFraction!.Value, 9);
        // One read taking 3.5 milliseconds; 2,662 writes taking 0.1438397 seconds between them.
        Assert.Equal(0.0035207, rates.SecondsPerRead!.Value, 9);
        Assert.Equal(0.1438397 / 2662, rates.SecondsPerWrite!.Value, 12);
        // Every request's time at the disk added up - 35,207 reading and 1,438,397 writing - over the
        // interval: on average 0.146 requests in flight, though the instantaneous depth read zero.
        Assert.Equal((35207.0 + 1438397.0) / 10113157.0, rates.AverageQueueLength!.Value, 9);
    }

    [Fact]
    public void DiskRatesBetween_NoReadsInTheInterval_TimePerReadIsNullNotZero()
    {
        var earlier = new DiskCounters(0, 0, ReadTime: 50, WriteTime: 0, IdleTime: 0, ReadCount: 7, WriteCount: 0, QueueDepth: 0, QueryTime: 0);
        var later = earlier with { IdleTime = 9_000_000, QueryTime = 10_000_000 };

        var rates = WindowsResourceParsing.DiskRatesBetween(earlier, later)!.Value;

        Assert.Null(rates.SecondsPerRead);
        Assert.Null(rates.SecondsPerWrite);
        Assert.Equal(0.1, rates.BusyFraction!.Value, 9);
    }

    [Fact]
    public void DiskRatesBetween_NoTimePassed_IsNullNotZero()
    {
        var counters = WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-1.bin"));

        Assert.Null(WindowsResourceParsing.DiskRatesBetween(counters, counters));
    }

    [Fact]
    public void DiskRatesBetween_CountWrapsPastItsThirtyTwoBits_StillCountsTheReads()
    {
        // ReadCount is a 32-bit counter, and a busy disk passes four billion reads.
        var earlier = new DiskCounters(0, 0, ReadTime: 0, WriteTime: 0, IdleTime: 0, ReadCount: uint.MaxValue - 1, WriteCount: 0, QueueDepth: 0, QueryTime: 0);
        var later = earlier with { ReadCount = 2, ReadTime = 40_000, QueryTime = 10_000_000 };

        var rates = WindowsResourceParsing.DiskRatesBetween(earlier, later)!.Value;

        // Four reads (max-1 -> max -> 0 -> 1 -> 2), one millisecond each.
        Assert.Equal(0.001, rates.SecondsPerRead!.Value, 12);
    }

    [Fact]
    public void DiskRatesBetween_IdleRunsAHairPastTheInterval_BusyIsZeroNotNegative()
    {
        var earlier = new DiskCounters(0, 0, 0, 0, IdleTime: 0, 0, 0, 0, QueryTime: 0);
        var later = earlier with { IdleTime = 10_000_050, QueryTime = 10_000_000 };

        Assert.Equal(0.0, WindowsResourceParsing.DiskRatesBetween(earlier, later)!.Value.BusyFraction);
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


    // ---------------------------------------------------------------------------------------------
    // The system volume
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
}
