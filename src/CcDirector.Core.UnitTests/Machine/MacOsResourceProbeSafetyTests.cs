using System.Runtime.InteropServices;
using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE macOS PROBE MUST BE HARMLESS ON THE PLATFORMS IT CANNOT READ.
///
/// These tests run EVERYWHERE on purpose - they are not gated to macOS - because the failure they
/// exist for only happens off it. The probe's whole contract is that it never throws and that a
/// resource it cannot read comes back with a stated reason; a Windows or Linux build machine is where
/// a breach of that gets noticed, and only if something actually looks.
///
/// They also guard the narrowing itself. The sample no longer carries a volume list or a network
/// reading, and an assertion that one of them was non-empty is exactly the kind of leftover that keeps
/// a dropped reading alive in the contract after the code behind it has gone.
///
/// The regression that prompted them: the Mach timebase was read in a FIELD INITIALISER, so touching
/// the class called a system call that does not exist off macOS. Because <see cref="MacOsResourceProbe.Shared"/>
/// is static, that threw during class initialisation - before the probe's own platform check could
/// answer "this is the macOS probe and the machine is not a Mac". Nothing on a Mac could ever have
/// caught it.
/// </summary>
public class MacOsResourceProbeSafetyTests
{
    [Fact]
    [Trait("Category", "Resources")]
    public void Constructing_OnAnyPlatform_DoesNotThrow()
    {
        var probe = new MacOsResourceProbe();

        Assert.NotNull(probe);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TheSharedProbe_OnAnyPlatform_CanBeTouchedWithoutThrowing()
    {
        // Reading the static field is what runs class initialisation, which is where the original
        // defect threw. A TypeInitializationException here takes down whatever first touched the
        // class, which in the Director is the sampler on start-up.
        Assert.NotNull(MacOsResourceProbe.Shared);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void Read_OnAnyPlatform_ReturnsASampleRatherThanThrowing()
    {
        var sample = new MacOsResourceProbe().Read();

        Assert.NotNull(sample);
        Assert.NotEqual(default, sample.TakenAtUtc);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void Read_WhenTimeSynchronisationStepsTheWallClockBackwards_TheCacheIsUnaffected()
    {
        // THE CLOCK RULE, PINNED FOR THIS PROBE. Wall-clock time stamps a sample; only a monotonic clock
        // measures an interval. This probe used to age its cache by comparing two wall-clock stamps, so
        // a step backwards - which is what time synchronisation does, routinely - made the cached sample
        // look as though it came from the future, and the probe went on serving that one stale reading
        // until the wall clock caught up. On a five-second meter that is the Director showing a machine
        // that is filling up as though nothing were happening.
        //
        // Runs on every platform, not just macOS: the cache is platform-independent, and the figures
        // inside the sample do not matter here - only which object comes back.
        var clock = new SteppingClock(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
        var probe = new MacOsResourceProbe(clock);

        var first = probe.Read();

        // An hour backwards, with no monotonic time passed. The cache is genuinely still fresh, so the
        // same sample must come back - and must not be treated as being from the future.
        clock.StepWallClock(TimeSpan.FromHours(-1));
        Assert.Same(first, probe.Read());

        // Now real time passes, measured monotonically. The cache has expired and a fresh pass is owed,
        // which against the old code would not have happened until the wall clock caught up.
        clock.AdvanceMonotonic(MacOsResourceProbe.CacheFor + TimeSpan.FromMilliseconds(1));
        Assert.NotSame(first, probe.Read());
    }

    /// <summary>
    /// A clock whose two readings move INDEPENDENTLY, which is the whole point: a fake clock that
    /// derives both from one internal time steps the monotonic reading along with the wall clock, and
    /// then the defect being tested for cannot be reproduced at all.
    /// </summary>
    private sealed class SteppingClock(DateTimeOffset wall) : TimeProvider
    {
        private DateTimeOffset _wall = wall;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _wall;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>What time synchronisation does. May be negative.</summary>
        public void StepWallClock(TimeSpan by) => _wall = _wall.Add(by);

        /// <summary>What a monotonic clock does. Only ever forward.</summary>
        public void AdvanceMonotonic(TimeSpan by) => _timestamp += by.Ticks;
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void Read_OnAPlatformThisProbeCannotRead_SaysSoForEveryResourceAndNeverReportsZero()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // On a Mac this probe reads the machine, and that is covered by the live tests. The
            // assertion below is about the other platforms, so here there is nothing to check beyond
            // the sample existing - which the test above already does. Asserted rather than skipped
            // so the test is not silently doing nothing on the platform it runs on most.
            Assert.True(new MacOsResourceProbe().Read().Memory.CouldRead);
            return;
        }

        var sample = new MacOsResourceProbe().Read();

        // EVERY READING UNREADABLE, EACH WITH A REASON. A zero anywhere here would tell the Director
        // that a Windows machine had an idle processor, no swap in use and a disk doing no work.
        Assert.False(sample.Cpu.CouldRead);
        Assert.False(sample.Memory.CouldRead);
        Assert.False(sample.Swap.CouldRead);
        Assert.False(sample.Stall.CouldRead);
        Assert.False(sample.Processes.CouldRead);
        Assert.False(sample.DiskEvidence.CouldRead);

        foreach (var reason in new[]
                 {
                     sample.Cpu.UnreadableReason, sample.Memory.UnreadableReason,
                     sample.Swap.UnreadableReason, sample.Stall.UnreadableReason,
                     sample.Processes.UnreadableReason, sample.DiskEvidence.UnreadableReason,
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        // And the reason names the actual problem, so a log line from it is worth reading.
        Assert.Contains("macOS probe", sample.Cpu.UnreadableReason, StringComparison.Ordinal);

        // Nothing was invented behind the unreadable flags.
        Assert.Empty(sample.DiskEvidence.Disks);
        Assert.Empty(sample.Processes.Processes);
        // NULL, not zero - and the test's own name says why. A figure the platform does not offer is
        // null; zero would be a count, and "no processors" is not a thing a machine can have. Off a
        // Mac this probe returns CpuReading.Unreadable(reason) with no core count at all, so this
        // asserted a value the contract forbids it to invent. It passed on a Mac, where the probe
        // reads a real count, and failed on every Linux and Windows machine including the scheduled
        // full run on main.
        Assert.Null(sample.Cpu.LogicalCoreCount);
        Assert.Equal(StallSource.None, sample.Stall.Source);
    }
}
