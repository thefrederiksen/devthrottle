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
///
/// IT CARRIES <see cref="LiveMachinePlatformAttribute"/>, SO THE WHOLE PROJECT CAN BE RUN ON EVERY
/// MACHINE. The calls below exist only on a Mac, so on Linux or Windows - even with the opt-in set -
/// every test here SKIPS with that reason stated, rather than running and failing on the first one.
/// </summary>
[LiveMachinePlatform(LiveMachinePlatform.MacOs)]
public class MacOsPlatformReaderLiveTests
{
    // =============================================================================================
    // Disks
    // =============================================================================================

    [LiveMachineFact("IOKit's registry of this machine's disks")]
    [Trait("Category", "Resources")]
    public void ReadDisks_OnThisMachine_FindsADiskWithANameAndAKnownKind()
    {
        var disks = MacOsPlatformReader.ReadDisks();

        Assert.NotEmpty(disks);

        // AT LEAST ONE DISK OF A KNOWN KIND, NOT EVERY DISK. This asked every block storage driver for
        // a medium type, and a disk image has none: its device characteristics carry a product name
        // and a revision and nothing else, as do many external enclosures. So mounting an installer
        // image - or plugging in a drive - turned this test red on a machine where the probe was
        // behaving perfectly, reporting that disk's kind as Unknown, which is the correct answer. A
        // test that fails for what the developer has plugged in is not reporting a defect in the code.
        var knownKinds = 0;
        foreach (var disk in disks)
        {
            Assert.NotNull(disk.StatisticsPropertyList);

            var name = MacOsResourceParsing.ReadPropertyListString(disk.BsdNamePropertyList ?? "");
            Assert.False(string.IsNullOrWhiteSpace(name), "a physical disk must have a device name");
            Assert.StartsWith("disk", name, StringComparison.Ordinal);

            var characteristics = MacOsResourceParsing.ReadPropertyListDictionary(
                disk.DeviceCharacteristicsPropertyList ?? "");
            if (!characteristics.TryGetValue(MacOsResourceParsing.DeviceCharacteristicsMediumType, out var medium))
                continue;

            // The kind decides what the slowness diagnosis is allowed to say, so when a disk DOES
            // report a medium type it must be one of the two this code understands - an unrecognised
            // one must be visible here rather than quietly folded into one of them.
            Assert.True(
                medium is MacOsResourceParsing.MediumTypeSolidState or MacOsResourceParsing.MediumTypeRotational,
                $"disk {name} reported an unrecognised medium type \"{medium}\"");

            knownKinds++;
        }

        Assert.True(
            knownKinds > 0,
            "not one disk on this machine reported a medium type, so none of them can be called " +
            "spinning or solid-state");
    }

    [LiveMachineFact("IOKit's registry of this machine's disks")]
    [Trait("Category", "Resources")]
    public void ReadDisks_OnThisMachine_StatisticsAreRealAndCumulative()
    {
        var first = ReadSystemDiskCounters();

        // Any machine that has booted has read something from its boot disk, so these cannot be zero
        // on a working read. This is the assertion a fixture cannot make.
        Assert.True(first.OperationsRead > 0,
            "the disk reported no read operation ever, which cannot be true of a booted machine");
        Assert.True(first.TotalTimeReadNanoseconds > 0);

        // The time per operation has to be in the range real hardware works in. This is what pins the
        // units of "Total Time" as nanoseconds: reading Mach absolute time units as nanoseconds on
        // Apple Silicon is wrong by about forty times, which would put this outside the range.
        var millisecondsPerRead = first.TotalTimeReadNanoseconds / 1e6 / first.OperationsRead;
        Assert.InRange(millisecondsPerRead, 0.001, 100.0);
    }

    [LiveMachineFact("IOKit's registry of this machine's disks")]
    [Trait("Category", "Resources")]
    public void ReadDisks_AfterWritingAFile_TheCountersHaveMovedForward()
    {
        // The counters being cumulative is the whole basis of every rate the probe reports. If they
        // were a snapshot rather than a running total, every rate would be wrong and nothing else in
        // the suite would notice.
        var before = ReadSystemDiskCounters();

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

        var after = ReadSystemDiskCounters();

        Assert.True(after.OperationsWritten > before.OperationsWritten,
            "wrote eight mebibytes but the write-operation counter did not move: " +
            $"{before.OperationsWritten} then {after.OperationsWritten}");
        Assert.True(after.TotalTimeWrittenNanoseconds > before.TotalTimeWrittenNanoseconds,
            "wrote eight mebibytes but the disk reported spending no more time writing: " +
            $"{before.TotalTimeWrittenNanoseconds} then {after.TotalTimeWrittenNanoseconds}");
        Assert.True(after.OperationsRead >= before.OperationsRead, "a cumulative counter must never go backwards");
    }

