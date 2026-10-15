using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The whole Linux probe, run over two snapshots of devlinux's /proc taken five seconds apart on
/// 2026-10-15, and over small hand-built trees for the failure cases. The clocks, the free-space call,
/// the clock tick rate and the page size are injected, so these run on every platform.
/// </summary>
[Trait("Category", "Resources")]
public sealed class LinuxResourceProbeTests : IDisposable
{
    private static readonly DateTime At = new(2026, 10, 15, 8, 26, 0, DateTimeKind.Utc);
    private const ulong GiB = 1024UL * 1024 * 1024;

    private readonly List<string> _temporaryDirectories = [];
    private long _milliseconds;
    private DateTime _now = At;

    public void Dispose()
    {
        foreach (var directory in _temporaryDirectories) Directory.Delete(directory, recursive: true);
    }

    private (LinuxResourceProbe Probe, string Root) DevlinuxProbe()
    {
        var root = NewDirectory();
        CopySnapshot("snapshot-1", root);
        var probe = new LinuxResourceProbe(
            root,
            LinuxProcParsingTests.Fixture("sys", "block"),
            now: () => _now,
            monotonicMilliseconds: () => _milliseconds,
            volumeSpace: _ => (100 * GiB, 40 * GiB),
            clockTicksPerSecond: () => 100,
            pageSize: 4096);
        return (probe, root);
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
        _milliseconds += 5000;
        _now = At.AddSeconds(5);
        return (first, probe.Read());
    }

    // ----- the first sample ---------------------------------------------------------------------

