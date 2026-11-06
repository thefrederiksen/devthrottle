using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.UnitTests.Machine;

/// <summary>
/// When a resource has crossed a limit, and what that means for the banner (issue #2811, phase 2). Both
/// halves matter: a warning that fires and never clears is a defect, and so is an unreadable reading that
/// quietly clears one.
/// </summary>
[Trait("Category", "Resources")]
public class ResourceCrossingDetectorTests
{
    private static readonly DateTime At = FakeSampleBuilder.DefaultTakenAtUtc;

    private static ResourcePressureVerdict Cpu(ResourceLevel level) =>
        new([new ResourceVerdict(ResourceKind.Cpu, level, $"processor {level}")]);

    private static IReadOnlyList<ResourceCrossing> Run(params ResourceLevel[] levels)
    {
        var detector = new ResourceCrossingDetector();
        var all = new List<ResourceCrossing>();
        for (var i = 0; i < levels.Length; i++)
            all.AddRange(detector.Observe(Cpu(levels[i]), At.AddSeconds(5 * i)));
        return all;
    }

    [Fact]
    public void Observe_NormalToTight_IsRaised_AndShowsABanner()
    {
        var crossing = Assert.Single(Run(ResourceLevel.Normal, ResourceLevel.Tight));

        Assert.Equal(ResourceCrossingChange.Raised, crossing.Change);
        Assert.Equal(ResourceLevel.Normal, crossing.From);
        Assert.Equal(ResourceLevel.Tight, crossing.To);
        Assert.True(crossing.ShowsBanner);
        Assert.False(crossing.EndsWarning);
        Assert.Equal("processor Tight", crossing.Evidence);
        Assert.Equal(At.AddSeconds(5), crossing.AtUtc);
    }

    [Fact]
    public void Observe_NormalStraightToCritical_IsRaised()
    {
        var crossing = Assert.Single(Run(ResourceLevel.Normal, ResourceLevel.Critical));

        Assert.Equal(ResourceCrossingChange.Raised, crossing.Change);
        Assert.True(crossing.ShowsBanner);
    }

    [Fact]
    public void Observe_TightToCritical_IsEscalated_AndShowsABanner_BecauseItCrossedIntoRed()
    {
        var crossings = Run(ResourceLevel.Normal, ResourceLevel.Tight, ResourceLevel.Critical);

        Assert.Equal(2, crossings.Count);
        Assert.Equal(ResourceCrossingChange.Escalated, crossings[1].Change);
        Assert.True(crossings[1].ShowsBanner);
    }

    [Fact]
    public void Observe_CriticalToTight_IsEased_NoNewBanner_WarningStays()
    {
        var crossings = Run(ResourceLevel.Critical, ResourceLevel.Tight);

        Assert.Equal(ResourceCrossingChange.Eased, crossings[1].Change);
        Assert.False(crossings[1].ShowsBanner);
        Assert.False(crossings[1].EndsWarning);
    }

    [Fact]
    public void Observe_CriticalToNormal_IsCleared_AndEndsTheWarning()
    {
        var crossings = Run(ResourceLevel.Critical, ResourceLevel.Normal);

        Assert.Equal(ResourceCrossingChange.Cleared, crossings[1].Change);
        Assert.True(crossings[1].EndsWarning);
        Assert.False(crossings[1].ShowsBanner);
    }

    [Fact]
    public void Observe_SameLevelTwice_IsNothing()
    {
        var crossings = Run(ResourceLevel.Tight, ResourceLevel.Tight, ResourceLevel.Tight);

        Assert.Single(crossings);
    }

    [Fact]
    public void Observe_FirstReadingNormal_IsNothing_AHealthyMachineIsNotNews()
    {
        Assert.Empty(Run(ResourceLevel.Normal));
    }

    [Fact]
    public void Observe_FirstReadingTight_IsRaised_FromUnknown()
    {
        var crossing = Assert.Single(Run(ResourceLevel.Tight));

        Assert.Equal(ResourceLevel.Unknown, crossing.From);
        Assert.Equal(ResourceCrossingChange.Raised, crossing.Change);
    }

