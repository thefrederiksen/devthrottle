using CcDirector.Core.Machine;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static CcDirector.Core.Tests.Machine.ResourceSampleBuilder;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// "Why is this machine slow?", answered from samples built by hand or read from recordings of real
/// machines - so nothing here reads the clock or the machine it runs on.
///
/// The cases that matter most are on the rules a believable wrong answer would break:
/// <list type="bullet">
/// <item>the recorded devlinux minute of 2026-10-15 must say "disk, the hard drive sdc";</item>
/// <item>only a platform that measures stall may claim a stall percentage;</item>
/// <item>a disk's busy figure never decides;</item>
/// <item>the run queue and the load average are judged as the different quantities they are;</item>
/// <item>an unknown figure is never ranked or summed as zero.</item>
/// </list>
/// </summary>
[Trait("Category", "Resources")]
public sealed class SlownessDiagnoserTests : IDisposable
{
    private readonly List<string> _temporaryDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _temporaryDirectories) Directory.Delete(directory, recursive: true);
    }

    private static SlownessDiagnosis Diagnose(IReadOnlyList<ResourceSample> history) =>
        SlownessDiagnoser.Diagnose(OnTheTimeline(history), SessionProcessMembership.Empty);

    private static int CountOf(string word, string text) =>
        (text.Length - text.Replace(word, "", StringComparison.Ordinal).Length) / word.Length;

    /// <summary>Everything the diagnosis says, for checking what it claims.</summary>
    private static string AllWords(SlownessDiagnosis diagnosis) =>
        string.Join("\n", new[] { diagnosis.Summary, diagnosis.NextStep }.Concat(diagnosis.Evidence));

    // ---------------------------------------------------------------------------------------------
    // The recorded devlinux minute, read through the real Linux probe
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The two snapshots of devlinux's /proc recorded five seconds apart on 2026-10-15, read by the real
    /// <see cref="LinuxResourceProbe"/> - the sample the diagnosis gets in production, not a hand-built one.
    /// </summary>
    private List<ResourceSample> DevlinuxRecording()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagnoser-" + Guid.NewGuid().ToString("N"));
        _temporaryDirectories.Add(root);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 8, 26, 0, TimeSpan.Zero));
        var probe = new LinuxResourceProbe(root, LinuxProcParsingTests.Fixture("sys", "block"), clock,
            clockTicksPerSecond: () => 100, pageSize: 4096);

        CopyDirectory(LinuxProcParsingTests.Fixture("snapshot-1", "proc"), root);
        var first = probe.Read();
        Directory.Delete(root, recursive: true);
        CopyDirectory(LinuxProcParsingTests.Fixture("snapshot-2", "proc"), root);
        clock.Advance(TimeSpan.FromSeconds(5));
        return [first, probe.Read()];
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(from)) CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    [Fact]
    public void Diagnose_RecordedDevlinuxMinute_SaysDiskAndNamesTheHardDriveSdc()
    {
        var diagnosis = Diagnose(DevlinuxRecording());

        Assert.True(diagnosis.CouldDiagnose);
        Assert.True(diagnosis.IsSlow);
        Assert.Equal(SlownessCause.Disk, diagnosis.Cause); // the disk alone: processor and memory were not the cause
        Assert.NotNull(diagnosis.Culprit);
        Assert.Equal("sdc", diagnosis.Culprit.DiskName);
        Assert.Equal(DiskKind.Spinning, diagnosis.Culprit.DiskKind);
        Assert.Contains("on the hard drive sdc, 88% busy, 92 ms per write", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("in the system volume", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Equal(1, CountOf("mostly", diagnosis.Summary)); // one "mostly", for the process, not two
        // Linux measures stall directly, so it may - and does - say so, over the window it claims: the
        // kernel's own one-minute figure, 23%, and not 13%, the mean of two ten-second figures.
        Assert.Contains("stalled waiting on the disk 23% of the last minute", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains(diagnosis.Evidence, line => line.Contains("for a processor 4.7% of the last minute", StringComparison.Ordinal));
        Assert.Contains("hard drive sdc", diagnosis.NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_RecordedDevlinuxMinute_TheEvidenceSaysTheFiguresWereMeasuredDirectly()
    {
        var diagnosis = Diagnose(DevlinuxRecording());

        Assert.Contains(diagnosis.Evidence, line => line.StartsWith("Measured directly", StringComparison.Ordinal));
        Assert.Contains(diagnosis.Evidence, line => line.Contains("7 tasks were waiting on the disk", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // The wording rule (mission section 5c-i)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// macOS: the real recorded one-minute load average (2.43 on 10 cores) and solid-state disk0, with
    /// the time per write the macOS seat measured beside iostat on the Mac mini - 15.8 milliseconds with
    /// four contended writers. A single snapshot carries no interval, so no latency can be read out of
    /// the recording itself.
    /// </summary>
    private static ResourceSampleBuilder MacOs(double millisecondsPerWrite, int kernelLevel = 1)
    {
        var loadBytes = MacOsFixtureBytes("sysctl-vm-loadavg.base64");
        Assert.True(MacOsResourceParsing.TryReadLoadAverage(loadBytes, out var load, out var why), why);
        return new ResourceSampleBuilder
        {
            Cpu = CpuReading.Read(28, busiestCorePercent: 71, logicalCoreCount: 10),
            Stall = StallReading.Read(StallSource.MacOsSignals, loadAverageOneMinute: load.OneMinute, macOsMemoryPressureLevel: kernelLevel),
            DiskEvidence = DiskEvidenceSet.Read(
            [
                DiskEvidence.Read("disk0", DiskKind.SolidState, isInSystemVolume: true, busyPercent: null,
                    millisecondsPerRead: 0.4, millisecondsPerWrite: millisecondsPerWrite, averageQueueDepth: 3.9),
            ]),
        };
    }

    private static byte[] MacOsFixtureBytes(string name)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Machine", "MacOs", name));
        return Convert.FromBase64String(string.Concat(text.Split('\n').Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))));
    }

    /// <summary>
    /// Windows-shaped figures: its counters give a busy percentage and timings per disk, hard page faults,
    /// and no load average. Built by hand, because the Windows probe's narrowed recording has not landed;
    /// these tests check what the diagnosis does with that SHAPE, not what Windows reads.
    /// </summary>
    private static ResourceSampleBuilder Windows(double busy, double millisecondsPerWrite, double hardFaults = 50) => new()
    {
        Cpu = CpuReading.Read(35, busiestCorePercent: 80, logicalCoreCount: 8),
        Stall = StallReading.Read(StallSource.WindowsCounters, hardPageFaultsPerSecond: hardFaults),
        DiskEvidence = DiskEvidenceSet.Read(
        [
            DiskEvidence.Read("PhysicalDrive0", DiskKind.SolidState, isInSystemVolume: true, busyPercent: busy,
                millisecondsPerRead: 0.5, millisecondsPerWrite: millisecondsPerWrite, averageQueueDepth: 2.0),
        ]),
    };

    // ---------------------------------------------------------------------------------------------
    // The wall clock steps and the verdict does not (issue #4686)
    //
    // Before #4686 this diagnosis ordered its samples, chose its minute and printed its span by
    // SUBTRACTING ResourceSample.TakenAtUtc stamps - the one thing that record forbids, because a routine
    // time correction or a machine waking from sleep moves the wall clock while the load does not move at
    // all. The three uses fail in three different ways and in BOTH directions, so each has its own case
    // below and each names the wrong answer it would have given. Every history here states the stamp and
    // the timeline position separately, which is the only way to make the two clocks disagree; the ordinary
    // tests go through OnTheTimeline, where by construction they agree.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A history whose WALL CLOCK STEPS at a chosen point while its monotonic timeline does not: every
    /// sample carries a stamp of <c>Start + timeline (+ step, once the step has happened)</c> and a
    /// timeline position of <c>timeline</c>. A negative step is the clock going backwards.
    /// </summary>
    private static List<TimelineSample> SteppingClock(
        TimeSpan span, TimeSpan stepAt, TimeSpan step, Func<TimeSpan, ResourceSampleBuilder> at)
    {
        var history = new List<TimelineSample>();
        for (var timeline = TimeSpan.Zero; timeline <= span; timeline += TimeSpan.FromSeconds(5))
        {
            var stamped = timeline >= stepAt ? timeline + step : timeline;
            history.Add(new TimelineSample((at(timeline) with { At = stamped }).Build(), timeline));
        }
        return history;
    }

    /// <summary>
    /// THE ORDER. The clock steps BACK ten minutes half way through two minutes of samples, so the readings
    /// taken after the step carry the smallest stamps and an older reading holds the largest one. Ordering by
    /// the stamp therefore makes the last minute's readings sort FIRST and picks a sample from the first
    /// minute as the newest, and the minute that gets judged is the quiet one that has already passed.
    ///
    /// The wrong answer this would give is the dangerous direction: the disk is answering at 13.0
    /// milliseconds a write for the whole of the last minute, and the verdict is NOT SLOW.
    ///
    /// THE MARGINS ARE STATED, because review found this test caught the order line by an accident of
    /// arithmetic it did not declare. The figures are chosen so BOTH comparisons are far from the 10
    /// millisecond solid-state limit, rather than just over and just under it:
    /// <list type="bullet">
    /// <item>the real last minute is 13 readings at 13.0, so 13.0 - thirty per cent ABOVE the limit;</item>
    /// <item>the two minutes wrongly averaged together are 13 at 13.0 and 12 at 0.4, so (169 + 4.8) / 25 =
    /// 6.95 - thirty per cent BELOW it.</item>
    /// </list>
    /// With the earlier figures of 15.8 and 1.1 the wrong average landed at 8.7 against the limit of 10, so
    /// the test passed on a margin of 1.3 milliseconds; raising the slow figure to 20.0 would have made the
    /// order-only revert PASS and left that line guarded by nothing. Measured by the reviewer, not supposed.
    /// </summary>
    [Fact]
    public void Diagnose_TheClockStepsBackMidHistory_JudgesTheNewestMinuteNotTheOneWithTheLargestStamps()
    {
        var history = SteppingClock(
            TimeSpan.FromSeconds(120), stepAt: TimeSpan.FromSeconds(65), step: TimeSpan.FromMinutes(-10),
            at: timeline => MacOs(millisecondsPerWrite: timeline >= TimeSpan.FromSeconds(60) ? 13.0 : 0.4));

        // The stepped readings really do hold the smallest stamps, so this history would be mis-ordered.
        Assert.True(history[^1].Sample.TakenAtUtc < history[0].Sample.TakenAtUtc);

        var diagnosis = SlownessDiagnoser.Diagnose(history, SessionProcessMembership.Empty);

        Assert.True(diagnosis.IsSlow);
        Assert.Equal(SlownessCause.Disk, diagnosis.Cause);
        Assert.Contains("the disk is answering slowly", diagnosis.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// THAT A SORT HAPPENS AT ALL. Review found that every test in this file, old and new, hands the samples in
    /// ascending timeline order, so removing the sort entirely left all three stepping tests passing: they guard
    /// WHICH KEY sorts, not WHETHER sorting happens. The method's own contract says "in any order; they are put
    /// in timeline order before they are judged", and that sentence was true of nothing anyone checked.
    ///
    /// So this hands the identical history reversed and demands the identical verdict. Reversed is enough: with
    /// no sort, the first element decides the window's start and the last decides what counts as newest, so a
    /// reversal moves both to the wrong end.
    /// </summary>
    [Fact]
    public void Diagnose_TheSameHistoryHandedBackwards_GivesTheSameVerdict()
    {
        var forwards = SteppingClock(
            TimeSpan.FromSeconds(120), stepAt: TimeSpan.FromSeconds(65), step: TimeSpan.FromMinutes(-10),
            at: timeline => MacOs(millisecondsPerWrite: timeline >= TimeSpan.FromSeconds(60) ? 13.0 : 0.4));
        var backwards = Enumerable.Reverse(forwards).ToList();

        var one = SlownessDiagnoser.Diagnose(forwards, SessionProcessMembership.Empty);
        var other = SlownessDiagnoser.Diagnose(backwards, SessionProcessMembership.Empty);

        Assert.True(one.IsSlow);
        Assert.Equal(SlownessCause.Disk, one.Cause);
        Assert.Equal(one.Cause, other.Cause);
        Assert.Equal(one.Summary, other.Summary);
        Assert.Equal(one.Evidence, other.Evidence);
    }

    /// <summary>
    /// WHICH SAMPLES ARE IN THE MINUTE. The clock steps FORWARD ten minutes before the last two readings,
    /// which leaves the ORDER untouched - every later stamp is still larger - so this case is about
    /// membership alone. Measured from the newest stamp, only those last two readings are inside the
    /// minute, and the verdict is then built from ten seconds of a machine while claiming a minute.
    ///
    /// The wrong answer is again NOT SLOW, on a disk that has been answering at 40 milliseconds a write for
    /// the whole minute, and the evidence line would have said "averaged over 5 seconds" while the verdict
    /// claims the minute.
    /// </summary>
    [Fact]
    public void Diagnose_TheClockStepsForwardBeforeTheLastReadings_StillJudgesTheWholeMinute()
    {
        var history = SteppingClock(
            TimeSpan.FromSeconds(120), stepAt: TimeSpan.FromSeconds(115), step: TimeSpan.FromMinutes(10),
            at: timeline => MacOs(millisecondsPerWrite: timeline >= TimeSpan.FromSeconds(115) ? 1.1 : 40.0));

        // The step did NOT reorder anything: this case isolates membership from ordering.
        var stamps = history.Select(sample => sample.Sample.TakenAtUtc).ToList();
        Assert.Equal(stamps.OrderBy(stamp => stamp).ToList(), stamps);

        var diagnosis = SlownessDiagnoser.Diagnose(history, SessionProcessMembership.Empty);

        Assert.True(diagnosis.IsSlow);
        Assert.Equal(SlownessCause.Disk, diagnosis.Cause);
        Assert.Contains(diagnosis.Evidence, line => line.Contains("averaged over 60 seconds", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnosis.Evidence, line => line.Contains("averaged over 5 seconds", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE PRINTED SPAN, WHICH IS THE MILD ONE AND IS STILL A FALSE SENTENCE. The step here is small enough
    /// that every reading stays inside the minute and the order is untouched, so the SAME samples are
    /// averaged and not one figure in the verdict moves. All that changes is the window the evidence claims
    /// to have averaged over: thirty seconds of readings, described as fifty.
    ///
    /// This is the case that says what the span is and is not. It is a string in a sentence, never a
    /// divisor, so it cannot make a figure wrong - and a sentence that misreports its own window is still
    /// something we would have to withdraw.
    /// </summary>
    [Fact]
    public void Diagnose_AClockStepInsideTheMinute_PrintsTheSpanItReallyAveraged()
    {
        var history = SteppingClock(
            TimeSpan.FromSeconds(30), stepAt: TimeSpan.FromSeconds(5), step: TimeSpan.FromSeconds(20),
            at: _ => MacOs(millisecondsPerWrite: 1.1));

        // Nothing is reordered and nothing leaves the minute, so only the span can differ.
        var stamps = history.Select(sample => sample.Sample.TakenAtUtc).ToList();
        Assert.Equal(stamps.OrderBy(stamp => stamp).ToList(), stamps);
        Assert.Equal(TimeSpan.FromSeconds(50), stamps[^1] - stamps[0]);

        var diagnosis = SlownessDiagnoser.Diagnose(history, SessionProcessMembership.Empty);

        Assert.False(diagnosis.IsSlow);
        Assert.Contains(diagnosis.Evidence, line => line.Contains("averaged over 30 seconds", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnosis.Evidence, line => line.Contains("averaged over 50 seconds", StringComparison.Ordinal));
    }

    [Fact]
    public void Diagnose_MacOsDiskAnsweringSlowly_SaysAnsweringSlowlyAndNeverClaimsAStallPercentage()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => MacOs(millisecondsPerWrite: 15.8)));

        Assert.True(diagnosis.IsSlow);
        Assert.Equal(SlownessCause.Disk, diagnosis.Cause);
        Assert.Contains("the disk is answering slowly", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("solid-state drive disk0", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Equal("disk0", diagnosis.Culprit?.DiskName);
        AssertClaimsNoMeasuredStall(diagnosis);
    }

    [Fact]
    public void Diagnose_WindowsDiskAnsweringSlowly_SaysAnsweringSlowlyAndNeverClaimsAStallPercentage()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => Windows(busy: 60, millisecondsPerWrite: 40)));

        Assert.Equal(SlownessCause.Disk, diagnosis.Cause);
        Assert.Contains("the disk is answering slowly", diagnosis.Summary, StringComparison.Ordinal);
        AssertClaimsNoMeasuredStall(diagnosis);
    }

    [Fact]
    public void Diagnose_MacOsHealthy_IsNotSlowAndStillClaimsNoMeasuredStall()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => MacOs(millisecondsPerWrite: 1.1)));

        Assert.True(diagnosis.CouldDiagnose);
        Assert.False(diagnosis.IsSlow);
        AssertClaimsNoMeasuredStall(diagnosis);
        Assert.Contains(diagnosis.Evidence, line => line.Contains("publishes no figure for how long programs were stalled", StringComparison.Ordinal));
    }

    [Fact]
    public void Diagnose_MacOsKernelMemoryWarning_SaysMemoryThroughTheOneSharedMapping()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => MacOs(millisecondsPerWrite: 1.1, kernelLevel: 2)));

        Assert.Equal(SlownessCause.Memory, diagnosis.Cause);
        Assert.Contains("memory pressure warning", diagnosis.Summary, StringComparison.Ordinal);
        AssertClaimsNoMeasuredStall(diagnosis);
    }

    /// <summary>
    /// The wording rule, checked on every sentence the diagnosis produces. A platform without a stall
    /// figure may not say programs "were stalled waiting", and may not give a share "of the time".
    /// </summary>
    private static void AssertClaimsNoMeasuredStall(SlownessDiagnosis diagnosis)
    {
        var words = AllWords(diagnosis);
        Assert.DoesNotContain("stalled waiting", words, StringComparison.Ordinal);
        Assert.DoesNotContain("of the time", words, StringComparison.Ordinal);
        Assert.DoesNotContain("of the last minute", words, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // A disk's busy figure never decides
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Diagnose_DiskFullyBusyButAnsweringAtOnce_IsNotSlow()
    {
        // A solid-state drive 99% busy that answers in under a millisecond is working, not making programs wait.
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => Windows(busy: 99, millisecondsPerWrite: 0.8)));

        Assert.False(diagnosis.IsSlow);
        Assert.Contains("99% busy", AllWords(diagnosis), StringComparison.Ordinal); // shown, as evidence
    }

    [Fact]
    public void Diagnose_LinuxDiskBusyButNoStall_IsNotSlow()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            DiskEvidence = DiskEvidenceSet.Read(
            [
                DiskEvidence.Read("sdc", DiskKind.Spinning, isInSystemVolume: true, busyPercent: 97,
                    millisecondsPerRead: 30, millisecondsPerWrite: 40, averageQueueDepth: 9),
            ]),
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 1, memoryStalledPercent: 0,
                ioStalledPercent: 2, runQueueLength: 3, loadAverageOneMinute: 2, processesWaitingOnDisk: 0),
        });

        var diagnosis = Diagnose(history);

        Assert.True(diagnosis.CouldDiagnose);
        Assert.False(diagnosis.IsSlow); // the stall figure decides on Linux, and it says programs barely waited
    }

    [Fact]
    public void Diagnose_TheDiskNamedIsTheDeepestQueue_NotTheBusiest()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            DiskEvidence = DiskEvidenceSet.Read(
            [
                DiskEvidence.Read("sda", DiskKind.SolidState, isInSystemVolume: true, busyPercent: 95,
                    millisecondsPerRead: 0.3, millisecondsPerWrite: 0.5, averageQueueDepth: 0.4),
                DiskEvidence.Read("sdc", DiskKind.Spinning, isInSystemVolume: true, busyPercent: 60,
                    millisecondsPerRead: 40, millisecondsPerWrite: 80, averageQueueDepth: 12),
            ]),
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 1, memoryStalledPercent: 0,
                ioStalledPercent: 30, runQueueLength: 3, loadAverageOneMinute: 8, processesWaitingOnDisk: 9),
        });

        var diagnosis = Diagnose(history);

        Assert.Equal(SlownessCause.Disk, diagnosis.Cause);
        Assert.Equal("sdc", diagnosis.Culprit?.DiskName);
    }

    // ---------------------------------------------------------------------------------------------
    // The run queue and the load average are different quantities, each per core
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(9.0, 10, false)] // 0.9 per core: the cores are not all taken
    [InlineData(12.0, 10, true)] // 1.2 per core: every core taken, more queued
    [InlineData(47.0, 64, false)] // the same 47 on a big machine is room to spare
    public void Diagnose_LoadAverageWithoutAStallFigure_IsJudgedPerCore(double loadAverage, int cores, bool slow)
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Cpu = CpuReading.Read(40, busiestCorePercent: 90, logicalCoreCount: cores),
            Stall = StallReading.Read(StallSource.MacOsSignals, loadAverageOneMinute: loadAverage, macOsMemoryPressureLevel: 1),
        }));

        Assert.Equal(slow, diagnosis.Cause.HasFlag(SlownessCause.Cpu));
    }

    [Fact]
    public void Diagnose_RunQueueWithoutAStallFigure_IsShownButDecidesNothing()
    {
        // 200 threads wanting a processor on 8 cores would be a crushed machine, yet no threshold has been
        // written for the run queue, so it may not decide the verdict. What it counts IS now measured -
        // running plus waiting, the same on Linux and Windows - and it still decides nothing: it is one
        // moment every five seconds of a figure measured swinging 0 to 92 on a real machine, and a
        // saturated processor is already caught by busy-by-time over the whole interval.
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => Windows(busy: 10, millisecondsPerWrite: 0.5) with
        {
            Stall = StallReading.Read(StallSource.WindowsCounters, runQueueLength: 200, hardPageFaultsPerSecond: 10),
        }));

        Assert.False(diagnosis.Cause.HasFlag(SlownessCause.Cpu));
        Assert.Contains("on average 200 threads were on a processor or waiting for one, on 8 cores", AllWords(diagnosis), StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_LinuxLoadAverageHighButNoProcessorStall_IsNotTheProcessor()
    {
        // devlinux showed a load of 19 to 23 while 80% idle: the Linux load average counts tasks waiting
        // on the disk too. On Linux the processor stall figure decides, not the load average.
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Cpu = CpuReading.Read(20, busiestCorePercent: 100, logicalCoreCount: 4),
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 0.5, memoryStalledPercent: 0,
                ioStalledPercent: 1, runQueueLength: 30, loadAverageOneMinute: 22, processesWaitingOnDisk: 0),
        }));

        Assert.False(diagnosis.Cause.HasFlag(SlownessCause.Cpu));
    }

    [Fact]
    public void Diagnose_LinuxProcessorStall_IsTheProcessorAndNamesTheBusiestKnownProcess()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 45, memoryStalledPercent: 0,
                ioStalledPercent: 1, runQueueLength: 80, loadAverageOneMinute: 60, processesWaitingOnDisk: 0),
            Processes = ProcessSetReading.Read(
            [
                new ProcessReading(10, 1, "dotnet", Start.AddHours(-1), 62, 2_000_000_000, 0, 0),
                new ProcessReading(11, 1, "refused", null, null, null, null, null), // figures unknown
                new ProcessReading(12, 1, "editor", Start.AddHours(-1), 3, 500_000_000, 0, 0),
            ]),
        }));

        Assert.Equal(SlownessCause.Cpu, diagnosis.Cause);
        Assert.Equal("dotnet", diagnosis.Culprit?.ProcessName);
        Assert.Contains("stalled waiting for a processor 45% of the time", diagnosis.Summary, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Unknown is never zero, and "cannot tell" is never "not slow"
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Diagnose_MemoryCulprit_AProcessWithUnknownMemoryIsNotRankedAsUsingNone()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 1, memoryStalledPercent: 40,
                ioStalledPercent: 1, runQueueLength: 3, loadAverageOneMinute: 2, processesWaitingOnDisk: 0),
            Processes = ProcessSetReading.Read(
            [
                new ProcessReading(11, 1, "hidden", null, 5, null, null, null),
                new ProcessReading(12, 1, "browser", Start.AddHours(-1), 3, 6_000_000_000, 0, 0),
            ]),
        }));

        Assert.Equal(SlownessCause.Memory, diagnosis.Cause);
        Assert.Equal("browser", diagnosis.Culprit?.ProcessName);
    }

    [Fact]
    public void Diagnose_DiskCulprit_IsTheBiggestKnownReaderAndWriter_AndAProcessWithUnknownRatesIsLeftOut()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 1, memoryStalledPercent: 0,
                ioStalledPercent: 30, runQueueLength: 3, loadAverageOneMinute: 9, processesWaitingOnDisk: 6),
            Processes = ProcessSetReading.Read(
            [
                new ProcessReading(11, 1, "other-user", Start.AddHours(-1), 1, 100, null, null),
                new ProcessReading(12, 1, "dotnet test", Start.AddHours(-1), 6, 2_000_000_000, 100_000, 6_500_000),
                new ProcessReading(13, 1, "grep", Start.AddHours(-1), 2, 5_000_000, 2_000_000, 0),
            ]),
        }));

        Assert.Equal("dotnet test", diagnosis.Culprit?.ProcessName);
    }

    [Fact]
    public void Diagnose_CulpritInAKnownSession_NamesTheSessionAndItsName()
    {
        var history = Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 45, memoryStalledPercent: 0,
                ioStalledPercent: 1, runQueueLength: 80, loadAverageOneMinute: 60, processesWaitingOnDisk: 0),
            Processes = ProcessSetReading.Read(
            [
                new ProcessReading(4000, 1, "claude", Start.AddHours(-2), 2, 400_000_000, 0, 0),
                new ProcessReading(4100, 4000, "dotnet test", Start.AddMinutes(-5), 60, 2_000_000_000, 0, 0),
            ]),
        });
        var roots = new Dictionary<string, int> { ["140"] = 4000 };
        var membership = SessionProcessMembership.Update(SessionProcessMembership.Empty, history[^1].Processes, roots);

        var diagnosis = SlownessDiagnoser.Diagnose(OnTheTimeline(history), membership,
            new Dictionary<string, string> { ["140"] = "Resource Monitor - Dev - Linux probe" });

        Assert.Equal("140", diagnosis.Culprit?.SessionId);
        Assert.Contains("dotnet test (process 4100) in session 140 (Resource Monitor - Dev - Linux probe)", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_MemoryAndDiskTogether_BothAreNamedAndTheCulpritIsTheMemoryUser()
    {
        // Short of memory AND thrashing the disk is the classic pair: the disk is paging for the program
        // holding the memory, so that program is the one to name.
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 2, memoryStalledPercent: 35,
                ioStalledPercent: 40, runQueueLength: 3, loadAverageOneMinute: 9, processesWaitingOnDisk: 12),
            Processes = ProcessSetReading.Read(
            [
                new ProcessReading(12, 1, "browser", Start.AddHours(-1), 3, 9_000_000_000, 10_000, 10_000),
                new ProcessReading(13, 1, "kswapd-ish copier", Start.AddHours(-1), 2, 5_000_000, 50_000_000, 50_000_000),
            ]),
        }));

        Assert.Equal(SlownessCause.Memory | SlownessCause.Disk, diagnosis.Cause);
        Assert.Equal("browser", diagnosis.Culprit?.ProcessName);
        Assert.StartsWith("Slow because of memory and the disk", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_WindowsPagingHard_IsMemoryWithoutAStallClaim()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => Windows(busy: 20, millisecondsPerWrite: 0.6, hardFaults: 4_000)));

        Assert.Equal(SlownessCause.Memory, diagnosis.Cause);
        Assert.Contains("4000 memory pages a second are read back from disk", diagnosis.Summary, StringComparison.Ordinal);
        AssertClaimsNoMeasuredStall(diagnosis);
    }

    [Fact]
    public void Diagnose_NoSamples_CannotDiagnose()
    {
        var diagnosis = Diagnose([]);

        Assert.False(diagnosis.CouldDiagnose);
        Assert.False(diagnosis.IsSlow);
    }

    [Fact]
    public void Diagnose_NothingReadable_CannotDiagnoseWithTheReasonsNeverNotSlow()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(30), _ => new ResourceSampleBuilder
        {
            Cpu = CpuReading.Unreadable("no processor figures"),
            Memory = MachineMemoryReading.Unreadable("no memory figures", Start),
            Stall = StallReading.Unreadable("no pressure files"),
            DiskEvidence = DiskEvidenceSet.Unreadable("no disk statistics"),
        }));

        Assert.False(diagnosis.CouldDiagnose);
        Assert.Contains("could not be read", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_DiskUnreadableAndNothingElseStalled_CannotTellRatherThanNotSlow()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Cpu = CpuReading.Read(20, 40, 10),
            Stall = StallReading.Read(StallSource.MacOsSignals, loadAverageOneMinute: 2, macOsMemoryPressureLevel: 1),
            DiskEvidence = DiskEvidenceSet.Unreadable("IOKit refused the disk statistics"),
        }));

        Assert.False(diagnosis.CouldDiagnose);
        Assert.False(diagnosis.IsSlow);
        Assert.Contains("the disk could not be read", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_ProcessorSlowAndDiskUnreadable_NamesTheProcessorAndSaysTheDiskIsNotRuledOut()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Cpu = CpuReading.Read(97, 100, 10),
            Stall = StallReading.Read(StallSource.MacOsSignals, loadAverageOneMinute: 25, macOsMemoryPressureLevel: 1),
            DiskEvidence = DiskEvidenceSet.Unreadable("IOKit refused the disk statistics"),
        }));

        Assert.True(diagnosis.IsSlow);
        Assert.Equal(SlownessCause.Cpu, diagnosis.Cause);
        Assert.Contains(diagnosis.Evidence, line => line.Contains("not ruled out", StringComparison.Ordinal));
    }

    [Fact]
    public void Diagnose_IdleDiskWithNoOperations_IsNotJudgedAsFast()
    {
        // A disk that did no work has no time per operation. It is not slow, and it is not proof of a fast disk
        // either: the verdict is "within its limit" only for disks that did answer.
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => MacOs(millisecondsPerWrite: 1.1) with
        {
            DiskEvidence = DiskEvidenceSet.Read(
            [
                DiskEvidence.Read("disk0", DiskKind.SolidState, true, null, null, null, 0),
            ]),
        }));

        Assert.True(diagnosis.CouldDiagnose);
        Assert.False(diagnosis.IsSlow);
        Assert.Contains(diagnosis.Evidence, line => line.Contains("no disk completed an operation", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnosis.Evidence, line => line.Contains("within its limit", StringComparison.Ordinal));
        Assert.Null(diagnosis.Culprit);
    }

    [Fact]
    public void Diagnose_DiskWithOperationsWaitingAndNoneCompleting_CannotTellRatherThanFine()
    {
        // Eight operations in flight for a whole minute and not one completed may be a disk that has
        // stopped answering. With no time per operation it cannot be judged - and it is not "fine".
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => MacOs(millisecondsPerWrite: 1.1) with
        {
            DiskEvidence = DiskEvidenceSet.Read(
            [
                DiskEvidence.Read("disk0", DiskKind.SolidState, true, null, null, null, 8),
            ]),
        }));

        Assert.False(diagnosis.CouldDiagnose);
        Assert.False(diagnosis.IsSlow);
        Assert.Contains("disk0 had 8.0 operations in flight and completed none", diagnosis.Summary + string.Join(" ", diagnosis.Evidence), StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_HungExternalDiskBesideAHealthySystemDisk_CannotTellRatherThanFine()
    {
        // The system disk answers in a millisecond; the external hard drive has four operations in flight
        // and completed none all minute. A healthy disk must not hide a hung one.
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => MacOs(millisecondsPerWrite: 1.1) with
        {
            DiskEvidence = DiskEvidenceSet.Read(
            [
                DiskEvidence.Read("disk0", DiskKind.SolidState, true, null, 0.4, 1.1, 0.2),
                DiskEvidence.Read("disk2", DiskKind.Spinning, false, null, null, null, 4),
            ]),
        }));

        Assert.False(diagnosis.CouldDiagnose);
        Assert.Contains("disk2 had 4.0 operations in flight and completed none", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_LoadAverageClimbing_IsJudgedOnTheNewestFigureNotAveragedAgain()
    {
        // An idle 10-core machine, then twenty runnable threads: the kernel's one-minute load average climbs
        // to 12.6 by the newest sample (1.26 per core). Averaging it again over the window would say 7.4.
        var history = Every5Seconds(TimeSpan.FromSeconds(55), offset => new ResourceSampleBuilder
        {
            Cpu = CpuReading.Read(60, busiestCorePercent: 100, logicalCoreCount: 10),
            Stall = StallReading.Read(StallSource.MacOsSignals,
                loadAverageOneMinute: 12.6 * offset.TotalSeconds / 55, macOsMemoryPressureLevel: 1),
        });

        var diagnosis = Diagnose(history);

        Assert.True(diagnosis.Cause.HasFlag(SlownessCause.Cpu));
        Assert.Contains(diagnosis.Evidence, line => line.Contains("the one-minute load average is 12.60 on 10 cores", StringComparison.Ordinal));
    }

    [Fact]
    public void Diagnose_LinuxWithOnlyTenSecondFigures_SaysSoRatherThanCallingItAMinute()
    {
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 1, memoryStalledPercent: 0,
                ioStalledPercent: 35, runQueueLength: 3, loadAverageOneMinute: 9, processesWaitingOnDisk: 6),
        }));

        Assert.Equal(SlownessCause.Disk, diagnosis.Cause);
        Assert.DoesNotContain("of the last minute", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains(diagnosis.Evidence, line => line.Contains("ten-second figures, averaged over 60 seconds", StringComparison.Ordinal));
    }

    [Fact]
    public void Diagnose_OneMinuteFigureWinsOverTenSecondSpikes()
    {
        // A ten-second spike to 60% inside a minute the kernel puts at 5% is not a disk-bound minute.
        var diagnosis = Diagnose(Every5Seconds(TimeSpan.FromSeconds(60), _ => new ResourceSampleBuilder
        {
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 1, memoryStalledPercent: 0,
                ioStalledPercent: 60, cpuStalledPercentOneMinute: 1, memoryStalledPercentOneMinute: 0,
                ioStalledPercentOneMinute: 5, runQueueLength: 3, loadAverageOneMinute: 2, processesWaitingOnDisk: 0),
        }));

        Assert.False(diagnosis.IsSlow);
    }

    [Fact]
    public void Diagnose_OnlyTheLastMinuteCounts()
    {
        // A disk stall that ended two minutes ago is history, not the reason for slowness now.
        var history = Every5Seconds(TimeSpan.FromMinutes(3), offset => new ResourceSampleBuilder
        {
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 1, memoryStalledPercent: 0,
                ioStalledPercent: offset < TimeSpan.FromMinutes(1) ? 60 : 1, runQueueLength: 3, loadAverageOneMinute: 2,
                processesWaitingOnDisk: 0),
        });

        Assert.False(Diagnose(history).IsSlow);
    }

    [Fact]
    public void Diagnose_ASmallStallIsPrintedSoItDoesNotReadAsNever()
    {
        var diagnosis = Diagnose([new ResourceSampleBuilder
        {
            Stall = StallReading.Read(StallSource.LinuxPressureStall, cpuStalledPercent: 0.06, memoryStalledPercent: 0.17,
                ioStalledPercent: 0.5, runQueueLength: 3, loadAverageOneMinute: 2, processesWaitingOnDisk: 0),
        }.Build()]);

        Assert.Contains(diagnosis.Evidence, line => line.Contains("0.06% of the time", StringComparison.Ordinal));
        Assert.Contains(diagnosis.Evidence, line => line.Contains("0.17% of the time", StringComparison.Ordinal));
    }

    [Fact]
    public void Thresholds_AreTheOnesTheMissionDocumentRecords()
    {
        // Pinned so a change to a threshold is a visible decision, not a quiet edit.
        Assert.Equal(20.0, SlownessDiagnoser.IoStalledSlowPercent);
        Assert.Equal(20.0, SlownessDiagnoser.CpuStalledSlowPercent);
        Assert.Equal(10.0, SlownessDiagnoser.MemoryStalledSlowPercent);
        Assert.Equal(1.0, SlownessDiagnoser.LoadAveragePerCoreSlow);
        Assert.Equal(10.0, SlownessDiagnoser.SolidStateSlowMilliseconds);
        Assert.Equal(25.0, SlownessDiagnoser.SpinningSlowMilliseconds);
        Assert.Equal(TimeSpan.FromSeconds(60), SlownessDiagnoser.Window);
    }
}
