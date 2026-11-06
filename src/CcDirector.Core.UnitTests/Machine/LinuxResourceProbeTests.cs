using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The whole Linux probe, run over two snapshots of devlinux's /proc taken five seconds apart on
/// 2026-10-15, and over small hand-built trees for the failure cases. The clock, the clock tick rate and
/// the page size are injected, so these run on every platform and nothing here waits or reads the
/// machine.
/// </summary>
[Trait("Category", "Resources")]
public sealed class LinuxResourceProbeTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 15, 8, 26, 0, TimeSpan.Zero);

    private readonly List<string> _temporaryDirectories = [];
    private readonly TwoClocks _clock = new(At);

    public void Dispose()
    {
        foreach (var directory in _temporaryDirectories) Directory.Delete(directory, recursive: true);
    }

    /// <summary>
    /// A wall clock and a monotonic clock that move independently, so a test can step the wall clock
    /// backwards (as time synchronisation does) while the monotonic one only moves forward.
    /// </summary>
    private sealed class TwoClocks(DateTimeOffset wall) : TimeProvider
    {
        private DateTimeOffset _wall = wall;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _wall;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>Both clocks move forward together, as they do on a healthy machine.</summary>
        public void Advance(TimeSpan by)
        {
            _wall = _wall.Add(by);
            _timestamp += by.Ticks;
        }

        public void StepWallClock(TimeSpan by) => _wall = _wall.Add(by);
    }

    private LinuxResourceProbe Probe(string procRoot, string sysBlockRoot) =>
        new(procRoot, sysBlockRoot, _clock, clockTicksPerSecond: () => 100, pageSize: 4096);

    private (LinuxResourceProbe Probe, string Root) DevlinuxProbe()
    {
        var root = NewDirectory();
        CopySnapshot("snapshot-1", root);
        return (Probe(root, LinuxProcParsingTests.Fixture("sys", "block")), root);
    }

    private static void CopySnapshot(string snapshot, string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        CopyDirectory(LinuxProcParsingTests.Fixture(snapshot, "proc"), root);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(from)) CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "linux-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _temporaryDirectories.Add(path);
        return path;
    }

    /// <summary>Reads snapshot-1, then snapshot-2 five seconds later, and returns both samples.</summary>
    private (ResourceSample First, ResourceSample Second) ReadBothSnapshots()
    {
        var (probe, root) = DevlinuxProbe();
        var first = probe.Read();
        CopySnapshot("snapshot-2", root);
        _clock.Advance(TimeSpan.FromSeconds(5));
        return (first, probe.Read());
    }

    // ----- the shared contract ------------------------------------------------------------------

    [Fact]
    public void Read_BothDevlinuxSamples_KeepEveryRuleOfTheSharedContract()
    {
        var (first, second) = ReadBothSnapshots();

        Assert.Empty(ResourceSampleContract.Violations(first));
        Assert.Empty(ResourceSampleContract.Violations(second));
    }

    // ----- the first sample ---------------------------------------------------------------------

    [Fact]
    public void Read_FirstSample_ProcessorSaysFirstSampleButKeepsItsCoreCount_AndLevelsAreRead()
    {
        var (probe, _) = DevlinuxProbe();

        var sample = probe.Read();

        Assert.Equal(At.UtcDateTime, sample.TakenAtUtc);
        Assert.False(sample.Cpu.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, sample.Cpu.UnreadableReason);
        Assert.Equal(32, sample.Cpu.LogicalCoreCount);

        // Levels, not rates, are known at once.
        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.Swap.CouldRead);
        Assert.Equal(8388604UL * 1024, sample.Swap.TotalBytes);
        Assert.True(sample.Stall.CouldRead);
        Assert.Equal(9.67, sample.Stall.IoStalledPercent);
    }

    [Fact]
    public void Read_FirstSample_ListsEveryProcessNamedWithMemoryButNoProcessorShare()
    {
        var (probe, _) = DevlinuxProbe();

        var processes = probe.Read().Processes;

        Assert.True(processes.CouldRead);
        Assert.Equal(6, processes.Processes.Count);
        Assert.All(processes.Processes, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.Null(p.CpuPercent);
            Assert.Null(p.DiskReadBytesPerSecond);
            Assert.Null(p.DiskWriteBytesPerSecond);
            Assert.NotNull(p.MemoryBytes);
        });
    }

    [Fact]
    public void Read_FirstSample_ListsEachPhysicalDiskWithItsKindButNoFigures()
    {
        var (probe, _) = DevlinuxProbe();

        var disks = probe.Read().DiskEvidence;

        Assert.True(disks.CouldRead);
        Assert.Equal(["sda", "sdb", "sdc"], disks.Disks.Select(d => d.Name).ToArray());
        Assert.Equal([DiskKind.SolidState, DiskKind.SolidState, DiskKind.Spinning], disks.Disks.Select(d => d.Kind).ToArray());
        Assert.All(disks.Disks, d =>
        {
            Assert.False(d.CouldRead);
            Assert.Equal(ResourceUnreadableReasons.FirstSample, d.UnreadableReason);
            Assert.Null(d.BusyPercent);
        });
    }

    // ----- the second sample: devlinux, slow because of sdc -----------------------------------

    [Fact]
    public void Read_SecondSample_ProcessorFromTheTickDifference()
    {
        var (_, second) = ReadBothSnapshots();

        Assert.True(second.Cpu.CouldRead);
        Assert.Equal(32, second.Cpu.LogicalCoreCount);
        Assert.Equal(17.998805, second.Cpu.TotalBusyPercent, tolerance: 1e-5);
        Assert.Equal(39.885496, second.Cpu.BusiestCorePercent ?? -1, tolerance: 1e-5);
    }

    [Fact]
    public void Read_SecondSample_TheSpinningDriveIsBusyAndSlowWhileTheSolidStateDisksIdle()
    {
        var (_, second) = ReadBothSnapshots();

        var sdc = Assert.Single(second.DiskEvidence.Disks, d => d.Name == "sdc");
        Assert.True(sdc.CouldRead);
        Assert.Equal(DiskKind.Spinning, sdc.Kind);
        Assert.Equal(88.32, sdc.BusyPercent ?? -1, 2);
        Assert.Equal(81.316, sdc.MillisecondsPerRead ?? -1, 3);
        Assert.Equal(92.222, sdc.MillisecondsPerWrite ?? -1, 3);
        Assert.Equal(16.6376, sdc.AverageQueueDepth ?? -1, 4);

        var sda = Assert.Single(second.DiskEvidence.Disks, d => d.Name == "sda");
        Assert.Equal(DiskKind.SolidState, sda.Kind);
        Assert.Equal(0.78, sda.BusyPercent ?? -1, 2);
        Assert.Null(sda.MillisecondsPerRead); // no reads completed in the interval: nothing to average
        Assert.Equal(1.5185, sda.MillisecondsPerWrite ?? -1, 4);
    }

    [Fact]
    public void Read_Devlinux_TheSpinningDriveIsInsideTheRootVolume()
    {
        var (_, second) = ReadBothSnapshots();

        // The root logical volume dm-0 sat on sda1, sdb1 and sdc3 on 2026-10-15 - the whole cause of the
        // slowness, because everything the machine did could wait on sdc.
        Assert.All(second.DiskEvidence.Disks, d => Assert.True(d.IsInSystemVolume, $"{d.Name} should back /"));
    }

    [Fact]
    public void Read_SpinningDiskOutsideTheRootVolume_IsNotInTheSystemVolume()
    {
        // Today's devlinux: / is a logical volume on sda3 and sdb1, and sdc is not mounted at all.
        var sys = NewDirectory();
        WriteBlockDevice(sys, "sda", "8:0", rotational: 0, partitions: [("sda3", "8:3")]);
        WriteBlockDevice(sys, "sdb", "8:16", rotational: 0, partitions: [("sdb1", "8:17")]);
        WriteBlockDevice(sys, "sdc", "8:32", rotational: 1, partitions: [("sdc3", "8:35")]);
        WriteBlockDevice(sys, "dm-0", "252:0", rotational: 0, partitions: [], slaves: ["sda3", "sdb1"]);
        var (_, root) = DevlinuxProbe();
        var probe = Probe(root, sys);

        var disks = probe.Read().DiskEvidence.Disks;

        Assert.Equal([true, true, false], disks.Select(d => d.IsInSystemVolume).ToArray());
        Assert.Equal(["sda", "sdb", "sdc"], disks.Select(d => d.Name).ToArray());
    }

    [Fact]
    public void Read_NoMountTable_PlaceInTheSystemVolumeIsNullNotFalse()
    {
        var (probe, root) = DevlinuxProbe();
        File.Delete(Path.Combine(root, "self", "mountinfo"));

        var disks = probe.Read().DiskEvidence;

        Assert.True(disks.CouldRead);
        Assert.All(disks.Disks, d => Assert.Null(d.IsInSystemVolume));
    }

    [Fact]
    public void Read_SecondSample_StallsArePressureFiguresRunQueueAndTheProcessStuckOnDisk()
    {
        var (_, second) = ReadBothSnapshots();

        var stall = second.Stall;
        Assert.True(stall.CouldRead);
        Assert.Equal(StallSource.LinuxPressureStall, stall.Source);
        Assert.Equal(11.50, stall.CpuStalledPercent);
        Assert.Equal(1.99, stall.MemoryStalledPercent);
        Assert.Equal(16.41, stall.IoStalledPercent);
        Assert.Equal(47, stall.RunQueueLength);
        Assert.Equal(1, stall.ProcessesWaitingOnDisk); // jbd2/dm-0-8, the journal thread of the root volume
        Assert.Null(stall.HardPageFaultsPerSecond);
        Assert.Null(stall.MacOsMemoryPressureLevel);
    }

    [Fact]
    public void Read_SecondSample_ProcessesCarryParentStartMemoryAndShareOfTheWholeMachine()
    {
        var (_, second) = ReadBothSnapshots();

        Assert.True(second.Processes.CouldRead);
        Assert.Equal(6, second.Processes.Processes.Count);

        var agent = Assert.Single(second.Processes.Processes, p => p.Pid == 3766989);
        Assert.Equal(2916256, agent.ParentPid);
        // 7 ticks in 5 seconds at 100 ticks per second is 1.4% of ONE core; across 32 cores it is
        // 0.04375% of the whole machine. A per-core figure would read 1.4.
        Assert.Equal(0.04375, agent.CpuPercent ?? -1, 5);
        Assert.Equal(101977UL * 4096, agent.MemoryBytes);
        Assert.Equal(0, agent.DiskReadBytesPerSecond);
        Assert.Equal(0, agent.DiskWriteBytesPerSecond);

        var dotnet = Assert.Single(second.Processes.Processes, p => p.Pid == 3911504);
        // Started 21491067 ticks (214910.67 seconds) after boot at 1791851717.
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1791851717 + 214910.67), dotnet.StartTimeUtc);
    }

    [Fact]
    public void Read_SecondSample_ProcessWhoseIoIsClosedToUsHasNullDiskRatesNotZero()
    {
        var (_, second) = ReadBothSnapshots();

        var systemd = Assert.Single(second.Processes.Processes, p => p.Pid == 1);
        Assert.Equal("systemd", systemd.Name);
        Assert.NotNull(systemd.CpuPercent);
        Assert.Null(systemd.DiskReadBytesPerSecond);
        Assert.Null(systemd.DiskWriteBytesPerSecond);
    }

    // ----- the clock and the cache --------------------------------------------------------------

    [Fact]
    public void Read_WithinOneSecond_ReturnsTheCachedSample()
    {
        var (probe, _) = DevlinuxProbe();
        var first = probe.Read();

        _clock.Advance(TimeSpan.FromMilliseconds(999));

        Assert.Same(first, probe.Read());
    }

    [Fact]
    public void Read_AfterOneSecond_ReadsAgain()
    {
        var (probe, _) = DevlinuxProbe();
        var first = probe.Read();

        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.NotSame(first, probe.Read());
    }

    [Fact]
    public void Read_WallClockStepsBackwards_RatesAndTheCacheStillUseTheMonotonicInterval()
    {
        var (probe, root) = DevlinuxProbe();
        probe.Read();
        CopySnapshot("snapshot-2", root);
        _clock.Advance(TimeSpan.FromSeconds(5));
        _clock.StepWallClock(TimeSpan.FromHours(-3));

        var second = probe.Read();

        Assert.Equal(At.AddSeconds(5).AddHours(-3).UtcDateTime, second.TakenAtUtc); // the stamp is wall time
        Assert.Equal(88.32, Assert.Single(second.DiskEvidence.Disks, d => d.Name == "sdc").BusyPercent ?? -1, 2);
        Assert.Equal(0.04375, Assert.Single(second.Processes.Processes, p => p.Pid == 3766989).CpuPercent ?? -1, 5);
    }

    // ----- failure cases ------------------------------------------------------------------------

    [Fact]
    public void Read_KernelWithoutPressureStallInformation_StallIsUnreadableWithTheReason()
    {
        var (probe, root) = DevlinuxProbe();
        Directory.Delete(Path.Combine(root, "pressure"), recursive: true);

        var stall = probe.Read().Stall;

        Assert.False(stall.CouldRead);
        Assert.Equal(StallSource.None, stall.Source);
        Assert.Contains("pressure", stall.UnreadableReason);
        Assert.Contains("4.20", stall.UnreadableReason);
        Assert.Null(stall.IoStalledPercent);
    }

    [Fact]
    public void Read_PressureFileWithoutSomeLine_StallIsUnreadableNotZero()
    {
        var (probe, root) = DevlinuxProbe();
        File.WriteAllText(Path.Combine(root, "pressure", "io"), "full avg10=0.00 avg60=0.00 avg300=0.00 total=0\n");

        var stall = probe.Read().Stall;

        Assert.False(stall.CouldRead);
        Assert.Contains("some avg10", stall.UnreadableReason);
    }

    [Fact]
    public void Read_NoProcStat_ProcessorUnreadable_ProcessesStillListedWithoutAShare()
    {
        var (probe, root) = DevlinuxProbe();
        probe.Read();
        CopySnapshot("snapshot-2", root);
        File.Delete(Path.Combine(root, "stat"));
        _clock.Advance(TimeSpan.FromSeconds(5));

        var sample = probe.Read();

        Assert.False(sample.Cpu.CouldRead);
        Assert.NotEqual(ResourceUnreadableReasons.FirstSample, sample.Cpu.UnreadableReason);
        // With no core count a process's share of the machine cannot be worked out - null, never a
        // per-core figure passed off as one - but every process is still named.
        Assert.True(sample.Processes.CouldRead);
        Assert.Equal(6, sample.Processes.Processes.Count);
        Assert.All(sample.Processes.Processes, p => Assert.Null(p.CpuPercent));
        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.DiskEvidence.CouldRead);
        Assert.True(sample.Stall.CouldRead);
    }

    [Fact]
    public void Read_NothingThereAtAll_NeverThrowsAndEveryPartSaysWhy()
    {
        var empty = NewDirectory();
        var probe = Probe(Path.Combine(empty, "proc"), Path.Combine(empty, "sys", "block"));

        var sample = probe.Read();

        Assert.False(sample.Cpu.CouldRead);
        Assert.False(sample.Memory.CouldRead);
        Assert.False(sample.Swap.CouldRead);
        Assert.False(sample.DiskEvidence.CouldRead);
        Assert.False(sample.Stall.CouldRead);
        Assert.False(sample.Processes.CouldRead);
        Assert.All(new[] { sample.Cpu.UnreadableReason, sample.Memory.UnreadableReason, sample.Swap.UnreadableReason,
                sample.DiskEvidence.UnreadableReason, sample.Stall.UnreadableReason, sample.Processes.UnreadableReason },
            reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
        Assert.Empty(ResourceSampleContract.Violations(sample));
    }

    [Fact]
    public void Read_DiskWithoutRotationalFlag_IsUnknownKindNotGuessed()
    {
        var sys = NewDirectory();
        Directory.CreateDirectory(Path.Combine(sys, "vda"));
        var root = NewDirectory();
        CopySnapshot("snapshot-1", root);
        File.WriteAllText(Path.Combine(root, "diskstats"), " 253 0 vda 1 0 8 1 1 0 8 1 0 1 2 0 0 0 0 0 0\n");
        var probe = Probe(root, sys);

        var vda = Assert.Single(probe.Read().DiskEvidence.Disks);

        Assert.Equal("vda", vda.Name);
        Assert.Equal(DiskKind.Unknown, vda.Kind);
    }

    [Fact]
    public void Read_DiskCountersWentBackwards_TheDiskSaysSoWithNoFigures()
    {
        var (probe, root) = DevlinuxProbe();
        probe.Read();
        CopySnapshot("snapshot-2", root);
        // sdc removed and added again: every counter restarts near zero.
        var lines = File.ReadAllLines(Path.Combine(root, "diskstats"))
            .Select(l => l.Contains(" sdc ") ? "   8      32 sdc 1 0 8 1 1 0 8 1 0 1 2 0 0 0 0 0 0" : l);
        File.WriteAllLines(Path.Combine(root, "diskstats"), lines);
        _clock.Advance(TimeSpan.FromSeconds(5));

        var sdc = Assert.Single(probe.Read().DiskEvidence.Disks, d => d.Name == "sdc");

        Assert.False(sdc.CouldRead);
        Assert.Contains("went backwards", sdc.UnreadableReason);
        Assert.Null(sdc.BusyPercent);
        Assert.Null(sdc.MillisecondsPerWrite);
    }

    [Fact]
    public void Read_ProcessRunningAtThePreviousSampleButNotSeenThen_IsListedWithNoShareRatherThanGuessed()
    {
        var (probe, root) = DevlinuxProbe();
        probe.Read(); // previous sample at At, on 2026-10-15
        CopySnapshot("snapshot-2", root);
        // Started at boot, two days before the previous sample, yet absent from it: its processor time
        // since then is not known.
        WriteProcess(root, 500001, "unseen", startTicksAfterBoot: 0, cpuTicks: 50);
        _clock.Advance(TimeSpan.FromSeconds(5));

        var unseen = Assert.Single(probe.Read().Processes.Processes, p => p.Pid == 500001);

        Assert.Equal("unseen", unseen.Name);
        Assert.Null(unseen.CpuPercent);
        Assert.Null(unseen.DiskWriteBytesPerSecond);
        Assert.Equal(10UL * 4096, unseen.MemoryBytes);
    }

    [Fact]
    public void Read_ProcessStartedDuringTheInterval_IsListedWithNoShare()
    {
        var (probe, root) = DevlinuxProbe();
        probe.Read(); // previous sample at At
        CopySnapshot("snapshot-2", root);
        var bootUtc = DateTime.UnixEpoch.AddSeconds(1791851717);
        var secondsFromBootToPrevious = (At.UtcDateTime - bootUtc).TotalSeconds;
        WriteProcess(root, 500002, "fresh", startTicksAfterBoot: (ulong)((secondsFromBootToPrevious + 2) * 100), cpuTicks: 80);
        _clock.Advance(TimeSpan.FromSeconds(5));

        var fresh = Assert.Single(probe.Read().Processes.Processes, p => p.Pid == 500002);

        // It has no earlier reading to compare against, so its share is not known for this sample.
        Assert.Null(fresh.CpuPercent);
        Assert.Null(fresh.DiskWriteBytesPerSecond);
    }

    [Fact]
    public void Read_ProcessIdReusedByANewProcess_DoesNotInheritTheOldCounters()
    {
        var (probe, root) = DevlinuxProbe();
        WriteProcess(root, 500004, "old", startTicksAfterBoot: 100, cpuTicks: 9000);
        probe.Read();
        CopySnapshot("snapshot-2", root);
        WriteProcess(root, 500004, "new", startTicksAfterBoot: 200, cpuTicks: 5);
        _clock.Advance(TimeSpan.FromSeconds(5));

        var reused = Assert.Single(probe.Read().Processes.Processes, p => p.Pid == 500004);

        Assert.Equal("new", reused.Name);
        Assert.Null(reused.CpuPercent);
    }

    [Fact]
    public void Read_ProcessWhoseMemoryCannotBeRead_IsListedWithNullMemory()
    {
        var (probe, root) = DevlinuxProbe();
        WriteProcess(root, 500005, "nomemory", startTicksAfterBoot: 0, cpuTicks: 10);
        File.Delete(Path.Combine(root, "500005", "statm"));

        var process = Assert.Single(probe.Read().Processes.Processes, p => p.Pid == 500005);

        Assert.Equal("nomemory", process.Name);
        Assert.Null(process.MemoryBytes);
    }

    [Fact]
    public void Read_ProcessWhoseDiskCounterSteppedBackwards_HasNullDiskRateNotZero()
    {
        var (probe, root) = DevlinuxProbe();
        WriteProcess(root, 500003, "rewound", startTicksAfterBoot: 0, cpuTicks: 10, writeBytes: 1000);
        probe.Read(); // previous sample: 1000 bytes written
        CopySnapshot("snapshot-2", root);
        WriteProcess(root, 500003, "rewound", startTicksAfterBoot: 0, cpuTicks: 20, writeBytes: 500);
        _clock.Advance(TimeSpan.FromSeconds(5));

        var rewound = Assert.Single(probe.Read().Processes.Processes, p => p.Pid == 500003);

        Assert.Null(rewound.DiskWriteBytesPerSecond); // no honest difference, so not known - never zero
        Assert.Equal(0.0, rewound.DiskReadBytesPerSecond); // read_bytes stayed at 0: a real zero
        Assert.Equal(10.0 / 100 / 5 / 32 * 100, rewound.CpuPercent ?? -1, 6);
    }

    private static void WriteProcess(string root, int pid, string name, ulong startTicksAfterBoot, ulong cpuTicks, ulong writeBytes = 60)
    {
        var directory = Path.Combine(root, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stat"),
            $"{pid} ({name}) R 1 {pid} {pid} 0 -1 0 0 0 0 0 {cpuTicks} 0 0 0 20 0 1 0 {startTicksAfterBoot} 0 0");
        File.WriteAllText(Path.Combine(directory, "statm"), "100 10 5 1 0 50 0");
        File.WriteAllText(Path.Combine(directory, "io"), $"rchar: 0\nwchar: 0\nread_bytes: 0\nwrite_bytes: {writeBytes}\n");
    }

    private static void WriteBlockDevice(
        string sys, string name, string number, int rotational, (string Name, string Number)[] partitions, string[]? slaves = null)
    {
        var directory = Path.Combine(sys, name);
        Directory.CreateDirectory(Path.Combine(directory, "queue"));
        File.WriteAllText(Path.Combine(directory, "dev"), number + "\n");
        File.WriteAllText(Path.Combine(directory, "queue", "rotational"), rotational + "\n");
        foreach (var partition in partitions)
        {
            Directory.CreateDirectory(Path.Combine(directory, partition.Name));
            File.WriteAllText(Path.Combine(directory, partition.Name, "dev"), partition.Number + "\n");
        }

        if (slaves is null) return;
        Directory.CreateDirectory(Path.Combine(directory, "slaves"));
        foreach (var slave in slaves) File.WriteAllText(Path.Combine(directory, "slaves", slave), "");
    }
}
