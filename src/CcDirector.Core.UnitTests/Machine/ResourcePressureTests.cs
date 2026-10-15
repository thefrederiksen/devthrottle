using CcDirector.Core.Machine;
using Xunit;
using static CcDirector.Core.Tests.Machine.ResourceSampleBuilder;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The fold from a history of resource samples to one verdict per resource (mission section 5b).
///
/// Every history is built by hand from a fixed start time, so nothing here reads the clock or the
/// machine it runs on - a test for "the processor has been pinned for a minute" must not need a pinned
/// processor or a minute.
/// </summary>
[Trait("Category", "Resources")]
public class ResourcePressureTests
{
    private static ResourceVerdict Verdict(IReadOnlyList<ResourceSample> history, ResourceKind kind) =>
        ResourcePressure.Evaluate(history).Resources.Single(verdict => verdict.Kind == kind);

    private static ResourceSampleBuilder CpuAt(double busy) =>
        new() { Cpu = CpuReading.Read(busy, busiestCorePercent: Math.Min(100, busy + 5), logicalCoreCount: 32) };

    private static ResourceSampleBuilder DiskBusy(double busy) => new()
    {
        Disks = DiskSetReading.Read(new[] { PhysicalDiskReading.Read("sdc", DiskKind.Spinning, busy, 0, 5_000_000, 10, 20, 2) }),
        Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 0, memoryStalledPercent: 0, ioStalledPercent: 0),
    };

    // -------------------------------------------------------------------------------------------
    // The shape of the verdict
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_AnyHistory_GivesOneVerdictPerResourceInOrder()
    {
        var verdict = ResourcePressure.Evaluate(new[] { new ResourceSampleBuilder().Build() });

        Assert.Equal(
            new[] { ResourceKind.Cpu, ResourceKind.Memory, ResourceKind.DiskActivity, ResourceKind.Swap, ResourceKind.DiskFree },
            verdict.Resources.Select(resource => resource.Kind));
    }

    [Fact]
    public void Evaluate_EmptyHistory_IsUnknownForEveryResource()
    {
        var verdict = ResourcePressure.Evaluate(Array.Empty<ResourceSample>());

        Assert.All(verdict.Resources, resource => Assert.Equal(ResourceLevel.Unknown, resource.Level));
        Assert.Equal(ResourceLevel.Unknown, verdict.Worst);
    }

    [Fact]
    public void Evaluate_QuietMachineForAMinute_IsNormalEverywhere()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder());

        var verdict = ResourcePressure.Evaluate(history);

        Assert.All(verdict.Resources, resource => Assert.Equal(ResourceLevel.Normal, resource.Level));
        Assert.All(verdict.Resources, resource => Assert.False(string.IsNullOrWhiteSpace(resource.Evidence)));
    }

    // -------------------------------------------------------------------------------------------
    // Processor: over the limit for the whole sustained window
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(97.0, ResourceLevel.Critical)]
    [InlineData(95.1, ResourceLevel.Critical)]
    [InlineData(95.0, ResourceLevel.Tight)]    // "over 95" - exactly 95 is not over
    [InlineData(90.0, ResourceLevel.Tight)]
    [InlineData(85.1, ResourceLevel.Tight)]
    [InlineData(85.0, ResourceLevel.Normal)]   // "over 85" - exactly 85 is not over
    [InlineData(40.0, ResourceLevel.Normal)]
    public void Evaluate_CpuHeldForSixtySeconds_FoldsByTheLimits(double busy, ResourceLevel expected)
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => CpuAt(busy));

        Assert.Equal(expected, Verdict(history, ResourceKind.Cpu).Level);
    }

    [Fact]
    public void Evaluate_CpuPinnedForFiftyFiveSeconds_IsNormalNotYetCounted()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(55), _ => CpuAt(100));

        var cpu = Verdict(history, ResourceKind.Cpu);

        Assert.Equal(ResourceLevel.Normal, cpu.Level);
        Assert.Contains("55 seconds", cpu.Evidence);
    }

    [Fact]
    public void Evaluate_OneQuietSampleInsideTheWindow_HoldsTheWholeWindowUnderTheLimit()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(90), offset => CpuAt(offset == TimeSpan.FromSeconds(60) ? 50 : 100));

        Assert.Equal(ResourceLevel.Normal, Verdict(history, ResourceKind.Cpu).Level);
    }

    [Fact]
    public void Evaluate_QuietSampleOlderThanTheWindow_DoesNotCount()
    {
        // The quiet sample is at 0 s; the window from the newest at 90 s reaches back to 30 s.
        var history = Every5Seconds(TimeSpan.FromSeconds(90), offset => CpuAt(offset == TimeSpan.Zero ? 50 : 99));

        Assert.Equal(ResourceLevel.Critical, Verdict(history, ResourceKind.Cpu).Level);
    }

    [Fact]
    public void Evaluate_MixOfRedAndAmberInTheWindow_IsTheLowerLevelHeldThroughout()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), offset => CpuAt(offset.TotalSeconds % 10 == 0 ? 99 : 90));

        Assert.Equal(ResourceLevel.Tight, Verdict(history, ResourceKind.Cpu).Level);
    }

    [Fact]
    public void Evaluate_CpuCritical_EvidenceNamesTheFigureAndHowLong()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(120), _ => CpuAt(97));

        var cpu = Verdict(history, ResourceKind.Cpu);

        Assert.Equal(ResourceLevel.Critical, cpu.Level);
        Assert.Contains("97%", cpu.Evidence);
        Assert.Contains("95%", cpu.Evidence);
        Assert.Contains("60 seconds", cpu.Evidence);
    }

    [Fact]
    public void Evaluate_BusiestCorePinnedButMachineQuiet_NeverRaisesTheLevel()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(120),
            _ => new ResourceSampleBuilder { Cpu = CpuReading.Read(totalBusyPercent: 4, busiestCorePercent: 100, logicalCoreCount: 32) });

        Assert.Equal(ResourceLevel.Normal, Verdict(history, ResourceKind.Cpu).Level);
    }

    [Fact]
    public void Evaluate_NewestCpuUnreadable_IsUnknownWithTheReason()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), offset => offset == TimeSpan.FromSeconds(60)
            ? new ResourceSampleBuilder { Cpu = CpuReading.Unreadable("could not open /proc/stat") }
            : CpuAt(99));

        var cpu = Verdict(history, ResourceKind.Cpu);

        Assert.Equal(ResourceLevel.Unknown, cpu.Level);
        Assert.Contains("could not open /proc/stat", cpu.Evidence);
    }

    [Fact]
    public void Evaluate_FirstSampleCpu_IsUnknownNeverNormal()
    {
        var history = new[] { (new ResourceSampleBuilder { Cpu = CpuReading.Unreadable(ResourceUnreadableReasons.FirstSample) }).Build() };

        Assert.Equal(ResourceLevel.Unknown, Verdict(history, ResourceKind.Cpu).Level);
    }

    [Fact]
    public void Evaluate_OneUnreadableSampleInsideAPinnedWindow_IsSkippedAndTheWarningStands()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), offset => offset == TimeSpan.FromSeconds(30)
            ? new ResourceSampleBuilder { Cpu = CpuReading.Unreadable("a read failed") }
            : CpuAt(99));

        Assert.Equal(ResourceLevel.Critical, Verdict(history, ResourceKind.Cpu).Level);
    }

    [Fact]
    public void Evaluate_OnlyTheOldestReadableSampleReachesBack_IsNotCountedWhenItIsUnreadable()
    {
        // The only sample old enough to complete the window cannot be read, so the readable ones cover
        // only 55 seconds - not long enough to count.
        var history = Every5Seconds(TimeSpan.FromSeconds(60), offset => offset == TimeSpan.Zero
            ? new ResourceSampleBuilder { Cpu = CpuReading.Unreadable("a read failed") }
            : CpuAt(99));

        Assert.Equal(ResourceLevel.Normal, Verdict(history, ResourceKind.Cpu).Level);
    }

    [Fact]
    public void Evaluate_SamplesOutOfOrder_AreJudgedByTheirTimes()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => CpuAt(99));
        history.Reverse();

        Assert.Equal(ResourceLevel.Critical, Verdict(history, ResourceKind.Cpu).Level);
    }

    [Fact]
    public void Evaluate_TwoSamplesSixtySecondsApart_BothPinned_Counts()
    {
        var history = new[] { (CpuAt(99) with { At = TimeSpan.Zero }).Build(), (CpuAt(99) with { At = TimeSpan.FromSeconds(60) }).Build() };

        Assert.Equal(ResourceLevel.Critical, Verdict(history, ResourceKind.Cpu).Level);
    }

    [Fact]
    public void Evaluate_ClockJumpedBackwards_EachSampleIsPlacedByItsOwnTime()
    {
        // The clock jumped back two minutes: the last sample handed in carries the EARLIEST time. By time,
        // the newest sample is the 55-second one, and the readings span less than a minute.
        var history = Every5Seconds(TimeSpan.FromSeconds(55), _ => CpuAt(99));
        history.Add((CpuAt(99) with { At = TimeSpan.FromSeconds(-120) }).Build());

        // The -120 s sample is older than the window, so the window is complete: still pinned.
        Assert.Equal(ResourceLevel.Critical, Verdict(history, ResourceKind.Cpu).Level);
    }

    // -------------------------------------------------------------------------------------------
    // Disk activity: the busiest physical disk, or Linux disk stall, whichever is higher
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(97.0, ResourceLevel.Critical)]
    [InlineData(95.0, ResourceLevel.Tight)]
    [InlineData(81.0, ResourceLevel.Tight)]
    [InlineData(80.0, ResourceLevel.Normal)]
    [InlineData(10.0, ResourceLevel.Normal)]
    public void Evaluate_DiskBusyHeldForSixtySeconds_FoldsByTheLimits(double busy, ResourceLevel expected)
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => DiskBusy(busy));

        Assert.Equal(expected, Verdict(history, ResourceKind.DiskActivity).Level);
    }

    [Fact]
    public void Evaluate_DiskActivity_UsesTheBusiestDisk()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Disks = DiskSetReading.Read(new[]
            {
                PhysicalDiskReading.Read("sda", DiskKind.SolidState, 5, 0, 0, null, null, null),
                PhysicalDiskReading.Read("sdc", DiskKind.Spinning, 98, 0, 0, null, null, null),
            }),
        });

        var disk = Verdict(history, ResourceKind.DiskActivity);

        Assert.Equal(ResourceLevel.Critical, disk.Level);
        Assert.Contains("98%", disk.Evidence);
    }

    [Fact]
    public void Evaluate_LinuxIoStallHigherThanBusy_UsesTheStall()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => DiskBusy(20) with
        {
            Stall = StallReading.Read(StallSource.LinuxPressureStall, ioStalledPercent: 90),
        });

        Assert.Equal(ResourceLevel.Tight, Verdict(history, ResourceKind.DiskActivity).Level);
    }

    [Fact]
    public void Evaluate_IoStallFromAProxyPlatform_IsNotUsedAsDiskActivity()
    {
        // Only the Linux pressure figure is a direct share of time on the same scale as busy.
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => DiskBusy(20) with
        {
            Stall = StallReading.Read(StallSource.WindowsCounters, ioStalledPercent: 99),
        });

        Assert.Equal(ResourceLevel.Normal, Verdict(history, ResourceKind.DiskActivity).Level);
    }

    [Fact]
    public void DiskActivityPercent_OnlyTheStallReadable_IsTheStall()
    {
        var sample = new ResourceSampleBuilder
        {
            Disks = DiskSetReading.Unreadable("no /proc/diskstats"),
            Stall = StallReading.Read(StallSource.LinuxPressureStall, ioStalledPercent: 33),
        }.Build();

        Assert.Equal(33, ResourcePressure.DiskActivityPercent(sample));
    }

    [Fact]
    public void Evaluate_DisksAndStallUnreadable_IsUnknownWithTheReason()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Disks = DiskSetReading.Unreadable("no /proc/diskstats"),
            Stall = StallReading.Unreadable("no /proc/pressure"),
        });

        var disk = Verdict(history, ResourceKind.DiskActivity);

        Assert.Equal(ResourceLevel.Unknown, disk.Level);
        Assert.Contains("no /proc/diskstats", disk.Evidence);
    }

    [Fact]
    public void Evaluate_EveryDiskUnreadable_IsUnknown()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Disks = DiskSetReading.Read(new[] { PhysicalDiskReading.Unreadable("disk0", DiskKind.SolidState, "IOKit refused") }),
            Stall = StallReading.Read(StallSource.MacOsSignals, runQueueLength: 2),
        });

        var disk = Verdict(history, ResourceKind.DiskActivity);

        Assert.Equal(ResourceLevel.Unknown, disk.Level);
        Assert.Contains("IOKit refused", disk.Evidence);
    }

    [Fact]
    public void BusiestDisk_SkipsUnreadableDisks()
    {
        var sample = new ResourceSampleBuilder
        {
            Disks = DiskSetReading.Read(new[]
            {
                PhysicalDiskReading.Unreadable("sdz", DiskKind.Unknown, "gone"),
                PhysicalDiskReading.Read("sda", DiskKind.SolidState, 12, 0, 0, null, null, null),
            }),
        }.Build();

        Assert.Equal("sda", ResourcePressure.BusiestDisk(sample)?.Name);
    }

    [Fact]
    public void Evaluate_DevlinuxFixture_DiskActivityIsNormalBecauseItIsUnderEightyPercent()
    {
        // The slowness was real but the meter's limit is 80%: sdc at 75% and stalls at 32% are under it.
        // The diagnosis, not the meter, is what names this machine's trouble.
        var history = Every5Seconds(TimeSpan.FromSeconds(90), Devlinux);

        Assert.Equal(ResourceLevel.Normal, Verdict(history, ResourceKind.DiskActivity).Level);
    }

    // -------------------------------------------------------------------------------------------
    // Memory: the existing MemoryPressure levels, unchanged, on the newest sample
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(26.0, ResourceLevel.Normal)]
    [InlineData(1.5, ResourceLevel.Tight)]
    [InlineData(0.5, ResourceLevel.Critical)]
    public void Evaluate_Memory_UsesTheExistingLevelsOnTheNewestSample(double availableGigabytes, ResourceLevel expected)
    {
        var history = new[]
        {
            new ResourceSampleBuilder { At = TimeSpan.Zero }.Build(),
            (new ResourceSampleBuilder { At = TimeSpan.FromSeconds(5) } with
            {
                Memory = MachineMemoryReading.Read(39 * Gigabyte, (ulong)(availableGigabytes * Gigabyte), Start.AddSeconds(5)),
            }).Build(),
        };

        Assert.Equal(expected, Verdict(history, ResourceKind.Memory).Level);
    }

    [Fact]
    public void Evaluate_MemoryUnreadable_IsUnknownWithTheReason()
    {
        var history = new[] { new ResourceSampleBuilder { Memory = MachineMemoryReading.Unreadable("no /proc/meminfo", Start) }.Build() };

        var memory = Verdict(history, ResourceKind.Memory);

        Assert.Equal(ResourceLevel.Unknown, memory.Level);
        Assert.Contains("no /proc/meminfo", memory.Evidence);
    }

    [Theory]
    [InlineData(MemoryPressureLevel.Unknown, ResourceLevel.Unknown)]
    [InlineData(MemoryPressureLevel.Normal, ResourceLevel.Normal)]
    [InlineData(MemoryPressureLevel.Tight, ResourceLevel.Tight)]
    [InlineData(MemoryPressureLevel.Critical, ResourceLevel.Critical)]
    public void FromMemoryLevel_MapsEveryLevel(MemoryPressureLevel memory, ResourceLevel expected)
    {
        Assert.Equal(expected, ResourcePressure.FromMemoryLevel(memory));
    }

    // -------------------------------------------------------------------------------------------
    // Swap: red on share used, amber on growth over five minutes
    // -------------------------------------------------------------------------------------------

    private static ResourceSampleBuilder SwapAt(double usedGigabytes, double totalGigabytes = 8) =>
        new() { Swap = SwapReading.Read((ulong)(totalGigabytes * Gigabyte), (ulong)(usedGigabytes * Gigabyte)) };

    [Theory]
    [InlineData(6.5, ResourceLevel.Critical)]  // 81%
    [InlineData(6.0, ResourceLevel.Normal)]    // exactly 75% is not over
    [InlineData(2.0, ResourceLevel.Normal)]
    public void Evaluate_SwapShareUsed_FoldsByTheLimit(double usedGigabytes, ResourceLevel expected)
    {
        var history = new[] { SwapAt(usedGigabytes).Build() };

        Assert.Equal(expected, Verdict(history, ResourceKind.Swap).Level);
    }

    [Fact]
    public void Evaluate_SwapGrewMoreThanAGigabyteInFiveMinutes_IsTight()
    {
        var history = Every5Seconds(TimeSpan.FromMinutes(4), offset => SwapAt(1.0 + offset.TotalSeconds / 240.0 * 1.5));

        var swap = Verdict(history, ResourceKind.Swap);

        Assert.Equal(ResourceLevel.Tight, swap.Level);
        Assert.Contains("1.5 GB", swap.Evidence);
    }

    [Fact]
    public void Evaluate_SwapGrewExactlyAGigabyte_IsNormal()
    {
        var history = new[] { SwapAt(1.0).Build(), (SwapAt(2.0) with { At = TimeSpan.FromMinutes(4) }).Build() };

        Assert.Equal(ResourceLevel.Normal, Verdict(history, ResourceKind.Swap).Level);
    }

    [Fact]
    public void Evaluate_SwapGrowthOlderThanFiveMinutes_DoesNotCount()
    {
        var history = new[]
        {
            SwapAt(0.5).Build(),
            (SwapAt(3.0) with { At = TimeSpan.FromMinutes(1) }).Build(),
            (SwapAt(3.2) with { At = TimeSpan.FromMinutes(7) }).Build(),
        };

        Assert.Equal(ResourceLevel.Normal, Verdict(history, ResourceKind.Swap).Level);
    }

    [Fact]
    public void Evaluate_SwapShrank_IsNormal()
    {
        var history = new[] { SwapAt(4.0).Build(), (SwapAt(1.0) with { At = TimeSpan.FromMinutes(2) }).Build() };

        Assert.Equal(ResourceLevel.Normal, Verdict(history, ResourceKind.Swap).Level);
    }

    [Fact]
    public void Evaluate_NoSwapConfigured_IsNormalNotUnknown()
    {
        var history = new[] { new ResourceSampleBuilder { Swap = SwapReading.Read(0, 0) }.Build() };

        var swap = Verdict(history, ResourceKind.Swap);

        Assert.Equal(ResourceLevel.Normal, swap.Level);
        Assert.Contains("no swap", swap.Evidence);
    }

    [Fact]
    public void Evaluate_SwapUnreadable_IsUnknown()
    {
        var history = new[] { new ResourceSampleBuilder { Swap = SwapReading.Unreadable("no /proc/swaps") }.Build() };

        Assert.Equal(ResourceLevel.Unknown, Verdict(history, ResourceKind.Swap).Level);
    }

    [Fact]
    public void Evaluate_SwapUsedAboveTotal_IsUnknown()
    {
        var history = new[] { new ResourceSampleBuilder { Swap = SwapReading.Read(1 * Gigabyte, 2 * Gigabyte) }.Build() };

        Assert.Equal(ResourceLevel.Unknown, Verdict(history, ResourceKind.Swap).Level);
    }

    // -------------------------------------------------------------------------------------------
    // Disk free: per volume, the worst wins
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1000.0, 400.0, ResourceLevel.Normal)]
    [InlineData(1000.0, 140.0, ResourceLevel.Tight)]    // 14% free
    [InlineData(1000.0, 40.0, ResourceLevel.Critical)]  // 4% free
    [InlineData(100.0, 19.0, ResourceLevel.Tight)]      // 19% free but under 20 GB
    [InlineData(100.0, 4.0, ResourceLevel.Critical)]    // under 5 GB
    [InlineData(10_000.0, 1_000.0, ResourceLevel.Tight)] // 1 TB free but only 10%
    [InlineData(100.0, 20.0, ResourceLevel.Normal)]     // exactly 20 GB and 20% is not below either
    public void VolumeLevel_FoldsByPercentOrGigabytes(double totalGigabytes, double freeGigabytes, ResourceLevel expected)
    {
        var volume = VolumeReading.Read("/", (ulong)(totalGigabytes * Gigabyte), (ulong)(freeGigabytes * Gigabyte), Array.Empty<string>());

        Assert.Equal(expected, ResourcePressure.VolumeLevel(volume));
    }

    [Fact]
    public void Evaluate_DiskFree_TheWorstVolumeWins()
    {
        var history = new[]
        {
            new ResourceSampleBuilder
            {
                Volumes = new[]
                {
                    VolumeReading.Read("/", 1000 * Gigabyte, 500 * Gigabyte, new[] { "sda" }),
                    VolumeReading.Read("/data", 2000 * Gigabyte, 50 * Gigabyte, new[] { "sdc" }),
                    VolumeReading.Read("/home", 500 * Gigabyte, 60 * Gigabyte, new[] { "sdb" }),
                },
            }.Build(),
        };

        var free = Verdict(history, ResourceKind.DiskFree);

        Assert.Equal(ResourceLevel.Critical, free.Level);
        Assert.Contains("/data", free.Evidence);
        Assert.Contains("50.0 GB", free.Evidence);
    }

    [Fact]
    public void Evaluate_NoVolumes_IsUnknown()
    {
        var history = new[] { new ResourceSampleBuilder { Volumes = Array.Empty<VolumeReading>() }.Build() };

        Assert.Equal(ResourceLevel.Unknown, Verdict(history, ResourceKind.DiskFree).Level);
    }

    [Fact]
    public void Evaluate_EveryVolumeUnreadable_IsUnknownWithTheReason()
    {
        var history = new[] { new ResourceSampleBuilder { Volumes = new[] { VolumeReading.Unreadable("/", "statvfs failed") } }.Build() };

        var free = Verdict(history, ResourceKind.DiskFree);

        Assert.Equal(ResourceLevel.Unknown, free.Level);
        Assert.Contains("statvfs failed", free.Evidence);
    }

    [Fact]
    public void Evaluate_OneVolumeUnreadable_TheReadableOnesDecideAndTheEvidenceSaysWhichCouldNotBeRead()
    {
        var history = new[]
        {
            new ResourceSampleBuilder
            {
                Volumes = new[]
                {
                    VolumeReading.Unreadable("/mnt/network", "timed out"),
                    VolumeReading.Read("/", 1000 * Gigabyte, 100 * Gigabyte, new[] { "sda" }),
                },
            }.Build(),
        };

        var free = Verdict(history, ResourceKind.DiskFree);

        Assert.Equal(ResourceLevel.Tight, free.Level);
        Assert.Contains("/mnt/network", free.Evidence);
    }

    [Fact]
    public void VolumeLevel_ZeroSizeOrFreeAboveSize_IsUnknown()
    {
        Assert.Equal(ResourceLevel.Unknown, ResourcePressure.VolumeLevel(VolumeReading.Read("/", 0, 0, Array.Empty<string>())));
        Assert.Equal(ResourceLevel.Unknown, ResourcePressure.VolumeLevel(VolumeReading.Read("/", 10, 20, Array.Empty<string>())));
    }

    // -------------------------------------------------------------------------------------------
    // Worst across resources
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_RedProcessorAndUnreadableDisk_WorstIsRed()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => CpuAt(99) with
        {
            Disks = DiskSetReading.Unreadable("no /proc/diskstats"),
            Stall = StallReading.Unreadable("no /proc/pressure"),
        });

        var verdict = ResourcePressure.Evaluate(history);

        Assert.Equal(ResourceLevel.Critical, verdict.Worst);
        Assert.Equal(ResourceLevel.Unknown, verdict.Resources.Single(resource => resource.Kind == ResourceKind.DiskActivity).Level);
    }
}
