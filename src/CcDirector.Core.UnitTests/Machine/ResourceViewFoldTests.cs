using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// What the toolbar meter and the detail screen are handed (issue #2811, mission sections 5d to 5h). The
/// views decide nothing; every state the owner can see is pinned here, and "cannot read" is a dash and a
/// reason - never a zero.
/// </summary>
[Trait("Category", "Resources")]
public class ResourceViewFoldTests
{
    private const ulong Gb = FakeSampleBuilder.Gigabyte;
    private const ulong Mb = FakeSampleBuilder.Megabyte;
    private static readonly DateTime At = FakeSampleBuilder.DefaultTakenAtUtc;

    private static ResourceSamplerState State(ResourceSample sample, ResourceLevel cpu = ResourceLevel.Normal, ResourceLevel memory = ResourceLevel.Normal) =>
        new(sample, new ResourcePressureVerdict(
        [
            new ResourceVerdict(ResourceKind.Cpu, cpu, "cpu"),
            new ResourceVerdict(ResourceKind.Memory, memory, "memory"),
            new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal, "swap"),
        ]));

    private static ResourceMeterView Meter(ResourceSamplerState state, SessionUsage? own = null) =>
        ResourceMeterFold.Build(state, "devthrottle-mac-mini", 4,
            own is null ? null : [new ResourceSessionInput(own, own.SessionId, null)], TimeSpan.FromSeconds(3), []);

    // ---- the toolbar meter ----

    [Fact]
    public void Meter_Healthy_ShowsTheMockUpFigures()
    {
        var meter = Meter(State(new FakeSampleBuilder().Memory(16 * Gb, (ulong)(7.2 * Gb)).Build()));

        Assert.Equal("CPU", meter.Cpu.Label);
        Assert.Equal("28%", meter.Cpu.PercentText);
        Assert.Equal(0.28, meter.Cpu.Fraction!.Value, 3);
        Assert.Equal("MEM", meter.Memory.Label);
        Assert.Equal("55%", meter.Memory.PercentText);
        // The compact form keeps all three as text (the approved mock-up of 7 November 2026).
        Assert.Equal("CPU 28% MEM 55% C: 500 GB", meter.NarrowText);
        Assert.Equal(ResourceLevel.Normal, meter.Cpu.Level);
    }

    [Fact]
    public void Meter_Tooltip_MachineFiguresFirst_ThenThisDirectorsSessions()
    {
        var own = new SessionUsage("all", 0, 19, (ulong)(2.1 * Gb), [1, 2], 0, 0);

        var lines = Meter(State(new FakeSampleBuilder().Build()), own).TooltipLines;

        Assert.Equal("devthrottle-mac-mini - 4 sessions running", lines[0]);
        Assert.Equal("CPU 28% of 8 cores - busiest core 71%", lines[1]);
        Assert.StartsWith("Memory 8.86 GB used of 16.0 GB", lines[2]);
        // Every drive comes after the bars it shares the meter with (Disk Space on the Meter, 7 November 2026).
        Assert.Equal("C: 500 GB free of 931 GB - normal", lines[3]);
        Assert.Equal("Swap 966 MB of 2.00 GB", lines[4]);
        Assert.Equal("This Director's sessions 2.10 GB and 19% CPU", lines[5]);
        Assert.Equal("Updated 3 seconds ago - click for detail", lines[6]);
    }

    [Fact]
    public void Meter_SessionsWithRefusedFigures_SayAtLeast()
    {
        var own = new SessionUsage("all", 0, 19, (ulong)(2.1 * Gb), [1, 2], ProcessesWithUnknownProcessor: 1, ProcessesWithUnknownMemory: 1);

        var lines = Meter(State(new FakeSampleBuilder().Build()), own).TooltipLines;

        Assert.Equal("This Director's sessions at least 2.10 GB and at least 19% CPU", lines[5]);
    }

    /// <summary>
    /// SWAP MAY RAISE THE FRAME; IT MAY NEVER ASSERT HEALTH. Both bars unreadable and swap readable and Normal:
    /// the frame stays Unknown. Taking the plain maximum made it Normal - the meter telling you the machine is
    /// fine on the strength of the one reading you cannot see. Found in the second review of pull request 4732,
    /// which noticed the semantic change I had not listed.
    /// </summary>
    [Fact]
    public void Meter_EveryBarUnreadableAndSwapNormal_TheFrameStaysUnknown_NeverNormal()
    {
        var sample = new FakeSampleBuilder()
            .CpuUnreadable("the counters are switched off")
            .MemoryUnreadable("sysctl refused")
            .WithDiskSpace(DiskSpaceSet.Unreadable("the mount table could not be read"))
            .Build();
        var meter = Meter(State(sample, cpu: ResourceLevel.Unknown, memory: ResourceLevel.Unknown));

        Assert.Equal(ResourceLevel.Unknown, meter.Cpu.Level);
        Assert.Equal(ResourceLevel.Unknown, meter.Memory.Level);
        Assert.Equal(ResourceLevel.Unknown, meter.FrameLevel);
    }

    /// <summary>
    /// A WARNING IS SPOKEN IN ITS OWN RESOURCE'S WORDS. These are the deleted banner's headlines, kept per
    /// resource. The second review of pull request 4732 found I had flattened all three onto one pair, so a busy
    /// processor announced itself as "the processor is running short" - not English for what happened - and full
    /// swap as "maxed out" rather than "nearly full".
    /// </summary>
    [Theory]
    [InlineData(ResourceLevel.Tight, ResourceLevel.Normal, ResourceLevel.Normal, "the processor is running hot")]
    [InlineData(ResourceLevel.Critical, ResourceLevel.Normal, ResourceLevel.Normal, "the processor is maxed out")]
    [InlineData(ResourceLevel.Normal, ResourceLevel.Tight, ResourceLevel.Normal, "memory is running short")]
    [InlineData(ResourceLevel.Normal, ResourceLevel.Critical, ResourceLevel.Normal, "memory is nearly gone")]
    [InlineData(ResourceLevel.Normal, ResourceLevel.Normal, ResourceLevel.Tight, "swap is filling up")]
    [InlineData(ResourceLevel.Normal, ResourceLevel.Normal, ResourceLevel.Critical, "swap is nearly full")]
    public void Meter_AWarning_IsSpokenInItsOwnResourcesWords(
        ResourceLevel cpu, ResourceLevel memory, ResourceLevel swap, string expected)
    {
        var state = new ResourceSamplerState(new FakeSampleBuilder().Build(), new ResourcePressureVerdict(
        [
            new ResourceVerdict(ResourceKind.Cpu, cpu, "cpu evidence"),
            new ResourceVerdict(ResourceKind.Memory, memory, "memory evidence"),
            new ResourceVerdict(ResourceKind.Swap, swap, "swap evidence"),
        ]));

        var name = ResourceMeterFold.Build(state, "m", 1, null, TimeSpan.FromSeconds(3), []).AccessibleName;

        Assert.Contains(expected, name, StringComparison.Ordinal);
        Assert.StartsWith("Resources: CPU ", name, StringComparison.Ordinal);
    }

    /// <summary>A quiet machine says no warning at all - the sentence is the figures and nothing else.</summary>
    [Fact]
    public void Meter_NothingWarning_TheSpokenNameCarriesNoWarning()
    {
        var name = Meter(State(new FakeSampleBuilder().Build())).AccessibleName;

        Assert.DoesNotContain("Warning", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Meter_CpuUnreadable_ShowsADashAndWhy_MemoryKeepsItsRealFigure()
    {
        var meter = Meter(State(new FakeSampleBuilder().CpuUnreadable("the counters are switched off").Build(), cpu: ResourceLevel.Unknown));

        Assert.False(meter.Cpu.CouldRead);
        Assert.Null(meter.Cpu.Fraction);
        Assert.Equal(ResourceViewText.CannotReadText, meter.Cpu.PercentText);
        Assert.Equal(ResourceLevel.Unknown, meter.Cpu.Level);
        Assert.Equal("CPU cannot read: the counters are switched off", meter.Cpu.Tooltip);
        Assert.True(meter.Memory.CouldRead);
        Assert.Equal("55%", meter.Memory.PercentText);
        Assert.Equal("CPU - MEM 55% C: 500 GB", meter.NarrowText);
    }

    [Fact]
    public void Meter_CpuUnreadable_IsUnknownEvenIfTheVerdictSaysCritical_ADashIsNeverColouredRed()
    {
        var meter = Meter(State(new FakeSampleBuilder().CpuUnreadable("gone").Build(), cpu: ResourceLevel.Critical));

        Assert.Equal(ResourceLevel.Unknown, meter.Cpu.Level);
    }

    [Fact]
    public void Meter_MemoryUnreadable_ShowsADash_NeverZero()
    {
        var meter = Meter(State(new FakeSampleBuilder().MemoryUnreadable("sysctl refused").Build(), memory: ResourceLevel.Unknown));

        Assert.Null(meter.Memory.Fraction);
        Assert.Equal(ResourceViewText.CannotReadText, meter.Memory.PercentText);
        Assert.Equal("Memory cannot read: sysctl refused", meter.Memory.Tooltip);
        Assert.Equal("28%", meter.Cpu.PercentText);
    }

    [Fact]
    public void Meter_MemoryMoreAvailableThanInstalled_IsUnreadable_NotAConfidentBar()
    {
        var meter = Meter(State(new FakeSampleBuilder().Memory(8 * Gb, 9 * Gb).Build()));

        Assert.False(meter.Memory.CouldRead);
        Assert.Equal(ResourceLevel.Unknown, meter.Memory.Level);
    }

    [Fact]
    public void Meter_NotYetSampled_BothBarsAreDashes()
    {
        var meter = Meter(ResourceSamplerState.NotYetSampled);

        Assert.Equal("CPU - MEM - DISK -", meter.NarrowText);
        Assert.Equal(ResourceLevel.Unknown, meter.Cpu.Level);
        Assert.Equal(ResourceLevel.Unknown, meter.Memory.Level);
        Assert.Equal(ResourceLevel.Unknown, meter.Disk.Level);
        Assert.Equal("Waiting for the first reading - click for detail", meter.TooltipLines[^1]);
    }

    [Fact]
    public void Meter_FirstSample_CpuSaysWhyNotZero()
    {
        var meter = Meter(State(new FakeSampleBuilder().FirstSample().Build(), cpu: ResourceLevel.Unknown));

        Assert.Equal($"CPU cannot read: {ResourceUnreadableReasons.FirstSample}", meter.Cpu.Tooltip);
        Assert.Equal("55%", meter.Memory.PercentText);
    }

    [Theory]
    [InlineData(ResourceLevel.Tight)]
    [InlineData(ResourceLevel.Critical)]
    public void Meter_Level_IsTheVerdictsLevel_ForEachBarOnItsOwn(ResourceLevel level)
    {
        var meter = Meter(State(new FakeSampleBuilder().Cpu(97, 100).Build(), cpu: level));

        Assert.Equal(level, meter.Cpu.Level);
        Assert.Equal(ResourceLevel.Normal, meter.Memory.Level);
    }

    [Theory]
    [InlineData(ResourceLevel.Tight)]
    [InlineData(ResourceLevel.Critical)]
    public void Meter_MemoryLevel_ColoursOnlyTheMemoryBar(ResourceLevel level)
    {
        var meter = Meter(State(new FakeSampleBuilder().MemoryAvailable(1 * Gb).Build(), memory: level));

        Assert.Equal(level, meter.Memory.Level);
        Assert.Equal(ResourceLevel.Normal, meter.Cpu.Level);
    }

    [Theory]
    [InlineData(0, "Updated just now - click for detail")]
    [InlineData(-10, "Updated just now - click for detail")]
    [InlineData(5, "Updated 5 seconds ago - click for detail")]
    [InlineData(119, "Updated 119 seconds ago - click for detail")]
    [InlineData(120, "Updated 2 minutes ago - click for detail")]
    public void Meter_Updated_SaysHowOldTheReadingIs(int ageSeconds, string expected)
    {
        var meter = ResourceMeterFold.Build(State(new FakeSampleBuilder().Build()), "m", 1, null, TimeSpan.FromSeconds(ageSeconds), []);

        Assert.Equal(expected, meter.TooltipLines[^1]);
    }

    [Fact]
    public void Meter_SwapUnreadable_TooltipSaysSo()
    {
        var lines = Meter(State(new FakeSampleBuilder().SwapUnreadable("no page file counter").Build())).TooltipLines;

        Assert.Contains("Swap cannot read: no page file counter", lines);
    }

    // ---- the shared figures ----

    [Theory]
    [InlineData(966 * Mb, "966 MB")]
    [InlineData(2 * Gb, "2.00 GB")]
    [InlineData(16 * Gb, "16.0 GB")]
    [InlineData(128 * Gb, "128 GB")]
    public void Bytes_WritesSizesTheWayTheMockUpsDo(ulong bytes, string expected)
    {
        Assert.Equal(expected, ResourceViewText.Bytes(bytes));
    }

    [Fact]
    public void Bytes_UnderAMegabyte_IsKilobytes_NeverAMadeUpZeroMegabytes()
    {
        Assert.Equal("512 KB", ResourceViewText.Bytes(512 * 1024));
    }

    [Fact]
    public void Bytes_Null_IsTheDash()
    {
        Assert.Equal(ResourceViewText.CannotReadText, ResourceViewText.Bytes(null));
        Assert.Equal(ResourceViewText.CannotReadText, ResourceViewText.PercentOf100(null));
    }

    // ---- the detail screen ----

    private static ResourceSample Machine() => new FakeSampleBuilder()
        .Cpu(40, 90)
        .Memory(16 * Gb, 6 * Gb)
        .Process(FakeSampleBuilder.DefaultDirectorPid, 1, "cc-director", 2, 400 * Mb)
        .Process(200, FakeSampleBuilder.DefaultDirectorPid, "claude", 10, 1 * Gb)
        .Process(201, 200, "dotnet", 20, 1 * Gb)
        .Process(202, 201, "mds_stores", null, null)
        .Process(300, FakeSampleBuilder.DefaultDirectorPid, "codex", 3, 500 * Mb)
        .Build();

    private static ResourceDetailView Detail(ResourceSample sample, params ResourceSessionInput[] sessions) =>
        ResourceDetailFold.Build(State(sample), [sample], "devthrottle-mac-mini", sessions, FakeSampleBuilder.DefaultDirectorPid);

    private static readonly ResourceSessionInput Busy =
        new(new SessionUsage("s-140", 200, 30, 2 * Gb, [200, 201, 202], ProcessesWithUnknownProcessor: 1, ProcessesWithUnknownMemory: 1), "140 - Resource Monitor - Dev", null);

    private static readonly ResourceSessionInput Quiet =
        new(new SessionUsage("s-112", 300, 3, 500 * Mb, [300], 0, 0), "112 - Shorts - Builder", "Marketing");

    [Fact]
    public void Detail_Tiles_CpuAndMemoryOnly()
    {
        var detail = Detail(Machine(), Busy, Quiet);

        Assert.Equal("Resources - devthrottle-mac-mini", detail.Title);
        Assert.Equal("40%", detail.Cpu.Headline);
        Assert.Equal("8 cores - busiest core 90%", detail.Cpu.Detail);
        Assert.Equal("10.0 / 16.0 GB", detail.Memory.Headline);
        Assert.Equal("6.00 GB available - swap 966 MB", detail.Memory.Detail);
    }

    [Fact]
    public void Detail_Sessions_MostProcessorFirst_WithFactoryOrDash()
    {
        var detail = Detail(Machine(), Quiet, Busy);

        Assert.Equal(["s-140", "s-112"], detail.Sessions.Select(row => row.SessionId));
        Assert.Equal(ResourceViewText.CannotReadText, detail.Sessions[0].Factory);
        Assert.Equal("Marketing", detail.Sessions[1].Factory);
    }

    [Fact]
    public void Detail_SessionWithARefusedProcess_IsALowerBound_AndTheProcessIsStillNamed()
    {
        var row = Detail(Machine(), Busy).Sessions[0];

        Assert.Equal("at least 30%", row.CpuText);
        Assert.Equal("at least 2.00 GB", row.MemoryText);
        Assert.Equal("3", row.ProcessCountText);
        Assert.Equal(["claude", "dotnet", "mds_stores"], row.Processes.Select(process => process.Name));
        Assert.Equal([0, 1, 2], row.Processes.Select(process => process.Depth));
        Assert.Equal(ResourceViewText.CannotReadText, row.Processes[2].CpuText);
        Assert.Equal(ResourceViewText.CannotReadText, row.Processes[2].MemoryText);
    }

    [Fact]
    public void Detail_DirectorRow_IsTheDirectorsOwnProcess()
    {
        var row = Detail(Machine(), Quiet).Director;

        Assert.Equal(ResourceDetailFold.DirectorLabel, row.Label);
        Assert.Equal("2%", row.CpuText);
        Assert.Equal("400 MB", row.MemoryText);
    }

    [Fact]
    public void Detail_EverythingElse_IsTheMachineMinusWhatThisDirectorAccountsFor()
    {
        var row = Detail(Machine(), Quiet).EverythingElse;

        // 40% machine - 2% Director - 3% session; 10 GB used - 400 MB - 500 MB.
        Assert.Equal(ResourceDetailFold.EverythingElseLabel, row.Label);
        Assert.Equal("35%", row.CpuText);
        Assert.Equal("9.12 GB", row.MemoryText);
    }

    [Fact]
    public void Detail_EverythingElse_IsADash_WhenASessionTotalIsOnlyAFloor()
    {
        var row = Detail(Machine(), Busy, Quiet).EverythingElse;

        Assert.Equal(ResourceViewText.CannotReadText, row.CpuText);
        Assert.Equal(ResourceViewText.CannotReadText, row.MemoryText);
    }

    [Fact]
    public void Detail_OnlyTheProcessorIsAFloor_MemoryStaysExact_AndEverythingElseKeepsItsMemory()
    {
        var partial = new ResourceSessionInput(
            new SessionUsage("s-7", 300, 3, 500 * Mb, [300], ProcessesWithUnknownProcessor: 1, ProcessesWithUnknownMemory: 0), "7 - P", null);

        var detail = Detail(Machine(), partial);

        Assert.Equal("at least 3%", detail.Sessions[0].CpuText);
        Assert.Equal("500 MB", detail.Sessions[0].MemoryText);
        Assert.Equal(ResourceViewText.CannotReadText, detail.EverythingElse.CpuText);
        Assert.Equal("9.12 GB", detail.EverythingElse.MemoryText);
    }

    [Fact]
    public void Detail_EverythingElse_IsADash_WhenTheDirectorsOwnFiguresAreRefused()
    {
        var sample = new FakeSampleBuilder()
            .Process(FakeSampleBuilder.DefaultDirectorPid, 1, "cc-director", null, null)
            .Process(300, FakeSampleBuilder.DefaultDirectorPid, "codex", 3, 500 * Mb)
            .Build();

        var row = Detail(sample, Quiet).EverythingElse;

        Assert.Equal(ResourceViewText.CannotReadText, row.CpuText);
        Assert.Equal(ResourceViewText.CannotReadText, row.MemoryText);
    }

    [Fact]
    public void Detail_EverythingElse_IsADash_WhenOurProcessesSumPastTheMachine_NeverZero()
    {
        // 28% machine but a session measured at 40%; 8.86 GB used but sessions holding 12 GB (shared pages).
        var greedy = new ResourceSessionInput(new SessionUsage("s-9", 300, 40, 12 * Gb, [300], 0, 0), "9 - Greedy", null);
        var sample = new FakeSampleBuilder()
            .Process(FakeSampleBuilder.DefaultDirectorPid, 1, "cc-director", 2, 400 * Mb)
            .Process(300, FakeSampleBuilder.DefaultDirectorPid, "dotnet", 40, 12 * Gb)
            .Build();

        var row = Detail(sample, greedy).EverythingElse;

        Assert.Equal(ResourceViewText.CannotReadText, row.CpuText);
        Assert.Equal(ResourceViewText.CannotReadText, row.MemoryText);
    }

    [Fact]
    public void Detail_Tree_AProcessWhoseParentIsGone_IsStillListed_UnderTheRoot()
    {
        var sample = new FakeSampleBuilder()
            .Process(FakeSampleBuilder.DefaultDirectorPid, 1, "cc-director", 2, 400 * Mb)
            .Process(200, FakeSampleBuilder.DefaultDirectorPid, "claude", 10, 1 * Gb)
            .Process(205, 999, "orphaned-build", 5, 100 * Mb)
            .Process(206, null, "no-parent-known", 1, 10 * Mb)
            .Build();
        var session = new ResourceSessionInput(new SessionUsage("s-1", 200, 16, 2 * Gb, [200, 205, 206], 0, 0), "1 - S", null);

        var row = Detail(sample, session).Sessions[0];

        Assert.Equal("3", row.ProcessCountText);
        Assert.Equal(["claude", "orphaned-build", "no-parent-known"], row.Processes.Select(process => process.Name));
        Assert.Equal([0, 1, 1], row.Processes.Select(process => process.Depth));
    }

    [Fact]
    public void Detail_Tree_RootGone_TheRestAreStillListed()
    {
        var sample = new FakeSampleBuilder()
            .Process(FakeSampleBuilder.DefaultDirectorPid, 1, "cc-director", 2, 400 * Mb)
            .Process(201, 200, "dotnet", 20, 1 * Gb)
            .Build();
        var session = new ResourceSessionInput(new SessionUsage("s-1", 200, 20, 1 * Gb, [200, 201], 0, 0), "1 - S", null);

        var row = Detail(sample, session).Sessions[0];

        var only = Assert.Single(row.Processes);
        Assert.Equal("dotnet", only.Name);
        Assert.Equal(0, only.Depth);
    }

    [Fact]
    public void Detail_MemoryHistory_AnUnreadableSampleIsAGap()
    {
        var good = new FakeSampleBuilder().At(At).Memory(10 * Gb, 5 * Gb).Build();
        var bad = new FakeSampleBuilder().At(At.AddSeconds(5)).MemoryUnreadable("gone").Build();

        var detail = ResourceDetailFold.Build(State(bad), [good, bad], "m", [], FakeSampleBuilder.DefaultDirectorPid);

        Assert.Equal([0.5, null], detail.Memory.History.Select(point => point.Fraction));
    }

    [Fact]
    public void Detail_ProcessesUnreadable_RowsKeepTheirTotals_ButHaveNoTree()
    {
        var sample = new FakeSampleBuilder().ProcessesUnreadable("first sample, no interval yet").Build();

        var detail = Detail(sample, Quiet);

        Assert.Empty(detail.Sessions[0].Processes);
        Assert.Equal(ResourceViewText.CannotReadText, detail.Director.CpuText);
        Assert.Equal(ResourceViewText.CannotReadText, detail.EverythingElse.CpuText);
    }

    [Fact]
    public void Detail_CpuUnreadable_TileIsADashWithTheReason_MemoryTileStillReal()
    {
        var detail = Detail(new FakeSampleBuilder().CpuUnreadable("the counters are switched off").Build());

        Assert.Equal(ResourceViewText.CannotReadText, detail.Cpu.Headline);
        Assert.Equal("cannot read: the counters are switched off", detail.Cpu.Detail);
        Assert.Equal(ResourceLevel.Unknown, detail.Cpu.Level);
        Assert.Equal("8.86 / 16.0 GB", detail.Memory.Headline);
    }

    [Fact]
    public void Detail_History_AnUnreadableSampleIsAGap_NeverAZero()
    {
        var good = new FakeSampleBuilder().At(At).Cpu(50).Build();
        var bad = new FakeSampleBuilder().At(At.AddSeconds(5)).CpuUnreadable("gone").Build();

        var detail = ResourceDetailFold.Build(State(bad), [good, bad], "m", [], FakeSampleBuilder.DefaultDirectorPid);

        Assert.Equal([0.5, null], detail.Cpu.History.Select(point => point.Fraction));
        Assert.Equal([At, At.AddSeconds(5)], detail.Cpu.History.Select(point => point.AtUtc));
    }

    [Fact]
    public void Detail_NotYetSampled_TilesAreDashes()
    {
        var detail = ResourceDetailFold.Build(ResourceSamplerState.NotYetSampled, [], "m", [], FakeSampleBuilder.DefaultDirectorPid);

        Assert.Equal(ResourceViewText.CannotReadText, detail.Cpu.Headline);
        Assert.Equal(ResourceViewText.CannotReadText, detail.Memory.Headline);
        Assert.Equal(ResourceViewText.CannotReadText, detail.Director.CpuText);
    }
}
