using System.Text.Json;
using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE 24-HOUR LOCAL RECORDING (the Resource Monitor mission, issue #2811), and the three failure cases
/// the mission names for it: the recording capping at 24 hours, a session's child process outliving the
/// session, and the clock jumping forwards and backwards.
///
/// Nothing here sleeps or reads a real clock. The clock is injected, and its wall-clock and monotonic
/// readings move independently - the library's fake clock moves both together, under which a wall-clock
/// jump cannot be told apart from real time passing, so the defect being tested for could not happen.
/// Each test writes into its own temporary folder and deletes it.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceRecorderTests : IDisposable
{
    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _wall;
        private long _timestamp;

        public SteppingClock(DateTimeOffset wall) => _wall = wall;
        public override DateTimeOffset GetUtcNow() => _wall;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>What time synchronisation does. May be negative. The monotonic clock does not move.</summary>
        public void StepWallClock(TimeSpan by) => _wall = _wall.Add(by);

        /// <summary>Real time passing: both clocks move together.</summary>
        public void Pass(TimeSpan by)
        {
            _wall = _wall.Add(by);
            _timestamp += by.Ticks;
        }
    }

    private static readonly DateTimeOffset Morning = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTime ProcessStart = new(2026, 11, 6, 7, 0, 0, DateTimeKind.Utc);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "resource-recorder-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        if (File.Exists(_directory)) File.Delete(_directory);
    }

    // -------------------------------------------------------------------------------------------
    // Building samples
    // -------------------------------------------------------------------------------------------

    private static ProcessReading Proc(int pid, string name, double? cpu = 1, ulong? memory = 100_000_000,
        int? parent = 1, DateTime? start = null) =>
        new(pid, parent, name, start ?? ProcessStart, cpu, memory, null, null);

    private static ResourceSample Sample(DateTime takenAtUtc, IReadOnlyList<ProcessReading>? processes = null,
        CpuReading? cpu = null, MachineMemoryReading? memory = null) =>
        new(takenAtUtc,
            cpu ?? CpuReading.Read(30, 80, 8),
            memory ?? MachineMemoryReading.Read(16UL << 30, 8UL << 30, takenAtUtc),
            SwapReading.Read(4UL << 30, 0),
            StallReading.Read(StallSource.LinuxPressureStall, 0.1, 0.2, 1.5),
            DiskEvidenceSet.Read(new[] { DiskEvidence.Read("sda", DiskKind.SolidState, true, 3, 0.4, 0.6, 0.1) }),
            ProcessSetReading.Read(processes ?? new[] { Proc(10, "init") }));

    private static RecordedSession Session(string id, string? name, params int[] pids) =>
        new(new SessionUsage(id, pids[0], 5, 200_000_000, pids, 0, 0), name);

    private static ResourceMoment Moment(SteppingClock clock, IReadOnlyList<ProcessReading>? processes = null,
        IReadOnlyList<RecordedSession>? sessions = null, CpuReading? cpu = null, MachineMemoryReading? memory = null) =>
        new(Sample(clock.GetUtcNow().UtcDateTime, processes, cpu, memory),
            sessions ?? Array.Empty<RecordedSession>(), Verdict: null, Diagnosis: null);

    /// <summary>Records <paramref name="count"/> samples, five seconds of real time apart, the first one now.</summary>
    private static void RecordEvery5Seconds(ResourceRecorder recorder, SteppingClock clock, int count)
    {
        for (var i = 0; i < count; i++)
        {
            recorder.Record(Moment(clock));
            clock.Pass(ResourceRecorder.SampleInterval);
        }
    }

    private int LinesOnDisk() =>
        ResourceRecorder.RecordingFiles(_directory).Sum(f => File.ReadAllLines(f.Path).Count(l => l.Length > 0));

    private static JsonElement Parse(RecordedLine line) => JsonDocument.Parse(line.Json).RootElement;

    // -------------------------------------------------------------------------------------------
    // Failure case 1: the recording caps at 24 hours and drops the oldest
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Record_ThirtyHoursOfSamples_KeepsExactlyTheNewest24HoursAndDropsTheOldest()
    {
        // Arrange: thirty hours at one sample every five seconds - six hours more than the recording keeps.
        var clock = new SteppingClock(Morning);
        const int thirtyHours = 30 * 720;

        // Act
        using (var recorder = new ResourceRecorder(_directory, clock))
            RecordEvery5Seconds(recorder, clock, thirtyHours);

        // Assert: what is read is exactly 24 hours, and it is the NEWEST 24 hours.
        var read = ResourceRecordingReader.ReadRetained(_directory);
        Assert.Equal(17_280, ResourceRecorder.RetainedSamples);
        Assert.Equal(ResourceRecorder.RetainedSamples, read.Lines.Count);
        Assert.Equal(thirtyHours - 1, read.Lines[^1].Seq);
        Assert.Equal(thirtyHours - ResourceRecorder.RetainedSamples, read.Lines[0].Seq);
        Assert.Equal(0, read.UnreadableLines);

        // Every sample of those 24 hours is there, in order, none missing.
        for (var i = 1; i < read.Lines.Count; i++) Assert.Equal(read.Lines[i - 1].Seq + 1, read.Lines[i].Seq);

        // And the disk holds no more than 24 hours plus the one file not yet old enough to delete whole.
        var onDisk = LinesOnDisk();
        Assert.InRange(onDisk, ResourceRecorder.RetainedSamples, ResourceRecorder.RetainedSamples + ResourceRecorder.SamplesPerFile);
        var oldestOnDisk = ResourceRecorder.RecordingFiles(_directory)[0].FirstSeq;
        Assert.True(oldestOnDisk > 0, "the oldest samples must have been deleted");
        Assert.True(oldestOnDisk <= thirtyHours - ResourceRecorder.RetainedSamples,
            "pruning must never delete a sample the newest 24 hours still needs");
    }

    [Fact]
    public void Record_LessThan24Hours_DeletesNothing()
    {
        var clock = new SteppingClock(Morning);

        using (var recorder = new ResourceRecorder(_directory, clock))
            RecordEvery5Seconds(recorder, clock, 23 * 720);

        var read = ResourceRecordingReader.ReadRetained(_directory);
        Assert.Equal(23 * 720, read.Lines.Count);
        Assert.Equal(0, read.Lines[0].Seq);
    }

    // -------------------------------------------------------------------------------------------
    // Written once a minute, in one append
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Record_WritesNothingForAMinute_ThenEverythingHeldInOneAppend()
    {
        // Arrange
        var clock = new SteppingClock(Morning);
        using var recorder = new ResourceRecorder(_directory, clock);

        // Act: twelve samples cover 0 to 55 seconds - under a minute on the monotonic clock.
        RecordEvery5Seconds(recorder, clock, 12);

        // Assert: nothing has touched the disk yet.
        Assert.Equal(0, LinesOnDisk());
        Assert.Equal(12, recorder.HeldCount);

        // The thirteenth, at 60 seconds, writes all thirteen together.
        RecordEvery5Seconds(recorder, clock, 1);
        Assert.Equal(13, LinesOnDisk());
        Assert.Equal(0, recorder.HeldCount);

        // And the next eleven are held again.
        RecordEvery5Seconds(recorder, clock, 11);
        Assert.Equal(13, LinesOnDisk());
    }

    [Fact]
    public void Dispose_WritesWhatIsHeld()
    {
        var clock = new SteppingClock(Morning);

        using (var recorder = new ResourceRecorder(_directory, clock))
            RecordEvery5Seconds(recorder, clock, 5);

        Assert.Equal(5, LinesOnDisk());
    }

    // -------------------------------------------------------------------------------------------
    // Failure case 3: the clock jumps, forwards and backwards
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Record_WallClockJumpsADayForward_DeletesNothingAndTheTimelineStaysMonotonic()
    {
        // This is the defect a wall-clock ring buffer has: "now minus 24 hours" after a day's jump forward
        // is later than every sample it holds, so it deletes the whole recording - the evidence of exactly
        // the moment something went wrong with the machine.
        var clock = new SteppingClock(Morning);

        using (var recorder = new ResourceRecorder(_directory, clock))
        {
            RecordEvery5Seconds(recorder, clock, 100);
            clock.StepWallClock(TimeSpan.FromDays(2));
            RecordEvery5Seconds(recorder, clock, 100);
        }

        var lines = ResourceRecordingReader.ReadRetained(_directory).Lines;
        Assert.Equal(200, lines.Count);

        // The recording's own timeline moved five seconds, not two days, across the jump.
        Assert.Equal(lines[99].RecordingSeconds + 5, lines[100].RecordingSeconds, precision: 3);

        // The jump is written on the line where it happened, and only there.
        var steps = lines.Select(Parse).Where(l => l.GetProperty("clockStepSeconds").ValueKind == JsonValueKind.Number).ToList();
        var step = Assert.Single(steps);
        Assert.Equal(100, step.GetProperty("seq").GetInt64());
        Assert.Equal(TimeSpan.FromDays(2).TotalSeconds, step.GetProperty("clockStepSeconds").GetDouble(), precision: 3);

        // And the wall-clock stamp is what the wall clock really said, so a person reading it sees the jump.
        Assert.Equal(lines[99].TakenAtUtc.AddDays(2).AddSeconds(5), lines[100].TakenAtUtc);
    }

    [Fact]
    public void Record_WallClockJumpsAnHourBackward_KeepsOrderAndTheTimelineStaysMonotonic()
    {
        // The quiet corruption: two different moments now carry the same wall-clock stamp, and anything
        // that sorted or windowed on that stamp would interleave them.
        var clock = new SteppingClock(Morning);

        using (var recorder = new ResourceRecorder(_directory, clock))
        {
            RecordEvery5Seconds(recorder, clock, 900); // 75 minutes, so the stamps after the jump repeat ones before it
            clock.StepWallClock(TimeSpan.FromHours(-1));
            RecordEvery5Seconds(recorder, clock, 100);
        }

        var lines = ResourceRecordingReader.ReadRetained(_directory).Lines;
        Assert.Equal(1000, lines.Count);

        for (var i = 1; i < lines.Count; i++)
        {
            Assert.Equal(lines[i - 1].Seq + 1, lines[i].Seq);
            Assert.True(lines[i].RecordingSeconds > lines[i - 1].RecordingSeconds,
                $"the recording's timeline went backwards at seq {lines[i].Seq}");
        }

        // The wall clock went back, and the line says so.
        Assert.True(lines[900].TakenAtUtc < lines[899].TakenAtUtc);
        Assert.Equal(-TimeSpan.FromHours(1).TotalSeconds, Parse(lines[900]).GetProperty("clockStepSeconds").GetDouble(), precision: 3);

        // "The last hour" measured on the timeline is 720 samples, as it should be, though on the wall clock the
        // newest stamp is now earlier than samples taken an hour ago.
        var newest = lines[^1].RecordingSeconds;
        Assert.Equal(720, lines.Count(l => l.RecordingSeconds > newest - 3600));
    }

    [Fact]
    public void Record_SmallDrift_IsNotRecordedAsAStep()
    {
        var clock = new SteppingClock(Morning);

        using (var recorder = new ResourceRecorder(_directory, clock))
        {
            RecordEvery5Seconds(recorder, clock, 10);
            clock.StepWallClock(TimeSpan.FromSeconds(1)); // within ClockStepTolerance
            RecordEvery5Seconds(recorder, clock, 10);
        }

        var lines = ResourceRecordingReader.ReadRetained(_directory).Lines;
        Assert.All(lines, l => Assert.Equal(JsonValueKind.Null, Parse(l).GetProperty("clockStepSeconds").ValueKind));
    }

    [Fact]
    public void NewRun_WallClockBehindThePreviousRun_ContinuesTheTimelineAndSaysTheClockWentBackwards()
    {
        // Arrange: a first run, stopped.
        var clock = new SteppingClock(Morning);
        using (var first = new ResourceRecorder(_directory, clock))
            RecordEvery5Seconds(first, clock, 20);
        var endOfFirst = ResourceRecordingReader.ReadRetained(_directory).Lines[^1];

        // The Director restarts with its wall clock an hour behind the last sample.
        clock.StepWallClock(TimeSpan.FromHours(-1));

        // Act
        using (var second = new ResourceRecorder(_directory, clock))
            RecordEvery5Seconds(second, clock, 3);

        // Assert
        var lines = ResourceRecordingReader.ReadRetained(_directory).Lines;
        Assert.Equal(23, lines.Count);
        var restart = Parse(lines[20]);
        Assert.Equal(20, lines[20].Seq);
        Assert.True(restart.GetProperty("runStart").GetBoolean());
        Assert.True(restart.GetProperty("clockWentBackwardsAtRestart").GetBoolean());
        Assert.True(restart.GetProperty("restartGapSeconds").GetDouble() < 0);
        Assert.Equal(endOfFirst.RecordingSeconds, lines[20].RecordingSeconds, precision: 3);
        Assert.True(lines[21].RecordingSeconds > lines[20].RecordingSeconds);
        Assert.NotEqual(Parse(lines[19]).GetProperty("run").GetString(), restart.GetProperty("run").GetString());
    }

    [Fact]
    public void NewRun_AfterAGap_CarriesTheGapOnTheTimeline()
    {
        var clock = new SteppingClock(Morning);
        using (var first = new ResourceRecorder(_directory, clock))
            RecordEvery5Seconds(first, clock, 20);
        var endOfFirst = ResourceRecordingReader.ReadRetained(_directory).Lines[^1];

        clock.Pass(TimeSpan.FromMinutes(10));
        using (var second = new ResourceRecorder(_directory, clock))
            RecordEvery5Seconds(second, clock, 1);

        var restart = ResourceRecordingReader.ReadRetained(_directory).Lines[^1];
        var parsed = Parse(restart);
        Assert.False(parsed.GetProperty("clockWentBackwardsAtRestart").GetBoolean());
        Assert.Equal(605, parsed.GetProperty("restartGapSeconds").GetDouble(), precision: 3);
        Assert.Equal(endOfFirst.RecordingSeconds + 605, restart.RecordingSeconds, precision: 3);
    }

    // -------------------------------------------------------------------------------------------
    // Failure case 2: a session's child process outlives the session
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Record_ChildOutlivesItsSession_IsStillListedNamingTheEndedSession()
    {
        // Arrange: session 140 runs an agent (pid 500) that started a test run (pid 501).
        var clock = new SteppingClock(Morning);
        var agent = Proc(500, "agent", cpu: 2, parent: 1);
        var testRun = Proc(501, "dotnet", cpu: 1, parent: 500);
        var session = Session("session-140", "Resource Monitor - Developer", 500, 501);

        using (var recorder = new ResourceRecorder(_directory, clock))
        {
            recorder.Record(Moment(clock, new[] { Proc(1, "systemd"), agent, testRun }, new[] { session }));
            clock.Pass(ResourceRecorder.SampleInterval);

            // Act: the session ends. Its agent is gone, but the test run was re-parented and is now flat out.
            var orphan = testRun with { ParentPid = 1, CpuPercent = 90 };
            recorder.Record(Moment(clock, new[] { Proc(1, "systemd"), orphan },
                sessions: Array.Empty<RecordedSession>(), cpu: CpuReading.Read(93, 100, 8)));
        }

        // Assert
        var lines = ResourceRecordingReader.ReadRetained(_directory).Lines;
        var before = Parse(lines[0]).GetProperty("processes").GetProperty("listed").EnumerateArray()
            .Single(p => p.GetProperty("pid").GetInt32() == 501);
        Assert.Equal("session-140", before.GetProperty("sessionId").GetString());

        var after = Parse(lines[1]);
        var listed = after.GetProperty("processes").GetProperty("listed").EnumerateArray()
            .Single(p => p.GetProperty("pid").GetInt32() == 501);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("sessionId").ValueKind);
        Assert.Equal("session-140", listed.GetProperty("formerSessionId").GetString());
        Assert.Equal("Resource Monitor - Developer", listed.GetProperty("formerSessionName").GetString());
        Assert.True(listed.GetProperty("formerSessionEnded").GetBoolean());
        Assert.Equal(90, listed.GetProperty("cpuPercent").GetDouble());
        Assert.Equal(1, after.GetProperty("processes").GetProperty("outlivedOrLeftSession").GetInt32());

        // The machine's own figure is the kernel's total and still carries it: the orphan did not vanish from it.
        Assert.Equal(93, after.GetProperty("cpu").GetProperty("totalBusyPercent").GetDouble());
        Assert.Empty(after.GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public void Record_ChildLeavesARunningSession_SaysTheSessionHasNotEnded()
    {
        var clock = new SteppingClock(Morning);
        var agent = Proc(500, "agent", parent: 1);
        var daemon = Proc(502, "node", parent: 500);

        using (var recorder = new ResourceRecorder(_directory, clock))
        {
            recorder.Record(Moment(clock, new[] { agent, daemon }, new[] { Session("s", "S", 500, 502) }));
            clock.Pass(ResourceRecorder.SampleInterval);
            // The daemon detached: the session still runs, but the daemon is no longer in its tree.
            recorder.Record(Moment(clock, new[] { agent, daemon with { ParentPid = 1 } }, new[] { Session("s", "S", 500) }));
        }

        var listed = Parse(ResourceRecordingReader.ReadRetained(_directory).Lines[1])
            .GetProperty("processes").GetProperty("listed").EnumerateArray()
            .Single(p => p.GetProperty("pid").GetInt32() == 502);
        Assert.Equal("s", listed.GetProperty("formerSessionId").GetString());
        Assert.False(listed.GetProperty("formerSessionEnded").GetBoolean());
    }

    [Fact]
    public void Record_ProcessIdReusedByANewProcess_InheritsNoSession()
    {
        // Same process id, a different start time: a different process, which owes the old session nothing.
        var clock = new SteppingClock(Morning);

        using (var recorder = new ResourceRecorder(_directory, clock))
        {
            recorder.Record(Moment(clock, new[] { Proc(501, "dotnet") }, new[] { Session("s", "S", 501) }));
            clock.Pass(ResourceRecorder.SampleInterval);
            recorder.Record(Moment(clock, new[] { Proc(501, "bash", start: ProcessStart.AddHours(1)) }));
        }

        var listed = Parse(ResourceRecordingReader.ReadRetained(_directory).Lines[1])
            .GetProperty("processes").GetProperty("listed").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("formerSessionId").ValueKind);
    }

    [Fact]
    public void Record_ChildOutlivesSessionButIsTheQuietestProcess_IsStillListed()
    {
        // Twenty busier processes would push it out of the top eight by both figures; it is listed anyway,
        // because "a process from an ended session is still here" is the fact being recorded.
        var clock = new SteppingClock(Morning);
        var busy = Enumerable.Range(1000, 20).Select(pid => Proc(pid, "busy", cpu: 4, memory: 900_000_000)).ToList();
        var leftover = Proc(501, "dotnet", cpu: 0, memory: 1_000_000);

        using (var recorder = new ResourceRecorder(_directory, clock))
        {
            recorder.Record(Moment(clock, busy.Append(leftover).ToList(), new[] { Session("s", "S", 501) }));
            clock.Pass(ResourceRecorder.SampleInterval);
            recorder.Record(Moment(clock, busy.Append(leftover).ToList()));
        }

        var processes = Parse(ResourceRecordingReader.ReadRetained(_directory).Lines[1]).GetProperty("processes");
        Assert.Contains(processes.GetProperty("listed").EnumerateArray(), p => p.GetProperty("pid").GetInt32() == 501);
        Assert.Equal(21, processes.GetProperty("count").GetInt32());
        Assert.True(processes.GetProperty("listed").GetArrayLength() < 21, "the list is shortened, and says by how much in count");
    }

    [Fact]
    public void Record_ProcessListUnreadable_ForgetsNoSession()
    {
        // A list that failed to read is not evidence that every process ended.
        var clock = new SteppingClock(Morning);
        var unreadable = new ResourceMoment(
            Sample(Morning.UtcDateTime) with { Processes = ProcessSetReading.Unreadable("permission denied") },
            Array.Empty<RecordedSession>(), null, null);

        using (var recorder = new ResourceRecorder(_directory, clock))
        {
            recorder.Record(Moment(clock, new[] { Proc(501, "dotnet") }, new[] { Session("s", "S", 501) }));
            clock.Pass(ResourceRecorder.SampleInterval);
            recorder.Record(unreadable);
            clock.Pass(ResourceRecorder.SampleInterval);
            recorder.Record(Moment(clock, new[] { Proc(501, "dotnet") }));
        }

        var lines = ResourceRecordingReader.ReadRetained(_directory).Lines;
        var during = Parse(lines[1]).GetProperty("processes");
        Assert.False(during.GetProperty("couldRead").GetBoolean());
        Assert.Equal("permission denied", during.GetProperty("unreadableReason").GetString());
        var after = Parse(lines[2]).GetProperty("processes").GetProperty("listed").EnumerateArray().Single();
        Assert.Equal("s", after.GetProperty("formerSessionId").GetString());
    }

    // -------------------------------------------------------------------------------------------
    // A figure that could not be read is null with its reason, never the zero its type carries
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Record_UnreadableProcessorAndMemory_AreWrittenAsNullWithTheReasonAndTheOthersStillRead()
    {
        var clock = new SteppingClock(Morning);

        using (var recorder = new ResourceRecorder(_directory, clock))
        {
            recorder.Record(Moment(clock,
                processes: new[] { Proc(7, "refused", cpu: null, memory: null) },
                cpu: CpuReading.Unreadable(ResourceUnreadableReasons.FirstSample, 8),
                memory: MachineMemoryReading.Unreadable("sysctl refused", Morning.UtcDateTime)));
        }

        var line = Parse(ResourceRecordingReader.ReadRetained(_directory).Lines.Single());
        var cpu = line.GetProperty("cpu");
        Assert.False(cpu.GetProperty("couldRead").GetBoolean());
        Assert.Equal(ResourceUnreadableReasons.FirstSample, cpu.GetProperty("unreadableReason").GetString());
        Assert.Equal(JsonValueKind.Null, cpu.GetProperty("totalBusyPercent").ValueKind);
        Assert.Equal(8, cpu.GetProperty("logicalCoreCount").GetInt32());

        var memory = line.GetProperty("memory");
        Assert.Equal(JsonValueKind.Null, memory.GetProperty("availableBytes").ValueKind);
        Assert.Equal("sysctl refused", memory.GetProperty("unreadableReason").GetString());

        // Swap read fine, and says so with its own figure.
        Assert.True(line.GetProperty("swap").GetProperty("couldRead").GetBoolean());
        Assert.Equal(4UL << 30, line.GetProperty("swap").GetProperty("totalBytes").GetUInt64());

        var processes = line.GetProperty("processes");
        Assert.Equal(1, processes.GetProperty("withUnknownProcessor").GetInt32());
        Assert.Equal(1, processes.GetProperty("withUnknownMemory").GetInt32());
    }

    // -------------------------------------------------------------------------------------------
    // A disk that refuses the write
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Record_DiskRefusesTheWrite_HoldsTheSamplesAndWritesThemWhenItRecovers()
    {
        // Arrange: the recording's folder is replaced by a file, so every write is refused.
        var clock = new SteppingClock(Morning);
        using var recorder = new ResourceRecorder(_directory, clock);
        Directory.Delete(_directory);
        File.WriteAllText(_directory, "in the way");

        // Act: two minutes of refused writes.
        RecordEvery5Seconds(recorder, clock, 25);
        Assert.Equal(25, recorder.HeldCount);

        // The disk recovers.
        File.Delete(_directory);
        Directory.CreateDirectory(_directory);
        RecordEvery5Seconds(recorder, clock, 12);

        // Assert: nothing was lost.
        Assert.Equal(0, recorder.HeldCount);
        var lines = ResourceRecordingReader.ReadRetained(_directory).Lines;
        Assert.Equal(Enumerable.Range(0, 37).Select(i => (long)i), lines.Select(l => l.Seq));
    }

    [Fact]
    public void Record_DiskRefusesForOverHalfAnHour_DropsTheOldestHeldAndTheNextLineSaysHowMany()
    {
        var clock = new SteppingClock(Morning);
        using var recorder = new ResourceRecorder(_directory, clock);
        Directory.Delete(_directory);
        File.WriteAllText(_directory, "in the way");

        RecordEvery5Seconds(recorder, clock, ResourceRecorder.HeldAtMost + 10);
        Assert.Equal(ResourceRecorder.HeldAtMost, recorder.HeldCount);
        Assert.Equal(10, recorder.LostCount);

        File.Delete(_directory);
        Directory.CreateDirectory(_directory);
        recorder.Flush();
        RecordEvery5Seconds(recorder, clock, 1);
        recorder.Flush();

        var lines = ResourceRecordingReader.ReadRetained(_directory).Lines;
        Assert.Equal(10, lines[0].Seq); // the ten oldest were dropped; the gap in seq shows where
        Assert.Equal(ResourceRecorder.HeldAtMost + 1, lines.Count);
        Assert.Equal(10, Parse(lines[0]).GetProperty("samplesLostBefore").GetInt32());
        Assert.All(lines.Skip(1), l => Assert.Equal(0, Parse(l).GetProperty("samplesLostBefore").GetInt32()));
    }

    [Fact]
    public void Reader_TornLastLine_IsCountedNotHidden()
    {
        var clock = new SteppingClock(Morning);
        using (var recorder = new ResourceRecorder(_directory, clock))
            RecordEvery5Seconds(recorder, clock, 3);
        var file = ResourceRecorder.RecordingFiles(_directory).Single().Path;
        File.AppendAllText(file, "{\"v\":1,\"seq\":3,\"run\":\"tor");

        var read = ResourceRecordingReader.ReadRetained(_directory);

        Assert.Equal(3, read.Lines.Count);
        Assert.Equal(1, read.UnreadableLines);

        // And the next run carries on after the last WHOLE line, in a new file.
        using (var next = new ResourceRecorder(_directory, clock))
            RecordEvery5Seconds(next, clock, 1);
        Assert.Equal(3, ResourceRecordingReader.ReadRetained(_directory).Lines[^1].Seq);
        Assert.Equal(2, ResourceRecorder.RecordingFiles(_directory).Count);
    }
}
