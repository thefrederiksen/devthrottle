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
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A clock the test drives, because the probe measures its intervals off a MONOTONIC clock now and a
    /// wall-clock stamp can no longer set them. Both readings advance together here, which is what these
    /// tests need - they are about the figures over a known interval, not about the clock itself.
    ///
    /// Stepping the wall clock backwards while monotonic time moves forward is the separate question,
    /// and it is pinned once for all three probes in ResourceClockAndRateTests rather than again here.
    /// This lives in this assembly because that one is in another.
    /// </summary>
    private sealed class TestClock(DateTimeOffset wall) : TimeProvider
    {
        private DateTimeOffset _wall = wall;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _wall;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>Moves both readings forward by the same amount, which is the ordinary case.</summary>
        public void Advance(TimeSpan by)
        {
            _wall = _wall.Add(by);
            _timestamp += by.Ticks;
        }

        /// <summary>What a monotonic clock does. Only ever forward, and never the wall clock.</summary>
        public void AdvanceMonotonic(TimeSpan by) => _timestamp += by.Ticks;

        /// <summary>What time synchronisation does to the wall clock, and only to it. May be negative.</summary>
        public void StepWallClock(TimeSpan by) => _wall = _wall.Add(by);
    }

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
        Assert.True(sample.DiskEvidence.CouldRead);
        Assert.All(sample.DiskEvidence.Disks, disk =>
        {
            Assert.False(disk.CouldRead);
            Assert.Equal(ResourceUnreadableReasons.FirstSample, disk.UnreadableReason);
        });
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_TheFirstSample_StillReportsEverythingThatNeedsNoInterval()
    {
        // Memory, swap and the stall signals are figures for right now, so there is no reason to
        // withhold them on the first sample - and the meter would otherwise show "cannot read" against
        // three healthy resources every time the Director starts.
        var sample = new MacOsResourceProbe().Read();

        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.Memory.TotalBytes > 1024UL * 1024 * 1024);
        Assert.True(sample.Swap.CouldRead);
        Assert.True(sample.Stall.CouldRead);
        Assert.True(sample.Processes.CouldRead);
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_TheSecondSample_ReadsRealFiguresForEveryResource()
    {
        var sample = SecondSample();

        Assert.True(sample.Cpu.CouldRead, sample.Cpu.UnreadableReason);
        // BOTH ARE NULLABLE ON THE SHARED TYPE AND NEITHER MAY BE NULL HERE. A platform that cannot
        // offer a core count or a busiest core says null; macOS offers both, so a null would be a
        // failed read dressed as "not offered".
        Assert.Equal(Environment.ProcessorCount, sample.Cpu.LogicalCoreCount);
        Assert.NotNull(sample.Cpu.BusiestCorePercent);
        Assert.InRange(sample.Cpu.TotalBusyPercent, 0.0, 100.0);
        Assert.InRange(sample.Cpu.BusiestCorePercent.Value, 0.0, 100.0);

        // The busiest core cannot be less busy than the machine's average, by definition. This catches
        // the two figures being taken from each other's arithmetic.
        Assert.True(sample.Cpu.BusiestCorePercent.Value >= sample.Cpu.TotalBusyPercent - 0.001,
            $"the machine read {sample.Cpu.TotalBusyPercent:F2}% busy but its busiest core read " +
            $"{sample.Cpu.BusiestCorePercent:F2}%");

        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.Swap.CouldRead);
        Assert.True(sample.Stall.CouldRead);
        Assert.True(sample.Processes.CouldRead);
        Assert.True(sample.DiskEvidence.CouldRead, sample.DiskEvidence.UnreadableReason);
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

        Assert.NotEmpty(sample.DiskEvidence.Disks);
        Assert.All(sample.DiskEvidence.Disks, disk => Assert.Null(disk.BusyPercent));
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_DiskQueueDepthAndTimes_AreRealFiguresInsteadOfTheBusyPercentage()
    {
        // What the probe reports in place of a busy percentage has to be real, or the disk verdict has
        // nothing left to stand on.
        var sample = SecondSample();
        var disk = sample.DiskEvidence.Disks.First(entry => entry.CouldRead);

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

        Assert.NotEmpty(sample.DiskEvidence.Disks);
        Assert.All(sample.DiskEvidence.Disks, disk =>
        {
            Assert.False(string.IsNullOrWhiteSpace(disk.Name));
            Assert.StartsWith("disk", disk.Name, StringComparison.Ordinal);
        });

        // AT LEAST ONE DISK OF A KNOWN KIND, NOT EVERY DISK. The slowness diagnosis puts the words
        // "spinning hard drive" in front of the owner, so a kind must never be guessed - but Unknown
        // IS the honest answer for a disk image or an external enclosure that publishes no medium
        // type, and this used to go red whenever one was attached. The internal disk always reports
        // one, so requiring one such disk keeps the guarantee the mission asked for without failing
        // for what somebody has plugged in.
        Assert.Contains(sample.DiskEvidence.Disks, disk => disk.Kind != DiskKind.Unknown);
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_WhileAFileIsBeingWritten_TheTimePerWriteAndQueueDepthAreReal()
    {
        var clock = new TestClock(Start);
        var probe = new MacOsResourceProbe(clock);
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

        clock.Advance(Interval);

        // THE DISK THE WRITE ACTUALLY WENT TO, not whichever one IOKit happens to list first. The
        // temporary folder is on the volume the machine runs from, so that is the disk whose counters
        // must have moved. Asserting on the first readable disk fails on correct code the moment
        // somebody attaches an external disk or mounts a disk image that IOKit enumerates earlier -
        // the same way the medium-type check used to fail, which was finding 6 of the review of pull
        // request 4035.
        var disk = probe.Read().DiskEvidence.Disks.Single(entry => entry.IsInSystemVolume == true && entry.CouldRead);

        // SIXTEEN MEBIBYTES WERE REALLY WRITTEN, and the narrowed probe reports no byte rate at all -
        // the disk read and write speeds went out with the disk meter. So the evidence that the write
        // reached the driver is that the disk spent time on it and had work in flight.
        //
        // THE SHARP ASSERTION IS THE RATIO, which no interval enters: time spent writing divided by
        // the number of writes. It must land in the range real hardware works in, which is what pins
        // the units of the IOKit time counter as nanoseconds - reading Mach absolute time units as
        // nanoseconds would put it outside by about forty times on Apple Silicon.
        Assert.NotNull(disk.MillisecondsPerWrite);
        Assert.InRange(disk.MillisecondsPerWrite.Value, 0.0001, 1000.0);
        Assert.NotNull(disk.AverageQueueDepth);
        Assert.True(disk.AverageQueueDepth > 0);
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_TheDiskTheMachineRunsFrom_IsNamedAsSuchAndExactlyOneSetIs()
    {
        // THIS IS WHAT IS LEFT OF THE VOLUME READING, AND WHY IT MATTERS. The narrowed mission has no
        // per-volume figures at all, but a slow disk is only an explanation when it is the disk the
        // machine runs from - on devlinux the root volume spanned the spinning sdc, which is why
        // everything the machine did waited on it.
        //
        // It also exercises the upward walk through the IOKit registry. The system volume's own device
        // is an Apple file system volume inside a container synthesized on a partition, so without the
        // walk no disk here would ever be marked.
        var sample = SecondSample();

        Assert.NotEmpty(sample.DiskEvidence.Disks);

        // NOT ONE DISK MAY ANSWER "not known", because this machine's system volume is readable. A null
        // would mean the walk failed, and that is the state in which a disk-bound Mac stops being
        // diagnosable.
        Assert.All(sample.DiskEvidence.Disks, disk => Assert.NotNull(disk.IsInSystemVolume));

        Assert.Contains(sample.DiskEvidence.Disks, disk => disk.IsInSystemVolume == true);
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
        Assert.NotEqual(KernelMemoryPressureLevel.Unknown, stall.MacOsMemoryPressure);

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
            // EVERY process is named, including the ones whose figures this user may not see. That is
            // the whole point: a process that cannot be measured is still the process that has to be
            // named when the machine is slow.
            Assert.False(string.IsNullOrWhiteSpace(process.Name));

            // No figure is ever negative, and an absent figure is null rather than zero.
            if (process.CpuPercent is { } percent) Assert.True(percent >= 0);
            if (process.DiskReadBytesPerSecond is { } read) Assert.True(read >= 0);
            if (process.DiskWriteBytesPerSecond is { } written) Assert.True(written >= 0);
        });
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_Processes_IncludesTheOnesThisUserMayNotMeasure()
    {
        var sample = SecondSample();

        // THE DEFECT THIS EXISTS FOR. The probe used to build its list from the per-process call,
        // which an ordinary user is refused for another user's process, so roughly three hundred of
        // this machine's processes were left out entirely - and the two busiest processes on it, the
        // Spotlight indexer running as root, were among them. A probe whose job is to name the culprit
        // could not see the culprit. The list now comes from the kernel's process table, which names
        // every process, and only the FIGURES come from the call that can be refused.
        // MEMORY IS THE PRECISE TEST FOR "REFUSED", NOT THE PROCESSOR SHARE. Resident memory is a figure
        // for right now and needs no interval, so it is null if and only if the platform refused this
        // process. CpuPercent is null for a second reason - a process with no earlier reading to compare
        // against - so counting nulls there would also count processes that merely started recently, and
        // the test would pass on a machine where the refused ones were being dropped after all.
        var refused = sample.Processes.Processes.Where(process => process.MemoryBytes is null).ToList();

        // WHAT IS ASSERTED DEPENDS ON WHO IS RUNNING, and both branches assert something specific rather
        // than one of them asserting nothing. As an ordinary user the platform refuses a good share of
        // the machine, and some processes MUST come back refused or they are being dropped again. As
        // root nothing is refused, because root may read every process - so the honest assertion there
        // is that everything really is readable. Written this way round because a test that fails when
        // the Director happens to run as root is a test that fails on correct code, and this repository
        // deletes those rather than carrying them.
        if (Environment.IsPrivilegedProcess)
        {
            Assert.Empty(refused);
            Assert.All(sample.Processes.Processes, process => Assert.NotNull(process.MemoryBytes));
            return;
        }

        Assert.True(
            refused.Count > 0,
            "not one process had unreadable figures, which on a Mac running as an ordinary user means " +
            "the refused processes are being dropped again rather than listed");

        // A refused process is a REAL entry: named, with a parent and a start time, and with figures
        // that say "not known" rather than nought.
        Assert.All(refused, process =>
        {
            Assert.False(string.IsNullOrWhiteSpace(process.Name));
            Assert.NotNull(process.StartTimeUtc);
            Assert.Null(process.CpuPercent);
            Assert.Null(process.MemoryBytes);
        });

        // And the ones we CAN measure are still measured.
        Assert.Contains(sample.Processes.Processes, process => process.MemoryBytes is not null);
        Assert.Contains(sample.Processes.Processes, process => process.CpuPercent is not null);
        Assert.True(sample.Processes.CouldRead, sample.Processes.UnreadableReason);
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
        var clock = new TestClock(Start);
        var probe = new MacOsResourceProbe(clock);
        probe.Read();

        var processorBefore = Process.GetCurrentProcess().TotalProcessorTime;
        Assert.True(MachineWork.BurnOnThreads(threads: 2, units: 4) > 0);
        var usedByTheRuntime = Process.GetCurrentProcess().TotalProcessorTime - processorBefore;

        clock.Advance(Interval);
        var sample = probe.Read();

        // A SHARE OF THE WHOLE MACHINE, which is what the shared contract asks for - so the runtime's
        // own processor time is divided by the core count too, or the two sides would be comparing
        // different quantities and the test would demand the probe be wrong by the core count.
        var expectedPercent =
            usedByTheRuntime.TotalSeconds / Interval.TotalSeconds * 100.0 / CoreCountOf(sample);
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
        Assert.NotNull(us.CpuPercent);
        Assert.InRange(us.CpuPercent.Value, expectedPercent * 0.5, expectedPercent * 2.0);

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
    public void Read_WhenTheWallClockStepsBackMidSample_TheIntervalAndEveryRateSurviveIt()
    {
        // THE OTHER HALF OF THE CLOCK RULE, AND IT WAS GENUINELY UNPINNED UNTIL NOW. The unit test in
        // MacOsResourceProbeSafetyTests proves a backwards wall-clock step does not freeze the CACHE,
        // but it asserts only which object comes back - so putting the INTERVAL back to a wall-clock
        // subtraction would leave it passing. The reviewer said so, I agreed, and this is the test that
        // closes it. It has to be a live test: off macOS every reading is unreadable whatever the clock
        // does, so there is no rate to get wrong.
        //
        // The arrangement is the one that cannot happen with a single clock reading: monotonic time
        // moves FORWARD by the interval while the wall clock is stepped an hour BACKWARDS, which is
        // what time synchronisation does to a machine mid-sample.
        var clock = new TestClock(Start);
        var probe = new MacOsResourceProbe(clock);
        probe.Read();

        var processorBefore = Process.GetCurrentProcess().TotalProcessorTime;
        Assert.True(MachineWork.BurnOnThreads(threads: 2, units: 4) > 0);
        var usedByTheRuntime = Process.GetCurrentProcess().TotalProcessorTime - processorBefore;
        Assert.True(usedByTheRuntime > TimeSpan.FromMilliseconds(20),
            "a fixed amount of arithmetic accounted for almost no processor time, so there is nothing to compare");

        clock.AdvanceMonotonic(Interval);
        clock.StepWallClock(TimeSpan.FromHours(-1));
        var sample = probe.Read();

        // WITH A WALL-CLOCK INTERVAL THIS SAMPLE WOULD REPORT "first sample, no interval yet" - the
        // subtraction would be minus an hour, which is not positive - and every process rate would be
        // null. The machine would look unreadable because a clock was corrected.
        Assert.True(sample.Cpu.CouldRead, sample.Cpu.UnreadableReason);
        Assert.True(sample.Cpu.TotalBusyPercent > 0);

        // And the interval really is the monotonic one, not merely positive: the process figure is
        // checked against the runtime's own measurement over the same interval, from a different system
        // call, the way the two-thread test does. An interval of minus an hour, or of an hour, would put
        // this out by orders of magnitude rather than within a factor of two.
        var expectedPercent =
            usedByTheRuntime.TotalSeconds / Interval.TotalSeconds * 100.0 / CoreCountOf(sample);
        var us = sample.Processes.Processes.Single(process => process.Pid == Environment.ProcessId);

        Assert.NotNull(us.CpuPercent);
        Assert.InRange(us.CpuPercent.Value, expectedPercent * 0.5, expectedPercent * 2.0);

        // The sample is still STAMPED with wall-clock time, which is now an hour earlier. That is
        // correct and is the whole division of labour: wall time says when, monotonic time says how
        // long.
        Assert.Equal(Start.Add(TimeSpan.FromHours(-1)).UtcDateTime, sample.TakenAtUtc);
    }

    [MacOsOnlyFact("the probe reads macOS system calls")]
    [Trait("Category", "Resources")]
    public void Read_TwiceInsideTheCacheWindow_IsTheSameSample()
    {
        // Several callers ask within the same moment - the meter, the banner, the detail screen - and
        // the answer cannot meaningfully change between them. Reading the machine once for all of them
        // is the difference between one pass over 425 processes and twenty.
        var clock = new TestClock(Start);
        var probe = new MacOsResourceProbe(clock);

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
        var clock = new TestClock(Start);
        var probe = new MacOsResourceProbe(clock);

        var first = probe.Read();
        clock.Advance(MacOsResourceProbe.CacheFor + TimeSpan.FromMilliseconds(1));
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
        var clock = new TestClock(Start);
        var probe = new MacOsResourceProbe(clock);
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
        var clock = new TestClock(Start);
        var probe = new MacOsResourceProbe(clock);

        probe.Read();
        Assert.True(MachineWork.Burn() > 0);
        clock.Advance(Interval);

        return probe.Read();
    }
    /// <summary>
    /// The machine's logical core count, asserted to be present first. It is nullable on the shared
    /// type because a platform may not offer it; macOS does, and dividing by a null would otherwise
    /// make every expected figure null and the comparison vacuous.
    /// </summary>
    private static int CoreCountOf(ResourceSample sample)
    {
        Assert.NotNull(sample.Cpu.LogicalCoreCount);
        Assert.True(sample.Cpu.LogicalCoreCount > 0);
        return sample.Cpu.LogicalCoreCount.Value;
    }


}
