using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE macOS PROBE AGAINST THE REAL MACHINE.
///
/// These tests call the operating system and assert that what comes back is real: a disk with a name
/// and a known kind, a volume with a real size, one block of tick counters for every core the runtime
/// reports, this very test process in the process list with its own parent. They are the half the
/// committed fixtures CANNOT prove - a fixture goes on parsing correctly long after the system call
/// that produced it has stopped working, or has started returning something else on a newer macOS.
///
/// SO NOTHING HERE ASSERTS ZERO, OR MERELY THAT A CALL DID NOT THROW. A probe that reports zero for
/// everything would satisfy a suite written that way while telling the owner his machine was idle.
/// Every assertion below is for a value that can only be there if the read actually worked.
///
/// The one exception is stated where it appears: the swap file on this platform is legitimately zero
/// on a healthy machine, so that test asserts the reading SUCCEEDED rather than that it is non-zero.
/// </summary>
public class MacOsPlatformReaderLiveTests
{
    // =============================================================================================
    // Disks
    // =============================================================================================

    [MacOsOnlyFact("IOKit exists only on macOS")]
    [Trait("Category", "Resources")]
    public void ReadDisks_OnThisMachine_FindsADiskWithANameAndAKnownKind()
    {
        var disks = MacOsPlatformReader.ReadDisks();

        Assert.NotEmpty(disks);

        var named = 0;
        foreach (var disk in disks)
        {
            Assert.NotNull(disk.StatisticsPropertyList);

            var name = MacOsResourceParsing.ReadPropertyListString(disk.BsdNamePropertyList ?? "");
            Assert.False(string.IsNullOrWhiteSpace(name), "a physical disk must have a device name");
            Assert.StartsWith("disk", name, StringComparison.Ordinal);

            var characteristics = MacOsResourceParsing.ReadPropertyListDictionary(
                disk.DeviceCharacteristicsPropertyList ?? "");
            Assert.True(
                characteristics.TryGetValue(MacOsResourceParsing.DeviceCharacteristicsMediumType, out var medium),
                $"disk {name} reported no medium type, so it cannot be called spinning or solid-state");

            // The kind decides what the slowness diagnosis is allowed to say, so an unrecognised
            // medium type must be visible here rather than quietly folded into one of the two.
            Assert.True(
                medium is MacOsResourceParsing.MediumTypeSolidState or MacOsResourceParsing.MediumTypeRotational,
                $"disk {name} reported an unrecognised medium type \"{medium}\"");

            named++;
        }

        Assert.True(named > 0);
    }

    [MacOsOnlyFact("IOKit exists only on macOS")]
    [Trait("Category", "Resources")]
    public void ReadDisks_OnThisMachine_StatisticsAreRealAndCumulative()
    {
        var first = ReadFirstDiskCounters();

        // Any machine that has booted has read something from its boot disk, so these cannot be zero
        // on a working read. This is the assertion a fixture cannot make.
        Assert.True(first.BytesRead > 0, "the disk reported no bytes ever read, which cannot be true of a booted machine");
        Assert.True(first.OperationsRead > 0);
        Assert.True(first.TotalTimeReadNanoseconds > 0);

        // The time per operation has to be in the range real hardware works in. This is what pins the
        // units of "Total Time" as nanoseconds: reading Mach absolute time units as nanoseconds on
        // Apple Silicon is wrong by about forty times, which would put this outside the range.
        var millisecondsPerRead = first.TotalTimeReadNanoseconds / 1e6 / first.OperationsRead;
        Assert.InRange(millisecondsPerRead, 0.001, 100.0);
    }

    [MacOsOnlyFact("IOKit exists only on macOS")]
    [Trait("Category", "Resources")]
    public void ReadDisks_AfterWritingAFile_TheCountersHaveMovedForward()
    {
        // The counters being cumulative is the whole basis of every rate the probe reports. If they
        // were a snapshot rather than a running total, every rate would be wrong and nothing else in
        // the suite would notice.
        var before = ReadFirstDiskCounters();

        var path = Path.Combine(Path.GetTempPath(), $"resource-probe-live-{Guid.NewGuid():N}.bin");
        try
        {
            var block = new byte[8 * 1024 * 1024];
            Random.Shared.NextBytes(block);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                       1 << 20, FileOptions.WriteThrough))
            {
                stream.Write(block);
                stream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            File.Delete(path);
        }

