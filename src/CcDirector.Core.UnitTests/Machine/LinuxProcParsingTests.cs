using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The Linux resource parsers, run against text captured on devlinux on 2026-10-15 - the machine whose
/// root volume spans a spinning hard drive (sdc) and two solid-state disks, captured while it was slow
/// because of that drive. The fixture is committed under TestData/LinuxResources so these run on every
/// platform, not only on Linux.
/// </summary>
[Trait("Category", "Resources")]
public class LinuxProcParsingTests
{
    internal static string Fixture(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "TestData", "LinuxResources", "devlinux-2026-10-15", .. parts]);

    private static string[] Lines(params string[] parts) => File.ReadAllLines(Fixture(parts));
    private static string Text(params string[] parts) => File.ReadAllText(Fixture(parts));

    // ----- /proc/stat -------------------------------------------------------------------------------

    [Fact]
    public void ParseCpuTimes_RealFile_ReadsMachineTicksCoresAndBootTime()
    {
        var times = LinuxProcParsing.ParseCpuTimes(Lines("snapshot-1", "proc", "stat"));

        Assert.NotNull(times);
        Assert.Equal(696431256UL, times.Machine.Total);
        Assert.Equal(27074407UL, times.Machine.Busy);
        Assert.Equal(32, times.Cores.Count);
        Assert.Equal(1791851717L, times.BootTimeUnixSeconds);
    }

    [Fact]
    public void ParseCpuTimes_IoWaitIsNotBusy()
    {
        // user nice system idle iowait irq softirq steal: only the idle 100 and the iowait 900 are not busy.
        var times = LinuxProcParsing.ParseCpuTimes(["cpu  10 20 30 100 900 5 5 0 0 0"]);

        Assert.NotNull(times);
        Assert.Equal(1070UL, times.Machine.Total);
        Assert.Equal(70UL, times.Machine.Busy);
    }

    [Fact]
    public void ParseCpuTimes_NoAggregateLine_ReturnsNull()
    {
        Assert.Null(LinuxProcParsing.ParseCpuTimes(["cpu0 1 2 3 4 5 6 7 8 0 0", "btime 5"]));
    }

    [Fact]
    public void ParseCpuTimes_NoBootTime_ReturnsNullBootTime()
    {
        var times = LinuxProcParsing.ParseCpuTimes(["cpu  1 2 3 4 5 6 7 8 0 0"]);

        Assert.NotNull(times);
        Assert.Null(times.BootTimeUnixSeconds);
        Assert.Empty(times.Cores);
    }

    // ----- /proc/diskstats --------------------------------------------------------------------------

    [Fact]
    public void ParseDiskStats_RealFile_ReadsTheSpinningDriveCounters()
    {
        var disks = LinuxProcParsing.ParseDiskStats(Lines("snapshot-1", "proc", "diskstats"));

        var sdc = Assert.Single(disks, d => d.Name == "sdc");
        Assert.Equal(8, sdc.Major);
        Assert.Equal(32, sdc.Minor);
        Assert.Equal(1263713UL, sdc.ReadsCompleted);
        Assert.Equal(94698220UL, sdc.SectorsRead);
        Assert.Equal(35253519UL, sdc.MillisecondsReading);
        Assert.Equal(6660415UL, sdc.WritesCompleted);
        Assert.Equal(551614016UL, sdc.SectorsWritten);
        Assert.Equal(75762302UL, sdc.MillisecondsWriting);
        Assert.Equal(4UL, sdc.InFlight);
        Assert.Equal(24798247UL, sdc.MillisecondsBusy);
        Assert.Equal(134827763UL, sdc.WeightedMillisecondsBusy);
        Assert.Contains(disks, d => d.Name == "dm-0" && d.Major == 252);
        Assert.Contains(disks, d => d.Name == "sdc3");
    }

    [Fact]
    public void ParseDiskStats_ShortOrGarbledLines_AreSkipped()
    {
        var disks = LinuxProcParsing.ParseDiskStats(["8 0 sda 1 2 3", "x 0 sda 1 2 3 4 5 6 7 8 9 10 11", ""]);

        Assert.Empty(disks);
    }

    // ----- /proc/pressure, /proc/loadavg ------------------------------------------------------------

    [Fact]
    public void ParsePressure_RealIoFile_ReadsSomeAverage10()
    {
        Assert.Equal(9.67, LinuxProcParsing.ParsePressureSomeAverage10(Text("snapshot-1", "proc", "pressure", "io")));
    }

    [Fact]
    public void ParsePressure_NoSomeLine_ReturnsNull()
    {
        Assert.Null(LinuxProcParsing.ParsePressureSomeAverage10("full avg10=1.00 avg60=0.00 avg300=0.00 total=0\n"));
        Assert.Null(LinuxProcParsing.ParsePressureSomeAverage10(""));
    }

    [Fact]
    public void ParseLoadAverageRunning_RealFile_ReadsRunningCount()
    {
        Assert.Equal(6, LinuxProcParsing.ParseLoadAverageRunning(Text("snapshot-1", "proc", "loadavg")));
    }

    [Fact]
    public void ParseLoadAverageRunning_Garbled_ReturnsNull()
    {
        Assert.Null(LinuxProcParsing.ParseLoadAverageRunning("1.0 2.0"));
        Assert.Null(LinuxProcParsing.ParseLoadAverageRunning("1.0 2.0 3.0 x/12 5"));
    }

    // ----- /proc/net/dev ----------------------------------------------------------------------------

    [Fact]
    public void ParseNetworkDevices_RealFile_SkipsLoopbackAndHeaders()
    {
        var interfaces = LinuxProcParsing.ParseNetworkDevices(Lines("snapshot-1", "proc", "net", "dev"));

        Assert.DoesNotContain(interfaces, i => i.Name == "lo");
        var wired = Assert.Single(interfaces, i => i.Name == "enp5s0");
        Assert.Equal(21677739638UL, wired.ReceivedBytes);
        Assert.Equal(6291881005UL, wired.SentBytes);
        Assert.Contains(interfaces, i => i.Name == "tailscale0");
    }

    // ----- /proc/self/mountinfo ---------------------------------------------------------------------

    [Fact]
    public void ParseMountInfo_RealFile_ReadsRootOnTheLogicalVolume()
    {
        var mounts = LinuxProcParsing.ParseMountInfo(Lines("snapshot-1", "proc", "mountinfo"));

        var root = Assert.Single(mounts, m => m.MountPoint == "/");
        Assert.Equal(252, root.Major);
        Assert.Equal(0, root.Minor);
        Assert.Equal("ext4", root.FileSystemType);
        Assert.Equal("/dev/mapper/ubuntu--vg-ubuntu--lv", root.Source);
        Assert.Contains(mounts, m => m.MountPoint == "/boot/efi" && m.Major == 8 && m.Minor == 33 && m.FileSystemType == "vfat");
    }

    [Fact]
    public void ParseMountInfo_EscapedSpaceInMountPoint_IsDecoded()
    {
        var mounts = LinuxProcParsing.ParseMountInfo(
            ["99 31 8:17 / /media/my\\040disk rw,relatime shared:1 - ext4 /dev/sdb1 rw"]);

        Assert.Equal("/media/my disk", Assert.Single(mounts).MountPoint);
    }

    [Fact]
    public void ParseMountInfo_NoOptionalFields_StillFindsTypeAndSource()
    {
        var mounts = LinuxProcParsing.ParseMountInfo(["20 1 8:2 / /data rw,relatime - xfs /dev/sda2 rw"]);

        var data = Assert.Single(mounts);
        Assert.Equal("xfs", data.FileSystemType);
        Assert.Equal("/dev/sda2", data.Source);
    }

    [Fact]
    public void SelectVolumes_RealFiles_GivesRootBootAndEfiButNoSnapOrVirtualFileSystem()
    {
        var mounts = LinuxProcParsing.ParseMountInfo(Lines("snapshot-1", "proc", "mountinfo"));
        var disks = LinuxProcParsing.ParseDiskStats(Lines("snapshot-1", "proc", "diskstats"));

        var volumes = LinuxProcParsing.SelectVolumes(mounts, disks);

        Assert.Equal(["/", "/boot", "/boot/efi"], volumes.Select(v => v.MountPoint).ToArray());
        Assert.Equal(["dm-0", "sdc2", "sdc1"], volumes.Select(v => v.DeviceName).ToArray());
    }

    [Fact]
    public void SelectVolumes_SameDeviceMountedTwice_IsOneVolumeUnderTheFirstMount()
    {
        LinuxMount[] mounts =
        [
            new(8, 1, "/", "ext4", "/dev/sda1"),
            new(8, 1, "/var/lib/docker/bind", "ext4", "/dev/sda1"),
        ];
        LinuxDiskCounters[] disks = [new(8, 1, "sda1", 0, 0, 0, 0, 0, 0, 0, 0, 0)];

        Assert.Equal("/", Assert.Single(LinuxProcParsing.SelectVolumes(mounts, disks)).MountPoint);
    }

    [Fact]
    public void SelectVolumes_BtrfsAnonymousDeviceNumber_MatchesBySourceName()
    {
        LinuxMount[] mounts = [new(0, 31, "/", "btrfs", "/dev/nvme0n1p2"), new(0, 32, "/tmp", "tmpfs", "tmpfs")];
        LinuxDiskCounters[] disks = [new(259, 2, "nvme0n1p2", 0, 0, 0, 0, 0, 0, 0, 0, 0)];

        var volume = Assert.Single(LinuxProcParsing.SelectVolumes(mounts, disks));
        Assert.Equal("nvme0n1p2", volume.DeviceName);
    }

    // ----- /proc/<pid>/stat, statm, io --------------------------------------------------------------

    [Fact]
    public void ParseProcessStat_RealDotnet_ReadsParentTicksAndStart()
    {
        var stat = LinuxProcParsing.ParseProcessStat(Text("snapshot-1", "proc", "3911504", "stat"));

        Assert.NotNull(stat);
        Assert.Equal(3911504, stat.ProcessId);
        Assert.Equal("dotnet", stat.Name);
        Assert.Equal('S', stat.State);
        Assert.Equal(3911503, stat.ParentProcessId);
        Assert.Equal(1826UL, stat.CpuTicks);
        Assert.Equal(21491067UL, stat.StartTimeTicks);
    }

    [Fact]
    public void ParseProcessStat_NameWithSpaces_IsReadWhole()
    {
        var stat = LinuxProcParsing.ParseProcessStat(Text("snapshot-1", "proc", "2339886", "stat"));

        Assert.NotNull(stat);
        Assert.Equal("npm exec astro ", stat.Name);
        Assert.Equal(2293, stat.ParentProcessId);
        Assert.Equal(47UL, stat.CpuTicks);
    }

    [Fact]
    public void ParseProcessStat_NameWithParentheses_ReadsFromTheLastOne()
    {
        var stat = LinuxProcParsing.ParseProcessStat("42 (a) b (c)) R 7 42 42 0 -1 0 0 0 0 0 3 4 0 0 20 0 1 0 99 0 0");

        Assert.NotNull(stat);
        Assert.Equal("a) b (c)", stat.Name);
        Assert.Equal('R', stat.State);
        Assert.Equal(7, stat.ParentProcessId);
        Assert.Equal(7UL, stat.CpuTicks);
        Assert.Equal(99UL, stat.StartTimeTicks);
    }

    [Fact]
    public void ParseProcessStat_KernelThreadWaitingOnDisk_HasStateD()
    {
        var stat = LinuxProcParsing.ParseProcessStat(Text("snapshot-1", "proc", "647", "stat"));

        Assert.NotNull(stat);
        Assert.Equal("jbd2/dm-0-8", stat.Name);
        Assert.Equal('D', stat.State);
    }

    [Fact]
    public void ParseProcessStat_Garbled_ReturnsNull()
    {
        Assert.Null(LinuxProcParsing.ParseProcessStat(""));
        Assert.Null(LinuxProcParsing.ParseProcessStat("42 (x) R 7"));
        Assert.Null(LinuxProcParsing.ParseProcessStat("x (y) R 7 1 1 0 -1 0 0 0 0 0 3 4 0 0 20 0 1 0 99"));
    }

    [Fact]
    public void ParseStatmResidentPages_RealFile_ReadsSecondField()
    {
        Assert.Equal(54589UL, LinuxProcParsing.ParseStatmResidentPages(Text("snapshot-1", "proc", "3911504", "statm")));
        Assert.Null(LinuxProcParsing.ParseStatmResidentPages("12"));
    }

    [Fact]
    public void ParseProcessIo_RealFile_ReadsStorageBytesNotCharacters()
    {
        var io = LinuxProcParsing.ParseProcessIo(Lines("snapshot-1", "proc", "3766989", "io"));

        Assert.Equal((2900901888UL, 1410351104UL), io);
    }

    [Fact]
    public void ParseProcessIo_MissingLines_ReturnsNull()
    {
        Assert.Null(LinuxProcParsing.ParseProcessIo(["rchar: 5", "read_bytes: 4"]));
        Assert.Null(LinuxProcParsing.ParseProcessIo([]));
    }

    // ----- /sys/block topology ----------------------------------------------------------------------

    private static readonly Dictionary<string, LinuxBlockDevice> Devlinux = new()
    {
        ["sda"] = new("sda", 0, [], ["sda1"]),
        ["sdb"] = new("sdb", 0, [], ["sdb1"]),
        ["sdc"] = new("sdc", 1, [], ["sdc1", "sdc2", "sdc3"]),
        ["dm-0"] = new("dm-0", 1, ["sda1", "sdb1", "sdc3"], []),
        ["loop0"] = new("loop0", 1, [], []),
    };

    [Fact]
    public void ResolvePhysicalDisks_RootLogicalVolume_GivesAllThreeDisks()
    {
        var disks = LinuxProcParsing.ResolvePhysicalDisks("dm-0", Devlinux);

        Assert.Equal(["sda", "sdb", "sdc"], disks);
    }

    [Fact]
    public void ResolvePhysicalDisks_Partition_GivesItsDisk()
    {
        Assert.Equal(["sdc"], LinuxProcParsing.ResolvePhysicalDisks("sdc1", Devlinux));
    }

    [Fact]
    public void ResolvePhysicalDisks_NestedRaidUnderLvm_FollowsEveryLayer()
    {
        var devices = new Dictionary<string, LinuxBlockDevice>
        {
            ["nvme0n1"] = new("nvme0n1", 0, [], ["nvme0n1p1"]),
            ["nvme1n1"] = new("nvme1n1", 0, [], ["nvme1n1p1"]),
            ["md0"] = new("md0", 0, ["nvme0n1p1", "nvme1n1p1"], []),
            ["dm-1"] = new("dm-1", 0, ["md0"], []),
        };

        Assert.Equal(["nvme0n1", "nvme1n1"], LinuxProcParsing.ResolvePhysicalDisks("dm-1", devices));
    }

    [Fact]
    public void ResolvePhysicalDisks_LoopOrUnknown_GivesNothing()
    {
        Assert.Empty(LinuxProcParsing.ResolvePhysicalDisks("loop0", Devlinux));
        Assert.Empty(LinuxProcParsing.ResolvePhysicalDisks("nosuch", Devlinux));
    }

    [Fact]
    public void ResolvePhysicalDisks_Cycle_DoesNotRecurseForever()
    {
        var devices = new Dictionary<string, LinuxBlockDevice>
        {
            ["dm-0"] = new("dm-0", 0, ["dm-1"], []),
            ["dm-1"] = new("dm-1", 0, ["dm-0"], []),
        };

        Assert.Empty(LinuxProcParsing.ResolvePhysicalDisks("dm-0", devices));
    }

    [Theory]
    [InlineData("sda", true)]
    [InlineData("nvme0n1", true)]
    [InlineData("vda", true)]
    [InlineData("loop3", false)]
    [InlineData("ram0", false)]
    [InlineData("zram0", false)]
    [InlineData("dm-0", false)]
    [InlineData("md127", false)]
    [InlineData("sr0", false)]
    public void IsPhysicalDisk_ByName(string name, bool expected)
    {
        Assert.Equal(expected, LinuxProcParsing.IsPhysicalDisk(new LinuxBlockDevice(name, 0, [], [])));
    }
}
