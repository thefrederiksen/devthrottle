using System.Diagnostics;
using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE WHOLE macOS PROBE AGAINST THE REAL MACHINE.
///
/// <see cref="MacOsPlatformReaderLiveTests"/> proves each system call; this proves the probe built on
/// top of them - the arithmetic between two readings, the first sample that has no interval, the
/// cache, and that a resource this platform does not offer comes back null rather than zero.
///
/// NOTHING HERE ASSERTS ZERO OR MERELY THAT A CALL DID NOT THROW. A probe that reported zero for
/// everything would satisfy a suite written that way while telling the owner his machine was idle.
/// Every assertion is for a value that can only be there if the read actually worked.
/// </summary>
public class MacOsResourceProbeLiveTests
{
    /// <summary>
    /// A fixed instant to start every probe's clock from, so no test here reads the real one.
    ///
    /// WHY THE CLOCK IS INJECTED RATHER THAN SLEPT THROUGH. A gate test may not sleep or read the
    /// clock - the owner's ruling of 11 October 2026, enforced by
    /// <c>GateTestsDoNotDependOnTheMachineTests</c> - because a test that does is measuring the
    /// machine's scheduler and speed rather than deciding on the code.
    ///
    /// WHAT THAT COSTS, STATED PLAINLY. Between two samples these tests do REAL work and real disk
    /// writes, so the counters really move; but the INTERVAL the probe divides by is the one handed to
    /// it here, not the wall-clock time that really passed. So:
    ///   * Figures that are RATIOS of counters are exactly right, because no interval enters them -
    ///     the processor busy percentage (busy ticks over total ticks) and the milliseconds per read
    ///     and per write (time over operations). These are where the sharp assertions are.
    ///   * Figures that are PER SECOND are scaled by however far the real elapsed time differs from
    ///     the handed interval. Those are asserted only as "something moved", never as a quantity.
    ///   * The per-process processor percentage is a rate, so it is cross-checked against the
    ///     runtime's own TotalProcessorTime over the same interval rather than against a number.
    /// </summary>
    private static readonly DateTime Start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The interval handed to the probe between two samples. See <see cref="Start"/>.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    // =============================================================================================
    // The first sample, and the second
    // =============================================================================================

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_TheFirstSample_SaysTheRatesHaveNoIntervalYetRatherThanReportingZero()
    {
        // THIS IS THE WHOLE "NEVER ZERO" RULE AT ITS MOST DANGEROUS POINT. On the very first sample
        // there is genuinely nothing to compare against, and a probe that answered "0% busy" would be
        // indistinguishable from a completely idle machine - on the Director's first meter draw, every
        // time it starts.
        var sample = new MacOsResourceProbe().Read();

        Assert.False(sample.Cpu.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, sample.Cpu.UnreadableReason);
        Assert.False(sample.Network.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, sample.Network.UnreadableReason);

        Assert.True(sample.Disks.CouldRead);
        Assert.All(sample.Disks.Disks, disk =>
        {
            Assert.False(disk.CouldRead);
            Assert.Equal(ResourceUnreadableReasons.FirstSample, disk.UnreadableReason);
        });
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_TheFirstSample_StillReportsEverythingThatNeedsNoInterval()
    {
        // Memory, swap, the volumes and the stall signals are figures for right now, so there is no
        // reason to withhold them on the first sample - and the meter would otherwise show "cannot
        // read" against four healthy resources every time the Director starts.
        var sample = new MacOsResourceProbe().Read();

        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.Memory.TotalBytes > 1024UL * 1024 * 1024);
        Assert.True(sample.Swap.CouldRead);
        Assert.True(sample.Stall.CouldRead);
        Assert.True(sample.Processes.CouldRead);
        Assert.NotEmpty(sample.Volumes);
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_TheSecondSample_ReadsRealFiguresForEveryResource()
    {
        var sample = SecondSample();

        Assert.True(sample.Cpu.CouldRead, sample.Cpu.UnreadableReason);
        Assert.Equal(Environment.ProcessorCount, sample.Cpu.LogicalCoreCount);
        Assert.InRange(sample.Cpu.TotalBusyPercent, 0.0, 100.0);
        Assert.InRange(sample.Cpu.BusiestCorePercent, 0.0, 100.0);

        // The busiest core cannot be less busy than the machine's average, by definition. This catches
        // the two figures being taken from each other's arithmetic.
        Assert.True(sample.Cpu.BusiestCorePercent >= sample.Cpu.TotalBusyPercent - 0.001,
            $"the machine read {sample.Cpu.TotalBusyPercent:F2}% busy but its busiest core read " +
            $"{sample.Cpu.BusiestCorePercent:F2}%");

        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.Swap.CouldRead);
        Assert.True(sample.Disks.CouldRead);
        Assert.True(sample.Network.CouldRead, sample.Network.UnreadableReason);
        Assert.True(sample.Stall.CouldRead);
        Assert.True(sample.Processes.CouldRead);
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_TheSecondSample_MemoryAgreesWithTheMemoryProbe()
    {
        // The probe does not measure memory twice; it uses the probe that already exists and is
        // already tested. This checks it really is that figure and not a second, divergent one.
        var sample = SecondSample();
        var direct = MachineMemoryProbe.Shared.Read();

        Assert.Equal(direct.TotalBytes, sample.Memory.TotalBytes);
    }

    // =============================================================================================
    // The disk, and the figure this platform does not offer
    // =============================================================================================

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_DiskBusyPercent_IsNullBecauseThisPlatformDoesNotPublishOne()
    {
        // The phase's named risk, settled. IOKit's disk-time counters measure concurrency-weighted
        // service time, not a share of the interval: measured on this hardware they read 234 percent
        // while one program copied a file on an entirely healthy machine, and 2454 percent under four
        // contended writers. macOS's own iostat has no busy column either. So the honest answer is
        // null - the mission's rule is that a figure the platform does not offer is null, never
        // invented and never zero, and a zero here would read as a disk doing no work at all.
        // The evidence is in missions/resource-monitor/phase-1/macos-disk-busy-risk.txt.
        var sample = SecondSample();

        Assert.NotEmpty(sample.Disks.Disks);
        Assert.All(sample.Disks.Disks, disk => Assert.Null(disk.BusyPercent));
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_DiskQueueDepthAndTimes_AreRealFiguresInsteadOfTheBusyPercentage()
    {
        // What the probe reports in place of a busy percentage has to be real, or the disk verdict has
        // nothing left to stand on.
        var sample = SecondSample();
        var disk = sample.Disks.Disks.First(entry => entry.CouldRead);

        Assert.NotNull(disk.AverageQueueDepth);
        Assert.True(disk.AverageQueueDepth >= 0);

        // The machine is running a test suite, so it is reading from disk. A time per read that is
        // present must be in the range real hardware works in; the units being wrong would put it
        // outside by about forty times on Apple Silicon.
        if (disk.MillisecondsPerRead is { } perRead) Assert.InRange(perRead, 0.0001, 1000.0);
        if (disk.MillisecondsPerWrite is { } perWrite) Assert.InRange(perWrite, 0.0001, 1000.0);
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_EveryPhysicalDisk_HasANameAndAKindThatIsNotGuessed()
    {
        var sample = SecondSample();

        Assert.NotEmpty(sample.Disks.Disks);
        Assert.All(sample.Disks.Disks, disk =>
        {
            Assert.False(string.IsNullOrWhiteSpace(disk.Name));
            Assert.StartsWith("disk", disk.Name, StringComparison.Ordinal);

            // This machine's disk is solid state. The point of asserting it is not known rather than
            // merely "some value" is that the slowness diagnosis puts the words "spinning hard drive"
            // in front of the owner, and saying that about a disk nobody identified is worse than
            // saying nothing.
            Assert.NotEqual(DiskKind.Unknown, disk.Kind);
        });
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_WhileAFileIsBeingWritten_TheWriteRateAndTimePerWriteAreReal()
    {
        var now = Start;
        var probe = new MacOsResourceProbe(() => now);
        probe.Read();

        // A fixed amount of real writing, flushed to the disk each time so it reaches the driver
        // rather than sitting in the page cache where the disk's own counters would never see it.
        var path = Path.Combine(Path.GetTempPath(), $"resource-probe-live-{Guid.NewGuid():N}.bin");
        try
        {
            var block = new byte[4 * 1024 * 1024];
            Random.Shared.NextBytes(block);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 20, FileOptions.WriteThrough);
            for (var written = 0; written < 4; written++)
            {
                stream.Write(block);
                stream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            File.Delete(path);
        }

        now = now.Add(Interval);
        var disk = probe.Read().Disks.Disks.First(entry => entry.CouldRead);

        // SIXTEEN MEBIBYTES WERE REALLY WRITTEN, so the counter must have moved. Asserted as movement
        // rather than as a rate, because the per-second figure is scaled by the handed interval.
        Assert.True(disk.WriteBytesPerSecond > 0,
            "sixteen mebibytes were written and flushed to the disk, and the written-bytes counter did not move");

        // THE SHARP ASSERTION IS THE RATIO, which no interval enters: time spent writing divided by
        // the number of writes. It must land in the range real hardware works in, which is what pins
        // the units of the IOKit time counter as nanoseconds.
        Assert.NotNull(disk.MillisecondsPerWrite);
        Assert.InRange(disk.MillisecondsPerWrite.Value, 0.0001, 1000.0);
        Assert.NotNull(disk.AverageQueueDepth);
        Assert.True(disk.AverageQueueDepth > 0);
    }

    // =============================================================================================
    // Volumes
    // =============================================================================================

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_TheRootVolume_IsRealAndNamesThePhysicalDiskItIsStoredOn()
    {
        var sample = SecondSample();
        var root = sample.Volumes.Single(volume => volume.MountPoint == "/");

        Assert.True(root.CouldRead);
        Assert.True(root.TotalBytes > 1024UL * 1024 * 1024);
        Assert.True(root.FreeBytes > 0);
        Assert.True(root.FreeBytes <= root.TotalBytes);

        // The whole point of the upward walk through the IOKit registry. The root volume's own device
        // is an Apple file system volume inside a container synthesized on a partition, so without the
        // walk this list would either be empty or name a device with no statistics at all.
        Assert.NotEmpty(root.PhysicalDiskNames);

        var physical = sample.Disks.Disks.Select(disk => disk.Name).ToList();
        Assert.All(root.PhysicalDiskNames, name => Assert.Contains(name, physical));
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_AVolumeWithNoBackingDevice_NamesNoPhysicalDiskRatherThanGuessing()
    {
        // /dev is one of the synthetic file systems macOS mounts. Attributing its activity to a
        // physical disk would put traffic on hardware that has nothing to do with it.
        var sample = SecondSample();
        var synthetic = sample.Volumes.Single(volume => volume.MountPoint == "/dev");

        Assert.Empty(synthetic.PhysicalDiskNames);
    }

    // =============================================================================================
    // Stall signals
    // =============================================================================================

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_StallSignals_NameThisPlatformAndLeaveWhatItCannotOfferNull()
    {
        var sample = SecondSample();
        var stall = sample.Stall;

        Assert.True(stall.CouldRead);
        Assert.Equal(StallSource.MacOsSignals, stall.Source);

        // WHAT THIS PLATFORM HAS. The run queue and the kernel's own memory-pressure level.
        Assert.NotNull(stall.RunQueueLength);
        Assert.InRange(stall.RunQueueLength.Value, 0.0, 1000.0);
        Assert.NotNull(stall.MacOsMemoryPressureLevel);
        Assert.NotEqual(
            MacOsResourceParsing.KernelPressureLevel.Unknown,
            MacOsResourceParsing.ReadKernelPressureLevel(stall.MacOsMemoryPressureLevel.Value));

        // WHAT IT HAS NOT, AND WHICH MUST THEREFORE BE NULL RATHER THAN ZERO. Linux publishes the
        // share of time programs spent frozen waiting on each resource; macOS publishes nothing
        // equivalent. Zero would read as "nothing was ever stalled", which is the opposite of "we
        // cannot tell" - and it is the figure the slowness diagnosis would then rule on.
        Assert.Null(stall.CpuStalledPercent);
        Assert.Null(stall.MemoryStalledPercent);
        Assert.Null(stall.IoStalledPercent);
        Assert.Null(stall.HardPageFaultsPerSecond);
        Assert.Null(stall.ProcessesWaitingOnDisk);
    }

    // =============================================================================================
    // Processes
    // =============================================================================================

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_Processes_IncludeThisTestProcessWithItsParentAndItsMemory()
    {
        var sample = SecondSample();

        Assert.True(sample.Processes.CouldRead);
        Assert.True(sample.Processes.Processes.Count > 50,
            $"only {sample.Processes.Processes.Count} processes were read, which no running Mac has");

        var us = sample.Processes.Processes.Single(process => process.Pid == Environment.ProcessId);

        Assert.NotNull(us.ParentPid);
        Assert.True(us.ParentPid > 0);
        Assert.False(string.IsNullOrWhiteSpace(us.Name));
        Assert.True(us.MemoryBytes > 1024 * 1024);
        Assert.NotNull(us.StartTimeUtc);
        Assert.Equal(DateTimeKind.Utc, us.StartTimeUtc.Value.Kind);

        // A wide fixed window, for the reason given in MacOsPlatformReaderLiveTests: what this catches
        // is the epoch and the field offset, and both land far outside any plausible date. Bounding it
        // by the clock would measure the clock, which a gate test may not do.
        Assert.InRange(
            us.StartTimeUtc.Value,
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_Processes_EveryOneHasANameAndNoProcessorTimeIsNegative()
    {
        var sample = SecondSample();

        Assert.All(sample.Processes.Processes, process =>
        {
            Assert.False(string.IsNullOrWhiteSpace(process.Name));
            Assert.True(process.CpuPercent >= 0);
            if (process.DiskReadBytesPerSecond is { } read) Assert.True(read >= 0);
            if (process.DiskWriteBytesPerSecond is { } written) Assert.True(written >= 0);
        });
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_WhileTwoThreadsBurnProcessor_TheProbeAgreesWithTheRuntimesOwnFigure()
    {
        // THE TEST THAT FOUND THE TIMEBASE BUG, and it finds it without depending on this machine's
        // speed. The probe's per-process percentage is checked against the figure the .NET runtime
        // reports for the SAME process over the SAME interval, from a different system call. Two
        // independent measurements of one quantity either agree or one of them is wrong - and reading
        // Mach absolute time as nanoseconds made them disagree by a factor of about forty-two.
        //
        // A fixed amount of work costs very nearly a fixed amount of PROCESSOR time whatever else the
        // machine is doing, so neither side of this comparison moves with the load.
        var now = Start;
        var probe = new MacOsResourceProbe(() => now);
        probe.Read();

        var processorBefore = Process.GetCurrentProcess().TotalProcessorTime;
        Assert.True(MachineWork.BurnOnThreads(threads: 2, units: 4) > 0);
        var usedByTheRuntime = Process.GetCurrentProcess().TotalProcessorTime - processorBefore;

        now = now.Add(Interval);
        var sample = probe.Read();

        var expectedPercent = usedByTheRuntime.TotalSeconds / Interval.TotalSeconds * 100.0;
        var us = sample.Processes.Processes.Single(process => process.Pid == Environment.ProcessId);

        // A FLOOR ON THE WORK DONE, NOT ON THE MACHINE'S SPEED. Eight units of arithmetic across two
        // threads costs hundreds of milliseconds of processor time on anything that can run .NET, and
        // this only checks that the two sides are not both agreeing on nothing. It is a floor on a
        // fixed amount of work, which does not move with how busy or how fast the machine is.
        Assert.True(usedByTheRuntime > TimeSpan.FromMilliseconds(20),
            $"a fixed eight units of arithmetic accounted for only {usedByTheRuntime.TotalMilliseconds:F0} " +
            "milliseconds of processor time, so there is nothing here to compare");

        // THE ASSERTION THAT MATTERS: the probe's figure against the runtime's, for the same process
        // over the same interval, from a different system call. Allowed to differ by up to a factor of
        // two, because the two read the counter at slightly different moments and neither is a
        // reference clock. The bug this exists for was out by a factor of FORTY-TWO, so a factor of
        // two catches it twenty times over while never failing for timing.
        Assert.InRange(us.CpuPercent, expectedPercent * 0.5, expectedPercent * 2.0);

        // The machine's own figure is a ratio of tick counters, so no interval enters it at all.
        Assert.True(sample.Cpu.CouldRead, sample.Cpu.UnreadableReason);
        Assert.True(sample.Cpu.TotalBusyPercent > 0);
        Assert.True(sample.Cpu.BusiestCorePercent > 0);
    }

    // =============================================================================================
    // The cache
    // =============================================================================================

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_TwiceInsideTheCacheWindow_IsTheSameSample()
    {
        // Several callers ask within the same moment - the meter, the banner, the detail screen - and
        // the answer cannot meaningfully change between them. Reading the machine once for all of them
        // is the difference between one pass over 425 processes and twenty.
        var now = Start;
        var probe = new MacOsResourceProbe(() => now);

        var first = probe.Read();
        var second = probe.Read();

        Assert.Same(first, second);
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_OnceTheCacheHasAged_IsAFreshSample()
    {
        // The clock is moved rather than slept through, so this stays fast and still proves the cache
        // is bounded by time rather than never expiring.
        var now = Start;
        var probe = new MacOsResourceProbe(() => now);

        var first = probe.Read();
        now = now.Add(MacOsResourceProbe.CacheFor).AddMilliseconds(1);
        var second = probe.Read();

        Assert.NotSame(first, second);
        Assert.True(second.TakenAtUtc > first.TakenAtUtc);
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_FromManyThreadsAtOnceOnAFreshProbe_TakesExactlyOnePassOverTheMachine()
    {
        // THE REGRESSION TEST FOR A RACE THAT CORRUPTED EVERY RATE. The cache used to be checked under
        // one lock and filled under another, so two callers arriving together both missed it and both
        // made a full pass - and the second pass's interval was the fraction of a millisecond since
        // the first, which is the number every rate is divided by. The meter, the banner and the
        // detail screen all ask at once, so this is the ordinary case and not an unlucky one.
        var now = Start;
        var probe = new MacOsResourceProbe(() => now);
        var samples = new ResourceSample[8];

        Parallel.For(0, samples.Length, index => samples[index] = probe.Read());

        Assert.All(samples, sample => Assert.Same(samples[0], sample));
    }

    // =============================================================================================
    // What it costs - deliberately NOT asserted here
    // =============================================================================================
    //
    // There was a test here asserting that one sample costs under twenty-five milliseconds of
    // processor time, which at the mission's five second interval is the half a percent of one core
    // the goal allows. It has been removed on purpose.
    //
    // It decided pass or fail on HOW FAST THIS MACHINE IS, which is the precise thing the owner ruled
    // out on 11 October 2026: a test must fail only for a code defect. On a machine running eighteen
    // sessions it would eventually have failed for being busy, and a gate failure that means "the
    // machine was loaded" is worse than no test, because it trains everyone to re-run the gate.
    //
    // The measurement still exists and is still reported - it belongs in the proof, where a number is
    // read by a person rather than compared against a threshold by a machine. It is in
    // missions/resource-monitor/phase-1/macos-live-reading.txt, measured at 1.3 milliseconds a sample
    // reading all 420 processes, which is 0.027 percent of one core against the budget of 0.5.

    // =============================================================================================
    // Helpers
    // =============================================================================================

    /// <summary>
    /// A sample with a previous reading behind it, which is what every rate needs. The first read is
    /// thrown away on purpose: it is the one that has nothing to compare against. Real work happens in
    /// between so the counters move; the interval is handed to the probe rather than waited out.
    /// </summary>
    private static ResourceSample SecondSample()
    {
        var now = Start;
        var probe = new MacOsResourceProbe(() => now);

        probe.Read();
        Assert.True(MachineWork.Burn() > 0);
        now = now.Add(Interval);

        return probe.Read();
    }
}
