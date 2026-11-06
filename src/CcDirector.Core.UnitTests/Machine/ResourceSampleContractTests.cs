using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE TESTS THAT PROVE THE CONTRACT CHECKER IS A WORKING INSTRUMENT.
///
/// <see cref="ResourceSampleContract"/> is what three platform probes are judged against, and it runs
/// against a real machine where nobody can arrange a violation on purpose. A checker that has never been
/// watched REJECT anything is indistinguishable from one that returns an empty list whatever it is
/// given - and an empty list is exactly what it returns when a probe is correct, so a green contract run
/// would look identical either way.
///
/// So each check is fed a sample that breaks it, and must reject that sample naming the right thing.
/// These are pure values: no machine is read and no clock is consulted, which is why they live in the
/// parallel half of the Core tests.
/// </summary>
[Trait("Category", "Resources")]
public class ResourceSampleContractTests
{
    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    /// <summary>
    /// A sample that keeps every rule, shaped like a healthy ten-core machine. Each test bends one part
    /// of it, so the rest stays out of the way of what is being proved.
    /// </summary>
    private static ResourceSample Healthy(
        CpuReading? cpu = null,
        StallReading? stall = null,
        DiskEvidenceSet? disks = null,
        ProcessSetReading? processes = null) =>
        new(
            TakenAtUtc: new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc),
            Cpu: cpu ?? CpuReading.Read(totalBusyPercent: 20, busiestCorePercent: 55, logicalCoreCount: 10),
            Memory: MachineMemoryReading.Read(16 * Gigabyte, 8 * Gigabyte, new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc)),
            Swap: SwapReading.Read(totalBytes: 2 * Gigabyte, usedBytes: Gigabyte),
            Stall: stall ?? StallReading.Read(StallSource.LinuxPressureStall,
                cpuStalledPercent: 0.06, memoryStalledPercent: 0.17, ioStalledPercent: 32),
            DiskEvidence: disks ?? DiskEvidenceSet.Read(
            [
                DiskEvidence.Read("sdc", DiskKind.Spinning, isInSystemVolume: true, busyPercent: 75,
                    millisecondsPerRead: null, millisecondsPerWrite: 26, averageQueueDepth: 3.5),
            ]),
            Processes: processes ?? ProcessSetReading.Read(
            [
                new ProcessReading(1, null, "systemd", null, 0.1, 8UL * 1024 * 1024, null, null),
                new ProcessReading(4242, 1, "dotnet", null, 14.0, 512UL * 1024 * 1024, null, null),
            ]));

    private static ProcessReading Process(int pid, int? parentPid, string name, double? cpuPercent) =>
        new(pid, parentPid, name, null, cpuPercent, 1024, null, null);

    [Fact]
    public void AHealthySample_BreaksNothing()
    {
        // Stated first, because every test below means nothing if the checker rejects a correct sample.
        Assert.Empty(ResourceSampleContract.Violations(Healthy()));
    }

    [Fact]
    public void Violations_NullSample_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ResourceSampleContract.Violations(null!));
    }

    // -------------------------------------------------------------------------------------------
    // The rule that matters most across platforms
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void PerCoreProcessorFigures_AreRejected_BecauseTheySumPastOneMachine()
    {
        // Four processes each holding one core of ten. Measured as a share of the whole machine that is
        // 10% each; measured per core it is 100% each, and the sum gives the convention away.
        var perCore = ProcessSetReading.Read(
        [
            Process(1, null, "worker one", 100),
            Process(2, null, "worker two", 100),
            Process(3, null, "worker three", 100),
            Process(4, null, "worker four", 100),
        ]);

        var broken = ResourceSampleContract.Violations(Healthy(processes: perCore));

        Assert.Contains(broken, one => one.Contains("per core", StringComparison.Ordinal));
        Assert.Contains(broken, one => one.Contains("400%", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSameFourProcessesAsAShareOfTheWholeMachine_AreAccepted()
    {
        // The counterpart to the test above: the checker must not simply dislike four busy processes.
        var perMachine = ProcessSetReading.Read(
        [
            Process(1, null, "worker one", 10),
            Process(2, null, "worker two", 10),
            Process(3, null, "worker three", 10),
            Process(4, null, "worker four", 10),
        ]);

        Assert.Empty(ResourceSampleContract.Violations(Healthy(processes: perMachine)));
    }

    [Fact]
    public void AProcessWithNoFigures_IsAccepted_BecauseTheyAreRefusedNotMissing()
    {
        // The macOS case: roughly three hundred of seven hundred processes refuse their figures, and they
        // must stay in the list, named, with null - never dropped and never zeroed.
        var someRefused = ProcessSetReading.Read(
        [
            Process(4242, 1, "dotnet", 14.0),
            Process(501, 1, "mds_stores", null),
        ]);

        Assert.Empty(ResourceSampleContract.Violations(Healthy(processes: someRefused)));
    }

    [Fact]
    public void AnEmptyProcessList_IsRejected_BecauseThisProcessIsAlwaysInIt()
    {
        var broken = ResourceSampleContract.Violations(Healthy(processes: ProcessSetReading.Read([])));

        Assert.Contains(broken, one => one.Contains("empty", StringComparison.Ordinal));
    }

    [Fact]
    public void ANamelessProcess_IsRejected_BecauseADiagnosisCouldNotNameIt()
    {
        var broken = ResourceSampleContract.Violations(
            Healthy(processes: ProcessSetReading.Read([Process(9, 1, "   ", 1)])));

        Assert.Contains(broken, one => one.Contains("has no name", StringComparison.Ordinal));
    }

    [Fact]
    public void AProcessListedTwice_IsRejected_BecauseASessionTotalWouldDoubleCountIt()
    {
        var broken = ResourceSampleContract.Violations(Healthy(processes: ProcessSetReading.Read(
        [
            Process(4242, 1, "dotnet", 5),
            Process(4242, 1, "dotnet", 5),
        ])));

        Assert.Contains(broken, one => one.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void AProcessThatIsItsOwnParent_IsRejected_BecauseWalkingTheTreeWouldNotEnd()
    {
        var broken = ResourceSampleContract.Violations(
            Healthy(processes: ProcessSetReading.Read([Process(7, 7, "loop", 1)])));

        Assert.Contains(broken, one => one.Contains("its own parent", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------------------------
    // A reading that failed must carry no figure, and one that succeeded must carry no reason
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnreadableProcessorCarryingFigures_IsRejected()
    {
        var lying = new CpuReading(CouldRead: false, UnreadableReason: "no access",
            TotalBusyPercent: 42, BusiestCorePercent: 99, LogicalCoreCount: 10);

        var broken = ResourceSampleContract.Violations(Healthy(cpu: lying));

        // The core count is allowed to survive; the busy figures are not.
        Assert.Contains(broken, one => one.Contains("carries a busy figure", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableReadingWithNoReason_IsRejected()
    {
        var silent = new CpuReading(CouldRead: false, UnreadableReason: null, 0, 0, 0);

        var broken = ResourceSampleContract.Violations(Healthy(cpu: silent));

        Assert.Contains(broken, one => one.Contains("gave no reason", StringComparison.Ordinal));
    }

    [Fact]
    public void AReadableReadingThatStillCarriesAReason_IsRejected_BecauseTheTwoDisagree()
    {
        var confused = new CpuReading(CouldRead: true, UnreadableReason: "no access", 20, 55, 10);

        var broken = ResourceSampleContract.Violations(Healthy(cpu: confused));

        Assert.Contains(broken, one => one.Contains("the two disagree", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(101.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AProcessorFigureOutsideNoughtToOneHundred_IsRejected(double percent)
    {
        var broken = ResourceSampleContract.Violations(
            Healthy(cpu: CpuReading.Read(totalBusyPercent: percent, busiestCorePercent: 100, logicalCoreCount: 10)));

        Assert.NotEmpty(broken);
    }

    [Fact]
    public void ZeroLogicalCores_OnAReadableProcessor_IsRejected()
    {
        var broken = ResourceSampleContract.Violations(
            Healthy(cpu: CpuReading.Read(totalBusyPercent: 20, busiestCorePercent: 55, logicalCoreCount: 0)));

        Assert.Contains(broken, one => one.Contains("logical cores", StringComparison.Ordinal));
    }

    [Fact]
    public void ABusiestCoreBelowTheWholeMachine_IsRejected_BecauseOneIsMeasuredWrong()
    {
        var broken = ResourceSampleContract.Violations(
            Healthy(cpu: CpuReading.Read(totalBusyPercent: 80, busiestCorePercent: 40, logicalCoreCount: 10)));

        Assert.Contains(broken, one => one.Contains("the wrong base", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------------------------
    // Stalls and disk evidence
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnreadableDisk_CarryingAFigure_IsRejected()
    {
        var lying = new DiskEvidence("sdc", DiskKind.Spinning, IsInSystemVolume: true, CouldRead: false,
            UnreadableReason: "statistics locked away", BusyPercent: 75,
            MillisecondsPerRead: null, MillisecondsPerWrite: null, AverageQueueDepth: null);

        var broken = ResourceSampleContract.Violations(
            Healthy(disks: DiskEvidenceSet.Read([lying])));

        Assert.Contains(broken, one => one.Contains("carries a figure", StringComparison.Ordinal));
    }

    [Fact]
    public void ANegativeDiskLatency_IsRejected()
    {
        var broken = ResourceSampleContract.Violations(Healthy(disks: DiskEvidenceSet.Read(
        [
            DiskEvidence.Read("sdc", DiskKind.Spinning, isInSystemVolume: true, busyPercent: 10,
                millisecondsPerRead: -1, millisecondsPerWrite: null, averageQueueDepth: null),
        ])));

        Assert.Contains(broken, one => one.Contains("cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableDiskListing_WithDisksInIt_IsRejected()
    {
        var lying = new DiskEvidenceSet(CouldRead: false, UnreadableReason: "could not be listed",
            Disks: [DiskEvidence.Read("sdc", DiskKind.Spinning, true, 10, null, null, null)]);

        var broken = ResourceSampleContract.Violations(Healthy(disks: lying));

        Assert.Contains(broken, one => one.Contains("not empty", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableProcessList_WithProcessesInIt_IsRejected()
    {
        var lying = new ProcessSetReading(CouldRead: false, UnreadableReason: "the enumeration was refused",
            Processes: [Process(1, null, "systemd", 0.1)]);

        var broken = ResourceSampleContract.Violations(Healthy(processes: lying));

        Assert.Contains(broken, one => one.Contains("not empty", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(250.0)]
    [InlineData(double.NaN)]
    public void APerProcessPercentageOutsideNoughtToOneHundred_IsRejected(double percent)
    {
        var broken = ResourceSampleContract.Violations(
            Healthy(processes: ProcessSetReading.Read([Process(4242, 1, "dotnet", percent)])));

        Assert.Contains(broken, one => one.Contains("dotnet", StringComparison.Ordinal));
    }

    [Fact]
    public void ANamelessDisk_IsRejected_BecauseADiagnosisCouldNotNameIt()
    {
        var broken = ResourceSampleContract.Violations(Healthy(disks: DiskEvidenceSet.Read(
        [
            DiskEvidence.Read("  ", DiskKind.Spinning, isInSystemVolume: true, busyPercent: 10,
                millisecondsPerRead: null, millisecondsPerWrite: null, averageQueueDepth: null),
        ])));

        Assert.Contains(broken, one => one.Contains("a disk has no name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(130.0)]
    public void ADiskBusyPercentageOutsideNoughtToOneHundred_IsRejected(double percent)
    {
        var broken = ResourceSampleContract.Violations(Healthy(disks: DiskEvidenceSet.Read(
        [
            DiskEvidence.Read("sdc", DiskKind.Spinning, isInSystemVolume: true, busyPercent: percent,
                millisecondsPerRead: null, millisecondsPerWrite: null, averageQueueDepth: null),
        ])));

        Assert.Contains(broken, one => one.Contains("disk sdc", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableStallReadingThatStillNamesASource_IsRejected()
    {
        var lying = new StallReading(CouldRead: false, UnreadableReason: "pressure is off in this kernel",
            StallSource.LinuxPressureStall, null, null, null, null, null, null, null);

        var broken = ResourceSampleContract.Violations(Healthy(stall: lying));

        Assert.Contains(broken, one => one.Contains("name LinuxPressureStall", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeHardPageFaults_AreRejected()
    {
        var broken = ResourceSampleContract.Violations(Healthy(
            stall: StallReading.Read(StallSource.WindowsCounters, hardPageFaultsPerSecond: -3)));

        Assert.Contains(broken, one => one.Contains("hard page faults", StringComparison.Ordinal));
    }

    [Fact]
    public void ANegativeCountOfProcessesWaitingOnDisk_IsRejected()
    {
        var broken = ResourceSampleContract.Violations(Healthy(
            stall: StallReading.Read(StallSource.LinuxPressureStall, processesWaitingOnDisk: -1)));

        Assert.Contains(broken, one => one.Contains("waiting on the disk", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------------------------
    // The processor figures a platform may not offer
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AReadableProcessorWithNoCoreCount_IsRejected_BecauseItDisablesTwoOtherChecks()
    {
        // Every platform can answer the core count, so null on a SUCCESSFUL read is never an honest
        // "not offered". It is also the hole that mattered most: without the count, ShareOfMachine
        // returns null for every process and the sum-past-one-machine check cannot run, so a probe that
        // merely forgot to pass it would report nothing per process and still pass the whole contract.
        var noCount = CpuReading.Read(totalBusyPercent: 20, busiestCorePercent: 55, logicalCoreCount: null);

        var broken = ResourceSampleContract.Violations(Healthy(cpu: noCount));

        Assert.Contains(broken, one => one.Contains("gave no logical core count", StringComparison.Ordinal));
    }

    [Fact]
    public void ANullBusiestCore_IsAccepted_BecauseWindowsMayNotOfferOne()
    {
        // More than 64 logical processors: the whole-machine figure is known, the per-core one is not.
        var noPerCore = CpuReading.Read(totalBusyPercent: 20, busiestCorePercent: null, logicalCoreCount: 128);

        Assert.Empty(ResourceSampleContract.Violations(Healthy(cpu: noPerCore)));
    }

    [Fact]
    public void ACoreCountThatSurvivedAFailedBusyRead_IsAccepted()
    {
        // A static fact about the machine is not part of what failed, exactly as a disk keeps its name.
        var unreadableButCounted = CpuReading.Unreadable("the counter was refused", logicalCoreCount: 10);

        Assert.Empty(ResourceSampleContract.Violations(Healthy(cpu: unreadableButCounted)));
    }

    [Fact]
    public void AZeroCoreCountOnAnUnreadableProcessor_IsRejected_BecauseUnknownIsNull()
    {
        var zeroed = new CpuReading(CouldRead: false, UnreadableReason: "refused", 0, null, 0);

        var broken = ResourceSampleContract.Violations(Healthy(cpu: zeroed));

        Assert.Contains(broken, one => one.Contains("never zero", StringComparison.Ordinal));
    }

    [Fact]
    public void AStallReadingThatSucceededWithNoSource_IsRejected()
    {
        var sourceless = new StallReading(CouldRead: true, UnreadableReason: null, StallSource.None,
            CpuStalledPercent: 1, MemoryStalledPercent: null, IoStalledPercent: null,
            RunQueueLength: null, HardPageFaultsPerSecond: null,
            MacOsMemoryPressureLevel: null, ProcessesWaitingOnDisk: null);

        var broken = ResourceSampleContract.Violations(Healthy(stall: sourceless));

        Assert.Contains(broken, one => one.Contains("name no source", StringComparison.Ordinal));
    }

    [Fact]
    public void AStallPercentageOutsideNoughtToOneHundred_IsRejected()
    {
        var broken = ResourceSampleContract.Violations(Healthy(
            stall: StallReading.Read(StallSource.LinuxPressureStall, ioStalledPercent: 140)));

        Assert.Contains(broken, one => one.Contains("the disk stall figure", StringComparison.Ordinal));
    }

    [Fact]
    public void ANegativeRunQueue_IsRejected()
    {
        var broken = ResourceSampleContract.Violations(Healthy(
            stall: StallReading.Read(StallSource.WindowsCounters, runQueueLength: -2)));

        Assert.Contains(broken, one => one.Contains("run queue", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDevlinuxStallReading_BreaksNothing()
    {
        // The real figures from 2026-10-15, the fixture this whole mission is built around: the processor
        // and memory healthy while the disk stalls a third of the time. The checker must accept it.
        var devlinux = StallReading.Read(StallSource.LinuxPressureStall,
            cpuStalledPercent: 0.06, memoryStalledPercent: 0.17, ioStalledPercent: 32);

        Assert.Empty(ResourceSampleContract.Violations(Healthy(stall: devlinux)));
    }
}