        var after = ReadFirstDiskCounters();

        Assert.True(after.BytesWritten > before.BytesWritten,
            $"wrote eight mebibytes but the written-bytes counter did not move: " +
            $"{before.BytesWritten} then {after.BytesWritten}");
        Assert.True(after.OperationsWritten > before.OperationsWritten);
        Assert.True(after.TotalTimeWrittenNanoseconds > before.TotalTimeWrittenNanoseconds);
        Assert.True(after.BytesRead >= before.BytesRead, "a cumulative counter must never go backwards");
    }

    private static MacOsResourceParsing.DiskCounters ReadFirstDiskCounters()
    {
        var disks = MacOsPlatformReader.ReadDisks();
        Assert.NotEmpty(disks);

        var statistics = MacOsResourceParsing.ReadPropertyListDictionary(disks[0].StatisticsPropertyList ?? "");
        Assert.True(MacOsResourceParsing.TryReadDiskCounters(statistics, out var counters, out var why), why);
        return counters;
    }

    // =============================================================================================
    // Which physical disk a volume sits on
    // =============================================================================================

    [MacOsOnlyFact("IOKit exists only on macOS")]
    [Trait("Category", "Resources")]
    public void ReadAncestorDeviceNames_TheBootVolume_ReachesADiskWeHaveStatisticsFor()
    {
        // The point of the whole upward walk. The boot volume's own device is an Apple file system
        // volume inside a container SYNTHESIZED on a partition - that container is not hardware, has
        // no statistics and no medium type. Unless the walk reaches the real disk, the slowness
        // diagnosis can never name the hardware that was busy.
        var volumes = MacOsPlatformReader.ReadVolumes();
        Assert.NotNull(volumes);

        var root = MacOsResourceParsing.ReadVolumes(volumes.Value.Buffer, volumes.Value.Count)
            .Single(volume => volume.MountedOn == "/");
        Assert.NotNull(root.DeviceName);

        var physical = PhysicalDiskNames();
        var ancestors = MacOsPlatformReader.ReadAncestorDeviceNames(root.DeviceName);

        Assert.NotEmpty(ancestors);
        Assert.Contains(ancestors, physical.Contains);
    }

    [MacOsOnlyFact("IOKit exists only on macOS")]
    [Trait("Category", "Resources")]
    public void ReadAncestorDeviceNames_ADeviceThatDoesNotExist_IsEmptyRatherThanThrowing()
    {
        Assert.Empty(MacOsPlatformReader.ReadAncestorDeviceNames("disk9999"));
    }

    [MacOsOnlyFact("IOKit exists only on macOS")]
    [Trait("Category", "Resources")]
    public void ReadAncestorDeviceNames_NoDeviceName_IsEmpty()
    {
        Assert.Empty(MacOsPlatformReader.ReadAncestorDeviceNames(""));
    }

    private static HashSet<string> PhysicalDiskNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var disk in MacOsPlatformReader.ReadDisks())
        {
            var name = MacOsResourceParsing.ReadPropertyListString(disk.BsdNamePropertyList ?? "");
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }

        return names;
    }

    // =============================================================================================
    // Processor
    // =============================================================================================

    [MacOsOnlyFact("host_processor_info is a Mach call")]
    [Trait("Category", "Resources")]
    public void ReadProcessorTicks_OnThisMachine_ReturnsOneBlockForEveryCore()
    {
        var ticks = MacOsPlatformReader.ReadProcessorTicks(out var coreCount, out var why);

        Assert.NotNull(ticks);
        Assert.Null(why);

        // Checked against the runtime's own count, which comes from a different system call entirely,
        // so the two agreeing is real evidence rather than the probe agreeing with itself.
        Assert.Equal(Environment.ProcessorCount, coreCount);
        Assert.Equal(coreCount * MacOsNative.CpuStateMax, ticks.Length);

        Assert.True(MacOsResourceParsing.TryReadCoreTicks(ticks, coreCount, out var cores, out var coresWhy), coresWhy);
        Assert.All(cores, core => Assert.True(core.Total > 0, "a core that has never accounted for a tick cannot exist on a running machine"));
        Assert.All(cores, core => Assert.True(core.Idle > 0, "a core with no idle ticks at all would mean the machine had been fully busy since boot"));
    }

    [MacOsOnlyFact("host_processor_info is a Mach call")]
    [Trait("Category", "Resources")]
    public void ReadProcessorTicks_TwoReadingsWithWorkBetween_ShowTimePassing()
    {
        var before = ReadCores();

        // Real work, measured in WORK rather than in time - see MachineWork for why a sleep or a
        // clock-bounded spin is refused here. The result is asserted on so the loop cannot be
        // optimised away, which would otherwise leave this test proving that no work moves no
        // counters.
        var burned = MachineWork.Burn();
        Assert.True(burned > 0);

        var after = ReadCores();

        // Total ticks must have advanced across the machine: that is wall-clock time passing, and it
        // is what every percentage the probe reports is divided by.
        var totalBefore = before.Sum(core => (double)core.Total);
        var totalAfter = after.Sum(core => (double)core.Total);
        Assert.True(totalAfter > totalBefore,
            $"no processor ticks passed across {before.Count} cores while a unit of work was done");

        // And at least one core must have done some of that work.
        var busyBefore = before.Sum(core => (double)core.Busy);
        var busyAfter = after.Sum(core => (double)core.Busy);
        Assert.True(busyAfter > busyBefore, "a unit of real arithmetic was done and no core recorded any of it");
    }

    private static IReadOnlyList<MacOsResourceParsing.CoreTicks> ReadCores()
    {
        var ticks = MacOsPlatformReader.ReadProcessorTicks(out var coreCount, out var why);
        Assert.NotNull(ticks);
        Assert.True(MacOsResourceParsing.TryReadCoreTicks(ticks, coreCount, out var cores, out why), why);
        return cores;
    }

    // =============================================================================================
    // Swap, the run queue and the memory-pressure level
    // =============================================================================================

    [MacOsOnlyFact("vm.swapusage is a macOS sysctl")]
    [Trait("Category", "Resources")]
    public void ReadSwapUsage_OnThisMachine_IsReadableEvenWhenThereIsNoSwapFile()
    {
        // THE ONE PLACE A ZERO IS THE RIGHT ANSWER, and it is asserted as a successful READING rather
        // than as a non-zero figure. macOS writes no swap file until it needs one, so a healthy Apple
        // Silicon machine legitimately reports zero of everything - and the probe must report that as
        // a reading that worked, not as a resource it could not read.
        var bytes = MacOsPlatformReader.ReadSysctlBytes("vm.swapusage");

        Assert.NotNull(bytes);
        Assert.Equal(MacOsResourceParsing.SwapUsageSize, bytes.Length);
        Assert.True(MacOsResourceParsing.TryReadSwapUsage(bytes, out var usage, out var why), why);
        Assert.True(usage.UsedBytes <= usage.TotalBytes);
    }

    [MacOsOnlyFact("vm.loadavg is a macOS sysctl")]
    [Trait("Category", "Resources")]
    public void ReadLoadAverage_OnThisMachine_IsARealRunQueueFigure()
    {
        var bytes = MacOsPlatformReader.ReadSysctlBytes("vm.loadavg");

        Assert.NotNull(bytes);
        Assert.Equal(MacOsResourceParsing.LoadAverageSize, bytes.Length);
        Assert.True(MacOsResourceParsing.TryReadLoadAverage(bytes, out var load, out var why), why);

        // A machine running a test suite has a run queue above zero, and a figure in the thousands
        // would mean the kernel's scale was read wrongly.
        Assert.InRange(load.OneMinute, 0.01, 1000.0);
        Assert.InRange(load.FiveMinutes, 0.0, 1000.0);
        Assert.InRange(load.FifteenMinutes, 0.0, 1000.0);
    }

    [MacOsOnlyFact("kern.memorystatus_vm_pressure_level is a macOS sysctl")]
    [Trait("Category", "Resources")]
    public void ReadMemoryPressureLevel_OnThisMachine_IsALevelWeRecognise()
    {
        var level = MacOsPlatformReader.ReadSysctlInt32("kern.memorystatus_vm_pressure_level");

        Assert.NotNull(level);

        // An unrecognised number here means the kernel's numbering has changed under us, and the
        // probe would be reporting a level it does not understand into the slowness diagnosis.
        Assert.NotEqual(
            MacOsResourceParsing.KernelPressureLevel.Unknown,
            MacOsResourceParsing.ReadKernelPressureLevel(level.Value));
    }

    [MacOsOnlyFact("sysctl is read through libc on macOS")]
    [Trait("Category", "Resources")]
    public void ReadSysctl_ANameThatDoesNotExist_IsNullRatherThanThrowing()
    {
        Assert.Null(MacOsPlatformReader.ReadSysctlBytes("kern.there_is_no_such_setting"));
        Assert.Null(MacOsPlatformReader.ReadSysctlInt32("kern.there_is_no_such_setting"));
    }

    // =============================================================================================
    // Volumes
    // =============================================================================================

    [MacOsOnlyFact("getfsstat is a BSD call")]
    [Trait("Category", "Resources")]
    public void ReadVolumes_OnThisMachine_FindsTheRootVolumeWithARealSize()
    {
        var captured = MacOsPlatformReader.ReadVolumes();

        Assert.NotNull(captured);
        Assert.True(captured.Value.Count > 0);

        var volumes = MacOsResourceParsing.ReadVolumes(captured.Value.Buffer, captured.Value.Count);
        Assert.Equal(captured.Value.Count, volumes.Count);

        var root = volumes.Single(volume => volume.MountedOn == "/");
        Assert.Equal("apfs", root.FileSystemType);
        Assert.True(root.BlockSizeBytes > 0);
        Assert.True(root.TotalBytes > 1024UL * 1024 * 1024, "the root volume read as smaller than a gibibyte");
        Assert.True(root.AvailableBytes > 0);
        Assert.True(root.AvailableBytes <= root.TotalBytes);
        Assert.NotNull(root.DeviceName);
    }

    // =============================================================================================
    // Network interfaces
    // =============================================================================================

    [MacOsOnlyFact("getifaddrs if_data counters are read from the BSD layer")]
    [Trait("Category", "Resources")]
    public void ReadInterfaces_OnThisMachine_FindsTheLoopbackAndSomethingCarryingTraffic()
    {
        var interfaces = MacOsPlatformReader.ReadInterfaces();

        Assert.NotEmpty(interfaces);

        var names = interfaces.Select(entry => entry.Name).ToList();
        Assert.Contains("lo0", names);

        // Exactly one entry per interface. getifaddrs returns one entry per ADDRESS, so an interface
        // with four addresses appears four times and only the link-level entry carries counters.
        // Counting an interface twice would double every byte rate the probe reports.
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        var carrying = 0;
        foreach (var (name, data) in interfaces)
        {
            Assert.True(MacOsResourceParsing.TryReadInterfaceCounters(name, data, out var counters, out var why), why);
            if (counters.BytesReceived > 0 || counters.BytesSent > 0) carrying++;
        }

        Assert.True(carrying > 0, "not one network interface reported a single byte in or out");
    }

    // =============================================================================================
    // Processes
    // =============================================================================================

    [MacOsOnlyFact("proc_listallpids is a libproc call")]
    [Trait("Category", "Resources")]
    public void ReadProcessIds_OnThisMachine_IncludesThisTestProcess()
    {
        var pids = MacOsPlatformReader.ReadProcessIds();

        Assert.NotNull(pids);

        // THE REGRESSION TEST FOR READING THE COUNT AS BYTES. That mistake returned a quarter of the
        // machine's processes - a list that looks perfectly ordinary. A machine running a test suite
        // has well over a hundred processes, and launchd is always process 1.
        Assert.True(pids.Length > 50, $"only {pids.Length} processes were listed, which no running Mac has");
        Assert.Contains(Environment.ProcessId, pids);
        Assert.Contains(1, pids);
    }

    [MacOsOnlyFact("proc_pidinfo is a libproc call")]
    [Trait("Category", "Resources")]
    public void ReadProcess_ThisTestProcess_ReadsItsOwnNameParentAndUsage()
    {
        var (taskInfo, bsdInfo, rusage) = MacOsPlatformReader.ReadProcess(Environment.ProcessId);

        Assert.NotNull(taskInfo);
        Assert.NotNull(bsdInfo);
        Assert.NotNull(rusage);

        Assert.True(MacOsResourceParsing.TryReadProcessTaskInfo(taskInfo, out var task));
        Assert.True(MacOsResourceParsing.TryReadProcessBsdInfo(bsdInfo, out var bsd));
        Assert.True(MacOsResourceParsing.TryReadProcessDiskUsage(rusage, out _));

        // This process is running these assertions, so it has used processor time and holds memory.
        Assert.True(task.TotalMachTime > 0);
        Assert.True(task.ResidentBytes > 1024 * 1024);

        // Its parent, its name and its start time, checked against what the runtime says rather than
        // against the probe's own other readings.
        Assert.True(bsd.ParentPid > 0);
        Assert.False(string.IsNullOrWhiteSpace(bsd.Name));
        Assert.Equal(DateTimeKind.Utc, bsd.StartedAtUtc.Kind);

        // A WIDE, FIXED WINDOW RATHER THAN ONE AROUND THE CLOCK. What this test is for is the epoch
        // and the field offset, and both failure modes land far outside any plausible date - the wrong
        // offset read 1970, and reading the microseconds field as the seconds field lands hundreds of
        // thousands of years out. Bounding it by "the last day" instead would measure the clock, which
        // a gate test may not do, and would catch nothing extra.
        Assert.InRange(
            bsd.StartedAtUtc,
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [MacOsOnlyFact("proc_pidinfo is a libproc call")]
    [Trait("Category", "Resources")]
    public void ReadProcess_ManyProcessesOnThisMachine_AreReadableAndTheRestAreNullNotAFailure()
    {
        // A process this user may not read must give null figures FOR THAT PROCESS. The requirement is
        // that it never fails the whole set: the Director runs as an ordinary user and a good share of
        // a Mac's processes belong to root, so a reader that gave up on the first refusal would report
        // no processes at all on every machine.
        var pids = MacOsPlatformReader.ReadProcessIds();
        Assert.NotNull(pids);

        var readable = 0;
        var refused = 0;
        foreach (var pid in pids)
        {
            var (taskInfo, bsdInfo, _) = MacOsPlatformReader.ReadProcess(pid);
            if (taskInfo is null || bsdInfo is null) refused++; else readable++;
        }

        Assert.True(readable > 10, $"only {readable} of {pids.Length} processes could be read");
        Assert.Equal(pids.Length, readable + refused);
    }

    [MacOsOnlyFact("mach_timebase_info is a Mach call")]
    [Trait("Category", "Resources")]
    public void ReadMachTimebase_OnThisMachine_IsUsableAndConvertsProcessorTimeSensibly()
    {
        // WITHOUT THIS CONVERSION EVERY PROCESS'S PROCESSOR USE IS WRONG BY THE TIMEBASE. The
        // processor times in struct proc_taskinfo are Mach absolute time units, not nanoseconds,
        // although they are widely described as nanoseconds - because on Intel Macs the timebase is
        // 1 to 1 and the two are the same thing. On this Apple Silicon machine it is 125/3, and
        // skipping the conversion reported two threads spinning flat out as 4.79 percent of a core.
        var timebase = MacOsPlatformReader.ReadMachTimebase();

        Assert.True(timebase.IsUsable, "the machine reported a timebase that cannot be used as a fraction");

        // This process has run for at least a few milliseconds of processor time and cannot possibly
        // have run for a year, so the converted figure has to land inside a very wide window. Getting
        // the conversion the wrong way round puts it outside.
        var (taskInfo, _, _) = MacOsPlatformReader.ReadProcess(Environment.ProcessId);
        Assert.NotNull(taskInfo);
        Assert.True(MacOsResourceParsing.TryReadProcessTaskInfo(taskInfo, out var task));

        var seconds = MacOsResourceParsing.MachAbsoluteToNanoseconds(task.TotalMachTime, timebase) / 1e9;
        Assert.InRange(seconds, 0.001, 86_400.0);
    }

    [MacOsOnlyFact("proc_pidinfo is a libproc call")]
    [Trait("Category", "Resources")]
    public void ReadProcess_AProcessThatDoesNotExist_IsNullRatherThanThrowing()
    {
        var (taskInfo, bsdInfo, rusage) = MacOsPlatformReader.ReadProcess(int.MaxValue - 1);

        Assert.Null(taskInfo);
        Assert.Null(bsdInfo);
        Assert.Null(rusage);
    }
}
