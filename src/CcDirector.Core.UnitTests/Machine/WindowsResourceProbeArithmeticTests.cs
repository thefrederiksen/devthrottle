using System.Diagnostics;
using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The arithmetic <see cref="WindowsResourceProbe"/> does between two cumulative readings, tested
/// without Windows: on the real captured readings where two exist, and on hand-built pairs for the
/// cases a capture cannot show on demand - a reused process id, a counter reset, an adapter appearing,
/// the first sample.
/// </summary>
[Trait("Category", "Resources")]
public class WindowsResourceProbeArithmeticTests
{
    private static readonly long OneSecond = Stopwatch.Frequency;

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "resource-monitor", "windows", name));

    private static IReadOnlyList<RawProcess> RealProcesses() =>
        WindowsResourceParsing.ParseProcessInformation(
            Fixture("process-information.bin"),
            long.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "resource-monitor", "windows", "process-information.address.txt")).Trim()));

    private static RawProcess Process(int pid, long created, long cpu = 0, uint faults = 0, ulong read = 0, ulong written = 0, int waiting = 0, string? name = "worker.exe") =>
        new(pid, ParentProcessId: 1, name, created, UserTime: cpu, KernelTime: 0, faults, ThreadCount: 1, waiting,
            WorkingSetBytes: 1000, PrivateBytes: 1000, read, written);

    // ---------------------------------------------------------------------------------------------
    // Processor
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CpuBetween_FirstSample_IsUnreadableWithTheSharedReason()
    {
        var reading = WindowsResourceProbe.CpuBetween(null, new CpuTimes(1, 2, 3), null, [new CpuTimes(1, 2, 3)]);

        Assert.False(reading.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, reading.UnreadableReason);
    }

    [Fact]
    public void CpuBetween_RealCaptures_TotalFromSystemTimesBusiestFromTheCores()
    {
        var earlierCores = WindowsResourceParsing.ParseProcessorPerformance(Fixture("processor-performance-1.bin"));
        var laterCores = WindowsResourceParsing.ParseProcessorPerformance(Fixture("processor-performance-2.bin"));
        var earlierSystem = new CpuTimes(Idle: 21479250781250, Kernel: 27843232187500, User: 9546977500000);
        var laterSystem = new CpuTimes(Idle: 21479423125000, Kernel: 27843443281250, User: 9547009687500);

        var reading = WindowsResourceProbe.CpuBetween(earlierSystem, laterSystem, earlierCores, laterCores);

        Assert.True(reading.CouldRead);
        Assert.Equal(24, reading.LogicalCoreCount);
        Assert.Equal(70937500.0 / 243281250.0 * 100, reading.TotalBusyPercent, 6);

        var busiest = Enumerable.Range(0, 24).Max(i => WindowsResourceParsing.BusyFraction(earlierCores[i], laterCores[i])!.Value) * 100;
        Assert.Equal(busiest, reading.BusiestCorePercent, 9);
        Assert.True(reading.BusiestCorePercent >= reading.TotalBusyPercent - 5,
            "the busiest core cannot be much less busy than the machine as a whole");
    }

    [Fact]
    public void CpuBetween_NoTimePassed_IsUnreadableNotZero()
    {
        var times = new CpuTimes(10, 20, 5);

        var reading = WindowsResourceProbe.CpuBetween(times, times, [times], [times]);

        Assert.False(reading.CouldRead);
    }

    // ---------------------------------------------------------------------------------------------
    // Disks
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void DiskBetween_FirstSample_KeepsNameAndKind()
    {
        var reading = WindowsResourceProbe.DiskBetween("PhysicalDrive0", DiskKind.SolidState, null,
            WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-1.bin")));

        Assert.False(reading.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, reading.UnreadableReason);
        Assert.Equal("PhysicalDrive0", reading.Name);
        Assert.Equal(DiskKind.SolidState, reading.Kind);
    }

    [Fact]
    public void DiskBetween_RealCaptures_PercentAndMilliseconds()
    {
        var reading = WindowsResourceProbe.DiskBetween("PhysicalDrive0", DiskKind.SolidState,
            WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-1.bin")),
            WindowsResourceParsing.ParseDiskPerformance(Fixture("disk0-performance-2.bin")));

        Assert.True(reading.CouldRead);
        Assert.Equal((1 - 8656723.0 / 10113157.0) * 100, reading.BusyPercent, 6);
        Assert.Equal(19536384 / 1.0113157, reading.WriteBytesPerSecond, 3);
        Assert.Equal(3.5207, reading.MillisecondsPerRead!.Value, 9);
        Assert.Equal(143.8397 / 2662, reading.MillisecondsPerWrite!.Value, 9);
        Assert.Equal((35207.0 + 1438397.0) / 10113157.0, reading.QueueLength!.Value, 9);
    }

    [Fact]
    public void DiskBetween_NoWritesInTheInterval_TimePerWriteIsNull()
    {
        var earlier = new DiskCounters(0, 0, 0, WriteTime: 500, IdleTime: 0, ReadCount: 0, WriteCount: 9, 0, QueryTime: 0);
        var later = earlier with { IdleTime = 10_000_000, QueryTime = 10_000_000 };

        var reading = WindowsResourceProbe.DiskBetween("PhysicalDrive3", DiskKind.Spinning, earlier, later);

        Assert.True(reading.CouldRead);
        Assert.Equal(0, reading.BusyPercent);
        Assert.Null(reading.MillisecondsPerWrite);
        Assert.Null(reading.MillisecondsPerRead);
    }

    // ---------------------------------------------------------------------------------------------
    // Network
    // ---------------------------------------------------------------------------------------------

    private static RawInterface Nic(uint index, ulong received, ulong sent) =>
        new(index, "Ethernet", "adapter", Type: 6, IsHardware: true, OperStatus: 1, ReceiveLinkSpeed: 0, received, sent);

    [Fact]
    public void NetworkBetween_FirstSample_IsUnreadable()
    {
        var reading = WindowsResourceProbe.NetworkBetween(null, new InterfaceSnapshot(0, [Nic(1, 0, 0)]));

        Assert.Equal(ResourceUnreadableReasons.FirstSample, reading.UnreadableReason);
    }

    [Fact]
    public void NetworkBetween_TwoAdaptersOverTwoSeconds_AddsThemPerSecond()
    {
        var earlier = new InterfaceSnapshot(0, [Nic(1, 1_000, 500), Nic(2, 10_000, 0)]);
        var later = new InterfaceSnapshot(2 * OneSecond, [Nic(1, 5_000, 2_500), Nic(2, 14_000, 1_000)]);

        var reading = WindowsResourceProbe.NetworkBetween(earlier, later);

        Assert.True(reading.CouldRead);
        Assert.Equal((4_000 + 4_000) / 2.0, reading.ReceiveBytesPerSecond, 9);
        Assert.Equal((2_000 + 1_000) / 2.0, reading.SendBytesPerSecond, 9);
    }

    [Fact]
    public void NetworkBetween_AdapterResetOrNewlyUp_IsLeftOutOfThatInterval()
    {
        // Adapter 1 restarted its counters; adapter 3 came up mid-interval. Neither has a difference
        // that means anything, so only adapter 2 is counted.
        var earlier = new InterfaceSnapshot(0, [Nic(1, 9_000_000, 9_000_000), Nic(2, 0, 0)]);
        var later = new InterfaceSnapshot(OneSecond, [Nic(1, 100, 100), Nic(2, 300, 200), Nic(3, 7_000_000, 7_000_000)]);

        var reading = WindowsResourceProbe.NetworkBetween(earlier, later);

        Assert.Equal(300, reading.ReceiveBytesPerSecond, 9);
        Assert.Equal(200, reading.SendBytesPerSecond, 9);
    }

    [Fact]
    public void NetworkBetween_NoTimePassed_IsUnreadable()
    {
        var snapshot = new InterfaceSnapshot(5, [Nic(1, 0, 0)]);

        Assert.False(WindowsResourceProbe.NetworkBetween(snapshot, snapshot).CouldRead);
    }

    // ---------------------------------------------------------------------------------------------
    // Processes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ProcessesBetween_FirstSample_IsUnreadable()
    {
        var reading = WindowsResourceProbe.ProcessesBetween(null, new ProcessSnapshot(0, RealProcesses()), 24);

        Assert.False(reading.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, reading.UnreadableReason);
        Assert.Empty(reading.Processes);
    }

    [Fact]
    public void ProcessesBetween_RealList_LeavesOutTheIdleProcessAndCarriesParentsAndStartTimes()
    {
        var processes = RealProcesses();

        var reading = WindowsResourceProbe.ProcessesBetween(new ProcessSnapshot(0, processes), new ProcessSnapshot(OneSecond, processes), 24);

        Assert.True(reading.CouldRead);
        Assert.Equal(40, reading.Processes.Count);
        Assert.DoesNotContain(reading.Processes, p => p.Pid == 0);

        var services = Assert.Single(reading.Processes, p => p.Pid == 1816);
        Assert.Equal("services.exe", services.Name);
        Assert.Equal(1676, services.ParentPid);
        Assert.Equal(new DateTime(2026, 10, 13, 18, 10, 20, DateTimeKind.Utc).AddTicks(5323074), services.StartTimeUtc);

        // The same reading twice: nothing was used in between, by anyone.
        Assert.All(reading.Processes, p => Assert.Equal(0, p.CpuPercent));
        Assert.All(reading.Processes, p => Assert.Equal(0, p.DiskReadBytesPerSecond));
    }

    [Fact]
    public void ProcessesBetween_OneCoreForOneSecondOnTwentyFour_IsAShareOfTheWholeMachine()
    {
        var earlier = new ProcessSnapshot(0, [Process(500, created: 100, cpu: 0)]);
        var later = new ProcessSnapshot(OneSecond, [Process(500, created: 100, cpu: 10_000_000)]);

        var reading = WindowsResourceProbe.ProcessesBetween(earlier, later, 24);

        Assert.Equal(100.0 / 24, Assert.Single(reading.Processes).CpuPercent, 9);
    }

    [Fact]
    public void ProcessesBetween_ProcessIdReusedByANewProcess_IsNotChargedTheOldOnesDifference()
    {
        // Process 500 exited and a new process was given its id. Measured against the old one, its
        // 1 second of processor would read as minus 99 seconds; it is a new process, so everything it
        // has used, it used in this interval.
        var earlier = new ProcessSnapshot(0, [Process(500, created: 100, cpu: 1_000_000_000, read: 9_000_000)]);
        var later = new ProcessSnapshot(OneSecond, [Process(500, created: 200, cpu: 10_000_000, read: 4_096)]);

        var reading = Assert.Single(WindowsResourceProbe.ProcessesBetween(earlier, later, 1).Processes);

        Assert.Equal(100, reading.CpuPercent, 9);
        Assert.Equal(4_096, reading.DiskReadBytesPerSecond!.Value, 9);
    }

    [Fact]
    public void ProcessesBetween_CounterWentBackwards_GrewByNothing()
    {
        var earlier = new ProcessSnapshot(0, [Process(7, created: 1, read: 9_000, written: 9_000)]);
        var later = new ProcessSnapshot(OneSecond, [Process(7, created: 1, read: 10, written: 9_500)]);

        var reading = Assert.Single(WindowsResourceProbe.ProcessesBetween(earlier, later, 1).Processes);

        Assert.Equal(0, reading.DiskReadBytesPerSecond);
        Assert.Equal(500, reading.DiskWriteBytesPerSecond);
    }

    [Fact]
    public void ProcessesBetween_ProcessWithNoName_IsLabelledByItsId()
    {
        var earlier = new ProcessSnapshot(0, []);
        var later = new ProcessSnapshot(OneSecond, [Process(42, created: 1, name: null)]);

        Assert.Equal("process 42", Assert.Single(WindowsResourceProbe.ProcessesBetween(earlier, later, 1).Processes).Name);
    }

    [Fact]
    public void ProcessesBetween_NoTimePassed_IsUnreadable()
    {
        var snapshot = new ProcessSnapshot(9, [Process(1, 1)]);

        Assert.False(WindowsResourceProbe.ProcessesBetween(snapshot, snapshot, 4).CouldRead);
    }

    // ---------------------------------------------------------------------------------------------
    // Stall proxies
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void StallBetween_FirstSample_IsUnreadable()
    {
        Assert.Equal(ResourceUnreadableReasons.FirstSample,
            WindowsResourceProbe.StallBetween(null, new ProcessSnapshot(0, RealProcesses())).UnreadableReason);
    }

    [Fact]
    public void StallBetween_RealListUnderLoad_RunQueueIsEveryThreadWaitingForAProcessor()
    {
        var processes = RealProcesses();

        var reading = WindowsResourceProbe.StallBetween(new ProcessSnapshot(0, processes), new ProcessSnapshot(OneSecond, processes));

        Assert.True(reading.CouldRead);
        Assert.Equal(StallSource.WindowsCounters, reading.Source);
        Assert.Equal(processes.Sum(p => p.ThreadsWaitingOnCpu), reading.RunQueueLength);
        Assert.True(reading.RunQueueLength >= 25, "the capture tool alone had 25 threads waiting");
        Assert.Equal(0, reading.HardPageFaultsPerSecond);
    }

    [Fact]
    public void StallBetween_WhatWindowsDoesNotOffer_IsNullNotZero()
    {
        var reading = WindowsResourceProbe.StallBetween(new ProcessSnapshot(0, []), new ProcessSnapshot(OneSecond, []));

        Assert.Null(reading.CpuStalledPercent);
        Assert.Null(reading.MemoryStalledPercent);
        Assert.Null(reading.IoStalledPercent);
        Assert.Null(reading.ProcessesWaitingOnDisk);
        Assert.Null(reading.MacOsMemoryPressureLevel);
    }

    [Fact]
    public void StallBetween_HardFaults_CountsGrowthAndEveryFaultOfANewProcess()
    {
        // Process 1 faulted 300 more times; process 2 is new, and its 100 faults all happened in the
        // interval; process 3 exited and is not counted. Over two seconds: 400 / 2.
        var earlier = new ProcessSnapshot(0, [Process(1, created: 1, faults: 1_000), Process(3, created: 1, faults: 50)]);
        var later = new ProcessSnapshot(2 * OneSecond, [Process(1, created: 1, faults: 1_300), Process(2, created: 5, faults: 100)]);

        Assert.Equal(200, WindowsResourceProbe.StallBetween(earlier, later).HardPageFaultsPerSecond);
    }

    [Fact]
    public void StallBetween_HardFaultCountWrapsPastThirtyTwoBits_StillCountsTheGrowth()
    {
        var earlier = new ProcessSnapshot(0, [Process(1, created: 1, faults: uint.MaxValue - 9)]);
        var later = new ProcessSnapshot(OneSecond, [Process(1, created: 1, faults: 10)]);

        Assert.Equal(20, WindowsResourceProbe.StallBetween(earlier, later).HardPageFaultsPerSecond);
    }
}
