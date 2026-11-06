using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The probe a platform uses while its full probe is not built: real memory, and every other figure unreadable
/// with the reason - never a zero.
/// </summary>
[Trait("Category", "Resources")]
public sealed class MemoryOnlyResourceProbeTests
{
    private const ulong Gb = FakeSampleBuilder.Gigabyte;
    private static readonly DateTime At = FakeSampleBuilder.DefaultTakenAtUtc;

    private sealed class FixedMemory(MachineMemoryReading reading) : IMachineMemoryProbe
    {
        public MachineMemoryReading Read() => reading;
    }

    [Fact]
    public void Read_MemoryIsTheRealReading_AndEverythingElseSaysWhyItCannotBeRead()
    {
        var memory = MachineMemoryReading.Read(32 * Gb, 3 * Gb, At);
        var probe = new MemoryOnlyResourceProbe(new FixedMemory(memory), "no probe for this platform yet");

        var sample = probe.Read();

        Assert.Equal(memory, sample.Memory);
        Assert.Equal(At, sample.TakenAtUtc);
        Assert.False(sample.Cpu.CouldRead);
        Assert.Equal("no probe for this platform yet", sample.Cpu.UnreadableReason);
        Assert.Equal(Environment.ProcessorCount, sample.Cpu.LogicalCoreCount);
        Assert.False(sample.Swap.CouldRead);
        Assert.False(sample.Stall.CouldRead);
        Assert.False(sample.DiskEvidence.CouldRead);
        Assert.False(sample.Processes.CouldRead);
        Assert.Empty(sample.Processes.Processes);
    }

    [Fact]
    public void Read_ThroughTheMeter_MemoryShowsItsFigure_AndCpuIsADashWithTheReason()
    {
        var probe = new MemoryOnlyResourceProbe(
            new FixedMemory(MachineMemoryReading.Read(16 * Gb, 8 * Gb, At)), ResourceProbes.NoWindowsProbeYet);
        var sample = probe.Read();
        var state = new ResourceSamplerState(sample, ResourcePressure.Evaluate([new TimelineSample(sample, TimeSpan.Zero)]));

        var meter = ResourceMeterFold.Build(state, "soren-north", 0, null, TimeSpan.Zero);

        Assert.Equal("50%", meter.Memory.PercentText);
        Assert.Equal("-", meter.Cpu.PercentText);
        Assert.Contains(ResourceProbes.NoWindowsProbeYet, meter.Cpu.Tooltip);
        Assert.Equal("-/50%", meter.NarrowText);
    }
}
