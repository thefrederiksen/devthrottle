using System.Diagnostics;
using CcDirector.Core.Machine;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// <see cref="WindowsResourceProbe"/> against the REAL operating system of the machine running it. The
/// parsers are proved from captured bytes in the unit tests; this proves the calls are made, the handles
/// open without administrator rights, and the figures are real on a real machine.
///
/// Two samples are taken with real work done between them - every processor spun, and 64 MB written
/// through to disk - so the rates are shown to MOVE with the load, not only to be present. The samples
/// are taken once and shared by every test here.
///
/// Lives in the live project and skips unless asked for: it sleeps, spins and writes to a real disk,
/// which no gate test may do.
/// </summary>
[Trait("Category", "Resources")]
public class WindowsResourceProbeLiveTests : IClassFixture<WindowsResourceProbeLiveTests.TwoSamples>
{
    private const string Reads = "processor, memory, page file, disks, stalls and processes, under load";

    private readonly TwoSamples _samples;
    private readonly ITestOutputHelper _output;

    public WindowsResourceProbeLiveTests(TwoSamples samples, ITestOutputHelper output)
    {
        _samples = samples;
        _output = output;
    }

    /// <summary>The two samples, and what was done between them. Takes nothing unless the tests will run.</summary>
    public sealed class TwoSamples
    {
        public ResourceSample? First { get; }
        public ResourceSample? Second { get; }
        public TimeSpan WarmReadTook { get; }
        public TimeSpan WarmReadProcessorTime { get; }

        public TwoSamples()
        {
            if (WindowsLiveMachineFactAttribute.SkipReason(Reads, LiveMachineFactAttribute.Enabled, OperatingSystem.IsWindows(), Environment.ProcessorCount) is not null)
                return;

            var probe = new WindowsResourceProbe();
            First = probe.Read();

            SpinEveryProcessor(TimeSpan.FromSeconds(1.5));
            WriteThroughToDisk(64 * 1024 * 1024);

            Second = probe.Read();

            // The cost, measured on warm reads: twenty uncached samples from a probe whose monotonic clock
            // moves on two seconds every read, so the cache never answers. Wall time and this process's
            // own processor time, which is what the sampler's budget is written in.
            var costProbe = new WindowsResourceProbe(new SteppingClock(TimeSpan.FromSeconds(2)));
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

    /// <summary>A clock whose monotonic time moves on a fixed step at every read, so no read is served from the cache.</summary>
    private sealed class SteppingClock(TimeSpan step) : TimeProvider
    {
        private long _timestamp;
        public override long GetTimestamp() => _timestamp += step.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [WindowsLiveMachineFact(Reads)]
    public void FirstSample_RatesSayNoIntervalYet_LevelsAndTheProcessListAreReadable()
    {
        var first = _samples.First!;

        Assert.Equal(ResourceUnreadableReasons.FirstSample, first.Cpu.UnreadableReason);
        Assert.Equal(Environment.ProcessorCount, first.Cpu.LogicalCoreCount);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, first.Stall.UnreadableReason);
        Assert.All(first.DiskEvidence.Disks, d => Assert.Equal(ResourceUnreadableReasons.FirstSample, d.UnreadableReason));

        Assert.True(first.Memory.CouldRead);
        Assert.True(first.Swap.CouldRead);
        Assert.True(first.Processes.CouldRead, first.Processes.UnreadableReason);
        Assert.Contains(first.Processes.Processes, p => p.Pid == Environment.ProcessId && p.CpuPercent is null && p.MemoryBytes > 0);
    }

    [WindowsLiveMachineFact(Reads)]
    public void Cpu_EveryProcessorSpun_ReadsTheRealCoreCountAndHeavyLoad()
    {
        var cpu = _samples.Second!.Cpu;
        _output.WriteLine(cpu.ToString());

        Assert.True(cpu.CouldRead, cpu.UnreadableReason);
        Assert.Equal(Environment.ProcessorCount, cpu.LogicalCoreCount);
        // Every processor was spun for 1.5 of the roughly 2 seconds between the samples.
        Assert.InRange(cpu.TotalBusyPercent, 50, 100);
        Assert.InRange(cpu.BusiestCorePercent!.Value, cpu.TotalBusyPercent, 100);
    }

    [WindowsLiveMachineFact(Reads)]
    public void MemoryAndPageFile_ReadRealTotals()
    {
        var sample = _samples.Second!;
        _output.WriteLine($"{sample.Memory}; {sample.Swap}");

        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.Memory.TotalBytes > 0);
        Assert.True(sample.Swap.CouldRead, sample.Swap.UnreadableReason);
        Assert.True(sample.Swap.UsedBytes <= sample.Swap.TotalBytes);
    }

