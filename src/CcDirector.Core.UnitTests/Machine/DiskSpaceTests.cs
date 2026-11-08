using CcDirector.Core.Machine;
using CcDirector.Core.Machine.ResourceSummaries;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// Disk Space on the Meter (the owner's answers of 7 November 2026): the DISK bar is always shown beside CPU and MEM,
/// amber under 25 GB free and red under 10 GB free on any drive; a drive that cannot be read says so and is never
/// "0 GB"; and the five-minute upload carries each drive's total, free at the end, lowest free and crossings. Every
/// reading here is hand-made, so nothing reads the machine the test runs on.
/// </summary>
[Trait("Category", "Resources")]
public class DiskSpaceTests
{
    private const ulong Gb = 1024UL * 1024 * 1024;
    private const ulong Mb = 1024UL * 1024;
    private static readonly DateTime W0 = new(2026, 11, 7, 15, 0, 0, DateTimeKind.Utc);

    private static DiskSpaceSet Drives(params DriveSpaceReading[] drives) => DiskSpaceSet.Read(drives);

    private static ResourceSamplerState State(ResourceSample sample) =>
        new(sample, new ResourcePressureVerdict(
        [
            new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Normal, "cpu"),
            new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Normal, "memory"),
            new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal, "swap"),
        ]));

    private static ResourceMeterView Meter(DiskSpaceSet disks, IReadOnlyList<TimelineSample>? history = null) =>
        ResourceMeterFold.Build(State(new FakeSampleBuilder().WithDiskSpace(disks).Build()), "SOREN_NORTH", 2, null,
            TimeSpan.FromSeconds(3), history ?? []);

    // ---- the lines, in gigabytes ----

    [Theory]
    [InlineData(25 * Gb, ResourceLevel.Normal)]
    [InlineData(25 * Gb - 1, ResourceLevel.Tight)]
    [InlineData(10 * Gb, ResourceLevel.Tight)]
    [InlineData(10 * Gb - 1, ResourceLevel.Critical)]
    [InlineData(0UL, ResourceLevel.Critical)]
    [InlineData(325 * Gb, ResourceLevel.Normal)]
    public void Level_AmberUnder25GbAndRedUnder10Gb_OnAnyDrive(ulong free, ResourceLevel expected)
    {
        Assert.Equal(expected, DiskSpacePressure.Level(DriveSpaceReading.Read("C:", 931 * Gb, free)));
    }

    [Fact]
    public void Level_IsInGigabytesNotAShare_ABigDriveAt8PercentFreeIsNormal()
    {
        // D: on SOREN_NORTH on 7 November: 325 GB free of 3.8 TB. A percentage line would have called it amber.
        Assert.Equal(ResourceLevel.Normal, DiskSpacePressure.Level(DriveSpaceReading.Read("D:", 3815 * Gb, 325 * Gb)));
    }

    [Fact]
    public void Level_UnreadableDrive_IsUnknown_NeverNormalOrRed()
    {
        Assert.Equal(ResourceLevel.Unknown, DiskSpacePressure.Level(DriveSpaceReading.Unreadable("E:", "the device is not ready")));
    }

    [Fact]
    public void Tightest_IsTheReadableDriveWithLeastFree_AnUnreadableOneNeverStandsIn()
    {
        var set = Drives(
            DriveSpaceReading.Read("C:", 931 * Gb, 6 * Gb),
            DriveSpaceReading.Unreadable("E:", "the device is not ready"),
            DriveSpaceReading.Read("D:", 3815 * Gb, 325 * Gb));

        Assert.Equal("C:", DiskSpacePressure.Tightest(set)!.Value.Name);
        Assert.Null(DiskSpacePressure.Tightest(Drives(DriveSpaceReading.Unreadable("E:", "not ready"))));
    }

    // ---- the DISK bar ----

    [Fact]
    public void Meter_DiskBar_ShowsTheTightestDrive_FillsWithHowFull_TextIsWhatIsLeft()
    {
        var meter = Meter(Drives(DriveSpaceReading.Read("C:", 931 * Gb, (ulong)(6.4 * Gb)), DriveSpaceReading.Read("D:", 3815 * Gb, 325 * Gb)));

        Assert.Equal("DISK", meter.Disk.Label);
        Assert.True(meter.Disk.CouldRead);
        Assert.Equal("C: 6 GB", meter.Disk.PercentText);
        Assert.Equal(1 - 6.4 / 931, meter.Disk.Fraction!.Value, 3);
        Assert.Equal(ResourceLevel.Critical, meter.Disk.Level);
        Assert.Equal(ResourceLevel.Critical, meter.FrameLevel);
        Assert.Equal("CPU 28% MEM 55% C: 6 GB", meter.NarrowText);
        Assert.Contains("a drive has under 10 GB free", meter.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void Meter_DiskBar_IsShownWhenAllIsWell_Green()
    {
        var meter = Meter(Drives(DriveSpaceReading.Read("C:", 931 * Gb, 260 * Gb)));

        Assert.Equal("C: 260 GB", meter.Disk.PercentText);
        Assert.Equal(ResourceLevel.Normal, meter.Disk.Level);
        Assert.Equal(ResourceLevel.Normal, meter.FrameLevel);
    }

    [Fact]
    public void Meter_DiskBar_Amber_ColoursTheFrameAmber()
    {
        var meter = Meter(Drives(DriveSpaceReading.Read("C:", 931 * Gb, 20 * Gb)));

        Assert.Equal(ResourceLevel.Tight, meter.Disk.Level);
        Assert.Equal(ResourceLevel.Tight, meter.FrameLevel);
        Assert.Contains("a drive has under 25 GB free", meter.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void Meter_DiskBar_UnderAGigabyte_SaysMegabytes_NeverZeroGigabytes()
    {
        var meter = Meter(Drives(DriveSpaceReading.Read("C:", 931 * Gb, 600 * Mb)));

        Assert.Equal("C: 600 MB", meter.Disk.PercentText);
    }

    [Fact]
    public void Meter_DrivesCannotBeListed_TheBarIsUnreadableWithTheReason_NeverZero()
    {
        var meter = Meter(DiskSpaceSet.Unreadable("the mount table could not be read"));

        Assert.False(meter.Disk.CouldRead);
        Assert.Null(meter.Disk.Fraction);
        Assert.Equal(ResourceViewText.CannotReadText, meter.Disk.PercentText);
        Assert.Equal(ResourceLevel.Unknown, meter.Disk.Level);
        Assert.Equal("Disk space cannot read: the mount table could not be read", meter.Disk.Tooltip);
        Assert.Equal("CPU 28% MEM 55% DISK -", meter.NarrowText);
        Assert.DoesNotContain(meter.TooltipLines, line => System.Text.RegularExpressions.Regex.IsMatch(line, @"(^|\s)0(\.0+)? (GB|MB) free"));
        Assert.Contains("Disk space cannot read: the mount table could not be read", meter.TooltipLines);
    }

    [Fact]
    public void Meter_EveryDriveUnreadable_TheBarSaysWhy()
    {
        var meter = Meter(Drives(DriveSpaceReading.Unreadable("C:", "access is denied")));

        Assert.False(meter.Disk.CouldRead);
        Assert.Equal("Disk space cannot read: no drive could be read; C:: access is denied", meter.Disk.Tooltip);
        Assert.Contains("C: cannot read: access is denied", meter.TooltipLines);
    }

    [Fact]
    public void Meter_Tooltip_ListsEveryDrive_AndTheLossOfALosingOne()
    {
        var c = (ulong)(6.4 * Gb);
        var history = Enumerable.Range(0, 3)
            .Select(i => new TimelineSample(
                new FakeSampleBuilder().WithDiskSpace(Drives(
                    DriveSpaceReading.Read("C:", 931 * Gb, c + (ulong)(2 - i) * 4 * Gb),
                    DriveSpaceReading.Read("D:", 3815 * Gb, 325 * Gb))).Build(),
                TimeSpan.FromMinutes(30 * i)))
            .ToList();

        var meter = Meter(history[^1].Sample.DiskSpace, history);

        Assert.Contains("C: 6.40 GB free of 931 GB - red, under 10 GB", meter.TooltipLines);
        Assert.Contains("D: 325 GB free of 3815 GB - normal", meter.TooltipLines);
        Assert.Contains("C: lost 8.00 GB in the last 60 minutes", meter.TooltipLines);
        Assert.DoesNotContain(meter.TooltipLines, line => line.StartsWith("D: lost", StringComparison.Ordinal));
    }

    [Fact]
    public void Meter_NotStarted_TheDiskBarIsUnreadableToo()
    {
        var meter = ResourceMeterFold.NotStarted("SOREN_NORTH", "the probe could not be built");

        Assert.False(meter.Disk.CouldRead);
        Assert.Equal("CPU - MEM - DISK -", meter.NarrowText);
    }

    // ---- the trend ----

    [Fact]
    public void Trend_UnderAGigabyte_IsSteady_AndOneReadingHasNoTrend()
    {
        var steady = new List<(TimeSpan, DiskSpaceSet)>
        {
            (TimeSpan.Zero, Drives(DriveSpaceReading.Read("C:", 931 * Gb, 100 * Gb))),
            (TimeSpan.FromHours(3), Drives(DriveSpaceReading.Read("C:", 931 * Gb, 100 * Gb - 500 * Mb))),
        };
        Assert.Equal("steady", DiskSpaceText.Trend("C:", steady));
        Assert.Null(DiskSpaceText.Trend("C:", steady.Take(1).ToList()));
    }

    // ---- the detail tiles ----

    [Fact]
    public void Detail_HasATilePerDrive_AndAnUnreadableTileSaysSo()
    {
        var sample = new FakeSampleBuilder().WithDiskSpace(Drives(
            DriveSpaceReading.Read("C:", 931 * Gb, (ulong)(6.4 * Gb)),
            DriveSpaceReading.Unreadable("E:", "the device is not ready"))).Build();

        var detail = ResourceDetailFold.Build(State(sample), [sample], "SOREN_NORTH", [], FakeSampleBuilder.DefaultDirectorPid);

        Assert.Equal(2, detail.Disks.Count);
        Assert.Equal("Disk C:", detail.Disks[0].Title);
        Assert.Equal("6.40 GB free", detail.Disks[0].Headline);
        Assert.Equal("of 931 GB", detail.Disks[0].Detail);
        Assert.Equal(ResourceLevel.Critical, detail.Disks[0].Level);
        Assert.Equal("Disk E:", detail.Disks[1].Title);
        Assert.Equal(ResourceViewText.CannotReadText, detail.Disks[1].Headline);
        Assert.Equal("cannot read: the device is not ready", detail.Disks[1].Detail);
        Assert.Equal(ResourceLevel.Unknown, detail.Disks[1].Level);
    }

    // ---- reading the drives ----

    [Fact]
    public void ReadEach_OneDriveRefuses_ItIsUnreadableWithTheReason_AndTheOthersStillRead()
    {
        var set = DiskSpaceReader.ReadEach(
            [("C:", @"C:\"), ("E:", @"E:\"), ("D:", @"D:\")],
            path => path == @"E:\" ? throw new IOException("The device is not ready.") : (931 * Gb, 100 * Gb));

        Assert.True(set.CouldRead);
        Assert.Equal(new[] { "C:", "E:", "D:" }, set.Drives.Select(d => d.Name));
        Assert.True(set.Drives[0].CouldRead);
        Assert.False(set.Drives[1].CouldRead);
        Assert.Equal("The device is not ready.", set.Drives[1].UnreadableReason);
        Assert.Equal(0UL, set.Drives[1].FreeBytes);
        Assert.True(set.Drives[2].CouldRead);
    }

    [Fact]
    public void ReadEach_MoreFreeThanTotal_IsUnreadable_NotBelieved()
    {
        var set = DiskSpaceReader.ReadEach([("C:", @"C:\")], _ => (10 * Gb, 20 * Gb));

        Assert.False(set.Drives[0].CouldRead);
    }

    [Fact]
    public void ReadEach_ADriveOfZeroBytes_IsUnreadableWithAReason_NeverAZeroSizedDrive()
    {
        var set = DiskSpaceReader.ReadEach([("/mnt/x", "/mnt/x"), ("/", "/")], p => p == "/" ? (931 * Gb, 500 * Gb) : (0UL, 0UL));

        Assert.False(set.Drives[0].CouldRead);
        Assert.Contains("0 bytes in size", set.Drives[0].UnreadableReason, StringComparison.Ordinal);
        Assert.True(set.Drives[1].CouldRead);
    }

    [Fact]
    public void ReadEach_AnyOtherFailure_IsThatDrivesReason_AndTheOthersStillRead()
    {
        var set = DiskSpaceReader.ReadEach([("C:", @"C:\"), ("D:", @"D:\")],
            p => p == @"C:\" ? throw new InvalidOperationException("the device is not ready") : (931 * Gb, 500 * Gb));

        Assert.False(set.Drives[0].CouldRead);
        Assert.Equal("the device is not ready", set.Drives[0].UnreadableReason);
        Assert.True(set.Drives[1].CouldRead);
    }

    [Fact]
    public void SelectLinuxMounts_SkipsFuseAndAMountPointThatIsAFile_KeepsFuseblk()
    {
        const string mounts = """
            /dev/sda2 / ext4 rw 0 0
            /dev/fuse /mnt/remote fuse rw,nosuid 0 0
            /dev/fuse /mnt/sshfs fuse.sshfs rw,nosuid 0 0
            /dev/sdb1 /mnt/windows fuseblk rw 0 0
            /dev/sda2 /etc/hosts ext4 rw 0 0
            /dev/sdc1 /etc/hostname ext4 rw 0 0
            """;

        var picked = DiskSpaceReader.SelectLinuxMounts(mounts, p => !p.StartsWith("/etc/", StringComparison.Ordinal));

        Assert.Equal(new[] { "/", "/mnt/windows" }, picked.Select(p => p.Name));
    }

    [Fact]
    public void SelectLinuxMounts_KeepsRealReadWriteDrives_OnceEach_AndSkipsTheOnesThatWouldLie()
    {
        const string mounts = """
            /dev/nvme0n1p2 / ext4 rw,relatime 0 0
            proc /proc proc rw,nosuid 0 0
            tmpfs /run tmpfs rw,nosuid 0 0
            /dev/nvme0n1p1 /boot/efi vfat rw,relatime 0 0
            /dev/sda1 /boot ext4 rw,relatime 0 0
            /dev/loop3 /snap/core22/1612 squashfs ro,nodev 0 0
            /dev/sdb1 /home ext4 rw,relatime 0 0
            /dev/nvme0n1p2 /var/lib/containers ext4 rw,relatime 0 0
            server:/export /mnt/nas nfs4 rw,relatime 0 0
            //nas/share /mnt/smb cifs rw 0 0
            /dev/sdc1 /media/soren/USB vfat rw 0 0
            /dev/sdd1 /mnt/archive ext4 ro,relatime 0 0
            overlay /var/lib/containers/storage/overlay/x/merged overlay rw 0 0
            /dev/sde1 /mnt/big\040disk xfs rw 0 0
            """;

        var picked = DiskSpaceReader.SelectLinuxMounts(mounts, _ => true);

        Assert.Equal(new[] { "/", "/home", "/mnt/big disk" }, picked.Select(p => p.Name));
        Assert.Equal(new[] { "/", "/home", "/mnt/big disk" }, picked.Select(p => p.Path));
    }

    // ---- the five-minute upload ----

    private static ResourceSummarySample At(int index, params ResourceSummaryDriveSample[] drives) =>
        new(W0.AddSeconds(5 * index), 20, null, ResourceLevel.Normal, 8L << 30, 16L << 30, null, ResourceLevel.Normal, [], null, drives);

    [Fact]
    public void SummaryFold_CarriesEachDrive_AndAnUnreadableOneAsItsReason()
    {
        var sample = new FakeSampleBuilder().WithDiskSpace(Drives(
            DriveSpaceReading.Read("C:", 931 * Gb, 6 * Gb),
            DriveSpaceReading.Unreadable("E:", "not ready"))).Build();

        var folded = ResourceSummarySampleFold.From(State(sample), null)!;

        Assert.Equal(new ResourceSummaryDriveSample("C:", (long)(931 * Gb), (long)(6 * Gb), null), folded.Drives[0]);
        Assert.Equal(new ResourceSummaryDriveSample("E:", null, null, "not ready"), folded.Drives[1]);
    }

    [Fact]
    public void SummaryBuilder_PerDrive_TotalFreeAtEndLowestAndTheCrossings()
    {
        // C: falls from 30 GB to 8 GB across the window (amber, then red) and is cleaned up back to 30 GB at the end.
        var samples = Enumerable.Range(0, 60).Select(i =>
        {
            long free = i < 10 ? 30L << 30 : i < 30 ? 20L << 30 : i < 50 ? 8L << 30 : 30L << 30;
            return At(i, new ResourceSummaryDriveSample("C:", 931L << 30, free, null),
                new ResourceSummaryDriveSample("D:", 3815L << 30, 325L << 30, null));
        });

        var window = ResourceSummaryBuilder.Build(W0, "SOREN_NORTH", samples);

        Assert.Equal(new[] { "C:", "D:" }, window.Drives.Select(d => d.Name));
        var c = window.Drives[0];
        Assert.True(c.CouldRead);
        Assert.Equal(931L << 30, c.TotalBytes);
        Assert.Equal(30L << 30, c.FreeBytesAtEnd);
        Assert.Equal(8L << 30, c.LowestFreeBytes);
        var crossing = Assert.Single(c.Crossings);
        Assert.Equal(ResourceSummaryContract.Disk, crossing.Resource);
        Assert.Equal(ResourceSummaryContract.Red, crossing.Level);
        Assert.Equal(W0.AddSeconds(50), crossing.StartedUtc);
        Assert.Equal(200, crossing.Seconds);
        Assert.Equal(100 - 8.0 * 100 / 931, crossing.PeakPercent!.Value, 3);
        Assert.Empty(window.Drives[1].Crossings);
        Assert.Equal(325L << 30, window.Drives[1].LowestFreeBytes);
    }

    [Fact]
    public void SummaryBuilder_AmberOnly_IsAnAmberCrossing()
    {
        var samples = Enumerable.Range(0, 60).Select(i =>
            At(i, new ResourceSummaryDriveSample("C:", 931L << 30, i >= 30 ? 20L << 30 : 40L << 30, null)));

        var crossing = Assert.Single(ResourceSummaryBuilder.Build(W0, "SOREN_NORTH", samples).Drives[0].Crossings);

        Assert.Equal(ResourceSummaryContract.Amber, crossing.Level);
        Assert.Equal(150, crossing.Seconds);
    }

    [Fact]
    public void SummaryBuilder_ADriveNoSampleCouldRead_IsARowWithTheReasonAndNoFigures()
    {
        var samples = Enumerable.Range(0, 60).Select(i => At(i, new ResourceSummaryDriveSample("E:", null, null, "the device is not ready")));

        var e = Assert.Single(ResourceSummaryBuilder.Build(W0, "SOREN_NORTH", samples).Drives);

        Assert.False(e.CouldRead);
        Assert.Equal("the device is not ready", e.UnreadableReason);
        Assert.Null(e.TotalBytes);
        Assert.Null(e.FreeBytesAtEnd);
        Assert.Null(e.LowestFreeBytes);
        Assert.Empty(e.Crossings);
    }

    [Fact]
    public void SummaryBuilder_NoDriveReported_TheWindowHasNoDrives()
    {
        var window = ResourceSummaryBuilder.Build(W0, "SOREN_NORTH", Enumerable.Range(0, 60).Select(i => At(i)));

        Assert.Empty(window.Drives);
    }
}
