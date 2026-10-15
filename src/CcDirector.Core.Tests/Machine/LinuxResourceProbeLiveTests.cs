using System.Text;
using CcDirector.Core.Machine;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The Linux probe against the REAL machine it runs on. Each test asserts real, non-zero figures, so a
/// probe that quietly read nothing fails here rather than passing on an empty sample.
/// </summary>
[Trait("Category", "Resources")]
public class LinuxResourceProbeLiveTests(ITestOutputHelper output)
{
    private const string Because = "reads the real /proc and /sys of the machine running the test";

    /// <summary>Two reads 1.5 seconds apart, so every rate has an interval.</summary>
    private static ResourceSample ReadWithInterval()
    {
        var probe = new LinuxResourceProbe();
        probe.Read();
        Thread.Sleep(1500);
        return probe.Read();
    }

    [LinuxOnlyFact(Because)]
    public void Read_ThisMachine_ProcessorHasCoresAndABusyFigure()
    {
        var sample = ReadWithInterval();

        Assert.True(sample.Cpu.CouldRead, sample.Cpu.UnreadableReason);
        Assert.Equal(Environment.ProcessorCount, sample.Cpu.LogicalCoreCount);
        Assert.InRange(sample.Cpu.TotalBusyPercent, 0, 100);
        Assert.InRange(sample.Cpu.BusiestCorePercent, sample.Cpu.TotalBusyPercent / 100, 100);
        Assert.True(sample.Cpu.BusiestCorePercent > 0, "a core running this test cannot have been idle for 1.5 seconds");
    }

    [LinuxOnlyFact(Because)]
    public void Read_ThisMachine_MemoryAndSwapAreRead()
    {
        var sample = ReadWithInterval();

        Assert.True(sample.Memory.CouldRead, sample.Memory.UnreadableReason);
        Assert.True(sample.Memory.TotalBytes > 0);
        Assert.True(sample.Memory.AvailableBytes > 0);
        Assert.True(sample.Swap.CouldRead, sample.Swap.UnreadableReason);
        Assert.True(sample.Swap.UsedBytes <= sample.Swap.TotalBytes);
    }

    [LinuxOnlyFact(Because)]
    public void Read_ThisMachine_AtLeastOnePhysicalDiskWithANameAndAKnownKind()
    {
        var sample = ReadWithInterval();

        Assert.True(sample.Disks.CouldRead, sample.Disks.UnreadableReason);
        Assert.NotEmpty(sample.Disks.Disks);
        Assert.Contains(sample.Disks.Disks, d => d.Name.Length > 0 && d.Kind != DiskKind.Unknown);
        Assert.All(sample.Disks.Disks, d => Assert.True(d.CouldRead, d.UnreadableReason));
    }

    [LinuxOnlyFact(Because)]
    public void Read_ThisMachine_RootVolumeHasSpaceAndSitsOnAPhysicalDisk()
    {
        var sample = ReadWithInterval();

        var root = Assert.Single(sample.Volumes, v => v.MountPoint == "/");
        Assert.True(root.CouldRead, root.UnreadableReason);
        Assert.True(root.TotalBytes > 0);
        Assert.True(root.FreeBytes <= root.TotalBytes);
        Assert.NotEmpty(root.PhysicalDiskNames);
        Assert.All(root.PhysicalDiskNames, name => Assert.Contains(sample.Disks.Disks, d => d.Name == name));
    }

    [LinuxOnlyFact(Because)]
    public void Read_ThisMachine_NetworkAndStallsAreRead()
    {
        var sample = ReadWithInterval();

        Assert.True(sample.Network.CouldRead, sample.Network.UnreadableReason);
        Assert.True(sample.Stall.CouldRead, sample.Stall.UnreadableReason);
        Assert.Equal(StallSource.LinuxPressureStall, sample.Stall.Source);
        Assert.NotNull(sample.Stall.CpuStalledPercent);
        Assert.NotNull(sample.Stall.MemoryStalledPercent);
        Assert.NotNull(sample.Stall.IoStalledPercent);
        Assert.True(sample.Stall.RunQueueLength >= 1, "this test's own thread is running while it reads");
        Assert.NotNull(sample.Stall.ProcessesWaitingOnDisk);
    }

    [LinuxOnlyFact(Because)]
    public void Read_ThisMachine_ProcessListIncludesThisTestProcessWithItsParent()
    {
        var sample = ReadWithInterval();

        Assert.True(sample.Processes.CouldRead, sample.Processes.UnreadableReason);
        Assert.True(sample.Processes.Processes.Count > 1);
        var self = Assert.Single(sample.Processes.Processes, p => p.Pid == Environment.ProcessId);
        Assert.NotNull(self.ParentPid);
        Assert.True(self.ParentPid > 0);
        Assert.Contains(sample.Processes.Processes, p => p.Pid == self.ParentPid);
        Assert.True(self.MemoryBytes > 0);
        Assert.NotNull(self.StartTimeUtc);
        Assert.InRange(self.StartTimeUtc.Value, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        Assert.NotNull(self.DiskReadBytesPerSecond); // our own io file is always open to us
    }

    /// <summary>
    /// Prints one full sample of this machine. The proof under missions/resource-monitor/phase-1/ is
    /// made by running this test with RESOURCE_PROBE_LIVE_READING set to the file to write, beside the
    /// operating system's own tools read at the same moment.
    /// </summary>
    [LinuxOnlyFact(Because)]
    public void Read_ThisMachine_PrintsOneFullSample()
    {
        var sample = ReadWithInterval();

        var text = new StringBuilder();
        text.AppendLine($"taken at {sample.TakenAtUtc:O} on {Environment.MachineName}");
        text.AppendLine(sample.Cpu.ToString());
        text.AppendLine(sample.Memory.ToString());
        text.AppendLine(sample.Swap.ToString());
        foreach (var disk in sample.Disks.Disks)
            text.AppendLine($"{disk}; {disk.MillisecondsPerRead?.ToString("F1") ?? "no reads"} ms per read; queue {disk.QueueLength:F2}");
        foreach (var volume in sample.Volumes) text.AppendLine(volume.ToString());
        text.AppendLine(sample.Network.ToString());
        text.AppendLine(sample.Stall.ToString());
        text.AppendLine(sample.Processes.ToString());
        foreach (var process in sample.Processes.Processes.OrderByDescending(p => p.CpuPercent).Take(10))
            text.AppendLine($"  {process}, parent {process.ParentPid}, " +
                            $"disk {process.DiskReadBytesPerSecond?.ToString("F0") ?? "not readable"} B/s read, " +
                            $"{process.DiskWriteBytesPerSecond?.ToString("F0") ?? "not readable"} B/s written");

        output.WriteLine(text.ToString());
        if (Environment.GetEnvironmentVariable("RESOURCE_PROBE_LIVE_READING") is { Length: > 0 } path)
            File.WriteAllText(path, text.ToString());

        Assert.True(sample.Cpu.CouldRead && sample.Memory.CouldRead && sample.Disks.CouldRead && sample.Processes.CouldRead);
    }
}
