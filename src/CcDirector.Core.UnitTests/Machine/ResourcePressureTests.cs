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
        ResourcePressure.Evaluate(Timed(history)).Resources.Single(verdict => verdict.Kind == kind);

    // Each fixture sample sits on the monotonic timeline at the offset the test built it at. Fixture
    // construction only: the fold itself never subtracts two stamps.
    private static TimelineSample[] Timed(IEnumerable<ResourceSample> history) =>
        history.Select(sample => new TimelineSample(sample, sample.TakenAtUtc - Start)).ToArray();

    [Fact]
    public void Evaluate_TheWallClockStepsForward_ThreeSamplesTenSecondsApartOnTheTimeline_AreNotHeldForAMinute()
    {
        // The wall clock jumped an hour forward between the first and second sample - a time correction, or a
        // machine waking from sleep. On the timeline the three readings span ten seconds, nothing like a minute.
        var first = (CpuAt(99) with { At = TimeSpan.Zero }).Build();
        var second = (CpuAt(99) with { At = TimeSpan.FromHours(1) }).Build();
        var third = (CpuAt(99) with { At = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5) }).Build();

        var verdict = ResourcePressure.Evaluate(
        [
            new TimelineSample(first, TimeSpan.Zero),
            new TimelineSample(second, TimeSpan.FromSeconds(5)),
            new TimelineSample(third, TimeSpan.FromSeconds(10)),
        ]).Resources.Single(v => v.Kind == ResourceKind.Cpu);

        Assert.Equal(ResourceLevel.Normal, verdict.Level);
        Assert.Contains("10 seconds of readings so far", verdict.Evidence);
    }

    [Fact]
    public void Evaluate_TheWallClockStepsBack_AMinuteOnTheTimelineAtFullLoad_IsStillRed()
    {
        // Thirteen readings five seconds apart on the timeline, all pinned; the wall clock stepped back an hour
        // half way through, so the stamps are out of order. The timeline still says a full minute.
        var history = Enumerable.Range(0, 13).Select(i =>
        {
            var stamp = TimeSpan.FromSeconds(i * 5) - (i >= 6 ? TimeSpan.FromHours(1) : TimeSpan.Zero);
            return new TimelineSample((CpuAt(99) with { At = stamp }).Build(), TimeSpan.FromSeconds(i * 5));
        }).ToArray();

        var verdict = ResourcePressure.Evaluate(history).Resources.Single(v => v.Kind == ResourceKind.Cpu);

        Assert.Equal(ResourceLevel.Critical, verdict.Level);
    }

    private static ResourceSampleBuilder CpuAt(double busy) =>
        new() { Cpu = CpuReading.Read(busy, busiestCorePercent: Math.Min(100, busy + 5), logicalCoreCount: 32) };

    // -------------------------------------------------------------------------------------------
    // The shape of the verdict
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_AnyHistory_GivesOneVerdictPerResourceInOrder()
    {
        var verdict = ResourcePressure.Evaluate(Timed(new[] { new ResourceSampleBuilder().Build() }));

        // Three resources and no disk: the disk is not a meter (mission section 5b).
        Assert.Equal(
            new[] { ResourceKind.Cpu, ResourceKind.Memory, ResourceKind.Swap },
            verdict.Resources.Select(resource => resource.Kind));
    }

    [Fact]
    public void Evaluate_EmptyHistory_IsUnknownForEveryResource()
    {
        var verdict = ResourcePressure.Evaluate(Timed(Array.Empty<ResourceSample>()));

        Assert.All(verdict.Resources, resource => Assert.Equal(ResourceLevel.Unknown, resource.Level));
        Assert.Equal(ResourceLevel.Unknown, verdict.Worst);
    }

    [Fact]
    public void Evaluate_QuietMachineForAMinute_IsNormalEverywhere()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder());

        var verdict = ResourcePressure.Evaluate(Timed(history));

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
    // Memory: the existing MemoryPressure levels, unchanged
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

    // Memory is held ASYMMETRICALLY: raised on the newest reading, cleared only after sixty seconds
    // continuously below the line (the Implementation Lead's ruling of 2026-11-06).

    private static ResourceSample MemoryAt(TimeSpan at, double availableGigabytes) =>
        (new ResourceSampleBuilder { At = at } with
        {
            Memory = MachineMemoryReading.Read(39 * Gigabyte, (ulong)(availableGigabytes * Gigabyte), Start + at),
        }).Build();

    [Fact]
    public void Evaluate_MemoryCrossesAndDropsStraightBack_StaysUpForTheMinute_ThenClears()
    {
        // One tight reading at five seconds, then plenty of memory every five seconds after it.
        var history = new List<ResourceSample> { MemoryAt(TimeSpan.Zero, 26), MemoryAt(TimeSpan.FromSeconds(5), 1.5) };
        for (var seconds = 10; seconds <= 65; seconds += 5) history.Add(MemoryAt(TimeSpan.FromSeconds(seconds), 26));

        // Sixty seconds after the tight reading it is still inside the window: held.
        var held = Verdict(history, ResourceKind.Memory);
        Assert.Equal(ResourceLevel.Tight, held.Level);
        Assert.Contains("clears only after 60 seconds continuously below the line", held.Evidence);

        // Five seconds later the tight reading has left the window: cleared.
        history.Add(MemoryAt(TimeSpan.FromSeconds(70), 26));
        Assert.Equal(ResourceLevel.Normal, Verdict(history, ResourceKind.Memory).Level);
    }

    [Fact]
    public void Evaluate_MemoryRaisesOnTheFirstReading_WithNoWindow()
    {
        var history = new[] { MemoryAt(TimeSpan.Zero, 26), MemoryAt(TimeSpan.FromSeconds(5), 0.5) };

        Assert.Equal(ResourceLevel.Critical, Verdict(history, ResourceKind.Memory).Level);
    }

    [Fact]
    public void Evaluate_MemoryOscillatingAcrossTheLineEveryFiveSeconds_RaisesExactlyOnce()
    {
        // Ten minutes of a machine sitting on the Tight line, alternating each five-second sample.
        var detector = new ResourceCrossingDetector();
        var history = new List<ResourceSample>();
        var crossings = new List<ResourceCrossing>();
        for (var i = 0; i < 120; i++)
        {
            history.Add(MemoryAt(TimeSpan.FromSeconds(5 * i), i % 2 == 0 ? 26 : 1.5));
            crossings.AddRange(detector.Observe(ResourcePressure.Evaluate(Timed(history)), Start.AddSeconds(5 * i)));
        }

        var memory = crossings.Where(c => c.Kind == ResourceKind.Memory).ToList();
        var raised = Assert.Single(memory, c => c.IsNewWarning);
        Assert.Equal(ResourceLevel.Tight, raised.To);
        Assert.DoesNotContain(memory, c => c.EndsWarning);
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
    public void Evaluate_SwapInUseWithNoSwapConfigured_IsUnknownNotNormal()
    {
        var history = new[] { new ResourceSampleBuilder { Swap = SwapReading.Read(0, 500UL * 1024 * 1024) }.Build() };

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

        var verdict = ResourcePressure.Evaluate(Timed(history));

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

        var verdict = ResourcePressure.Evaluate(Timed(history));

        Assert.All(verdict.Resources, resource => Assert.Equal(ResourceLevel.Unknown, resource.Level));
        Assert.Equal(ResourceLevel.Unknown, verdict.Worst);
    }
}