    [Fact]
    public void Observe_CriticalThenUnknown_DoesNotClear_BecauseUnknownIsNotNormal()
    {
        var crossings = Run(ResourceLevel.Critical, ResourceLevel.Unknown, ResourceLevel.Unknown);

        var only = Assert.Single(crossings);
        Assert.Equal(ResourceCrossingChange.Raised, only.Change);
    }

    [Fact]
    public void Observe_CriticalUnknownNormal_IsExactlyOneClear_FromTheLastKnownLevel()
    {
        var crossings = Run(ResourceLevel.Critical, ResourceLevel.Unknown, ResourceLevel.Normal);

        Assert.Equal(2, crossings.Count);
        Assert.Equal(ResourceCrossingChange.Cleared, crossings[1].Change);
        Assert.Equal(ResourceLevel.Critical, crossings[1].From);
    }

    [Fact]
    public void Observe_CriticalUnknownCritical_RaisesNoSecondBanner()
    {
        var crossings = Run(ResourceLevel.Critical, ResourceLevel.Unknown, ResourceLevel.Critical);

        Assert.Single(crossings);
    }

    [Fact]
    public void Observe_TwoResources_CrossIndependently()
    {
        var detector = new ResourceCrossingDetector();
        detector.Observe(new ResourcePressureVerdict(
        [
            new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Normal, "cpu"),
            new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Normal, "memory"),
        ]), At);

        var crossings = detector.Observe(new ResourcePressureVerdict(
        [
            new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Critical, "cpu"),
            new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Normal, "memory"),
        ]), At.AddSeconds(5));

        var crossing = Assert.Single(crossings);
        Assert.Equal(ResourceKind.Cpu, crossing.Kind);
    }

    [Fact]
    public void Observe_TightToNormal_IsCleared()
    {
        var crossings = Run(ResourceLevel.Tight, ResourceLevel.Normal);

        Assert.Equal(ResourceCrossingChange.Cleared, crossings[1].Change);
        Assert.True(crossings[1].EndsWarning);
    }

    [Fact]
    public void Observe_CriticalUnknownTight_IsOneEased_FromTheLastKnownLevel()
    {
        var crossings = Run(ResourceLevel.Critical, ResourceLevel.Unknown, ResourceLevel.Tight);

        Assert.Equal(2, crossings.Count);
        Assert.Equal(ResourceCrossingChange.Eased, crossings[1].Change);
        Assert.Equal(ResourceLevel.Critical, crossings[1].From);
    }

    [Fact]
    public void Observe_TwoResourcesCrossingInOneVerdict_ComeBackInTheVerdictsOrder()
    {
        var detector = new ResourceCrossingDetector();

        var crossings = detector.Observe(new ResourcePressureVerdict(
        [
            new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Tight, "memory"),
            new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Critical, "cpu"),
        ]), At);

        Assert.Equal([ResourceKind.Memory, ResourceKind.Cpu], crossings.Select(crossing => crossing.Kind));
    }

    [Fact]
    public void Observe_NullVerdict_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ResourceCrossingDetector().Observe(null!, At));
    }

    [Fact]
    public void NotYetSampled_EveryResourceIsUnknown_NeverNormal()
    {
        var state = ResourceSamplerState.NotYetSampled;

        Assert.Null(state.Latest);
        Assert.All(state.Verdict.Resources, verdict => Assert.Equal(ResourceLevel.Unknown, verdict.Level));
        Assert.Equal(ResourceLevel.Unknown, state.Verdict.Worst);
    }

    [Fact]
    public void Sampling_HistoryCapacity_IsOneHourOfFiveSecondSamples()
    {
        Assert.Equal(ResourceSampling.HistoryCapacity, (int)(ResourceSampling.HistoryLength / ResourceSampling.Interval));
    }
}
