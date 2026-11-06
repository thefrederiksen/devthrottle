using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The arithmetic <see cref="WindowsResourceProbe"/> does between two cumulative readings, tested
/// without Windows: on the real captured readings where two exist, and on hand-built pairs for the
/// cases a capture cannot show on demand - a reused process id, a counter that goes backwards, the
/// first sample. The interval is handed in, so no test here reads a clock.
/// </summary>
[Trait("Category", "Resources")]
public class WindowsResourceProbeArithmeticTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "resource-monitor", "windows", name));

    private static IReadOnlyList<RawProcess> RealProcesses() =>
        WindowsResourceParsing.ParseProcessInformation(
            Fixture("process-information.bin"),
            long.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "resource-monitor", "windows", "process-information.address.txt")).Trim()));

    private static RawProcess Process(int pid, long created, long cpu = 0, ulong read = 0, ulong written = 0, int waiting = 0, string? name = "worker.exe") =>
        new(pid, ParentProcessId: 1, name, created, UserTime: cpu, KernelTime: 0, ThreadCount: 1, waiting,
            WorkingSetBytes: 1000, PrivateBytes: 1000, read, written);

    private static ProcessSnapshot Snapshot(params RawProcess[] processes) => new(0, processes);

    // ---------------------------------------------------------------------------------------------
    // Processor
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CpuBetween_FirstSample_IsUnreadableWithTheSharedReasonAndStillCarriesTheCoreCount()
    {
        var reading = WindowsResourceProbe.CpuBetween(null, new CpuTimes(1, 2, 3), null, [new CpuTimes(1, 2, 3)], 24);

        Assert.False(reading.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, reading.UnreadableReason);
        Assert.Equal(24, reading.LogicalCoreCount);
    }

    [Fact]
    public void CpuBetween_RealCaptures_TotalFromSystemTimesBusiestFromTheCores()
    {
        var earlierCores = WindowsResourceParsing.ParseProcessorPerformance(Fixture("processor-performance-1.bin"));
        var laterCores = WindowsResourceParsing.ParseProcessorPerformance(Fixture("processor-performance-2.bin"));
        var earlierSystem = new CpuTimes(Idle: 21479250781250, Kernel: 27843232187500, User: 9546977500000);
        var laterSystem = new CpuTimes(Idle: 21479423125000, Kernel: 27843443281250, User: 9547009687500);

        var reading = WindowsResourceProbe.CpuBetween(earlierSystem, laterSystem, earlierCores, laterCores, 24);

        Assert.True(reading.CouldRead);
        Assert.Equal(24, reading.LogicalCoreCount);
        Assert.Equal(24, earlierCores.Count);
        Assert.Equal(70937500.0 / 243281250.0 * 100, reading.TotalBusyPercent, 6);

        var busiest = Enumerable.Range(0, 24).Max(i => WindowsResourceParsing.BusyFraction(earlierCores[i], laterCores[i])!.Value) * 100;
        Assert.Equal(busiest, reading.BusiestCorePercent!.Value, 9);
        Assert.True(reading.BusiestCorePercent >= reading.TotalBusyPercent - 5,
            "the busiest core cannot be much less busy than the machine as a whole");
    }

    [Fact]
    public void CpuBetween_NoTimePassed_IsUnreadableNotZero()
    {
        var times = new CpuTimes(10, 20, 5);

        var reading = WindowsResourceProbe.CpuBetween(times, times, [times], [times], 4);

        Assert.False(reading.CouldRead);
        Assert.Equal(4, reading.LogicalCoreCount);
    }

    [Fact]
    public void CpuBetween_CounterWentBackwards_IsUnreadableNotZero()
    {
        var reading = WindowsResourceProbe.CpuBetween(new CpuTimes(100, 200, 50), new CpuTimes(90, 190, 40),
            [new CpuTimes(100, 200, 50)], [new CpuTimes(90, 190, 40)], 4);

        Assert.False(reading.CouldRead);
    }

    // ---------------------------------------------------------------------------------------------
    // Disk evidence
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void DiskBetween_FirstSample_KeepsNameKindAndWhetherItHoldsTheSystemVolume()
    {
        var reading = WindowsResourceProbe.DiskBetween("PhysicalDrive1", DiskKind.SolidState, true, null,
            WindowsResourceParsing.ParseDiskPerformance(Fixture("disk1-performance-1.bin")));

        Assert.False(reading.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, reading.UnreadableReason);
        Assert.Equal("PhysicalDrive1", reading.Name);
        Assert.Equal(DiskKind.SolidState, reading.Kind);
        Assert.True(reading.IsInSystemVolume);
    }

    [Fact]
    public void DiskBetween_RealCaptures_PercentAndMilliseconds()
    {
        var reading = WindowsResourceProbe.DiskBetween("PhysicalDrive0", DiskKind.SolidState, false,
            WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-1.bin")),
            WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-2.bin")));

        Assert.True(reading.CouldRead);
        Assert.False(reading.IsInSystemVolume);
        Assert.Equal((1 - 8656723.0 / 10113157.0) * 100, reading.BusyPercent!.Value, 6);
        Assert.Equal(3.5207, reading.MillisecondsPerRead!.Value, 9);
        Assert.Equal(143.8397 / 2662, reading.MillisecondsPerWrite!.Value, 9);
        Assert.Equal((35207.0 + 1438397.0) / 10113157.0, reading.AverageQueueDepth!.Value, 9);
    }

    [Fact]
    public void DiskBetween_NoWritesInTheInterval_TimePerWriteIsNull()
    {
        var earlier = new DiskCounters(0, 0, 0, WriteTime: 500, IdleTime: 0, ReadCount: 0, WriteCount: 9, 0, QueryTime: 0);
        var later = earlier with { IdleTime = 10_000_000, QueryTime = 10_000_000 };

        var reading = WindowsResourceProbe.DiskBetween("PhysicalDrive3", DiskKind.Spinning, null, earlier, later);

        Assert.True(reading.CouldRead);
        Assert.Null(reading.IsInSystemVolume);
        Assert.Equal(0, reading.BusyPercent!.Value);
        Assert.Null(reading.MillisecondsPerWrite);
        Assert.Null(reading.MillisecondsPerRead);
    }

    [Fact]
    public void DiskBetween_TimeCounterWentBackwards_IsUnreadableNotZero()
    {
        var earlier = new DiskCounters(0, 0, ReadTime: 900, WriteTime: 900, IdleTime: 0, ReadCount: 1, WriteCount: 1, 0, QueryTime: 0);
        var later = earlier with { ReadTime = 100, ReadCount = 2, QueryTime = 10_000_000 };

        var reading = WindowsResourceProbe.DiskBetween("PhysicalDrive3", DiskKind.Spinning, null, earlier, later);

        Assert.False(reading.CouldRead);
        Assert.Null(reading.MillisecondsPerRead);
    }

    [Fact]
    public void DiskRatesBetween_IdleFarPastTheInterval_BusyIsNullNotClampedToZero()
    {
        // Idle ran twice the interval: these counters do not belong together. A tidy 0% busy would hide it.
        var earlier = new DiskCounters(0, 0, 0, 0, IdleTime: 0, ReadCount: 0, WriteCount: 0, 0, QueryTime: 0);
        var later = earlier with { IdleTime = 20_000_000, QueryTime = 10_000_000 };

        var rates = WindowsResourceParsing.DiskRatesBetween(earlier, later);

        Assert.NotNull(rates);
        Assert.Null(rates!.Value.BusyFraction);
    }

    [Fact]
    public void DiskRatesBetween_IdleAHairPastTheInterval_ReadsAsIdle()
    {
        var earlier = new DiskCounters(0, 0, 0, 0, IdleTime: 0, ReadCount: 0, WriteCount: 0, 0, QueryTime: 0);
        var later = earlier with { IdleTime = 10_000_100, QueryTime = 10_000_000 };

        Assert.Equal(0, WindowsResourceParsing.DiskRatesBetween(earlier, later)!.Value.BusyFraction);
    }

    // ---------------------------------------------------------------------------------------------
    // Processes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ProcessesBetween_FirstSample_ListsAndNamesEveryProcessWithTheRatesNull()
    {
        var reading = WindowsResourceProbe.ProcessesBetween(null, Snapshot(RealProcesses().ToArray()), null, 24);

        Assert.True(reading.CouldRead);
        Assert.Equal(40, reading.Processes.Count);
        Assert.All(reading.Processes, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.NotNull(p.MemoryBytes);
            Assert.Null(p.CpuPercent);
            Assert.Null(p.DiskReadBytesPerSecond);
            Assert.Null(p.DiskWriteBytesPerSecond);
        });
    }

    [Fact]
    public void ProcessesBetween_RealList_LeavesOutTheIdleProcessAndCarriesParentsAndStartTimes()
    {
        var processes = Snapshot(RealProcesses().ToArray());

        var reading = WindowsResourceProbe.ProcessesBetween(processes, processes, OneSecond, 24);

        Assert.True(reading.CouldRead);
        Assert.Equal(40, reading.Processes.Count);
        Assert.DoesNotContain(reading.Processes, p => p.Pid == 0);

        var services = Assert.Single(reading.Processes, p => p.Pid == 1816);
        Assert.Equal("services.exe", services.Name);
        Assert.Equal(1676, services.ParentPid);
        Assert.Equal(new DateTime(2026, 10, 13, 18, 10, 20, DateTimeKind.Utc).AddTicks(5323074), services.StartTimeUtc);

        // The same reading twice: nothing was used in between, by anyone - a real, measured zero.
        Assert.All(reading.Processes, p => Assert.Equal(0, p.CpuPercent));
        Assert.All(reading.Processes, p => Assert.Equal(0, p.DiskReadBytesPerSecond));
    }

    [Fact]
    public void ProcessesBetween_OneCoreForOneSecondOnTwentyFour_IsAShareOfTheWholeMachine()
    {
        var earlier = Snapshot(Process(500, created: 100, cpu: 0));
        var later = Snapshot(Process(500, created: 100, cpu: 10_000_000));

        var reading = WindowsResourceProbe.ProcessesBetween(earlier, later, OneSecond, 24);

        Assert.Equal(100.0 / 24, Assert.Single(reading.Processes).CpuPercent!.Value, 9);
    }

    [Fact]
    public void ProcessesBetween_ProcessIdReusedByANewProcess_IsNotChargedTheOldOnesDifference()
    {
        // Process 500 exited and a new process was given its id. Measured against the old one, its
        // 1 second of processor would read as minus 99 seconds. It is a new process with no earlier
        // reading, so its rates are null - listed and named, never charged the old one's figures.
        var earlier = Snapshot(Process(500, created: 100, cpu: 1_000_000_000, read: 9_000_000));
        var later = Snapshot(Process(500, created: 200, cpu: 10_000_000, read: 4_096));

        var reading = Assert.Single(WindowsResourceProbe.ProcessesBetween(earlier, later, OneSecond, 1).Processes);

        Assert.Equal(500, reading.Pid);
        Assert.Null(reading.CpuPercent);
        Assert.Null(reading.DiskReadBytesPerSecond);
        Assert.NotNull(reading.MemoryBytes);
    }

    [Fact]
    public void ProcessesBetween_ProcessStartedDuringTheInterval_IsListedWithItsRatesNull()
    {
        var earlier = Snapshot(Process(1, created: 1, cpu: 0));
        var later = Snapshot(Process(1, created: 1, cpu: 5_000_000), Process(2, created: 50, cpu: 5_000_000));

        var reading = WindowsResourceProbe.ProcessesBetween(earlier, later, OneSecond, 2);

        Assert.Equal(25, Assert.Single(reading.Processes, p => p.Pid == 1).CpuPercent!.Value, 9);
        var started = Assert.Single(reading.Processes, p => p.Pid == 2);
        Assert.Null(started.CpuPercent);
        Assert.Equal(1000UL, started.MemoryBytes);
    }

    [Fact]
    public void ProcessesBetween_CounterWentBackwards_IsNullNeverZero()
    {
        var earlier = Snapshot(Process(7, created: 1, read: 9_000, written: 9_000));
        var later = Snapshot(Process(7, created: 1, read: 10, written: 9_500));

        var reading = Assert.Single(WindowsResourceProbe.ProcessesBetween(earlier, later, OneSecond, 1).Processes);

        Assert.Null(reading.DiskReadBytesPerSecond);
        Assert.Equal(500, reading.DiskWriteBytesPerSecond);
    }

    [Fact]
    public void ProcessesBetween_ProcessorTimeWentBackwards_ShareIsNullNeverZero()
    {
        var earlier = Snapshot(Process(7, created: 1, cpu: 50_000_000));
        var later = Snapshot(Process(7, created: 1, cpu: 10_000_000));

        Assert.Null(Assert.Single(WindowsResourceProbe.ProcessesBetween(earlier, later, OneSecond, 4).Processes).CpuPercent);
    }

    [Fact]
    public void ProcessesBetween_ProcessWithNoName_IsLabelledByItsId()
    {
        var later = Snapshot(Process(42, created: 1, name: null));

        Assert.Equal("process 42", Assert.Single(WindowsResourceProbe.ProcessesBetween(Snapshot(), later, OneSecond, 1).Processes).Name);
    }

    [Fact]
    public void ProcessesBetween_NoTimePassed_ListsTheProcessesWithTheRatesNull()
    {
        var snapshot = Snapshot(Process(1, 1));

        var reading = WindowsResourceProbe.ProcessesBetween(snapshot, snapshot, TimeSpan.Zero, 4);

        Assert.True(reading.CouldRead);
        Assert.Null(Assert.Single(reading.Processes).CpuPercent);
    }

    [Fact]
    public void ProcessesBetween_CoreCountNotKnown_ShareIsNullNotPerCore()
    {
        var earlier = Snapshot(Process(500, created: 100, cpu: 0));
        var later = Snapshot(Process(500, created: 100, cpu: 10_000_000));

        Assert.Null(Assert.Single(WindowsResourceProbe.ProcessesBetween(earlier, later, OneSecond, null).Processes).CpuPercent);
    }

    // ---------------------------------------------------------------------------------------------
    // Stall proxies
    // ---------------------------------------------------------------------------------------------

    private static readonly ProcessSnapshot NoProcesses = new(0, []);

    [Fact]
    public void StallBetween_FirstSample_IsUnreadable()
    {
        Assert.Equal(ResourceUnreadableReasons.FirstSample,
            WindowsResourceProbe.StallBetween(Snapshot(RealProcesses().ToArray()), null, new PagingSnapshot(0, 5), null).UnreadableReason);
    }

    [Fact]
    public void StallBetween_RealListUnderLoad_RunQueueIsEveryThreadWaitingForAProcessor()
    {
        var processes = RealProcesses();

        var reading = WindowsResourceProbe.StallBetween(Snapshot(processes.ToArray()), new PagingSnapshot(0, 10), new PagingSnapshot(1, 10), OneSecond);

        Assert.True(reading.CouldRead);
        Assert.Equal(StallSource.WindowsCounters, reading.Source);
        Assert.Equal(processes.Sum(p => p.ThreadsWaitingOnCpu), reading.RunQueueLength);
        Assert.True(reading.RunQueueLength >= 25, "the capture tool alone had 25 threads waiting");
    }

    [Fact]
    public void StallBetween_RealPagingCaptures_PagesReadPerSecond()
    {
        // The two captures were one second apart (911 pages read between them); timed here as exactly
        // one second, since the capture's own interval is not part of the bytes.
        var earlier = new PagingSnapshot(0, WindowsResourceParsing.ParsePageReadCount(Fixture("system-performance-1.bin")));
        var later = new PagingSnapshot(1, WindowsResourceParsing.ParsePageReadCount(Fixture("system-performance-2.bin")));

        Assert.Equal(911, WindowsResourceProbe.StallBetween(NoProcesses, earlier, later, OneSecond).HardPageFaultsPerSecond!.Value, 9);
    }

    [Fact]
    public void StallBetween_PageReadCountWrapsPastThirtyTwoBits_StillCountsTheGrowth()
    {
        var reading = WindowsResourceProbe.StallBetween(NoProcesses, new PagingSnapshot(0, uint.MaxValue - 9), new PagingSnapshot(1, 10), TimeSpan.FromSeconds(2));

        Assert.Equal(10, reading.HardPageFaultsPerSecond!.Value, 9);
    }

    [Fact]
    public void StallBetween_ProcessListUnreadable_KeepsThePageFaultRateWithTheRunQueueNull()
    {
        var reading = WindowsResourceProbe.StallBetween(null, new PagingSnapshot(0, 100), new PagingSnapshot(1, 600), OneSecond);

        Assert.True(reading.CouldRead);
        Assert.Null(reading.RunQueueLength);
        Assert.Equal(500, reading.HardPageFaultsPerSecond!.Value, 9);
    }

    [Fact]
    public void StallBetween_NoTimePassed_IsUnreadable()
    {
        var paging = new PagingSnapshot(7, 100);

        Assert.False(WindowsResourceProbe.StallBetween(NoProcesses, paging, paging, TimeSpan.Zero).CouldRead);
    }

    [Fact]
    public void StallBetween_WhatWindowsDoesNotOffer_IsNullNotZero()
    {
        var reading = WindowsResourceProbe.StallBetween(NoProcesses, new PagingSnapshot(0, 0), new PagingSnapshot(1, 0), OneSecond);

        Assert.Null(reading.CpuStalledPercent);
        Assert.Null(reading.MemoryStalledPercent);
        Assert.Null(reading.IoStalledPercent);
        Assert.Null(reading.ProcessesWaitingOnDisk);
        Assert.Null(reading.MacOsMemoryPressureLevel);
    }
}