    [WindowsLiveMachineFact(Reads)]
    public void DiskEvidence_NamedDisksOfAKnownKind_OneHoldsTheSystemVolume_AndADiskTimedTheWrite()
    {
        var disks = _samples.Second!.DiskEvidence;
        foreach (var disk in disks.Disks) _output.WriteLine(disk.ToString());

        Assert.True(disks.CouldRead, disks.UnreadableReason);
        Assert.NotEmpty(disks.Disks);
        Assert.All(disks.Disks, d => Assert.StartsWith("PhysicalDrive", d.Name));
        Assert.Contains(disks.Disks, d => d.CouldRead && d.Kind != DiskKind.Unknown);
        Assert.Contains(disks.Disks, d => d.IsInSystemVolume == true);

        // 64 MB was written through to disk between the samples, so at least one disk timed a write.
        Assert.Contains(disks.Disks, d => d.CouldRead && d.MillisecondsPerWrite is not null);
        Assert.All(disks.Disks.Where(d => d.CouldRead && d.BusyPercent is not null), d => Assert.InRange(d.BusyPercent!.Value, 0, 100));
    }

    [WindowsLiveMachineFact(Reads)]
    public void Stall_GivesTheWindowsProxiesAndNullForWhatWindowsDoesNotOffer()
    {
        var stall = _samples.Second!.Stall;
        _output.WriteLine(stall.ToString());

        Assert.True(stall.CouldRead, stall.UnreadableReason);
        Assert.Equal(StallSource.WindowsCounters, stall.Source);
        Assert.NotNull(stall.RunQueueLength);
        Assert.NotNull(stall.HardPageFaultsPerSecond);
        Assert.Null(stall.CpuStalledPercent);
        Assert.Null(stall.MemoryStalledPercent);
        Assert.Null(stall.IoStalledPercent);
    }

    [WindowsLiveMachineFact(Reads)]
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

    [WindowsLiveMachineFact(Reads)]
    public void Read_TwiceWithinASecond_ReturnsTheCachedSample()
    {
        var probe = new WindowsResourceProbe();

        var first = probe.Read();
        var again = probe.Read();

        Assert.Same(first, again);
    }

    [WindowsLiveMachineFact(Reads)]
    public void Read_CostOfOneUncachedSample_IsReported()
    {
        // Not asserted - a time limit here would fail on a busy machine for reasons that are not this
        // code's. Reported so the cost is visible in every live run; the sampler's own budget is measured
        // in phase 2.
        _output.WriteLine($"one uncached sample took {_samples.WarmReadTook.TotalMilliseconds:F1} ms of wall time and " +
                          $"{_samples.WarmReadProcessorTime.TotalMilliseconds:F1} ms of processor time (average of twenty warm reads); " +
                          $"at one sample every 5 seconds that is {_samples.WarmReadProcessorTime.TotalMilliseconds / 5000 * 100:F2}% of one core");
        Assert.True(_samples.WarmReadTook > TimeSpan.Zero);
    }
}

/// <summary>
/// The shared contract every platform probe is judged against, run against the Windows probe on this
/// machine. Every test is inherited from <see cref="ResourceProbeContractLiveTests"/>.
/// </summary>
[Trait("Category", "Resources")]
[LiveMachinePlatform(LiveMachinePlatform.Windows)]
public class WindowsResourceProbeContractLiveTests : ResourceProbeContractLiveTests
{
    protected override string ProbeName => "the Windows probe";

    protected override TimeSpan ProbeCacheFor => WindowsResourceProbe.CacheFor;

    protected override IResourceProbe CreateProbe()
    {
        // The inherited tests are gated on the opt-in variable alone, not on the platform, so a live run
        // on Linux or macOS reaches this. Fail it with the reason rather than judge the Windows probe on a
        // machine it cannot read.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"the Windows probe's contract can only be judged on Windows; this is {Environment.OSVersion}");
        return new WindowsResourceProbe();
    }
}
