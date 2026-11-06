using CcDirector.Core.Machine;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.Machine;

/// <summary>
/// The warning banner's words and its lifetime: one banner per resource per crossing, and a warning that
/// clears when the load goes away. Driven end to end through the sampler on a virtual clock.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceBannerTests
{
    private const ulong Gb = ResourceSampleBuilder.Gigabyte;
    private static readonly DateTime At = ResourceSampleBuilder.DefaultTakenAtUtc;

    private static ResourceCrossing Crossing(ResourceKind kind, ResourceLevel from, ResourceLevel to, ResourceCrossingChange change,
        string evidence = "the processor has been above 85% for 2 minutes") =>
        new(kind, from, to, change, At, evidence);

    private static ResourceSample BusySample() =>
        new ResourceSampleBuilder()
            .Cpu(88)
            .Process(200, 1, "dotnet test", 62, 1 * Gb)
            .Process(300, 1, "chrome", 9, 3 * Gb)
            .Process(400, 1, "refused", null, null)
            .Build();

    private static readonly IReadOnlyList<ResourceSessionInput> Sessions =
    [
        new(new SessionUsage("s-140", 200, 62, 1 * Gb, [200], 0, 0), "140 (Resource Monitor - Dev - Linux probe)", null),
    ];

    [Fact]
    public void Build_CpuAmber_NamesTheMachineTheEvidenceAndTheBusiestProcessWithItsSession()
    {
        var banner = ResourceBannerFold.Build(
            Crossing(ResourceKind.Cpu, ResourceLevel.Normal, ResourceLevel.Tight, ResourceCrossingChange.Raised),
            BusySample(), "devthrottle-mac-mini", Sessions);

        Assert.Equal(ResourceLevel.Tight, banner.Level);
        Assert.Equal("CPU is running hot on devthrottle-mac-mini.", banner.Headline);
        Assert.Equal(
            "The processor has been above 85% for 2 minutes. Busiest: dotnet test in session 140 (Resource Monitor - Dev - Linux probe), 62%.",
            banner.Body);
    }

    [Fact]
    public void Build_MemoryRed_NamesTheBiggestByMemory_AndSaysWhenItIsNotOurs()
    {
        var banner = ResourceBannerFold.Build(
            Crossing(ResourceKind.Memory, ResourceLevel.Tight, ResourceLevel.Critical, ResourceCrossingChange.Escalated,
                "only 600 MB available"),
            BusySample(), "soren-north", Sessions);

        Assert.Equal("Memory is nearly gone on soren-north.", banner.Headline);
        Assert.Equal("Only 600 MB available. Biggest: chrome (not one of this Director's sessions), 3.00 GB.", banner.Body);
    }

    [Fact]
    public void Build_WhenNoProcessHasAFigure_SaysSo_NeverNamesAProcessAtZero()
    {
        var sample = new ResourceSampleBuilder().Process(400, 1, "refused", null, null).Build();

        var banner = ResourceBannerFold.Build(
            Crossing(ResourceKind.Cpu, ResourceLevel.Normal, ResourceLevel.Critical, ResourceCrossingChange.Raised),
            sample, "m", []);

        Assert.Contains("not known yet", banner.Body);
        Assert.DoesNotContain("0%", banner.Body);
    }

    [Fact]
    public void Build_WhenTheProcessListIsUnreadable_GivesTheReason()
    {
        var sample = new ResourceSampleBuilder().ProcessesUnreadable("the platform refused the process list").Build();

        var banner = ResourceBannerFold.Build(
            Crossing(ResourceKind.Cpu, ResourceLevel.Normal, ResourceLevel.Tight, ResourceCrossingChange.Raised),
            sample, "m", []);

        Assert.EndsWith("could not be named: the platform refused the process list.", banner.Body);
    }

    [Fact]
    public void Build_ACrossingThatShowsNoBanner_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => ResourceBannerFold.Build(
            Crossing(ResourceKind.Cpu, ResourceLevel.Tight, ResourceLevel.Normal, ResourceCrossingChange.Cleared),
            BusySample(), "m", []));
    }

    [Fact]
    public void Board_AmberThenRed_IsOneBannerThatTurnsRed_NotTwo()
    {
        var board = new ResourceBannerBoard();
        var amber = Crossing(ResourceKind.Cpu, ResourceLevel.Normal, ResourceLevel.Tight, ResourceCrossingChange.Raised);
        var red = Crossing(ResourceKind.Cpu, ResourceLevel.Tight, ResourceLevel.Critical, ResourceCrossingChange.Escalated);

        board.Apply(amber, ResourceBannerFold.Build(amber, BusySample(), "m", []));
        board.Apply(red, ResourceBannerFold.Build(red, BusySample(), "m", []));

        var banner = Assert.Single(board.Visible);
        Assert.Equal(ResourceLevel.Critical, banner.Level);
    }

    [Fact]
    public void Board_Dismissed_StaysDownWhileTheWarningEases_AndComesBackOnTheNextCrossing()
    {
        var board = new ResourceBannerBoard();
        var raised = Crossing(ResourceKind.Cpu, ResourceLevel.Normal, ResourceLevel.Critical, ResourceCrossingChange.Raised);
        board.Apply(raised, ResourceBannerFold.Build(raised, BusySample(), "m", []));

        Assert.True(board.Dismiss(ResourceKind.Cpu));
        Assert.False(board.Apply(Crossing(ResourceKind.Cpu, ResourceLevel.Critical, ResourceLevel.Tight, ResourceCrossingChange.Eased), null));
        Assert.Empty(board.Visible);

        board.Apply(Crossing(ResourceKind.Cpu, ResourceLevel.Tight, ResourceLevel.Normal, ResourceCrossingChange.Cleared), null);
        board.Apply(raised, ResourceBannerFold.Build(raised, BusySample(), "m", []));

        Assert.Single(board.Visible);
    }

    [Fact]
    public void Board_TwoResources_AreTwoBanners_ProcessorFirst()
    {
        var board = new ResourceBannerBoard();
        var memory = Crossing(ResourceKind.Memory, ResourceLevel.Normal, ResourceLevel.Tight, ResourceCrossingChange.Raised, "short");
        var cpu = Crossing(ResourceKind.Cpu, ResourceLevel.Normal, ResourceLevel.Tight, ResourceCrossingChange.Raised);

        board.Apply(memory, ResourceBannerFold.Build(memory, BusySample(), "m", []));
        board.Apply(cpu, ResourceBannerFold.Build(cpu, BusySample(), "m", []));

        Assert.Equal([ResourceKind.Cpu, ResourceKind.Memory], board.Visible.Select(b => b.Kind));
    }

    [Fact]
    public void EndToEnd_ALoadHeldForAMinute_GivesOneBanner_AndRemovingItClearsTheBanner()
    {
        var time = new FakeTimeProvider(At);
        var probe = new FakeResourceProbe(BusySample());
        var level = ResourceLevel.Normal;
        using var sampler = new ResourceSampler(probe, _ => new ResourcePressureVerdict([
            new ResourceVerdict(ResourceKind.Cpu, level, "the processor has been above 85% for 2 minutes"),
            new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Normal, "fine"),
            new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal, "fine"),
        ]), time);
        var board = new ResourceBannerBoard();
        var changes = 0;
        sampler.Crossed += (_, e) =>
        {
            var words = e.Crossing.ShowsBanner ? ResourceBannerFold.Build(e.Crossing, e.State.Latest, "m", []) : null;
            if (board.Apply(e.Crossing, words)) changes++;
        };

        sampler.Start();
        level = ResourceLevel.Tight;
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Single(board.Visible);
        Assert.Equal(1, changes);

        level = ResourceLevel.Normal;
        time.Advance(ResourceSampling.Interval);
        Assert.Empty(board.Visible);
        Assert.Equal(2, changes);
    }
}