    /// <summary>
    /// The counters of the disk the machine RUNS FROM, found by walking up from the system volume.
    ///
    /// NOT simply the first disk IOKit lists. A test that writes a file and then asserts the counters
    /// moved has to read the disk the write went to - the temporary folder is on the system volume - and
    /// on a Mac with an external disk or a mounted disk image enumerated earlier, the first disk is not
    /// that one and the test fails on perfectly correct code. That is the same class of failure as
    /// finding 6 of the review of pull request 4035, which went red whenever a disk image was attached.
    /// </summary>
    private static MacOsResourceParsing.DiskCounters ReadSystemDiskCounters()
    {
        var device = MacOsPlatformReader.ReadSystemVolumeDevice();
        Assert.NotNull(device);

        var ancestors = MacOsPlatformReader.ReadAncestorDeviceNames(device);
        Assert.NotEmpty(ancestors);

        var disks = MacOsPlatformReader.ReadDisks();
        Assert.NotEmpty(disks);

        var system = disks.SingleOrDefault(disk =>
        {
            // A disk IOKit gave no device name is not a candidate, and it is not an error either: the
            // probe reports it unreadable with that reason. It simply cannot be matched to a volume.
            var name = MacOsResourceParsing.ReadPropertyListString(disk.BsdNamePropertyList ?? "");
            return name is not null && ancestors.Contains(name);
        });
        Assert.NotNull(system);

        var statistics = MacOsResourceParsing.ReadPropertyListDictionary(system.StatisticsPropertyList ?? "");
        Assert.True(MacOsResourceParsing.TryReadDiskCounters(statistics, out var counters, out var why), why);
        return counters;
    }

    // =============================================================================================
    // Which physical disk a volume sits on
    // =============================================================================================

    [LiveMachineFact("IOKit's registry of this machine's disks")]
    [Trait("Category", "Resources")]
    public void ReadAncestorDeviceNames_TheBootVolume_ReachesADiskWeHaveStatisticsFor()
    {
        // The point of the whole upward walk. The boot volume's own device is an Apple file system
        // volume inside a container SYNTHESIZED on a partition - that container is not hardware, has
        // no statistics and no medium type. Unless the walk reaches the real disk, the slowness
        // diagnosis can never name the hardware that was busy.
        var rootDevice = MacOsPlatformReader.ReadSystemVolumeDevice();
        Assert.NotNull(rootDevice);

        var physical = PhysicalDiskNames();
        var ancestors = MacOsPlatformReader.ReadAncestorDeviceNames(rootDevice);

        Assert.NotEmpty(ancestors);
        Assert.Contains(ancestors, physical.Contains);
    }

    [LiveMachineFact("IOKit's registry of this machine's disks")]
    [Trait("Category", "Resources")]
    public void ReadAncestorDeviceNames_ADeviceThatDoesNotExist_IsEmptyRatherThanThrowing()
    {
        Assert.Empty(MacOsPlatformReader.ReadAncestorDeviceNames("disk9999"));
    }

    [LiveMachineFact("IOKit's registry of this machine's disks")]
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

    [LiveMachineFact("per-core tick counters from host_processor_info")]
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

    [LiveMachineFact("per-core tick counters from host_processor_info")]
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

        // EVERY CORE'S COUNTERS MUST ADVANCE, NOT JUST THE MACHINE'S TOTAL, and this is a platform fact
        // the whole-machine percentage depends on. That figure is the summed busy ticks over the summed
        // total ticks across all cores. If an idle or power-gated core stopped accumulating ticks
        // altogether - which is a real possibility on Apple Silicon, where cores are parked
        // aggressively and this machine has four performance and six efficiency cores - then the
        // denominator would cover only the cores that happened to be awake, and the machine would read
        // far busier than it is. A mostly idle Mac would show a high percentage, which is exactly the
        // wrong direction for a meter whose job is to say when the machine is in trouble.
        //
        // Measured on this machine: all ten cores advance. Asserted rather than assumed, because if it
        // ever stops being true the arithmetic in FoldCpu needs to change and nothing else would notice.
        for (var core = 0; core < before.Count; core++)
        {
            Assert.True(
                after[core].Total > before[core].Total,
                $"core {core} of {before.Count} recorded no ticks at all across the interval, so the " +
                "whole-machine percentage would be divided by only the cores that were awake");
        }
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

