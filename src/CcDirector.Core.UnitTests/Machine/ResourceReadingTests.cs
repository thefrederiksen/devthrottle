using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The shared resource reading types (the Resource Monitor mission, issue #2811). These pin the rule every
/// reading keeps: an unreadable reading carries its reason and zeroes - never a figure - and a readable one
/// carries exactly the figures it was given. Pure values, so nothing here reads the machine it runs on.
/// </summary>
[Trait("Category", "Resources")]
public class ResourceReadingTests
{
    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    private static readonly string[] Digits = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];

    /// <summary>
    /// The reasons used below contain no digit, so any digit in an unreadable reading's text is a figure
    /// that leaked - exactly what the meter must never show.
    /// </summary>
    private static void AssertNoFigure(string text) =>
        Assert.DoesNotContain(Digits, digit => text.Contains(digit, StringComparison.Ordinal));

    // -------------------------------------------------------------------------------------------
    // Processor
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void CpuRead_CarriesItsFigures()
    {
        var reading = CpuReading.Read(totalBusyPercent: 42.5, busiestCorePercent: 99, logicalCoreCount: 32);

        Assert.True(reading.CouldRead);
        Assert.Null(reading.UnreadableReason);
        Assert.Equal(42.5, reading.TotalBusyPercent);
        Assert.Equal(99, reading.BusiestCorePercent);
        Assert.Equal(32, reading.LogicalCoreCount);
    }

    [Fact]
    public void CpuUnreadable_CarriesReasonAndZeroes()
    {
        var reading = CpuReading.Unreadable(ResourceUnreadableReasons.FirstSample);

        Assert.False(reading.CouldRead);
        Assert.Equal("first sample, no interval yet", reading.UnreadableReason);
        Assert.Equal(0, reading.TotalBusyPercent);
        Assert.Equal(0, reading.BusiestCorePercent);
        Assert.Equal(0, reading.LogicalCoreCount);
    }

    [Fact]
    public void CpuToString_Unreadable_PrintsReasonAndNoFigure()
    {
        var text = CpuReading.Unreadable("no access to the processor statistics").ToString();

        Assert.Contains("no access to the processor statistics", text);
        AssertNoFigure(text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Unreadable_BlankReason_Throws(string reason)
    {
        Assert.Throws<ArgumentException>(() => CpuReading.Unreadable(reason));
        Assert.Throws<ArgumentException>(() => SwapReading.Unreadable(reason));
        Assert.Throws<ArgumentException>(() => NetworkReading.Unreadable(reason));
        Assert.Throws<ArgumentException>(() => StallReading.Unreadable(reason));
        Assert.Throws<ArgumentException>(() => DiskSetReading.Unreadable(reason));
        Assert.Throws<ArgumentException>(() => ProcessSetReading.Unreadable(reason));
        Assert.Throws<ArgumentException>(() => PhysicalDiskReading.Unreadable("sdc", DiskKind.Spinning, reason));
        Assert.Throws<ArgumentException>(() => VolumeReading.Unreadable("/", reason));
        Assert.Throws<ArgumentException>(() => SlownessDiagnosis.CannotDiagnose(reason));
    }

    // -------------------------------------------------------------------------------------------
    // Swap
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void SwapRead_CarriesItsFigures()
    {
        var reading = SwapReading.Read(totalBytes: 8 * Gigabyte, usedBytes: 3 * Gigabyte);

        Assert.True(reading.CouldRead);
        Assert.Null(reading.UnreadableReason);
        Assert.Equal(8 * Gigabyte, reading.TotalBytes);
        Assert.Equal(3 * Gigabyte, reading.UsedBytes);
    }

    [Fact]
    public void SwapRead_NoSwapConfigured_IsReadableWithZeroTotal()
    {
        var reading = SwapReading.Read(totalBytes: 0, usedBytes: 0);

        Assert.True(reading.CouldRead);
        Assert.Equal(0UL, reading.TotalBytes);
    }

    [Fact]
    public void SwapUnreadable_CarriesReasonAndZeroes_AndPrintsNoFigure()
    {
        var reading = SwapReading.Unreadable("swap file is not readable");

        Assert.False(reading.CouldRead);
        Assert.Equal("swap file is not readable", reading.UnreadableReason);
        Assert.Equal(0UL, reading.TotalBytes);
        Assert.Equal(0UL, reading.UsedBytes);
        AssertNoFigure(reading.ToString());
    }

    // -------------------------------------------------------------------------------------------
    // Network
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void NetworkRead_CarriesItsFigures()
    {
        var reading = NetworkReading.Read(receiveBytesPerSecond: 1500, sendBytesPerSecond: 250);

        Assert.True(reading.CouldRead);
        Assert.Equal(1500, reading.ReceiveBytesPerSecond);
        Assert.Equal(250, reading.SendBytesPerSecond);
    }

    [Fact]
    public void NetworkUnreadable_CarriesReasonAndZeroes_AndPrintsNoFigure()
    {
        var reading = NetworkReading.Unreadable(ResourceUnreadableReasons.FirstSample);

        Assert.False(reading.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, reading.UnreadableReason);
        Assert.Equal(0, reading.ReceiveBytesPerSecond);
        Assert.Equal(0, reading.SendBytesPerSecond);
        AssertNoFigure(reading.ToString());
    }

    // -------------------------------------------------------------------------------------------
    // Disks and volumes
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void PhysicalDiskRead_CarriesItsFigures_AndNullForWhatIsNotOffered()
    {
        var reading = PhysicalDiskReading.Read("sdc", DiskKind.Spinning, busyPercent: 75,
            readBytesPerSecond: 1000, writeBytesPerSecond: 2000,
            millisecondsPerRead: null, millisecondsPerWrite: 26, averageQueueDepth: 3.5);

        Assert.True(reading.CouldRead);
        Assert.Equal("sdc", reading.Name);
        Assert.Equal(DiskKind.Spinning, reading.Kind);
        Assert.Equal(75, reading.BusyPercent);
        Assert.Equal(1000, reading.ReadBytesPerSecond);
        Assert.Equal(2000, reading.WriteBytesPerSecond);
        Assert.Null(reading.MillisecondsPerRead);
        Assert.Equal(26, reading.MillisecondsPerWrite);
        Assert.Equal(3.5, reading.AverageQueueDepth);
        Assert.Contains("spinning hard drive", reading.ToString());
        Assert.Contains("75% busy", reading.ToString());
        Assert.Contains("3.5 operations in flight", reading.ToString());
        Assert.Contains("26 ms per write", reading.ToString());
    }

    [Fact]
    public void PhysicalDiskRead_NoTrueUtilisation_BusyIsNull_AndIsNotPrintedAsBusy()
    {
        // macOS: the IOKit disk-time counter is a queue depth, not a utilisation, so busy is not offered.
        var reading = PhysicalDiskReading.Read("disk0", DiskKind.SolidState, busyPercent: null,
            readBytesPerSecond: 1000, writeBytesPerSecond: 2000,
            millisecondsPerRead: 0.4, millisecondsPerWrite: 0.6, averageQueueDepth: 2.6);

        Assert.True(reading.CouldRead);
        Assert.Null(reading.BusyPercent);
        Assert.Equal(2.6, reading.AverageQueueDepth);
        Assert.DoesNotContain("busy", reading.ToString());
        Assert.Contains("2.6 operations in flight", reading.ToString());
    }

    [Fact]
    public void PhysicalDiskUnreadable_KeepsNameAndKind_CarriesReasonAndZeroes()
    {
        var reading = PhysicalDiskReading.Unreadable("sdc", DiskKind.Spinning, "statistics locked away");

        Assert.False(reading.CouldRead);
        Assert.Equal("sdc", reading.Name);
        Assert.Equal(DiskKind.Spinning, reading.Kind);
        Assert.Equal("statistics locked away", reading.UnreadableReason);
        Assert.Null(reading.BusyPercent);
        Assert.Equal(0, reading.ReadBytesPerSecond);
        Assert.Equal(0, reading.WriteBytesPerSecond);
        Assert.Null(reading.MillisecondsPerRead);
        Assert.Null(reading.MillisecondsPerWrite);
        Assert.Null(reading.AverageQueueDepth);
    }

    [Fact]
    public void PhysicalDiskToString_Unreadable_PrintsNoFigure()
    {
        // The disk name is chosen with no digit so that a leaked figure is the only digit possible.
        var text = PhysicalDiskReading.Unreadable("sdc", DiskKind.Unknown, "statistics locked away").ToString();

        Assert.Contains("statistics locked away", text);
        AssertNoFigure(text);
    }

    [Fact]
    public void DiskKind_UnknownIsZero_SoADefaultIsNeverTakenForAKind()
    {
        Assert.Equal(DiskKind.Unknown, default(DiskKind));
    }

    [Fact]
    public void DiskSet_OneDiskCanFailWhileTheSetReads()
    {
        var set = DiskSetReading.Read(
        [
            PhysicalDiskReading.Read("nvme0n1", DiskKind.SolidState, 2, 0, 0, null, null, null),
            PhysicalDiskReading.Unreadable("sdc", DiskKind.Spinning, "statistics locked away"),
        ]);

        Assert.True(set.CouldRead);
        Assert.Equal(2, set.Disks.Count);
        Assert.True(set.Disks[0].CouldRead);
        Assert.False(set.Disks[1].CouldRead);
    }

    [Fact]
    public void DiskSetUnreadable_HasNoDisks_AndPrintsNoFigure()
    {
        var set = DiskSetReading.Unreadable("the disks could not be listed");

        Assert.False(set.CouldRead);
        Assert.Equal("the disks could not be listed", set.UnreadableReason);
        Assert.Empty(set.Disks);
        AssertNoFigure(set.ToString());
    }

    [Fact]
    public void VolumeRead_CarriesItsFigures_AndItsPhysicalDisks()
    {
        var volume = VolumeReading.Read("/", 1000 * Gigabyte, 200 * Gigabyte, ["nvme0n1", "nvme1n1", "sdc"]);

        Assert.True(volume.CouldRead);
        Assert.Equal("/", volume.MountPoint);
        Assert.Equal(1000 * Gigabyte, volume.TotalBytes);
        Assert.Equal(200 * Gigabyte, volume.FreeBytes);
        Assert.Contains("sdc", volume.PhysicalDiskNames);
    }

    [Fact]
    public void VolumeUnreadable_KeepsMountPoint_CarriesReasonAndZeroes_AndPrintsNoFigure()
    {
        var volume = VolumeReading.Unreadable("/mnt/archive", "the volume did not answer");

        Assert.False(volume.CouldRead);
        Assert.Equal("/mnt/archive", volume.MountPoint);
        Assert.Equal("the volume did not answer", volume.UnreadableReason);
        Assert.Equal(0UL, volume.TotalBytes);
        Assert.Equal(0UL, volume.FreeBytes);
        Assert.Empty(volume.PhysicalDiskNames);
        AssertNoFigure(volume.ToString());
    }

    // -------------------------------------------------------------------------------------------
    // Stalls
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void StallRead_Linux_CarriesTheThreePercentages_AndNullForTheProxies()
    {
        // The devlinux figures from 2026-10-15.
        var reading = StallReading.Read(StallSource.LinuxPressureStall,
            cpuStalledPercent: 0.06, memoryStalledPercent: 0.17, ioStalledPercent: 32);

        Assert.True(reading.CouldRead);
        Assert.Equal(StallSource.LinuxPressureStall, reading.Source);
        Assert.Equal(0.06, reading.CpuStalledPercent);
        Assert.Equal(0.17, reading.MemoryStalledPercent);
        Assert.Equal(32, reading.IoStalledPercent);
        Assert.Null(reading.RunQueueLength);
        Assert.Null(reading.HardPageFaultsPerSecond);
        Assert.Null(reading.MacOsMemoryPressureLevel);
        Assert.Null(reading.ProcessesWaitingOnDisk);
    }

    [Fact]
    public void StallRead_WithNoSource_Throws()
    {
        Assert.Throws<ArgumentException>(() => StallReading.Read(StallSource.None, ioStalledPercent: 1));
    }

    [Fact]
    public void StallUnreadable_CarriesReason_EveryFigureNull_AndPrintsNoFigure()
    {
        var reading = StallReading.Unreadable("pressure information is switched off in this kernel");

        Assert.False(reading.CouldRead);
        Assert.Equal(StallSource.None, reading.Source);
        Assert.Null(reading.CpuStalledPercent);
        Assert.Null(reading.MemoryStalledPercent);
        Assert.Null(reading.IoStalledPercent);
        Assert.Null(reading.RunQueueLength);
        Assert.Null(reading.HardPageFaultsPerSecond);
        Assert.Null(reading.MacOsMemoryPressureLevel);
        Assert.Null(reading.ProcessesWaitingOnDisk);
        AssertNoFigure(reading.ToString());
    }

    // -------------------------------------------------------------------------------------------
    // Processes
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ProcessSetRead_CarriesItsProcesses()
    {
        var process = new ProcessReading(3933596, 1, "grep", null, 12.5, 64UL * 1024 * 1024, null, 5_000_000);

        var set = ProcessSetReading.Read([process]);

        Assert.True(set.CouldRead);
        Assert.Same(process, Assert.Single(set.Processes));
    }

    [Fact]
    public void ProcessSetUnreadable_HasNoProcesses_AndPrintsNoFigure()
    {
        var set = ProcessSetReading.Unreadable(ResourceUnreadableReasons.FirstSample);

        Assert.False(set.CouldRead);
        Assert.Equal(ResourceUnreadableReasons.FirstSample, set.UnreadableReason);
        Assert.Empty(set.Processes);
        AssertNoFigure(set.ToString());
    }
}
