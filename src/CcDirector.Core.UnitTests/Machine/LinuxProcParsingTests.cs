using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The Linux resource parsers, run against text captured on devlinux on 2026-10-15 - the machine whose
/// root volume then spanned a spinning hard drive (sdc) and two solid-state disks, captured while it was
/// slow because of that drive. The fixture is committed under TestData/LinuxResources so these run on every
/// platform, not only on Linux.
/// </summary>
[Trait("Category", "Resources")]
public class LinuxProcParsingTests
{
    internal static string Fixture(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "TestData", "LinuxResources", "devlinux-2026-10-15", .. parts]);

    private static string[] Lines(params string[] parts) => File.ReadAllLines(Fixture(parts));
    private static string Text(params string[] parts) => File.ReadAllText(Fixture(parts));

    private const ulong SwapTotalKilobytes = 8388604;
    private const ulong SwapFreeKilobytes = 6259236;

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

    [Fact]
    public void ParseSwap_RealFile_ReadsTotalAndUsed()
    {
        var swap = LinuxProcParsing.ParseSwap(Lines("snapshot-1", "proc", "meminfo"));

        Assert.Equal((SwapTotalKilobytes * 1024, (SwapTotalKilobytes - SwapFreeKilobytes) * 1024), swap);
    }

    [Fact]
    public void ParseSwap_MoreFreeThanTotal_IsNullNotAMadeUpZero()
    {
        Assert.Null(LinuxProcParsing.ParseSwap(["SwapTotal:  1000 kB", "SwapFree:   2000 kB"]));
    }

    [Fact]
    public void ParseCpuTimes_RealFile_ReadsTheKernelsBlockedTaskCount()
    {
        Assert.Equal(3, LinuxProcParsing.ParseCpuTimes(Lines("snapshot-1", "proc", "stat"))?.ProcessesBlocked);
        Assert.Equal(7, LinuxProcParsing.ParseCpuTimes(Lines("snapshot-2", "proc", "stat"))?.ProcessesBlocked);
    }

    [Fact]
    public void ParseCpuTimes_NoBlockedLine_IsNullNotZero()
    {
        Assert.Null(LinuxProcParsing.ParseCpuTimes(["cpu  1 2 3 4 5 6 7 8 0 0"])?.ProcessesBlocked);
    }

    [Fact]
    public void ParseSwap_NoSwapConfigured_IsZeroNotMissing()
    {
        Assert.Equal((0UL, 0UL), LinuxProcParsing.ParseSwap(["SwapTotal:             0 kB", "SwapFree:              0 kB"]));
    }

    [Fact]
    public void ParseSwap_MissingLine_ReturnsNull()
    {
        Assert.Null(LinuxProcParsing.ParseSwap(["MemTotal:       16316576 kB", "SwapTotal:      1 kB"]));
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
        Assert.Equal(35253519UL, sdc.MillisecondsReading);
        Assert.Equal(6660415UL, sdc.WritesCompleted);
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
    public void ParseLoadAverageOneMinute_RealFile_ReadsTheFirstField()
    {
        Assert.Equal(22.25, LinuxProcParsing.ParseLoadAverageOneMinute(Text("snapshot-2", "proc", "loadavg")));
    }

    [Fact]
    public void ParseLoadAverageOneMinute_EmptyOrGarbled_ReturnsNull()
    {
        Assert.Null(LinuxProcParsing.ParseLoadAverageOneMinute(""));
        Assert.Null(LinuxProcParsing.ParseLoadAverageOneMinute("x.y 1 2 3/4 5"));
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
    [InlineData("zd0", false)]
    [InlineData("nbd0", false)]
    [InlineData("rbd0", false)]
    [InlineData("drbd0", false)]
    [InlineData("mmcblk0", true)]
    [InlineData("mmcblk0boot0", false)]
    [InlineData("mmcblk0rpmb", false)]
    public void IsPhysicalDisk_ByName(string name, bool expected)
    {
        Assert.Equal(expected, LinuxProcParsing.IsPhysicalDisk(new LinuxBlockDevice(name, 0, [], null, [])));
    }

    [Fact]
    public void ReadBlockDevices_CapturedSysBlock_FindsThreePhysicalDisksAndWhichOneSpins()
    {
        var devices = LinuxProcParsing.ReadBlockDevices(Fixture("sys", "block"));

        Assert.Equal(1, devices["sdc"].Rotational);
        Assert.Equal(0, devices["sda"].Rotational);
        Assert.Equal(0, devices["sdb"].Rotational);
        Assert.Equal(["sda1", "sdb1", "sdc3"], devices["dm-0"].Slaves);
        Assert.Equal(new LinuxDeviceNumber(252, 0), devices["dm-0"].DeviceNumber);
        Assert.Equal(["sdc1", "sdc2", "sdc3"], devices["sdc"].Partitions.Select(p => p.Name).ToArray());
        Assert.Equal(new LinuxDeviceNumber(8, 35), devices["sdc"].Partitions.Single(p => p.Name == "sdc3").DeviceNumber);
        Assert.Equal(["sda", "sdb", "sdc"], devices.Values.Where(LinuxProcParsing.IsPhysicalDisk).Select(d => d.Name).Order().ToArray());
    }

    // ----- the system volume: /proc/self/mountinfo and the disks under / --------------------------

    [Fact]
    public void ParseRootMount_RealFile_IsTheLogicalVolume()
    {
        var root = LinuxProcParsing.ParseRootMount(Lines("snapshot-1", "proc", "self", "mountinfo"));

        Assert.NotNull(root);
        Assert.Equal(new LinuxDeviceNumber(252, 0), root.DeviceNumber);
        Assert.Equal("/dev/mapper/ubuntu--vg-ubuntu--lv", root.Source);
    }

    [Fact]
    public void ParseRootMount_MountedTwice_TheLastMountIsTheOneInForce()
    {
        var root = LinuxProcParsing.ParseRootMount([
            "31 1 8:2 / / rw,relatime shared:1 - ext4 /dev/sda2 rw",
            "99 31 252:0 / / rw,relatime - ext4 /dev/mapper/vg-root rw",
        ]);

        Assert.Equal(new LinuxDeviceNumber(252, 0), root?.DeviceNumber);
    }

    [Fact]
    public void ParseRootMount_NoRootOrGarbled_ReturnsNull()
    {
        Assert.Null(LinuxProcParsing.ParseRootMount(["25 31 0:23 / /sys rw - sysfs sysfs rw", "garbage", ""]));
        Assert.Null(LinuxProcParsing.ParseRootMount(["31 1 x:y / / rw - ext4 /dev/sda2 rw"]));
    }

    [Fact]
    public void ResolveRootDisks_Devlinux_GivesAllThreeDisksThroughTheLogicalVolume()
    {
        var devices = LinuxProcParsing.ReadBlockDevices(Fixture("sys", "block"));
        var root = LinuxProcParsing.ParseRootMount(Lines("snapshot-1", "proc", "self", "mountinfo"));

        Assert.NotNull(root);
        Assert.Equal(["sda", "sdb", "sdc"], LinuxProcParsing.ResolveRootDisks(root, devices));
    }

    private static Dictionary<string, LinuxBlockDevice> Devices(params LinuxBlockDevice[] devices) =>
        devices.ToDictionary(d => d.Name, StringComparer.Ordinal);

    private static readonly LinuxBlockDevice Sda = new("sda", 0, [], new(8, 0), [new("sda1", new(8, 1)), new("sda2", new(8, 2))]);
    private static readonly LinuxBlockDevice Nvme = new("nvme0n1", 0, [], new(259, 0), [new("nvme0n1p2", new(259, 2))]);

    [Fact]
    public void ResolveRootDisks_RootOnAPartition_GivesItsDisk()
    {
        var disks = LinuxProcParsing.ResolveRootDisks(new LinuxRootMount(new(8, 2), "/dev/sda2"), Devices(Sda, Nvme));

        Assert.Equal(["sda"], disks);
    }

    [Fact]
    public void ResolveRootDisks_BtrfsAnonymousDeviceNumber_FindsTheDiskByTheMountSource()
    {
        var disks = LinuxProcParsing.ResolveRootDisks(new LinuxRootMount(new(0, 31), "/dev/nvme0n1p2"), Devices(Sda, Nvme));

        Assert.Equal(["nvme0n1"], disks);
    }

    [Fact]
    public void ResolveRootDisks_RaidUnderALogicalVolume_FollowsEveryLayer()
    {
        var sdb = new LinuxBlockDevice("sdb", 1, [], new(8, 16), [new("sdb1", new(8, 17))]);
        var md = new LinuxBlockDevice("md0", null, ["sda1", "sdb1"], new(9, 0), []);
        var dm = new LinuxBlockDevice("dm-0", null, ["md0"], new(252, 0), []);

        var disks = LinuxProcParsing.ResolveRootDisks(new LinuxRootMount(new(252, 0), "/dev/mapper/vg-root"), Devices(Sda, sdb, md, dm));

        Assert.Equal(["sda", "sdb"], disks);
    }

    [Fact]
    public void ResolveRootDisks_UnknownDevice_IsNullNotEmpty()
    {
        // Null means "cannot tell which disks back /", which the probe reports as null on every disk -
        // an empty list would claim, falsely, that no disk backs the root volume.
        Assert.Null(LinuxProcParsing.ResolveRootDisks(new LinuxRootMount(new(253, 7), "/dev/vdz"), Devices(Sda)));
        Assert.Null(LinuxProcParsing.ResolveRootDisks(new LinuxRootMount(new(0, 31), "overlay"), Devices(Sda)));
    }

    [Fact]
    public void ResolveRootDisks_CycleInTheSlaves_DoesNotRecurseForever()
    {
        var a = new LinuxBlockDevice("dm-0", null, ["dm-1"], new(252, 0), []);
        var b = new LinuxBlockDevice("dm-1", null, ["dm-0"], new(252, 1), []);

        var disks = LinuxProcParsing.ResolveRootDisks(new LinuxRootMount(new(252, 0), "/dev/dm-0"), Devices(a, b));

        Assert.NotNull(disks);
        Assert.Empty(disks);
    }
}
