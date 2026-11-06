using System.Text;
using CcDirector.Core.Machine;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The shared probe contract, run against the Linux probe on the real Linux machine running the test.
/// The tests are the ones in <see cref="ResourceProbeContractLiveTests"/>; this class only supplies the
/// probe. Run on Linux with CC_DIRECTOR_LIVE_MACHINE_TESTS=1.
/// </summary>
[Trait("Category", "Resources")]
[LiveMachinePlatform(LiveMachinePlatform.Linux)]
public sealed class LinuxResourceProbeContractLiveTests : ResourceProbeContractLiveTests
{
    protected override IResourceProbe CreateProbe() => new LinuxResourceProbe();

    protected override string ProbeName => "the Linux probe";
}

/// <summary>
/// What only the Linux probe offers, read from the REAL /proc and /sys of the machine running the test.
/// Each test asserts real, non-zero figures, so a probe that quietly read nothing fails here rather
/// than passing on an empty sample. Each skips, with its reason, unless it is on Linux AND
/// CC_DIRECTOR_LIVE_MACHINE_TESTS=1 is set.
/// </summary>
[Trait("Category", "Resources")]
[LiveMachinePlatform(LiveMachinePlatform.Linux)]
public sealed class LinuxResourceProbeLiveTests(ITestOutputHelper output)
{
    private const string Reads = "the real /proc and /sys of this Linux machine";

    /// <summary>Two reads just over the cache lifetime apart, so every rate has an interval.</summary>
    private static ResourceSample ReadWithInterval()
    {
        var probe = new LinuxResourceProbe();
        probe.Read();
        Thread.Sleep(LinuxResourceProbe.CacheFor + TimeSpan.FromMilliseconds(500));
        return probe.Read();
    }

    [LiveMachineFact(Reads)]
    public void Read_ThisMachine_ProcessorMemoryAndSwapAreRealFigures()
    {
        var sample = ReadWithInterval();

        Assert.True(sample.Cpu.CouldRead, sample.Cpu.UnreadableReason);
        Assert.Equal(Environment.ProcessorCount, sample.Cpu.LogicalCoreCount);
        Assert.InRange(sample.Cpu.TotalBusyPercent, 0, 100);
        Assert.True(sample.Cpu.BusiestCorePercent > 0, "a core running this test cannot have been idle for the whole interval");

        Assert.True(sample.Memory.CouldRead, sample.Memory.UnreadableReason);
        Assert.True(sample.Memory.TotalBytes > 0);
        Assert.True(sample.Memory.AvailableBytes > 0);
        Assert.True(sample.Swap.CouldRead, sample.Swap.UnreadableReason);
        Assert.True(sample.Swap.UsedBytes <= sample.Swap.TotalBytes);
    }

    [LiveMachineFact(Reads)]
    public void Read_ThisMachine_StallsAreReadFromPressureStallInformation()
    {
        var sample = ReadWithInterval();

        Assert.True(sample.Stall.CouldRead, sample.Stall.UnreadableReason);
        Assert.Equal(StallSource.LinuxPressureStall, sample.Stall.Source);
        Assert.NotNull(sample.Stall.CpuStalledPercent);
        Assert.NotNull(sample.Stall.MemoryStalledPercent);
        Assert.NotNull(sample.Stall.IoStalledPercent);
        Assert.True(sample.Stall.RunQueueLength >= 1, "this test's own thread is running while it reads");
        Assert.NotNull(sample.Stall.ProcessesWaitingOnDisk);
    }

    [LiveMachineFact(Reads)]
    public void Read_ThisMachine_EveryPhysicalDiskIsNamedAndSomeDiskBacksTheRootVolume()
    {
        var sample = ReadWithInterval();

        Assert.True(sample.DiskEvidence.CouldRead, sample.DiskEvidence.UnreadableReason);
        Assert.NotEmpty(sample.DiskEvidence.Disks);
        Assert.All(sample.DiskEvidence.Disks, d => Assert.True(d.CouldRead, d.UnreadableReason));
        Assert.Contains(sample.DiskEvidence.Disks, d => d.Kind != DiskKind.Unknown);
        // Stated as a presence: the root volume must be found on at least one real disk.
        Assert.Contains(sample.DiskEvidence.Disks, d => d.IsInSystemVolume == true);
    }

    [LiveMachineFact(Reads)]
    public void Read_ThisMachine_ThisProcessIsListedWithItsParentAndItsFigures()
    {
        var sample = ReadWithInterval();

        Assert.True(sample.Processes.CouldRead, sample.Processes.UnreadableReason);
        var self = Assert.Single(sample.Processes.Processes, p => p.Pid == Environment.ProcessId);
        Assert.True(self.ParentPid > 0);
        Assert.Contains(sample.Processes.Processes, p => p.Pid == self.ParentPid);
        Assert.True(self.MemoryBytes > 0);
        Assert.NotNull(self.CpuPercent);
        Assert.InRange(self.StartTimeUtc ?? DateTime.MinValue, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        Assert.NotNull(self.DiskReadBytesPerSecond); // our own io file is always open to us
    }

    /// <summary>
    /// Prints one full sample of this machine, for the proof beside the operating system's own tools.
    /// Set RESOURCE_PROBE_LIVE_READING to a file path to have it written there too.
    /// </summary>
    [LiveMachineFact(Reads)]
    public void Read_ThisMachine_PrintsOneFullSample()
    {
        var sample = ReadWithInterval();

        var text = new StringBuilder();
        text.AppendLine($"taken at {sample.TakenAtUtc:O} on {Environment.MachineName}");
        text.AppendLine(sample.Cpu.ToString());
        text.AppendLine(sample.Memory.ToString());
        text.AppendLine(sample.Swap.ToString());
        foreach (var disk in sample.DiskEvidence.Disks) text.AppendLine(disk.ToString());
        text.AppendLine(sample.Stall.ToString());
        text.AppendLine(sample.Processes.ToString());
        foreach (var process in sample.Processes.Processes.OrderByDescending(p => p.CpuPercent ?? -1).Take(10))
            text.AppendLine($"  {process}, parent {process.ParentPid}, " +
                            $"disk {process.DiskReadBytesPerSecond?.ToString("F0") ?? "not readable"} B/s read, " +
                            $"{process.DiskWriteBytesPerSecond?.ToString("F0") ?? "not readable"} B/s written");

        output.WriteLine(text.ToString());
        if (Environment.GetEnvironmentVariable("RESOURCE_PROBE_LIVE_READING") is { Length: > 0 } path)
            File.WriteAllText(path, text.ToString());

        Assert.True(sample.Cpu.CouldRead && sample.Memory.CouldRead && sample.DiskEvidence.CouldRead && sample.Processes.CouldRead);
    }
}