    [LiveMachineFact("this machine's swap file through vm.swapusage")]
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

    [LiveMachineFact("this machine's load average through vm.loadavg")]
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

    [LiveMachineFact("the memory pressure level through kern.memorystatus_vm_pressure_level")]
    [Trait("Category", "Resources")]
    public void ReadMemoryPressureLevel_OnThisMachine_IsALevelWeRecognise()
    {
        var level = MacOsPlatformReader.ReadSysctlInt32("kern.memorystatus_vm_pressure_level");

        Assert.NotNull(level);

        // An unrecognised number here means the kernel's numbering has changed under us, and the
        // probe would be reporting a level it does not understand into the slowness diagnosis.
        Assert.NotEqual(KernelMemoryPressureLevel.Unknown, MacOsKernelPressure.Read(level.Value));
    }

    [LiveMachineFact("a sysctl value through libc")]
    [Trait("Category", "Resources")]
    public void ReadSysctl_ANameThatDoesNotExist_IsNullRatherThanThrowing()
    {
        Assert.Null(MacOsPlatformReader.ReadSysctlBytes("kern.there_is_no_such_setting"));
        Assert.Null(MacOsPlatformReader.ReadSysctlInt32("kern.there_is_no_such_setting"));
    }

    // =============================================================================================
    // The device behind the volume the machine runs from
    // =============================================================================================

    [LiveMachineFact("this machine's system volume through statfs")]
    [Trait("Category", "Resources")]
    public void ReadSystemVolumeDevice_OnThisMachine_IsABsdNameIOKitCanMatch()
    {
        var device = MacOsPlatformReader.ReadSystemVolumeDevice();

        // A machine that is running HAS a system volume, and on macOS it is always mounted at "/". A
        // null here is a failed read, not a machine without one, and it would make every disk's
        // IsInSystemVolume come back "not known".
        Assert.NotNull(device);

        // A BARE BSD NAME, NOT A PATH. IOBSDNameMatching is handed this directly and matches nothing at
        // all when given "/dev/disk3s1s1" - silently, so the walk up to the physical disk just comes
        // back empty and no disk is ever marked as the one the machine runs from.
        Assert.DoesNotContain("/", device);
        Assert.StartsWith("disk", device, StringComparison.Ordinal);
    }

    // =============================================================================================
    // Processes
    // =============================================================================================

