using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE CLOCK RULE AND THE RATE RULE (the Resource Monitor mission, issue #2811).
///
/// Wall-clock time STAMPS a reading; only a monotonic clock MEASURES an interval. Time synchronisation
/// steps the wall clock, and "the clock jumps" is a failure case this mission has to prove, so the rule
/// is pinned here rather than left to a doc comment.
///
/// Nothing here sleeps, spins or reads a real clock: the clock is injected, and it is one that can step
/// its wall-clock reading BACKWARDS while its monotonic reading keeps moving forward. A test double is
/// the only way to arrange that, and without arranging it there is no way to tell the two clocks apart.
/// </summary>
[Trait("Category", "Resources")]
public class ResourceClockAndRateTests
{
    /// <summary>
    /// A clock whose two readings move INDEPENDENTLY, which is the whole point: the library's fake clock
    /// derives both from one internal time, so under it a backwards wall-clock step also steps the
    /// monotonic reading and the defect being tested for cannot be reproduced.
    /// </summary>
    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _wall;
        private long _timestamp;

        public SteppingClock(DateTimeOffset wall) => _wall = wall;

        public override DateTimeOffset GetUtcNow() => _wall;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>What time synchronisation does. May be negative.</summary>
        public void StepWallClock(TimeSpan by) => _wall = _wall.Add(by);

        /// <summary>What a monotonic clock does. Only ever forward.</summary>
        public void AdvanceMonotonic(TimeSpan by) => _timestamp += by.Ticks;
    }

    private static readonly DateTimeOffset Noon = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------------------------
    // The memory probe's cache, which the resource probes copy
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void TheCache_IsAgedOnTheMonotonicClock_SoABackwardsWallClockStepDoesNotFreezeIt()
    {
        // This is the defect in full. The cache used to compare two wall-clock stamps, so a step
        // backwards of more than a second made every reading look as though it came from the future -
        // and the Director went on answering memory questions from one stale reading until the wall clock
        // caught up, while the machine filled.
        var clock = new SteppingClock(Noon);
        var probe = new MachineMemoryProbe(clock);

        var first = probe.Read();
        Assert.Equal(Noon.UtcDateTime, first.TakenAtUtc);

        // Time synchronisation steps the wall clock an hour backwards. No monotonic time has passed, so
        // the cache is still fresh and must be served.
        clock.StepWallClock(TimeSpan.FromHours(-1));
        var duringTheStep = probe.Read();
        Assert.Equal(Noon.UtcDateTime, duringTheStep.TakenAtUtc);

        // Now real time passes. The cache has expired and must be read again - against the OLD code this
        // is where it stayed frozen, because the stepped-back stamp still looked newer than the cache.
        clock.AdvanceMonotonic(MachineMemoryProbe.CacheFor + TimeSpan.FromSeconds(1));
        var afterTheCacheExpired = probe.Read();

        Assert.Equal(Noon.UtcDateTime.AddHours(-1), afterTheCacheExpired.TakenAtUtc);
    }

    [Fact]
    public void TheCache_IsServedInsideItsWindow_AndReadAgainAfterIt()
    {
        var clock = new SteppingClock(Noon);
        var probe = new MachineMemoryProbe(clock);

        var first = probe.Read();

        clock.AdvanceMonotonic(MachineMemoryProbe.CacheFor / 2);
        clock.StepWallClock(MachineMemoryProbe.CacheFor / 2);
        Assert.Equal(first.TakenAtUtc, probe.Read().TakenAtUtc);

        clock.AdvanceMonotonic(MachineMemoryProbe.CacheFor);
        clock.StepWallClock(MachineMemoryProbe.CacheFor);
        Assert.NotEqual(first.TakenAtUtc, probe.Read().TakenAtUtc);
    }

    [Fact]
    public void TheReadingIsStampedWithWallClockTime_BecauseThatIsWhatAStampIsFor()
    {
        // The monotonic reading is meaningless on its own, so it can never be the stamp. A caller asking
        // "when was this taken" needs a time it can print.
        var clock = new SteppingClock(Noon);

        Assert.Equal(Noon.UtcDateTime, new MachineMemoryProbe(clock).Read().TakenAtUtc);
    }

    // -------------------------------------------------------------------------------------------
    // Rates
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void PerSecond_DividesTheAdvanceByTheInterval()
    {
        Assert.Equal(500, ResourceRate.PerSecond(1000, 2000, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void PerSecond_ACounterThatDidNotMove_IsZero_WhichIsARealMeasurement()
    {
        // Zero is only ever allowed when it was MEASURED. An idle disk really did nothing.
        Assert.Equal(0, ResourceRate.PerSecond(1000, 1000, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void PerSecond_ACounterThatSteppedBackwards_IsNull_NeverZero()
    {
        // A reset or a wrap: the disk was swapped, the interface was reconfigured, the process id was
        // reused. Reporting zero would show an idle machine that may be on fire.
        Assert.Null(ResourceRate.PerSecond(2000, 1000, TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PerSecond_WithNoInterval_IsNull_BecauseThereIsNothingToDivideBy(int seconds)
    {
        Assert.Null(ResourceRate.PerSecond(1000, 2000, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void BusyPercent_IsTheShareOfTheIntervalTheResourceWasWorking()
    {
        var busy = ResourceRate.BusyPercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));

        Assert.Equal(25, busy);
    }

    [Fact]
    public void BusyPercent_SlightlyPastTheWholeInterval_IsRoundedDownToOneHundred()
    {
        // A counter sampled microseconds out of step with the clock can show marginally more busy time
        // than elapsed time. The honest reading of "busier than the whole interval" is "flat out".
        var busy = ResourceRate.BusyPercent(TimeSpan.Zero, TimeSpan.FromSeconds(5.001), TimeSpan.FromSeconds(5));

        Assert.Equal(100, busy);
    }

    [Fact]
    public void BusyPercent_FarPastTheWholeInterval_IsNull_BecauseClampingWouldHideTheDefect()
    {
        // Four cores' worth of busy time over a one-core interval reads 400%. That is not sampling skew,
        // it is the arithmetic being wrong - usually a per-core counter over a whole-machine interval.
        // Clamping it to 100 would make it pass every range check there is, which is exactly how a
        // per-core convention bug would reach a session total undetected.
        Assert.Null(ResourceRate.BusyPercent(TimeSpan.Zero, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void BusyPercent_TheOvershootAllowanceIsSmall_NotEnoughToSwallowAPerCoreFigure()
    {
        // Pinned so the allowance cannot quietly grow into a clamp again. Two points absorbs skew; a
        // per-core figure on any multi-core machine is a multiple, not a margin.
        Assert.True(ResourceRate.OvershootAllowed > 0);
        Assert.True(ResourceRate.OvershootAllowed < 50);
    }

    // -------------------------------------------------------------------------------------------
    // The one division by core count - the rule the whole shared contract exists for
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ShareOfMachine_OneCoreOfTen_ReadsTen_NotOneHundred()
    {
        // THE rule. A process holding one core flat out for the whole interval on a ten-core machine is
        // using a tenth of the machine. Three probes writing this division by hand is how one of them
        // ends up answering 100, and nothing downstream could then tell.
        var share = ResourceRate.ShareOfMachine(
            TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), logicalCoreCount: 10);

        Assert.Equal(10, share);
    }

    [Fact]
    public void ShareOfMachine_EveryCoreFlatOut_ReadsOneHundred()
    {
        var share = ResourceRate.ShareOfMachine(
            TimeSpan.Zero, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(5), logicalCoreCount: 10);

        Assert.Equal(100, share);
    }

    [Fact]
    public void ShareOfMachine_OnASingleCoreMachine_OneCoreIsTheWholeMachine()
    {
        var share = ResourceRate.ShareOfMachine(
            TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), logicalCoreCount: 1);

        Assert.Equal(100, share);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-4)]
    public void ShareOfMachine_WithNoUsableCoreCount_IsNull_NotAPerCoreFigure(int? cores)
    {
        // Without the core count the figure cannot be expressed as a share of the machine at all, and
        // the per-core figure it would otherwise return is precisely the wrong answer. So it says nothing.
        Assert.Null(ResourceRate.ShareOfMachine(
            TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), cores));
    }

    [Fact]
    public void ShareOfMachine_AProcessorTimeCounterThatSteppedBackwards_IsNull()
    {
        // A reused process id is the usual cause: the counter belongs to a different process now.
        Assert.Null(ResourceRate.ShareOfMachine(
            TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), logicalCoreCount: 10));
    }

    [Fact]
    public void ShareOfMachine_WithNoInterval_IsNull()
    {
        Assert.Null(ResourceRate.ShareOfMachine(
            TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero, logicalCoreCount: 10));
    }

    [Fact]
    public void ShareOfMachine_FarPastTheWholeMachine_IsNull()
    {
        // Twice the machine's whole capacity. Impossible, so it is not reported as a figure.
        Assert.Null(ResourceRate.ShareOfMachine(
            TimeSpan.Zero, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(5), logicalCoreCount: 10));
    }

    [Fact]
    public void BusyPercent_ABusyCounterThatSteppedBackwards_IsNull()
    {
        Assert.Null(ResourceRate.BusyPercent(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void BusyPercent_WithNoInterval_IsNull()
    {
        Assert.Null(ResourceRate.BusyPercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero));
    }

    // -------------------------------------------------------------------------------------------
    // The macOS kernel's memory-pressure number, mapped in ONE place
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, KernelMemoryPressureLevel.Normal)]
    [InlineData(2, KernelMemoryPressureLevel.Warning)]
    [InlineData(4, KernelMemoryPressureLevel.Critical)]
    public void KernelPressure_TheThreeNumbersTheKernelUses_AreRead(int kernelLevel, KernelMemoryPressureLevel expected)
    {
        Assert.Equal(expected, MacOsKernelPressure.Read(kernelLevel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(null)]
    public void KernelPressure_ANumberWeDoNotKnow_IsUnknown_AndNeverFoldedIntoNormal(int? kernelLevel)
    {
        // Reporting an unknown number as Normal would tell the owner his machine is fine on the strength
        // of a number nobody has read. Three is the gap in the kernel's bit flag; null is "not offered".
        Assert.Equal(KernelMemoryPressureLevel.Unknown, MacOsKernelPressure.Read(kernelLevel));
    }

    [Fact]
    public void KernelPressure_UnknownIsZero_AndBelowNormal()
    {
        Assert.Equal(KernelMemoryPressureLevel.Unknown, default(KernelMemoryPressureLevel));
        Assert.True(KernelMemoryPressureLevel.Unknown < KernelMemoryPressureLevel.Normal);
        Assert.True(KernelMemoryPressureLevel.Normal < KernelMemoryPressureLevel.Warning);
        Assert.True(KernelMemoryPressureLevel.Warning < KernelMemoryPressureLevel.Critical);
    }

    [Fact]
    public void StallReading_ReadsTheKernelLevelThroughTheOneMappingFunction()
    {
        // The probe, its tests and the Gateway fold all read this property. None of them maps the number
        // itself, because a mapping written twice drifts - and on macOS this is one of only two stall
        // signals the platform publishes.
        var warning = StallReading.Read(StallSource.MacOsSignals, macOsMemoryPressureLevel: 2);

        Assert.Equal(2, warning.MacOsMemoryPressureLevel);
        Assert.Equal(KernelMemoryPressureLevel.Warning, warning.MacOsMemoryPressure);
    }

    [Fact]
    public void StallReading_OnAPlatformThatOffersNoKernelLevel_IsUnknown_NotNormal()
    {
        var linux = StallReading.Read(StallSource.LinuxPressureStall, ioStalledPercent: 32);

        Assert.Null(linux.MacOsMemoryPressureLevel);
        Assert.Equal(KernelMemoryPressureLevel.Unknown, linux.MacOsMemoryPressure);
    }

    [Fact]
    public void StallReading_Unreadable_HasNoKernelLevel_AndIsUnknown()
    {
        var unreadable = StallReading.Unreadable("the kernel offered no pressure figures");

        Assert.Equal(KernelMemoryPressureLevel.Unknown, unreadable.MacOsMemoryPressure);
    }
}
