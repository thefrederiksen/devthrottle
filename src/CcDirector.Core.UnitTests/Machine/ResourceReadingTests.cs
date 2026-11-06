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
        Assert.Throws<ArgumentException>(() => StallReading.Unreadable(reason));
        Assert.Throws<ArgumentException>(() => DiskEvidenceSet.Unreadable(reason));
        Assert.Throws<ArgumentException>(() => ProcessSetReading.Unreadable(reason));
        Assert.Throws<ArgumentException>(() => DiskEvidence.Unreadable("sdc", DiskKind.Spinning, isInSystemVolume: true, reason));
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
    // Disk evidence for the slowness diagnosis
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void DiskEvidenceRead_CarriesItsFigures_AndNullForWhatIsNotOffered()
    {
        // The devlinux disk from 2026-10-15: the sentence the owner approved is built out of exactly these.
        var reading = DiskEvidence.Read("sdc", DiskKind.Spinning, isInSystemVolume: true, busyPercent: 75,
            millisecondsPerRead: null, millisecondsPerWrite: 26, averageQueueDepth: 3.5);

        Assert.True(reading.CouldRead);
        Assert.Null(reading.UnreadableReason);
        Assert.Equal("sdc", reading.Name);
        Assert.Equal(DiskKind.Spinning, reading.Kind);
        Assert.True(reading.IsInSystemVolume);
        Assert.Equal(75, reading.BusyPercent);
        Assert.Null(reading.MillisecondsPerRead);
        Assert.Equal(26, reading.MillisecondsPerWrite);
        Assert.Equal(3.5, reading.AverageQueueDepth);
        Assert.Contains("spinning hard drive", reading.ToString());
        Assert.Contains("in the system volume", reading.ToString());
        Assert.Contains("75% busy", reading.ToString());
        Assert.Contains("3.5 operations in flight", reading.ToString());
        Assert.Contains("26 ms per write", reading.ToString());
    }

    [Fact]
    public void DiskEvidenceRead_NoTrueUtilisation_BusyIsNull_AndIsNotPrintedAsBusy()
    {
        // macOS: the IOKit disk-time counter is a queue depth, not a utilisation, so busy is not offered.
        var reading = DiskEvidence.Read("disk0", DiskKind.SolidState, isInSystemVolume: true, busyPercent: null,
            millisecondsPerRead: 0.4, millisecondsPerWrite: 0.6, averageQueueDepth: 2.6);

        Assert.True(reading.CouldRead);
        Assert.Null(reading.BusyPercent);
        Assert.Equal(2.6, reading.AverageQueueDepth);
        Assert.DoesNotContain("busy", reading.ToString());
        Assert.Contains("2.6 operations in flight", reading.ToString());
    }

    [Fact]
    public void DiskEvidenceRead_SystemVolumeNotKnown_SaysNothingAboutIt()
    {
        // Null is "this platform cannot say which disks back which volume", never "it does not".
        var reading = DiskEvidence.Read("PhysicalDrive2", DiskKind.Unknown, isInSystemVolume: null,
            busyPercent: 4, millisecondsPerRead: null, millisecondsPerWrite: null, averageQueueDepth: null);

        Assert.Null(reading.IsInSystemVolume);
        Assert.DoesNotContain("system volume", reading.ToString());
    }

    [Fact]
    public void DiskEvidenceRead_NoFigureOffered_SaysSo_RatherThanPrintingNothing()
    {
        var reading = DiskEvidence.Read("sdc", DiskKind.Spinning, isInSystemVolume: false, busyPercent: null,
            millisecondsPerRead: null, millisecondsPerWrite: null, averageQueueDepth: null);

        Assert.True(reading.CouldRead);
        Assert.Contains("no figures offered", reading.ToString());
    }

    [Theory]
    [InlineData(0.337, "0.34 ms per write")]
    [InlineData(0.004, "0.00 ms per write")]
    [InlineData(9.5, "9.50 ms per write")]
    [InlineData(26, "26 ms per write")]
    [InlineData(26.4, "26 ms per write")]
    public void DiskEvidenceToString_MillisecondsPerWrite_KeepsSubMillisecondFigures(double milliseconds, string expected)
    {
        // A solid-state drive answers in under a millisecond; whole milliseconds would print it as "0 ms".
        var reading = DiskEvidence.Read("nvme0n1", DiskKind.SolidState, isInSystemVolume: true, busyPercent: 3,
            millisecondsPerRead: null, millisecondsPerWrite: milliseconds, averageQueueDepth: null);

        Assert.EndsWith(expected, reading.ToString());
    }

    [Fact]
    public void DiskEvidenceUnreadable_KeepsNameKindAndVolume_CarriesReason_AndEveryFigureIsNull()
    {
        var reading = DiskEvidence.Unreadable("sdc", DiskKind.Spinning, isInSystemVolume: true, "statistics locked away");

        Assert.False(reading.CouldRead);
        Assert.Equal("sdc", reading.Name);
        Assert.Equal(DiskKind.Spinning, reading.Kind);
        Assert.True(reading.IsInSystemVolume);
        Assert.Equal("statistics locked away", reading.UnreadableReason);
        Assert.Null(reading.BusyPercent);
        Assert.Null(reading.MillisecondsPerRead);
        Assert.Null(reading.MillisecondsPerWrite);
        Assert.Null(reading.AverageQueueDepth);
    }

    [Fact]
    public void DiskEvidenceToString_Unreadable_PrintsNoFigure()
    {
        // The disk name is chosen with no digit so that a leaked figure is the only digit possible.
        var text = DiskEvidence.Unreadable("sdc", DiskKind.Unknown, isInSystemVolume: null, "statistics locked away").ToString();

        Assert.Contains("statistics locked away", text);
        AssertNoFigure(text);
    }

    [Fact]
    public void DiskKind_UnknownIsZero_SoADefaultIsNeverTakenForAKind()
    {
        Assert.Equal(DiskKind.Unknown, default(DiskKind));
    }

    [Fact]
    public void DiskEvidenceSet_OneDiskCanFailWhileTheSetReads()
    {
        var set = DiskEvidenceSet.Read(
        [
            DiskEvidence.Read("nvme0n1", DiskKind.SolidState, isInSystemVolume: true, 2, null, null, null),
            DiskEvidence.Unreadable("sdc", DiskKind.Spinning, isInSystemVolume: true, "statistics locked away"),
        ]);

        Assert.True(set.CouldRead);
        Assert.Equal(2, set.Disks.Count);
        Assert.True(set.Disks[0].CouldRead);
        Assert.False(set.Disks[1].CouldRead);
    }

    [Fact]
    public void DiskEvidenceSetUnreadable_HasNoDisks_AndPrintsNoFigure()
    {
        var set = DiskEvidenceSet.Unreadable("the disks could not be listed");

        Assert.False(set.CouldRead);
        Assert.Equal("the disks could not be listed", set.UnreadableReason);
        Assert.Empty(set.Disks);
        AssertNoFigure(set.ToString());
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
    public void ProcessRead_FiguresTheePlatformRefuses_AreNull_AndTheProcessIsStillNamed()
    {
        // The macOS case: an ordinary user is refused the figures for roughly three hundred of the
        // machine's seven hundred processes, and the busiest process on the machine is routinely one of
        // them. It belongs IN the list, named, with null figures - never dropped and never zeroed, or the
        // probe whose job is to name the culprit cannot see the culprit.
        var refused = new ProcessReading(501, 1, "mds_stores", null, null, null, null, null);

        Assert.Equal("mds_stores", refused.Name);
        Assert.Equal(501, refused.Pid);
        Assert.Null(refused.CpuPercent);
        Assert.Null(refused.MemoryBytes);
        Assert.Contains("mds_stores", refused.ToString());
        Assert.Contains("processor not readable", refused.ToString());
        Assert.Contains("memory not readable", refused.ToString());
    }

    [Fact]
    public void ProcessToString_WithFigures_PrintsThem()
    {
        var process = new ProcessReading(4242, 1, "dotnet", null, 12.5, 64UL * 1024 * 1024, null, null);

        Assert.Contains("12.5% processor", process.ToString());
        Assert.Contains("64 MB", process.ToString());
    }

    [Fact]
    public void SessionUsage_SaysHowManyProcessesCouldNotBeMeasured_SoATotalIsNotMistakenForTheWholeTree()
    {
        // A tree can hold processes whose figures the platform refused. Summing them as zero would report
        // the session as cheaper than it is, so the count travels and a screen can say "at least".
        var partial = new SessionUsage("session-one", RootPid: 4242, CpuPercent: 14.0,
            MemoryBytes: 512UL * 1024 * 1024, Pids: [4242, 4243, 4244], ProcessesWithUnknownFigures: 2);

        Assert.Equal(2, partial.ProcessesWithUnknownFigures);

        var complete = new SessionUsage("session-two", RootPid: 99, CpuPercent: 1.0,
            MemoryBytes: 1024, Pids: [99], ProcessesWithUnknownFigures: 0);

        Assert.Equal(0, complete.ProcessesWithUnknownFigures);
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