    [Fact]
    public void Read_FirstSample_RatesSayFirstSampleAndLevelsAreRead()
    {
        var (probe, _) = DevlinuxProbe();

        var sample = probe.Read();

        Assert.Equal(At, sample.TakenAtUtc);
        Assert.False(sample.Cpu.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, sample.Cpu.UnreadableReason);
        Assert.False(sample.Network.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, sample.Network.UnreadableReason);
        Assert.False(sample.Processes.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, sample.Processes.UnreadableReason);

        // Levels, not rates, are known at once.
        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.Swap.CouldRead);
        Assert.Equal(8388604UL * 1024, sample.Swap.TotalBytes);
        Assert.True(sample.Stall.CouldRead);
        Assert.Equal(9.67, sample.Stall.IoStalledPercent);
    }

    [Fact]
    public void Read_FirstSample_ListsEachPhysicalDiskWithItsKindButNoFigures()
    {
        var (probe, _) = DevlinuxProbe();

        var disks = probe.Read().Disks;

        Assert.True(disks.CouldRead);
        Assert.Equal(["sda", "sdb", "sdc"], disks.Disks.Select(d => d.Name).ToArray());
        Assert.Equal([DiskKind.SolidState, DiskKind.SolidState, DiskKind.Spinning], disks.Disks.Select(d => d.Kind).ToArray());
        Assert.All(disks.Disks, d =>
        {
            Assert.False(d.CouldRead);
            Assert.Equal(ResourceUnreadableReasons.FirstSample, d.UnreadableReason);
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
        Assert.Equal(39.885496, second.Cpu.BusiestCorePercent, tolerance: 1e-5);
    }

    [Fact]
    public void Read_SecondSample_TheSpinningDriveIsBusyAndSlowWhileTheSolidStateDisksIdle()
    {
        var (_, second) = ReadBothSnapshots();

        var sdc = Assert.Single(second.Disks.Disks, d => d.Name == "sdc");
        Assert.True(sdc.CouldRead);
        Assert.Equal(DiskKind.Spinning, sdc.Kind);
        Assert.Equal(88.32, sdc.BusyPercent, 2);
        Assert.Equal(21299.2, sdc.ReadBytesPerSecond, 1);
        Assert.Equal(67506995.2, sdc.WriteBytesPerSecond, 1);
        Assert.Equal(81.316, sdc.MillisecondsPerRead ?? -1, 3);
        Assert.Equal(92.222, sdc.MillisecondsPerWrite ?? -1, 3);
        Assert.Equal(16.6376, sdc.QueueLength ?? -1, 4);

        var sda = Assert.Single(second.Disks.Disks, d => d.Name == "sda");
        Assert.Equal(DiskKind.SolidState, sda.Kind);
        Assert.Equal(0.78, sda.BusyPercent, 2);
        Assert.Null(sda.MillisecondsPerRead); // no reads completed in the interval: nothing to average
        Assert.Equal(1.5185, sda.MillisecondsPerWrite ?? -1, 4);
    }

    [Fact]
    public void Read_SecondSample_RootVolumeSitsOnAllThreeDisksAndSnapsAreLeftOut()
    {
        var (_, second) = ReadBothSnapshots();

        Assert.Equal(["/", "/boot", "/boot/efi"], second.Volumes.Select(v => v.MountPoint).ToArray());
        var root = second.Volumes[0];
        Assert.True(root.CouldRead);
        Assert.Equal(100 * GiB, root.TotalBytes);
        Assert.Equal(40 * GiB, root.FreeBytes);
        Assert.Equal(["sda", "sdb", "sdc"], root.PhysicalDiskNames);
        Assert.Equal(["sdc"], second.Volumes[1].PhysicalDiskNames);
    }

    [Fact]
    public void Read_SecondSample_NetworkRateLeavesOutLoopback()
    {
        var (_, second) = ReadBothSnapshots();

        Assert.True(second.Network.CouldRead);
        Assert.Equal(27057.4, second.Network.ReceiveBytesPerSecond, 1);
        Assert.Equal(57308.6, second.Network.SendBytesPerSecond, 1);
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

        var claude = Assert.Single(second.Processes.Processes, p => p.Pid == 3766989);
        Assert.Equal(2916256, claude.ParentPid);
        // 7 ticks in 5 seconds at 100 ticks per second, across 32 cores.
        Assert.Equal(0.04375, claude.CpuPercent, 5);
        Assert.Equal(101977UL * 4096, claude.MemoryBytes);
        Assert.Equal(0, claude.DiskReadBytesPerSecond);
        Assert.Equal(0, claude.DiskWriteBytesPerSecond);

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
        Assert.Null(systemd.DiskReadBytesPerSecond);
        Assert.Null(systemd.DiskWriteBytesPerSecond);
    }

    // ----- caching ------------------------------------------------------------------------------

    [Fact]
    public void Read_WithinOneSecond_ReturnsTheCachedSample()
    {
        var (probe, _) = DevlinuxProbe();
        var first = probe.Read();

        _milliseconds += 999;

        Assert.Same(first, probe.Read());
    }

    [Fact]
    public void Read_AfterOneSecond_ReadsAgain()
    {
        var (probe, _) = DevlinuxProbe();
        var first = probe.Read();

        _milliseconds += 1000;

        Assert.NotSame(first, probe.Read());
    }

    [Fact]
    public void Read_WallClockJumpsBackwards_RatesStillUseTheMonotonicInterval()
    {
        var (probe, root) = DevlinuxProbe();
        probe.Read();
        CopySnapshot("snapshot-2", root);
        _milliseconds += 5000;
        _now = At.AddHours(-3);

        var second = probe.Read();

        Assert.Equal(88.32, Assert.Single(second.Disks.Disks, d => d.Name == "sdc").BusyPercent, 2);
        Assert.True(second.Processes.CouldRead);
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
    public void Read_NoProcStat_ProcessorAndProcessesUnreadableEverythingElseStillRead()
    {
        var (probe, root) = DevlinuxProbe();
        File.Delete(Path.Combine(root, "stat"));

        var sample = probe.Read();

        Assert.False(sample.Cpu.CouldRead);
        Assert.NotEqual(ResourceUnreadableReasons.FirstSample, sample.Cpu.UnreadableReason);
        Assert.False(sample.Processes.CouldRead);
        Assert.True(sample.Memory.CouldRead);
        Assert.True(sample.Disks.CouldRead);
        Assert.True(sample.Stall.CouldRead);
    }

    [Fact]
    public void Read_NothingThereAtAll_NeverThrowsAndEveryPartSaysWhy()
    {
        var empty = NewDirectory();
        var probe = new LinuxResourceProbe(Path.Combine(empty, "proc"), Path.Combine(empty, "sys", "block"),
            () => _now, () => _milliseconds, _ => throw new IOException("no such volume"), () => 100, 4096);

        var sample = probe.Read();

        Assert.False(sample.Cpu.CouldRead);
        Assert.False(sample.Memory.CouldRead);
        Assert.False(sample.Swap.CouldRead);
        Assert.False(sample.Disks.CouldRead);
        Assert.Empty(sample.Volumes);
        Assert.False(sample.Network.CouldRead);
        Assert.False(sample.Stall.CouldRead);
        Assert.False(sample.Processes.CouldRead);
        Assert.All(new[] { sample.Cpu.UnreadableReason, sample.Memory.UnreadableReason, sample.Swap.UnreadableReason,
                sample.Disks.UnreadableReason, sample.Network.UnreadableReason, sample.Stall.UnreadableReason, sample.Processes.UnreadableReason },
            reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void Read_VolumeWhoseSpaceCannotBeRead_IsUnreadableOnItsOwn()
    {
        var root = NewDirectory();
        CopySnapshot("snapshot-1", root);
        var probe = new LinuxResourceProbe(root, LinuxProcParsingTests.Fixture("sys", "block"), () => _now, () => _milliseconds,
            mount => mount == "/boot" ? throw new UnauthorizedAccessException("denied") : (10 * GiB, 5 * GiB), () => 100, 4096);

        var volumes = probe.Read().Volumes;

        var boot = Assert.Single(volumes, v => v.MountPoint == "/boot");
        Assert.False(boot.CouldRead);
        Assert.Contains("denied", boot.UnreadableReason);
        Assert.Equal(0UL, boot.TotalBytes);
        Assert.True(Assert.Single(volumes, v => v.MountPoint == "/").CouldRead);
    }

    [Fact]
    public void Read_DiskWithoutRotationalFlag_IsUnknownKindNotGuessed()
    {
        var sys = NewDirectory();
        Directory.CreateDirectory(Path.Combine(sys, "vda"));
        var root = NewDirectory();
        CopySnapshot("snapshot-1", root);
        File.WriteAllText(Path.Combine(root, "diskstats"), " 253 0 vda 1 0 8 1 1 0 8 1 0 1 2 0 0 0 0 0 0\n");
        var probe = new LinuxResourceProbe(root, sys, () => _now, () => _milliseconds, _ => (GiB, GiB), () => 100, 4096);

        var vda = Assert.Single(probe.Read().Disks.Disks);

        Assert.Equal("vda", vda.Name);
        Assert.Equal(DiskKind.Unknown, vda.Kind);
    }

    [Fact]
    public void Read_ProcessRunningAtThePreviousSampleButNotSeenThen_IsLeftOutRatherThanGuessed()
    {
        var (probe, root) = DevlinuxProbe();
        probe.Read(); // previous sample at At, on 2026-10-15
        CopySnapshot("snapshot-2", root);
        // Started at boot, two days before the previous sample, yet absent from it: its processor time
        // since then is not known.
        WriteProcess(root, 500001, "unseen", startTicksAfterBoot: 0, cpuTicks: 50);
        _milliseconds += 5000;
        _now = At.AddSeconds(5);

        var processes = probe.Read().Processes;

        Assert.True(processes.CouldRead);
        Assert.DoesNotContain(processes.Processes, p => p.Pid == 500001);
    }

    [Fact]
    public void Read_ProcessStartedAfterThePreviousSample_AllItsTicksFallInTheInterval()
    {
        var (probe, root) = DevlinuxProbe();
        probe.Read(); // previous sample at At
        CopySnapshot("snapshot-2", root);
        var bootUtc = DateTime.UnixEpoch.AddSeconds(1791851717);
        var secondsFromBootToPrevious = (At - bootUtc).TotalSeconds;
        WriteProcess(root, 500002, "fresh", startTicksAfterBoot: (ulong)((secondsFromBootToPrevious + 2) * 100), cpuTicks: 80);
        _milliseconds += 5000;
        _now = At.AddSeconds(5);

        var fresh = Assert.Single(probe.Read().Processes.Processes, p => p.Pid == 500002);

        // 80 ticks in 5 seconds at 100 ticks per second over 32 cores.
        Assert.Equal(80.0 / 100 / 5 / 32 * 100, fresh.CpuPercent, 6);
        Assert.Equal(12.0, fresh.DiskWriteBytesPerSecond); // 60 bytes written in its life, all in the interval
    }

    private static void WriteProcess(string root, int pid, string name, ulong startTicksAfterBoot, ulong cpuTicks)
    {
        var directory = Path.Combine(root, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stat"),
            $"{pid} ({name}) R 1 {pid} {pid} 0 -1 0 0 0 0 0 {cpuTicks} 0 0 0 20 0 1 0 {startTicksAfterBoot} 0 0");
        File.WriteAllText(Path.Combine(directory, "statm"), "100 10 5 1 0 50 0");
        File.WriteAllText(Path.Combine(directory, "io"), "rchar: 0\nwchar: 0\nread_bytes: 0\nwrite_bytes: 60\n");
    }
}
