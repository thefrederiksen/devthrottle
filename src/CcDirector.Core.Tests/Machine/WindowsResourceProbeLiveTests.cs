using System.Diagnostics;
using CcDirector.Core.Machine;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// <see cref="WindowsResourceProbe"/> against the REAL operating system of the machine running the
/// suite. The parsers are proved from captured bytes elsewhere; this proves the calls are made, the
/// handles open without administrator rights, and the figures are real on a real machine.
///
/// Two samples are taken with real work done between them - every processor spun, and 64 MB written
/// through to the disk under the temp folder - so the rates are shown to MOVE with the load, not only
/// to be present. The samples are taken once and shared by every test here.
/// </summary>
[Trait("Category", "Resources")]
public class WindowsResourceProbeLiveTests : IClassFixture<WindowsResourceProbeLiveTests.TwoSamples>
{
    private readonly TwoSamples _samples;
    private readonly ITestOutputHelper _output;

    public WindowsResourceProbeLiveTests(TwoSamples samples, ITestOutputHelper output)
    {
        _samples = samples;
        _output = output;
    }

    /// <summary>The two samples, and what was done between them.</summary>
    public sealed class TwoSamples
    {
        public ResourceSample? First { get; }
        public ResourceSample? Second { get; }
        public TimeSpan WarmReadTook { get; }
        public TimeSpan WarmReadProcessorTime { get; }
        public string TempVolume { get; } = Path.GetPathRoot(Path.GetTempPath())!;

        public TwoSamples()
        {
            if (!OperatingSystem.IsWindows()) return;

            var probe = new WindowsResourceProbe();
            First = probe.Read();

            SpinEveryProcessor(TimeSpan.FromSeconds(1.5));
            WriteThroughToDisk(64 * 1024 * 1024);

            Second = probe.Read();

            // The cost, measured on warm reads: twenty uncached samples from a probe whose clock moves on
            // two seconds every read, so the cache never answers. Wall time and this process's own
            // processor time, which is what the sampler's budget is written in.
            var fakeNow = DateTime.UtcNow;
            var costProbe = new WindowsResourceProbe(now: () => fakeNow = fakeNow.AddSeconds(2));
            costProbe.Read();
            costProbe.Read();
            using var self = Process.GetCurrentProcess();
            var processorBefore = self.TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++) costProbe.Read();
            WarmReadTook = stopwatch.Elapsed / 20;
            self.Refresh();
            WarmReadProcessorTime = (self.TotalProcessorTime - processorBefore) / 20;
        }

        private static void SpinEveryProcessor(TimeSpan forHowLong)
        {
            var until = Stopwatch.GetTimestamp() + (long)(forHowLong.TotalSeconds * Stopwatch.Frequency);
            var threads = Enumerable.Range(0, Environment.ProcessorCount)
                .Select(_ => new Thread(() => { while (Stopwatch.GetTimestamp() < until) { } }))
                .ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
        }

