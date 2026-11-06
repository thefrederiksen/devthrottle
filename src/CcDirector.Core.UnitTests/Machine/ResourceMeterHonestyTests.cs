using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.UnitTests.Machine;

/// <summary>
/// The meter never turns an impossible or unknown figure into a plausible one. Each test here is a case where
/// a tidy-looking answer would have been wrong: a clamped bar, a blank core count, an age from a stepped clock.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceMeterHonestyTests
{
    private static ResourceSamplerState State(ResourceSample sample, ResourceLevel cpu = ResourceLevel.Normal) =>
        new(sample, new ResourcePressureVerdict([
            new ResourceVerdict(ResourceKind.Cpu, cpu, "cpu"),
            new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Normal, "memory"),
            new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal, "swap"),
        ]));

    private static ResourceMeterView Meter(ResourceSamplerState state, TimeSpan? age) =>
        ResourceMeterFold.Build(state, "soren-north", 2, null, age);

    [Theory]
    [InlineData(400.0)]
    [InlineData(100.5)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    public void CpuBar_AFigureThatCannotBeTrue_ShowsADashAndSaysWhy_NeverAFullBar(double impossible)
    {
        var sample = new ResourceSampleBuilder().Build() with { Cpu = CpuReading.Read(impossible, null, 8) };

        var bar = Meter(State(sample, cpu: ResourceLevel.Critical), TimeSpan.Zero).Cpu;

        Assert.False(bar.CouldRead);
        Assert.Null(bar.Fraction);
        Assert.Equal(ResourceViewText.CannotReadText, bar.PercentText);
        Assert.Equal(ResourceLevel.Unknown, bar.Level);
        Assert.Contains("cannot be true", bar.Tooltip);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(100.0, 1.0)]
    [InlineData(28.0, 0.28)]
    public void CpuFraction_AFigureInRange_IsPassedThroughUnchanged(double percent, double expected)
    {
        Assert.Equal(expected, ResourceViewText.CpuFraction(CpuReading.Read(percent, null, 8))!.Value, 10);
    }

    [Fact]
    public void CpuTooltip_WhenTheCoreCountIsNotKnown_LeavesItOut_RatherThanPrintingABlank()
    {
        var sample = new ResourceSampleBuilder().Build() with { Cpu = CpuReading.Read(28, 71, logicalCoreCount: null) };

        var tooltip = Meter(State(sample), TimeSpan.Zero).Cpu.Tooltip;

        Assert.Equal("CPU 28% - busiest core 71%", tooltip);
    }

    [Fact]
    public void CpuTooltip_WhenTheCoreCountIsKnown_SaysIt()
    {
        var sample = new ResourceSampleBuilder().Build() with { Cpu = CpuReading.Read(28, 71, 8) };

        Assert.Equal("CPU 28% of 8 cores - busiest core 71%", Meter(State(sample), TimeSpan.Zero).Cpu.Tooltip);
    }

    [Fact]
    public void Updated_ReadsTheAgeItIsHanded_NotTheSamplesWallClockStamp()
    {
        // The stamp is forty minutes old, as a stepped clock leaves it; the monotonic age says three seconds.
        var sample = new ResourceSampleBuilder().At(ResourceSampleBuilder.DefaultTakenAtUtc.AddMinutes(-40)).Build();

        var lines = Meter(State(sample), TimeSpan.FromSeconds(3)).TooltipLines;

        Assert.Equal("Updated 3 seconds ago - click for detail", lines[^1]);
    }
}
