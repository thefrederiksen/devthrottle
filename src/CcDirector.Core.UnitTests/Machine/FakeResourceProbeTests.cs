using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The fake probe the Director side is built against (issue #2811, phase 2). Its samples must keep the
/// same contract the platform probes are held to, or a view built against it would be tested against a
/// machine that cannot exist.
/// </summary>
[Trait("Category", "Resources")]
public class FakeResourceProbeTests
{
    private const string Reason = "the kernel refused the read";

    public static TheoryData<string> EveryVariant() => new()
    {
        "default", "cpu unreadable", "memory unreadable", "swap unreadable", "stall unreadable",
        "disks unreadable", "processes unreadable", "first sample", "refused process figures",
    };

    private static ResourceSample Variant(string name)
    {
        var builder = new ResourceSampleBuilder();
        _ = name switch
        {
            "default" => builder,
            "cpu unreadable" => builder.CpuUnreadable(Reason),
            "memory unreadable" => builder.MemoryUnreadable(Reason),
            "swap unreadable" => builder.SwapUnreadable(Reason),
            "stall unreadable" => builder.Stall(StallReading.Unreadable(Reason)),
            "disks unreadable" => builder.DisksUnreadable(Reason),
            "processes unreadable" => builder.ProcessesUnreadable(Reason),
            "first sample" => builder.FirstSample(),
            "refused process figures" => builder.Process(10, 1, "mds_stores", null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        return builder.Build();
    }

    [Theory]
    [MemberData(nameof(EveryVariant))]
    public void Build_EveryVariant_KeepsTheProbeContract(string variant)
    {
        var violations = ResourceSampleContract.Violations(Variant(variant));

        Assert.Empty(violations);
    }

    [Fact]
    public void Build_Default_IsTheMacMiniFromTheMockUps()
    {
        var sample = new ResourceSampleBuilder().Build();

        Assert.True(sample.Cpu.CouldRead);
        Assert.Equal(28, sample.Cpu.TotalBusyPercent);
        Assert.Equal(71, sample.Cpu.BusiestCorePercent);
        Assert.Equal(8, sample.Cpu.LogicalCoreCount);
        Assert.Equal(16 * ResourceSampleBuilder.Gigabyte, sample.Memory.TotalBytes);
        Assert.True(sample.Memory.CouldRead);
        Assert.Equal(ResourceSampleBuilder.DefaultTakenAtUtc, sample.TakenAtUtc);
        Assert.Equal(ResourceSampleBuilder.DefaultTakenAtUtc, sample.Memory.TakenAtUtc);
        Assert.Single(sample.Processes.Processes, process => process.Pid == ResourceSampleBuilder.DefaultDirectorPid);
    }

    [Fact]
    public void Build_CpuUnreadable_LeavesMemoryReadable_BecauseThePartsFailIndependently()
    {
        var sample = new ResourceSampleBuilder().CpuUnreadable(Reason).Build();

        Assert.False(sample.Cpu.CouldRead);
        Assert.Equal(Reason, sample.Cpu.UnreadableReason);
        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.Swap.CouldRead);
    }

    [Fact]
    public void Build_FirstSample_RatesUnreadable_LevelsStillRead()
    {
        var sample = new ResourceSampleBuilder().FirstSample().Build();

        Assert.Equal(ResourceUnreadableReasons.FirstSample, sample.Cpu.UnreadableReason);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, sample.Processes.UnreadableReason);
        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.Swap.CouldRead);
    }

    [Fact]
    public void Process_FirstCall_ReplacesTheDefaultDirectorProcess()
    {
        var sample = new ResourceSampleBuilder().Process(42, 1, "dotnet", 12, 100 * ResourceSampleBuilder.Megabyte).Build();

        var process = Assert.Single(sample.Processes.Processes);
        Assert.Equal(42, process.Pid);
    }

    [Fact]
    public void Process_RefusedFigures_IsListedAndNamedWithNullFigures()
    {
        var sample = new ResourceSampleBuilder().Process(10, 1, "mds_stores", null, null).Build();

        var process = Assert.Single(sample.Processes.Processes);
        Assert.Equal("mds_stores", process.Name);
        Assert.Null(process.CpuPercent);
        Assert.Null(process.MemoryBytes);
    }

    [Fact]
    public void At_NotUtc_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ResourceSampleBuilder().At(new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void MemoryUnreadable_NoReason_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ResourceSampleBuilder().MemoryUnreadable(" "));
    }

    [Fact]
    public void Read_AfterSet_ReturnsTheSameSampleEveryTime()
    {
        var hot = new ResourceSampleBuilder().Cpu(99).Build();
        var probe = new FakeResourceProbe();

        probe.Set(hot);

        Assert.Same(hot, probe.Read());
        Assert.Same(hot, probe.Read());
        Assert.Equal(2, probe.ReadCount);
    }

    [Fact]
    public void Read_AfterEnqueue_ReturnsThemInOrder_ThenRepeatsTheLast()
    {
        var first = new ResourceSampleBuilder().Cpu(10).Build();
        var second = new ResourceSampleBuilder().Cpu(20).Build();
        var probe = new FakeResourceProbe();

        probe.Enqueue(first, second);

        Assert.Same(first, probe.Read());
        Assert.Same(second, probe.Read());
        Assert.Same(second, probe.Read());
    }

    [Fact]
    public void Set_DiscardsWhatWasQueued()
    {
        var queued = new ResourceSampleBuilder().Cpu(10).Build();
        var set = new ResourceSampleBuilder().Cpu(90).Build();
        var probe = new FakeResourceProbe();
        probe.Enqueue(queued);

        probe.Set(set);

        Assert.Same(set, probe.Read());
    }

    [Fact]
    public void Read_BeforeAnythingIsSet_ReturnsTheHealthyDefault()
    {
        var probe = new FakeResourceProbe();

        var sample = probe.Read();

        Assert.True(sample.Cpu.CouldRead);
        Assert.True(sample.Memory.CouldRead);
    }
}