        private static void WriteThroughToDisk(int bytes)
        {
            var path = Path.Combine(Path.GetTempPath(), $"resource-probe-live-{Guid.NewGuid():N}.bin");
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough);
                var block = new byte[1 << 20];
                Random.Shared.NextBytes(block);
                for (var written = 0; written < bytes; written += block.Length) file.Write(block);
                file.Flush(flushToDisk: true);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [WindowsFact]
    public void FirstSample_RatesSayNoIntervalYet_LevelsAreReadable()
    {
        var first = _samples.First!;

        Assert.Equal(ResourceUnreadableReasons.FirstSample, first.Cpu.UnreadableReason);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, first.Network.UnreadableReason);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, first.Stall.UnreadableReason);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, first.Processes.UnreadableReason);
        Assert.All(first.Disks.Disks, d => Assert.Equal(ResourceUnreadableReasons.FirstSample, d.UnreadableReason));

        Assert.True(first.Memory.CouldRead);
        Assert.True(first.Swap.CouldRead);
        Assert.NotEmpty(first.Volumes);
    }

    [WindowsFact]
    public void Cpu_EveryProcessorSpun_ReadsTheRealCoreCountAndHeavyLoad()
    {
        var cpu = _samples.Second!.Cpu;
        _output.WriteLine(cpu.ToString());

        Assert.True(cpu.CouldRead, cpu.UnreadableReason);
        Assert.Equal(Environment.ProcessorCount, cpu.LogicalCoreCount);
        // Every processor was spun for 1.5 of the roughly 2 seconds between the samples.
        Assert.InRange(cpu.TotalBusyPercent, 50, 100);
        Assert.InRange(cpu.BusiestCorePercent, cpu.TotalBusyPercent, 100);
    }

    [WindowsFact]
    public void MemoryAndPageFile_ReadRealTotals()
    {
        var sample = _samples.Second!;

        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.Memory.TotalBytes > 0);
        Assert.True(sample.Swap.CouldRead, sample.Swap.UnreadableReason);
        Assert.True(sample.Swap.UsedBytes <= sample.Swap.TotalBytes);
    }

    [WindowsFact]
    public void Disks_AtLeastOneNamedDiskOfAKnownKind_AndTheTempDiskSawTheWrite()
    {
        var disks = _samples.Second!.Disks;
        foreach (var disk in disks.Disks) _output.WriteLine(disk.ToString());

        Assert.True(disks.CouldRead, disks.UnreadableReason);
        Assert.NotEmpty(disks.Disks);
        Assert.All(disks.Disks, d => Assert.StartsWith("PhysicalDrive", d.Name));
        Assert.Contains(disks.Disks, d => d.CouldRead && d.Kind != DiskKind.Unknown);

        // The 64 MB written through to the temp folder's volume must show on a disk under that volume.
        var tempVolume = Assert.Single(_samples.Second.Volumes, v => string.Equals(v.MountPoint, _samples.TempVolume, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(tempVolume.PhysicalDiskNames);
        var written = disks.Disks.Where(d => tempVolume.PhysicalDiskNames.Contains(d.Name)).Sum(d => d.WriteBytesPerSecond);
        Assert.True(written > 0, $"no write was seen on {string.Join(", ", tempVolume.PhysicalDiskNames)}");
        Assert.All(disks.Disks.Where(d => d.CouldRead), d => Assert.InRange(d.BusyPercent, 0, 100));
    }

    [WindowsFact]
    public void Volumes_EachHasARealSizeAndSitsOnAListedDisk()
    {
        var sample = _samples.Second!;
        var diskNames = sample.Disks.Disks.Select(d => d.Name).ToHashSet();
        foreach (var volume in sample.Volumes) _output.WriteLine(volume.ToString());

        Assert.NotEmpty(sample.Volumes);
        Assert.All(sample.Volumes, v =>
        {
            Assert.True(v.CouldRead, v.UnreadableReason);
            Assert.True(v.TotalBytes > 0);
            Assert.True(v.FreeBytes <= v.TotalBytes);
            Assert.All(v.PhysicalDiskNames, name => Assert.Contains(name, diskNames));
        });
    }

    [WindowsFact]
    public void Network_IsReadable()
    {
        var network = _samples.Second!.Network;
        _output.WriteLine(network.ToString());

        Assert.True(network.CouldRead, network.UnreadableReason);
        Assert.True(network.ReceiveBytesPerSecond >= 0);
        Assert.True(network.SendBytesPerSecond >= 0);
    }

    [WindowsFact]
    public void Stall_GivesTheWindowsProxiesAndNullForWhatWindowsDoesNotOffer()
    {
        var stall = _samples.Second!.Stall;
        _output.WriteLine(stall.ToString());

        Assert.True(stall.CouldRead, stall.UnreadableReason);
        Assert.Equal(StallSource.WindowsCounters, stall.Source);
        Assert.NotNull(stall.RunQueueLength);
        Assert.NotNull(stall.HardPageFaultsPerSecond);
        Assert.Null(stall.CpuStalledPercent);
        Assert.Null(stall.IoStalledPercent);
    }

    [WindowsFact]
    public void Processes_IncludeThisTestProcessWithItsParentStartTimeAndTheLoadItMade()
    {
        var processes = _samples.Second!.Processes;
        using var self = Process.GetCurrentProcess();

        Assert.True(processes.CouldRead, processes.UnreadableReason);
        var me = Assert.Single(processes.Processes, p => p.Pid == Environment.ProcessId);
        _output.WriteLine(me.ToString());

        Assert.Equal(self.ProcessName, Path.GetFileNameWithoutExtension(me.Name), ignoreCase: true);
        Assert.NotNull(me.ParentPid);
        Assert.Contains(processes.Processes, p => p.Pid == me.ParentPid);
        Assert.Equal(self.StartTime.ToUniversalTime(), me.StartTimeUtc!.Value, TimeSpan.FromMilliseconds(1));
        Assert.True(me.MemoryBytes > 0);
        // This process spun every processor and wrote 64 MB between the two samples.
        Assert.True(me.CpuPercent > 25, $"this process used {me.CpuPercent:F1}% of the machine");
        Assert.True(me.DiskWriteBytesPerSecond > 0);
    }

    [WindowsFact]
    public void Read_TwiceWithinASecond_ReturnsTheCachedSample()
    {
        var probe = new WindowsResourceProbe();

        var first = probe.Read();
        var again = probe.Read();

        Assert.Same(first, again);
    }

    [WindowsFact]
    public void Read_WallClockWentBackwards_TakesAFreshSampleRatherThanReuseOne()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var probe = new WindowsResourceProbe(now: () => now);

        var first = probe.Read();
        now = now.AddMinutes(-5);
        var afterJump = probe.Read();

        Assert.NotSame(first, afterJump);
        Assert.Equal(now, afterJump.TakenAtUtc);
    }

    [WindowsFact]
    public void Read_CostOfOneUncachedSample_IsReported()
    {
        // Not asserted - a time limit here would fail on a busy machine for reasons that are not this
        // code's. Reported so the cost is visible in every live run; the sampler's own budget (under
        // 0.5% of one core) is measured in phase 2.
        _output.WriteLine($"one uncached sample took {_samples.WarmReadTook.TotalMilliseconds:F1} ms of wall time and " +
                          $"{_samples.WarmReadProcessorTime.TotalMilliseconds:F1} ms of processor time (average of twenty warm reads); " +
                          $"at one sample every 5 seconds that is {_samples.WarmReadProcessorTime.TotalMilliseconds / 5000 * 100:F2}% of one core");
        Assert.True(_samples.WarmReadTook > TimeSpan.Zero);
    }
}
