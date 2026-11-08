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
            Verdict(ResourceKind.Swap, ResourceLevel.Unknown),
        ]);

        Assert.Equal(ResourceLevel.Normal, verdict.Worst);
    }

    [Fact]
    public void Worst_UnknownBesideCritical_IsCritical_AnUnreadableSwapDoesNotHideARedProcessor()
    {
        var verdict = new ResourcePressureVerdict(
        [
            Verdict(ResourceKind.Swap, ResourceLevel.Unknown),
            Verdict(ResourceKind.Cpu, ResourceLevel.Critical),
            Verdict(ResourceKind.Memory, ResourceLevel.Normal),
        ]);

        Assert.Equal(ResourceLevel.Critical, verdict.Worst);
    }

    [Fact]
    public void ResourceKind_IsTheProcessorMemorySwapAndDiskSpace_PinnedByNameAndNumber()
    {
        // The owner narrowed the mission on 2026-11-06 and took the disk bar out; on 2026-11-07 he brought back
        // FREE SPACE as a DISK bar (Disk Space on the Meter). Disk activity still has no kind - it reaches him only
        // through the slowness diagnosis, in words. The whole set is pinned by name and number, so a further kind
        // is a decision, not a drift, and stored history is never silently relabelled.
        Assert.Equal(
            new[] { "Cpu", "Memory", "Swap", "Disk" },
            Enum.GetNames<ResourceKind>());
        Assert.Equal(0, (int)ResourceKind.Cpu);
        Assert.Equal(1, (int)ResourceKind.Memory);
        Assert.Equal(2, (int)ResourceKind.Swap);
        Assert.Equal(3, (int)ResourceKind.Disk);
    }

    [Fact]
    public void ResourceSample_CarriesExactlyTheNarrowedParts()
    {
        // The shape three platform probes are written against. The parts are pinned by name so that
        // re-adding a disk activity meter or a network figure - both cut on 2026-11-06 - fails here instead
        // of reaching a screen. DiskSpace (free space per fixed drive) is the one part the owner brought
        // back, on 2026-11-07.
        Assert.Equal(
            new[] { "TakenAtUtc", "Cpu", "Memory", "Swap", "Stall", "DiskEvidence", "Processes", "DiskSpace" },
            typeof(ResourceSample).GetProperties().Select(property => property.Name).ToArray());
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
