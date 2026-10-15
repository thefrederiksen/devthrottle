using CcDirector.Core.Machine;
using Xunit;
using static CcDirector.Core.Tests.Machine.ResourceSampleBuilder;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// "Why is it slow?" (mission sections 2 and 5). The fixture that must pass is devlinux on 2026-10-15:
/// the disk, the spinning hard drive sdc, with the processor and memory healthy.
/// </summary>
[Trait("Category", "Resources")]
public class SlownessDiagnoserTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    // -------------------------------------------------------------------------------------------
    // THE FIXTURE THAT MUST PASS: devlinux, 2026-10-15
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Diagnose_DevlinuxReadings_IsTheDiskAndNamesTheSpinningDriveSdc()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(90), Devlinux);

        var diagnosis = SlownessDiagnoser.Diagnose(history, DevlinuxSessions);

        Assert.True(diagnosis.CouldDiagnose);
        Assert.True(diagnosis.IsSlow);
        Assert.Equal(SlownessCause.Disk, diagnosis.Cause);
        Assert.NotNull(diagnosis.Culprit);
        Assert.Equal("sdc", diagnosis.Culprit.DiskName);
        Assert.Equal(DiskKind.Spinning, diagnosis.Culprit.DiskKind);
        Assert.Contains("sdc", diagnosis.Summary);
        Assert.Contains("hard drive", diagnosis.Summary);
    }

    [Fact]
    public void Diagnose_DevlinuxReadings_NamesTheBusiestWriterAndItsSession()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(90), Devlinux);

        var diagnosis = SlownessDiagnoser.Diagnose(history, DevlinuxSessions);

        Assert.NotNull(diagnosis.Culprit);
        Assert.Equal("dotnet test", diagnosis.Culprit.ProcessName);
        Assert.Equal(4100, diagnosis.Culprit.Pid);
        Assert.Equal("140", diagnosis.Culprit.SessionId);
        Assert.Contains("dotnet test", diagnosis.Summary);
        Assert.Contains("session 140", diagnosis.Summary);
    }

    [Fact]
    public void Diagnose_DevlinuxReadings_EvidenceCarriesTheFiguresAndTheRootVolume()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(90), Devlinux);

        var diagnosis = SlownessDiagnoser.Diagnose(history, DevlinuxSessions);

        var evidence = string.Join("\n", diagnosis.Evidence);
        Assert.Contains("waited on the disk 32", evidence);
        Assert.Contains("waited for a processor 0.06%", evidence);
        Assert.Contains("waited on memory 0.17%", evidence);
        Assert.Contains("75% busy", evidence);
        Assert.Contains("26 ms per write", evidence);
        Assert.Contains("volume /", evidence);
        Assert.Contains("hard drive sdc", diagnosis.NextStep);
    }

    [Fact]
    public void Diagnose_DevlinuxReadings_TheHighLoadAverageIsNotMistakenForTheProcessor()
    {
        // Load 19 to 23 is the run queue on Linux, which counts programs waiting on the disk too. With the
        // direct stall figures present it must not make the processor a cause.
        var history = Every5Seconds(TimeSpan.FromSeconds(90), Devlinux);

        var diagnosis = SlownessDiagnoser.Diagnose(history, DevlinuxSessions);

        Assert.False(diagnosis.Cause.HasFlag(SlownessCause.Cpu));
        Assert.False(diagnosis.Cause.HasFlag(SlownessCause.Memory));
    }

    [Fact]
    public void Diagnose_DevlinuxReadingsWithoutASessionMap_NamesNoSession()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(90), Devlinux);

        var diagnosis = SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null);

        Assert.NotNull(diagnosis.Culprit);
        Assert.Equal("dotnet test", diagnosis.Culprit.ProcessName);
        Assert.Null(diagnosis.Culprit.SessionId);
        Assert.DoesNotContain("session", diagnosis.Summary);
    }

    // -------------------------------------------------------------------------------------------
    // Linux, direct stall figures
    // -------------------------------------------------------------------------------------------

    private static ResourceSampleBuilder LinuxStalls(double cpu, double memory, double io) => new()
    {
        Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: cpu, memoryStalledPercent: memory, ioStalledPercent: io),
        Processes = ProcessSetReading.Read(new[]
        {
            new ProcessReading(4000, 1, "agent", Start.AddHours(-1), 2, 500_000_000, 0, 0),
            new ProcessReading(4200, 4000, "cc1plus", Start.AddMinutes(-2), 60, 300_000_000, 0, 0),
            new ProcessReading(7000, 1, "chrome", Start.AddHours(-3), 4, 9_000_000_000, 0, 0),
        }),
    };

    [Fact]
    public void Diagnose_LinuxCpuSaturated_IsCpuAndNamesTheBusiestProcess()
    {
        var history = Every5Seconds(Minute, _ => LinuxStalls(cpu: 65, memory: 0.2, io: 1) with
        {
            Cpu = CpuReading.Read(99, 100, 32),
        });

        var diagnosis = SlownessDiagnoser.Diagnose(history, new Dictionary<int, string> { [4000] = "141" });

        Assert.Equal(SlownessCause.Cpu, diagnosis.Cause);
        Assert.Equal("cc1plus", diagnosis.Culprit?.ProcessName);
        Assert.Equal("141", diagnosis.Culprit?.SessionId);
        Assert.Null(diagnosis.Culprit?.DiskName);
        Assert.StartsWith("Slow because of the processor", diagnosis.Summary);
    }

    [Fact]
    public void Diagnose_LinuxMemoryTight_IsMemoryAndNamesTheBiggestProcess()
    {
        var history = Every5Seconds(Minute, _ => LinuxStalls(cpu: 2, memory: 40, io: 5) with
        {
            Memory = MachineMemoryReading.Read(16 * Gigabyte, (ulong)(0.4 * Gigabyte), Start),
        });

        var diagnosis = SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null);

        Assert.Equal(SlownessCause.Memory, diagnosis.Cause);
        Assert.Equal("chrome", diagnosis.Culprit?.ProcessName);
        Assert.StartsWith("Slow because of memory", diagnosis.Summary);
        Assert.Contains("memory", diagnosis.NextStep);
    }

    [Fact]
    public void Diagnose_LinuxMemoryAndDisk_IsBothAndMemoryLeadsTheCulprit()
    {
        var history = Every5Seconds(Minute, _ => LinuxStalls(cpu: 2, memory: 30, io: 45));

        var diagnosis = SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null);

        Assert.Equal(SlownessCause.Memory | SlownessCause.Disk, diagnosis.Cause);
        Assert.Equal("chrome", diagnosis.Culprit?.ProcessName);
        Assert.Equal("sda", diagnosis.Culprit?.DiskName);
        Assert.StartsWith("Slow because of memory and the disk", diagnosis.Summary);
    }

    [Fact]
    public void Diagnose_QuietLinuxMachine_IsNotSlow()
    {
        var history = Every5Seconds(Minute, _ => new ResourceSampleBuilder());

        var diagnosis = SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null);

        Assert.True(diagnosis.CouldDiagnose);
        Assert.False(diagnosis.IsSlow);
        Assert.Equal(SlownessCause.None, diagnosis.Cause);
        Assert.Null(diagnosis.Culprit);
        Assert.StartsWith("Not slow", diagnosis.Summary);
        Assert.NotEmpty(diagnosis.Evidence);
    }

    [Theory]
    [InlineData(20.0, 0.0, 0.0, SlownessCause.None)]   // exactly at the limit is not over it
    [InlineData(20.1, 0.0, 0.0, SlownessCause.Cpu)]
    [InlineData(0.0, 10.0, 0.0, SlownessCause.None)]
    [InlineData(0.0, 10.1, 0.0, SlownessCause.Memory)]
    [InlineData(0.0, 0.0, 20.0, SlownessCause.None)]
    [InlineData(0.0, 0.0, 20.1, SlownessCause.Disk)]
    [InlineData(50.0, 50.0, 50.0, SlownessCause.Cpu | SlownessCause.Memory | SlownessCause.Disk)]
    public void Diagnose_LinuxStallLimits_AreHonoured(double cpu, double memory, double io, SlownessCause expected)
    {
        var history = Every5Seconds(Minute, _ => LinuxStalls(cpu, memory, io));

        Assert.Equal(expected, SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null).Cause);
    }

    [Fact]
    public void Diagnose_OneBurstInAQuietMinute_IsAveragedAwayAndNotSlow()
    {
        var history = Every5Seconds(Minute, offset => LinuxStalls(cpu: 0, memory: 0, io: offset == TimeSpan.FromSeconds(30) ? 90 : 2));

        Assert.False(SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null).IsSlow);
    }

    [Fact]
    public void Diagnose_SamplesOlderThanTheMinute_AreNotAveragedIn()
    {
        // Ten minutes of heavy disk stall, then a quiet minute: the machine is not slow NOW.
        var history = Every5Seconds(TimeSpan.FromMinutes(11), offset => LinuxStalls(cpu: 0, memory: 0, io: offset < TimeSpan.FromMinutes(10) ? 80 : 1));

        Assert.False(SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null).IsSlow);
    }

    [Fact]
    public void Diagnose_ASingleSample_IsJudgedOnIt()
    {
        var history = new[] { LinuxStalls(cpu: 0, memory: 0, io: 60).Build() };

        var diagnosis = SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null);

        Assert.Equal(SlownessCause.Disk, diagnosis.Cause);
        Assert.Contains(diagnosis.Evidence, line => line.Contains("one reading"));
    }

    // -------------------------------------------------------------------------------------------
    // Cannot tell is never "not slow"
    // -------------------------------------------------------------------------------------------

    private static ResourceSampleBuilder Unreadable() => new()
    {
        Cpu = CpuReading.Unreadable("could not open /proc/stat"),
        Memory = MachineMemoryReading.Unreadable("could not open /proc/meminfo", Start),
        Disks = DiskSetReading.Unreadable("could not open /proc/diskstats"),
        Volumes = Array.Empty<VolumeReading>(),
        Stall = StallReading.Unreadable("could not open /proc/pressure"),
        Processes = ProcessSetReading.Unreadable("could not list /proc"),
    };

    [Fact]
    public void Diagnose_AllUnreadable_CannotTellAndIsNeverNotSlow()
    {
        var history = Every5Seconds(Minute, _ => Unreadable());

        var diagnosis = SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null);

        Assert.False(diagnosis.CouldDiagnose);
        Assert.False(diagnosis.IsSlow);
        Assert.Equal(SlownessCause.None, diagnosis.Cause);
        Assert.StartsWith("Cannot tell", diagnosis.Summary);
        Assert.DoesNotContain("Not slow", diagnosis.Summary);
        Assert.Contains("/proc/stat", diagnosis.Summary);
    }

    [Fact]
    public void Diagnose_EmptyHistory_CannotTell()
    {
        var diagnosis = SlownessDiagnoser.Diagnose(Array.Empty<ResourceSample>(), sessionIdByRootPid: null);

        Assert.False(diagnosis.CouldDiagnose);
        Assert.StartsWith("Cannot tell", diagnosis.Summary);
    }

    [Fact]
    public void Diagnose_ProxyPlatformWithDisksUnreadableAndTheRestQuiet_CannotTellRatherThanNotSlow()
    {
        var history = Every5Seconds(Minute, _ => new ResourceSampleBuilder
        {
            Disks = DiskSetReading.Unreadable("IOKit refused"),
            Stall = StallReading.Read(StallSource.MacOsSignals, runQueueLength: 2, macOsMemoryPressureLevel: 1),
        });

        var diagnosis = SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null);

        Assert.False(diagnosis.CouldDiagnose);
        Assert.False(diagnosis.IsSlow);
        Assert.Contains("the disk could not be read", diagnosis.Summary);
    }

    [Fact]
    public void Diagnose_ProxyPlatformWithDisksUnreadableAndProcessorSaturated_NamesTheProcessorAndSaysTheDiskIsNotRuledOut()
    {
        var history = Every5Seconds(Minute, _ => new ResourceSampleBuilder
        {
            Cpu = CpuReading.Read(98, 100, 8),
            Disks = DiskSetReading.Unreadable("IOKit refused"),
            Stall = StallReading.Read(StallSource.MacOsSignals, runQueueLength: 20, macOsMemoryPressureLevel: 1),
        });

        var diagnosis = SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null);

        Assert.True(diagnosis.CouldDiagnose);
        Assert.Equal(SlownessCause.Cpu, diagnosis.Cause);
        Assert.Contains(diagnosis.Evidence, line => line.Contains("the disk could not be read"));
    }

    [Fact]
    public void Diagnose_ProcessesUnreadable_StillDiagnosesButNamesNoProcess()
    {
        var history = Every5Seconds(Minute, offset => Devlinux(offset) with { Processes = ProcessSetReading.Unreadable(ResourceUnreadableReasons.FirstSample) });

        var diagnosis = SlownessDiagnoser.Diagnose(history, DevlinuxSessions);

        Assert.Equal(SlownessCause.Disk, diagnosis.Cause);
        Assert.Equal("sdc", diagnosis.Culprit?.DiskName);
        Assert.Null(diagnosis.Culprit?.ProcessName);
        Assert.DoesNotContain("mostly", diagnosis.Summary);
    }

    // -------------------------------------------------------------------------------------------
    // Windows and macOS: proxies
    // -------------------------------------------------------------------------------------------

    private static ResourceSampleBuilder Windows(
        double cpuBusy = 10, int cores = 8, double queue = 1, double faults = 10,
        double diskBusy = 5, double? msPerWrite = 1, double availableGigabytes = 8) => new()
    {
        Cpu = CpuReading.Read(cpuBusy, Math.Min(100, cpuBusy + 10), cores),
        Memory = MachineMemoryReading.Read(16 * Gigabyte, (ulong)(availableGigabytes * Gigabyte), Start),
        Disks = DiskSetReading.Read(new[]
        {
            PhysicalDiskReading.Read("PhysicalDrive0", DiskKind.SolidState, diskBusy, 1_000_000, 4_000_000, msPerWrite, msPerWrite, 0.5),
        }),
        Volumes = new[] { VolumeReading.Read(@"C:\", 500 * Gigabyte, 200 * Gigabyte, new[] { "PhysicalDrive0" }) },
        Stall = StallReading.Read(StallSource.WindowsCounters, runQueueLength: queue, hardPageFaultsPerSecond: faults),
        Processes = ProcessSetReading.Read(new[]
        {
            new ProcessReading(100, 4, "MsMpEng.exe", Start.AddDays(-1), 3, 300_000_000, 20_000_000, 1_000_000),
            new ProcessReading(200, 4, "agent.exe", Start.AddHours(-1), 70, 700_000_000, 10_000, 30_000),
            new ProcessReading(300, 4, "Teams.exe", Start.AddHours(-2), 2, 2_000_000_000, 0, 0),
        }),
    };

    [Fact]
    public void Diagnose_WindowsQuiet_IsNotSlow()
    {
        var diagnosis = SlownessDiagnoser.Diagnose(Every5Seconds(Minute, _ => Windows()), sessionIdByRootPid: null);

        Assert.True(diagnosis.CouldDiagnose);
        Assert.False(diagnosis.IsSlow);
    }

    [Fact]
    public void Diagnose_WindowsProcessorBusy_IsCpu()
    {
        var diagnosis = SlownessDiagnoser.Diagnose(Every5Seconds(Minute, _ => Windows(cpuBusy: 96)), sessionIdByRootPid: null);

        Assert.Equal(SlownessCause.Cpu, diagnosis.Cause);
        Assert.Equal("agent.exe", diagnosis.Culprit?.ProcessName);
    }

    [Theory]
    [InlineData(16.0, SlownessCause.None)]  // exactly two per core is not over
    [InlineData(17.0, SlownessCause.Cpu)]
    public void Diagnose_WindowsQueuePerCore_IsHonoured(double queue, SlownessCause expected)
    {
        var diagnosis = SlownessDiagnoser.Diagnose(Every5Seconds(Minute, _ => Windows(cores: 8, queue: queue)), sessionIdByRootPid: null);

        Assert.Equal(expected, diagnosis.Cause);
    }

    [Fact]
    public void Diagnose_WindowsHardPageFaults_IsMemory()
    {
        var diagnosis = SlownessDiagnoser.Diagnose(Every5Seconds(Minute, _ => Windows(faults: 2500, availableGigabytes: 1.5)), sessionIdByRootPid: null);

        Assert.Equal(SlownessCause.Memory, diagnosis.Cause);
        Assert.Equal("Teams.exe", diagnosis.Culprit?.ProcessName);
        Assert.Contains("2500 memory pages", diagnosis.Summary);
    }

    [Fact]
    public void Diagnose_WindowsMemoryCritical_IsMemoryEvenWithoutPageFaults()
    {
        var diagnosis = SlownessDiagnoser.Diagnose(Every5Seconds(Minute, _ => Windows(availableGigabytes: 0.5)), sessionIdByRootPid: null);

        Assert.Equal(SlownessCause.Memory, diagnosis.Cause);
    }

    [Fact]
    public void Diagnose_WindowsDiskBusyAndSlow_IsDiskAndNamesTheBusiestReader()
    {
        var diagnosis = SlownessDiagnoser.Diagnose(Every5Seconds(Minute, _ => Windows(diskBusy: 70, msPerWrite: 45)), sessionIdByRootPid: null);

        Assert.Equal(SlownessCause.Disk, diagnosis.Cause);
        Assert.Equal("PhysicalDrive0", diagnosis.Culprit?.DiskName);
        Assert.Equal(DiskKind.SolidState, diagnosis.Culprit?.DiskKind);
        Assert.Equal("MsMpEng.exe", diagnosis.Culprit?.ProcessName);
        Assert.Contains("solid-state drive PhysicalDrive0", diagnosis.Summary);
    }

    [Fact]
    public void Diagnose_WindowsDiskBusyButFast_IsNotSlow()
    {
        // A fast solid-state drive can be busy and still answer at once.
        var diagnosis = SlownessDiagnoser.Diagnose(Every5Seconds(Minute, _ => Windows(diskBusy: 80, msPerWrite: 0.5)), sessionIdByRootPid: null);

        Assert.False(diagnosis.IsSlow);
    }

    [Fact]
    public void Diagnose_DiskSaturatedWithNoTimingsOffered_IsDisk()
    {
        var diagnosis = SlownessDiagnoser.Diagnose(Every5Seconds(Minute, _ => Windows(diskBusy: 95, msPerWrite: null)), sessionIdByRootPid: null);

        Assert.Equal(SlownessCause.Disk, diagnosis.Cause);
    }

    [Fact]
    public void Diagnose_MacOsMemoryPressureWarn_IsMemory()
    {
        var history = Every5Seconds(Minute, _ => Windows() with
        {
            Stall = StallReading.Read(StallSource.MacOsSignals, runQueueLength: 2, macOsMemoryPressureLevel: 2),
        });

        var diagnosis = SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null);

        Assert.Equal(SlownessCause.Memory, diagnosis.Cause);
        Assert.Contains("warn", diagnosis.Summary);
    }

    [Theory]
    [InlineData(8.0, SlownessCause.None)]   // one per core on macOS is every core taken, not yet over
    [InlineData(9.0, SlownessCause.Cpu)]
    public void Diagnose_MacOsRunQueuePerCore_IsHonoured(double queue, SlownessCause expected)
    {
        var history = Every5Seconds(Minute, _ => Windows(cores: 8) with
        {
            Stall = StallReading.Read(StallSource.MacOsSignals, runQueueLength: queue, macOsMemoryPressureLevel: 1),
        });

        Assert.Equal(expected, SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null).Cause);
    }

    private static ResourceSampleBuilder MacOs(double msPerWrite) => Windows() with
    {
        Disks = DiskSetReading.Read(new[]
        {
            PhysicalDiskReading.Read("disk0", DiskKind.SolidState, busyPercent: null, 1_000_000, 80_000_000, 0.3, msPerWrite, 4),
            PhysicalDiskReading.Read("disk4", DiskKind.Spinning, busyPercent: null, 0, 0, null, null, null),
        }),
        Volumes = new[] { VolumeReading.Read("/", 1000 * Gigabyte, 600 * Gigabyte, new[] { "disk0" }) },
        Stall = StallReading.Read(StallSource.MacOsSignals, runQueueLength: 2, macOsMemoryPressureLevel: 1),
    };

    [Theory]
    [InlineData(1.1, false)]   // one saturating writer on an M4
    [InlineData(10.0, false)]  // exactly the limit is not over it
    [InlineData(19.0, true)]   // four writers
    public void Diagnose_MacOsNoBusyFigure_JudgesTheDiskByTimePerOperation(double msPerWrite, bool expectSlow)
    {
        var diagnosis = SlownessDiagnoser.Diagnose(Every5Seconds(Minute, _ => MacOs(msPerWrite)), sessionIdByRootPid: null);

        Assert.True(diagnosis.CouldDiagnose);
        Assert.Equal(expectSlow, diagnosis.Cause.HasFlag(SlownessCause.Disk));
    }

    [Fact]
    public void Diagnose_MacOsSlowDisk_NamesItWithoutInventingABusyFigure()
    {
        var diagnosis = SlownessDiagnoser.Diagnose(Every5Seconds(Minute, _ => MacOs(19)), sessionIdByRootPid: null);

        Assert.Equal("disk0", diagnosis.Culprit?.DiskName);
        Assert.Contains("solid-state drive disk0 is taking 19 ms per write", diagnosis.Summary);
        Assert.DoesNotContain("% busy", diagnosis.Summary);
        Assert.DoesNotContain(diagnosis.Evidence, line => line.Contains("disk0") && line.Contains("busy"));
    }

    [Fact]
    public void Diagnose_DiskCulpritWithNoPerProcessDiskFigures_NamesTheDiskButNoProcess()
    {
        // Never fall back to the processor's busiest process when the guilty resource is the disk.
        var history = Every5Seconds(Minute, _ => Windows(diskBusy: 95, msPerWrite: 60) with
        {
            Processes = ProcessSetReading.Read(new[] { new ProcessReading(200, 4, "agent.exe", Start.AddHours(-1), 70, 700_000_000, null, null) }),
        });

        var diagnosis = SlownessDiagnoser.Diagnose(history, sessionIdByRootPid: null);

        Assert.Equal("PhysicalDrive0", diagnosis.Culprit?.DiskName);
        Assert.Null(diagnosis.Culprit?.ProcessName);
    }

    [Theory]
    [InlineData(DiskKind.Spinning, "hard drive")]
    [InlineData(DiskKind.SolidState, "solid-state drive")]
    [InlineData(DiskKind.Unknown, "disk")]
    public void KindNoun_NamesEveryKind(DiskKind kind, string expected)
    {
        Assert.Equal(expected, SlownessDiagnoser.KindNoun(kind));
    }
}