    [LiveMachineFact("the kernel process table through sysctl")]
    [Trait("Category", "Resources")]
    public void ReadKernelProcessTable_OnThisMachine_NamesEveryProcessIncludingAnotherUsers()
    {
        // THIS IS THE PRODUCTION PATH, and it is here because of the defect it fixed. The probe used to
        // build its list from proc_listallpids and then read each process with a per-process call - but
        // that call is REFUSED for a process belonging to another user, so roughly three hundred of this
        // machine's seven hundred processes were absent entirely, name and all, from a set reporting that
        // it had read fine. Among them were the two busiest processes on the machine. The kernel's own
        // table has no such restriction.
        var bytes = MacOsPlatformReader.ReadKernelProcessTable();
        Assert.NotNull(bytes);

        var entries = MacOsResourceParsing.ReadKernelProcessTable(bytes);

        // A machine running a test suite has well over a hundred processes, and launchd is always
        // process 1. Reading the table's length wrongly would cut the list short while leaving it
        // looking perfectly ordinary.
        Assert.True(entries.Count > 50, $"only {entries.Count} processes were listed, which no running Mac has");
        Assert.Contains(entries, entry => entry.Pid == Environment.ProcessId);
        Assert.Contains(entries, entry => entry.Pid == 1);

        // EVERY PROCESS IS NAMED. A blank name here would mean the diagnosis could name a culprit it
        // could not describe.
        Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Name)));

        // AND PROCESSES THIS USER DOES NOT OWN ARE IN THE LIST. That is the whole point: this test fails
        // if the table is ever filtered to what the per-process calls happen to allow. Checked as a
        // specific presence rather than as "nothing is missing", because an empty or filtered list would
        // satisfy any assertion phrased the other way round.
        var ourUserId = entries.Single(entry => entry.Pid == Environment.ProcessId).UserId;
        Assert.Contains(entries, entry => entry.UserId != ourUserId);
    }

    [LiveMachineFact("process figures through proc_pidinfo")]
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

    [LiveMachineFact("process figures through proc_pidinfo")]
    [Trait("Category", "Resources")]
    public void ReadProcess_ManyProcessesOnThisMachine_AreReadableAndTheRestAreNullNotAFailure()
    {
        // A process this user may not read must give null figures FOR THAT PROCESS. The requirement is
        // that it never fails the whole set: the Director runs as an ordinary user and a good share of
        // a Mac's processes belong to root, so a reader that gave up on the first refusal would report
        // no processes at all on every machine.
        // The identifiers come from the kernel's table, which is where production gets them, so this
        // walks exactly the list the probe walks.
        var bytes = MacOsPlatformReader.ReadKernelProcessTable();
        Assert.NotNull(bytes);
        var entries = MacOsResourceParsing.ReadKernelProcessTable(bytes);
        Assert.NotEmpty(entries);

        var readable = 0;
        var refused = 0;
        foreach (var entry in entries)
        {
            var (taskInfo, bsdInfo, _) = MacOsPlatformReader.ReadProcess(entry.Pid);
            if (taskInfo is null || bsdInfo is null) refused++; else readable++;
        }

        Assert.True(readable > 10, $"only {readable} of {entries.Count} processes could be read");
        Assert.Equal(entries.Count, readable + refused);

        // AND WHAT MUST BE TRUE DEPENDS ON WHO IS RUNNING, with both branches asserting something
        // specific. As an ordinary user some processes MUST come back refused, or this run proved
        // nothing about the case the test exists for and the null-figure path is uncovered. As root
        // nothing is refused, because root may read every process, so the honest assertion there is
        // that everything really was readable.
        //
        // Asserted this way round because the unconditional version fails on a correct reader whenever
        // the Director runs as root, and a test that fails while the code is correct is deleted here
        // rather than carried with a note. This is the same fix as in MacOsResourceProbeLiveTests; the
        // reviewer caught that I had made it in one place and not the other.
        if (Environment.IsPrivilegedProcess)
        {
            Assert.Equal(0, refused);
            return;
        }

        Assert.True(refused > 0,
            "every process was readable, so this run did not exercise the refusal this test is for");
    }

    [LiveMachineFact("the Mach timebase through mach_timebase_info")]
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

        // A SANITY WINDOW, AND IT IS WEAKER THAN IT LOOKS - said plainly rather than left to be assumed.
        // This process has used at least a few milliseconds of processor time and cannot have used a
        // year of it, so the converted figure must land in a very wide window. But skipping the
        // conversion entirely does NOT necessarily fall outside that window: for a test host with two
        // seconds of processor time it gives 0.048 seconds, which is inside it. What actually catches a
        // wrong or missing conversion is the pure tests, which pin 125/3 and 1 to 1 against captured
        // output, and the two-thread test that checks the probe against the runtime's own figure. This
        // one proves the timebase can be READ from the machine and is not absurd.
        var (taskInfo, _, _) = MacOsPlatformReader.ReadProcess(Environment.ProcessId);
        Assert.NotNull(taskInfo);
        Assert.True(MacOsResourceParsing.TryReadProcessTaskInfo(taskInfo, out var task));

        var seconds = MacOsResourceParsing.MachAbsoluteToNanoseconds(task.TotalMachTime, timebase) / 1e9;
        Assert.InRange(seconds, 0.001, 86_400.0);
    }

    [LiveMachineFact("process figures through proc_pidinfo")]
    [Trait("Category", "Resources")]
    public void ReadProcess_AProcessThatDoesNotExist_IsNullRatherThanThrowing()
    {
        var (taskInfo, bsdInfo, rusage) = MacOsPlatformReader.ReadProcess(int.MaxValue - 1);

        Assert.Null(taskInfo);
        Assert.Null(bsdInfo);
        Assert.Null(rusage);
    }
}
