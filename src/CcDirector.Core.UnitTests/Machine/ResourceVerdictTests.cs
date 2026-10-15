using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The shapes of the verdicts the Resource Monitor renders (issue #2811). The logic that produces them
/// comes later; what is pinned here is the one rule the shapes themselves own: an unreadable resource
/// never hides a real warning, and an undiagnosable machine is never reported as a healthy one.
/// </summary>
[Trait("Category", "Resources")]
public class ResourceVerdictTests
{
    private static ResourceVerdict Verdict(ResourceKind kind, ResourceLevel level) => new(kind, level, "evidence");

    [Fact]
    public void Worst_NoResources_IsUnknown()
    {
        Assert.Equal(ResourceLevel.Unknown, new ResourcePressureVerdict([]).Worst);
    }

    [Fact]
    public void Worst_EveryResourceUnknown_IsUnknown()
    {
        var verdict = new ResourcePressureVerdict(
        [
            Verdict(ResourceKind.Cpu, ResourceLevel.Unknown),
            Verdict(ResourceKind.Memory, ResourceLevel.Unknown),
        ]);

        Assert.Equal(ResourceLevel.Unknown, verdict.Worst);
    }

    [Fact]
    public void Worst_UnknownBesideNormal_IsNormal_UnknownNeverBeatsARealLevel()
    {
        var verdict = new ResourcePressureVerdict(
        [
            Verdict(ResourceKind.Cpu, ResourceLevel.Normal),
            Verdict(ResourceKind.DiskActivity, ResourceLevel.Unknown),
        ]);

        Assert.Equal(ResourceLevel.Normal, verdict.Worst);
    }

    [Fact]
    public void Worst_UnknownBesideCritical_IsCritical_AnUnreadableDiskDoesNotHideARedProcessor()
    {
        var verdict = new ResourcePressureVerdict(
        [
            Verdict(ResourceKind.DiskActivity, ResourceLevel.Unknown),
            Verdict(ResourceKind.Cpu, ResourceLevel.Critical),
            Verdict(ResourceKind.Memory, ResourceLevel.Normal),
        ]);

        Assert.Equal(ResourceLevel.Critical, verdict.Worst);
    }

    [Theory]
    [InlineData(ResourceLevel.Normal, ResourceLevel.Tight, ResourceLevel.Tight)]
    [InlineData(ResourceLevel.Tight, ResourceLevel.Normal, ResourceLevel.Tight)]
    [InlineData(ResourceLevel.Tight, ResourceLevel.Critical, ResourceLevel.Critical)]
    [InlineData(ResourceLevel.Critical, ResourceLevel.Tight, ResourceLevel.Critical)]
    [InlineData(ResourceLevel.Normal, ResourceLevel.Normal, ResourceLevel.Normal)]
    public void Worst_IsTheHighestRealLevel(ResourceLevel first, ResourceLevel second, ResourceLevel expected)
    {
        var verdict = new ResourcePressureVerdict(
        [
            Verdict(ResourceKind.Cpu, first),
            Verdict(ResourceKind.Swap, second),
        ]);

        Assert.Equal(expected, verdict.Worst);
    }

    [Fact]
    public void ResourceLevel_UnknownIsZero_AndBelowNormal()
    {
        // Worst relies on this order; pinning it means a reordering fails here rather than quietly
        // letting Unknown outrank a real warning.
        Assert.Equal(ResourceLevel.Unknown, default(ResourceLevel));
        Assert.True(ResourceLevel.Unknown < ResourceLevel.Normal);
        Assert.True(ResourceLevel.Normal < ResourceLevel.Tight);
        Assert.True(ResourceLevel.Tight < ResourceLevel.Critical);
    }

    [Fact]
    public void SlownessCause_Flags_Combine()
    {
        var cause = SlownessCause.Memory | SlownessCause.Disk;

        Assert.True(cause.HasFlag(SlownessCause.Disk));
        Assert.True(cause.HasFlag(SlownessCause.Memory));
        Assert.False(cause.HasFlag(SlownessCause.Cpu));
    }

    [Fact]
    public void CannotDiagnose_IsNotSlow_ButSaysItCouldNotTell()
    {
        var diagnosis = SlownessDiagnosis.CannotDiagnose("the stall figures could not be read");

        Assert.False(diagnosis.CouldDiagnose);
        Assert.False(diagnosis.IsSlow);
        Assert.Equal(SlownessCause.None, diagnosis.Cause);
        Assert.Empty(diagnosis.Evidence);
        Assert.Null(diagnosis.Culprit);
        Assert.Contains("the stall figures could not be read", diagnosis.Summary);
        Assert.Contains("Cannot tell", diagnosis.Summary);
        Assert.Equal(diagnosis.Summary, diagnosis.ToString());
    }
}
