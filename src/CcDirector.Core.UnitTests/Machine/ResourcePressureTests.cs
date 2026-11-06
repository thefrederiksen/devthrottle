using CcDirector.Core.Machine;
using Xunit;
using static CcDirector.Core.Tests.Machine.ResourceSampleBuilder;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The fold from a history of resource samples to one level per resource - the processor, memory and
/// swap (mission sections 5a and 5c).
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

    // -------------------------------------------------------------------------------------------
    // The shape of the verdict
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_AnyHistory_GivesOneVerdictPerResourceInOrder()
    {
        var verdict = ResourcePressure.Evaluate(new[] { new ResourceSampleBuilder().Build() });

        // Three resources and no disk: the disk is not a meter (mission section 5b).
        Assert.Equal(
            new[] { ResourceKind.Cpu, ResourceKind.Memory, ResourceKind.Swap },
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
    // Worst across resources
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_RedProcessorAndUnreadableSwap_WorstIsRed()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => CpuAt(99) with { Swap = SwapReading.Unreadable("no swap figures") });

        var verdict = ResourcePressure.Evaluate(history);

        Assert.Equal(ResourceLevel.Critical, verdict.Worst);
        Assert.Equal(ResourceLevel.Unknown, verdict.Resources.Single(resource => resource.Kind == ResourceKind.Swap).Level);
    }

    [Fact]
    public void Evaluate_EveryResourceUnreadable_IsUnknownNeverNormal()
    {
        var history = new[]
        {
            new ResourceSampleBuilder
            {
                Cpu = CpuReading.Unreadable("no processor figures"),
                Memory = MachineMemoryReading.Unreadable("no memory figures", Start),
                Swap = SwapReading.Unreadable("no swap figures"),
            }.Build(),
        };

        var verdict = ResourcePressure.Evaluate(history);

        Assert.All(verdict.Resources, resource => Assert.Equal(ResourceLevel.Unknown, resource.Level));
        Assert.Equal(ResourceLevel.Unknown, verdict.Worst);
    }
}
