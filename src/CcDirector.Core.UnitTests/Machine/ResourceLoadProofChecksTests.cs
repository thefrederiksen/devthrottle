using ResourceLoadProof;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The judgements the live load harness makes (tools/harnesses/resource-load-proof, issue #2811 section 8).
/// The harness's source is linked into this project so these run in the gate: a harness that judged wrongly
/// would turn a broken meter into a passing quality assurance report.
/// </summary>
[Trait("Category", "Resources")]
public class ResourceLoadProofChecksTests
{
    private static readonly DateTime Noon = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    // ---- agreement with the operating system ----

    [Theory]
    [InlineData(28, 30, true)]
    [InlineData(28, 33, true)]
    [InlineData(28, 33.1, false)]
    [InlineData(40, 30, false)]
    public void Agreement_WithinFivePoints_Passes(double ours, double theirs, bool expected)
    {
        var result = ProofChecks.Agreement("CPU", ours, new OsFigure(theirs, "vmstat"));

        Assert.Equal(expected, result.Passed);
    }

    [Fact]
    public void Agreement_OurFigureUnreadable_NeverPasses_ADashAgreesWithNothing()
    {
        var result = ProofChecks.Agreement("MEM", null, new OsFigure(0, "free"));

        Assert.False(result.Passed);
        Assert.Contains("could not read", result.Summary);
    }

    // ---- the warning limit, judged on the recording's own monotonic timeline ----

    [Theory]
    [InlineData(0, true)]
    [InlineData(70, true)]
    [InlineData(71, false)]
    public void WithinWarningDeadline_IsMeasuredTimeAgainstTheLimit(int seconds, bool expected)
    {
        Assert.Equal(expected, ProofChecks.WithinWarningDeadline(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void WithinWarningDeadline_ANegativeMeasurement_IsRefusedRatherThanClampedToZero()
    {
        // The recording going backwards is a broken instrument. Zero would be a believable figure for it,
        // and a believable figure for a broken instrument is this mission's whole defect class.
        Assert.False(ProofChecks.WithinWarningDeadline(TimeSpan.FromSeconds(-1)));
    }

    /// <summary>
    /// THE DEFECT THESE TESTS EXIST FOR, and they drive the real decision rather than arithmetic on
    /// constants. The harness decides whether the product met its 70-second criterion. Judged by
    /// subtracting two WALL-clock stamps, a time synchronisation step during the run changes the answer.
    ///
    /// Here the recording's own timeline says the warning came 65 seconds after the baseline - a pass -
    /// while the wall stamps have been moved 20 seconds forward by a step, so they say 85, a failure. The
    /// verdict must follow the recording.
    /// </summary>
    [Fact]
    public void JudgeWarning_AForwardClockStep_DoesNotTurnAPassIntoAFailure()
    {
        var window = new List<MeterObservation>
        {
            At(0, MeterLevel.Normal, recordingSeconds: 0),
            At(5, MeterLevel.Normal, recordingSeconds: 5),
            // The clock jumped 20 seconds here; the recorder marks the line and keeps its own count true.
            At(45, MeterLevel.Normal, recordingSeconds: 25, clockStepSeconds: 20),
            At(85, MeterLevel.Critical, recordingSeconds: 65),
        };

        var verdict = ProofChecks.JudgeWarning(window, Noon, Red);

        Assert.True(verdict.Seen);
        Assert.Equal(65, verdict.Elapsed.TotalSeconds);
        Assert.True(verdict.Met);
        Assert.True(verdict.ClockStepped);

        // And the arrangement really does demonstrate the defect: on the stamps it would have failed.
        Assert.True(window[^1].TakenAtUtc - Noon > ProofChecks.WarningDeadline);
    }

    [Fact]
    public void JudgeWarning_ABackwardClockStep_DoesNotTurnAFailureIntoAPass()
    {
        var window = new List<MeterObservation>
        {
            At(0, MeterLevel.Normal, recordingSeconds: 0),
            At(10, MeterLevel.Normal, recordingSeconds: 60, clockStepSeconds: -50),
            At(25, MeterLevel.Critical, recordingSeconds: 75),
        };

        var verdict = ProofChecks.JudgeWarning(window, Noon, Red);

        Assert.True(verdict.Seen);
        Assert.Equal(75, verdict.Elapsed.TotalSeconds);
        Assert.False(verdict.Met);
        Assert.True(verdict.ClockStepped);

        // On the stamps this would have read 25 seconds and passed.
        Assert.True(window[^1].TakenAtUtc - Noon < ProofChecks.WarningDeadline);
    }

    /// <summary>
    /// The harness must not judge on how long IT took to look. The recorder writes a minute of lines at
    /// once, so a product that turned red at 65 seconds is first VISIBLE up to a minute later. Judged on
    /// the observation, a correct product fails; judged on the recording, it passes. This is the finding
    /// that sent the first version of this change back.
    /// </summary>
    [Fact]
    public void JudgeWarning_AWarningSeenLateBecauseTheRecorderFlushedLate_StillPasses()
    {
        var window = new List<MeterObservation>
        {
            At(0, MeterLevel.Normal, recordingSeconds: 0),
            At(65, MeterLevel.Critical, recordingSeconds: 65),
        };

        // Whatever the harness's own stopwatch said - it may have polled at 140 seconds - the answer is 65.
        var verdict = ProofChecks.JudgeWarning(window, Noon, Red);

        Assert.True(verdict.Met);
        Assert.Equal(65, verdict.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// REVIEW ARRANGEMENT B: a backward step must not be able to CHOOSE the baseline.
    ///
    /// The measurement was always monotonic; selecting and ordering the lines was not. Ordered by the
    /// stamp, the line the recorder wrote at recordingSeconds 15 sorts BEFORE the one at 10, because the
    /// clock went back eight seconds between them - so it became the baseline, and 85 minus 15 is 70, a
    /// pass, where the true latency from the first sample after the change is 85 minus 10, a failure.
    /// </summary>
    [Fact]
    public void JudgeWarning_ABackwardStepReorderingTheStamps_DoesNotChooseTheBaseline()
    {
        var change = Noon.AddSeconds(6);
        var window = new List<MeterObservation>
        {
            At(10, MeterLevel.Normal, recordingSeconds: 10),
            At(7, MeterLevel.Normal, recordingSeconds: 15, clockStepSeconds: -8),
            At(77, MeterLevel.Critical, recordingSeconds: 85),
        };

        var verdict = ProofChecks.JudgeWarning(window, change, Red);

        // The earliest line ON THE RECORDING'S TIMELINE is the baseline, whatever the stamps say.
        Assert.Equal(10, verdict.Baseline!.RecordingSeconds);
        Assert.Equal(75, verdict.Elapsed.TotalSeconds);
        Assert.False(verdict.Met);

        // And the arrangement really does demonstrate it: in stamp order the step line sorts first.
        Assert.True(window[1].TakenAtUtc < window[0].TakenAtUtc);
    }

    /// <summary>
    /// REVIEW ARRANGEMENT A: the step mark must be found even on a line the stamp selection excludes.
    ///
    /// The line carrying the step is stamped BEFORE the change - that is what a backward step does - so it
    /// is not a member, yet it sits inside the span on the recording's timeline. Scanning only the members
    /// loses the one mark the run owes its reader.
    /// </summary>
    [Fact]
    public void JudgeWarning_AStepOnALineStampedBeforeTheChange_IsStillFound()
    {
        var change = Noon.AddSeconds(9);
        var window = new List<MeterObservation>
        {
            At(10, MeterLevel.Normal, recordingSeconds: 10),
            At(7, MeterLevel.Normal, recordingSeconds: 15, clockStepSeconds: -8),
            At(12, MeterLevel.Critical, recordingSeconds: 20),
        };

        var verdict = ProofChecks.JudgeWarning(window, change, Red);

        Assert.Equal(10, verdict.Elapsed.TotalSeconds);
        Assert.True(verdict.Met);
        Assert.True(verdict.ClockStepped);

        // The marked line is not a member: its stamp is before the change.
        Assert.False(ProofChecks.TakenAfterTheChange(change, window[1].TakenAtUtc));
    }

    /// <summary>
    /// Inherited from the deleted FirstMeeting, which pinned this and JudgeWarning did not: the condition
    /// is a pure judgement of samples and MUST NOT be a place to hang a safety check, because it does not
    /// run when nothing was recorded after the change.
    /// </summary>
    [Fact]
    public void JudgeWarning_NothingRecordedAfterTheChange_NeverCallsTheCondition()
    {
        var called = false;
        var window = new List<MeterObservation> { At(-30, MeterLevel.Critical, recordingSeconds: -30) };

        var verdict = ProofChecks.JudgeWarning(window, Noon, o => { called = true; return Red(o); });

        Assert.False(verdict.Seen);
        Assert.False(called);
    }

    [Fact]
    public void JudgeWarning_ARestartInsideTheSpan_NeverCountsAsMet_BecauseTheGapIsWallClock()
    {
        var window = new List<MeterObservation>
        {
            At(0, MeterLevel.Normal, recordingSeconds: 0, run: "run-a"),
            At(30, MeterLevel.Critical, recordingSeconds: 30, run: "run-b"),
        };

        var verdict = ProofChecks.JudgeWarning(window, Noon, Red);

        Assert.True(verdict.Seen);
        Assert.True(verdict.DirectorRestarted);
        Assert.False(verdict.Met);
    }

    [Fact]
    public void JudgeWarning_TheBatchWhoseNewestIsLate_IsJudgedOnItsEarliestRedLine()
    {
        // The recorder appended T+55 to T+110 at once; red was first recorded at T+65. Judging the newest
        // line would report a 65-second warning as 110.
        var batch = Enumerable.Range(11, 12)
            .Select(i => At(i * 5, i * 5 >= 65 ? MeterLevel.Critical : MeterLevel.Normal))
            .ToList();

        var verdict = ProofChecks.JudgeWarning(batch, Noon, Red);

        Assert.Equal(Noon.AddSeconds(65), verdict.Sample!.TakenAtUtc);
        // Measured from the first sample at or after the change (T+55), not from the change itself: the
        // baseline is a real sample, so this overstates by at most one interval and never understates.
        Assert.Equal(10, verdict.Elapsed.TotalSeconds);
        Assert.True(verdict.Met);
    }

    [Fact]
    public void JudgeWarning_NothingRecordedAfterTheChange_IsNotSeen_AStoppedDirectorNeverPasses()
    {
        var window = new List<MeterObservation> { At(-30, MeterLevel.Critical, recordingSeconds: -30) };

        var verdict = ProofChecks.JudgeWarning(window, Noon, Red);

        Assert.False(verdict.Seen);
        Assert.False(verdict.Met);
    }

    [Fact]
    public void JudgeWarning_SamplesButNoneMeetTheCondition_IsNotSeen()
    {
        var window = new List<MeterObservation> { At(0, MeterLevel.Normal), At(80, MeterLevel.Normal) };

        var verdict = ProofChecks.JudgeWarning(window, Noon, Red);

        Assert.False(verdict.Seen);
        Assert.False(verdict.Met);
        Assert.NotNull(verdict.Baseline);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(70, true)]
    [InlineData(-1, false)]
    public void TakenAfterTheChange_SelectsBySampleStamp_WhichIsSelectionNotMeasurement(int sampleSecondsAfter, bool expected)
    {
        Assert.Equal(expected, ProofChecks.TakenAfterTheChange(Noon, Noon.AddSeconds(sampleSecondsAfter)));
    }

    [Fact]
    public void PollDeadline_CoversTheLimitPlusTheRecordersWriteInterval()
    {
        Assert.True(ProofChecks.PollDeadline >= ProofChecks.WarningDeadline + CcDirector.Core.Machine.ResourceRecorder.WriteEvery);
    }

    private const string OneRun = "run-a";

    /// <summary>
    /// A recorded sample. <paramref name="recordingSeconds"/> defaults to the wall offset, which is what an
    /// undisturbed run looks like; a test that wants the two timelines to DISAGREE - which is what a clock
    /// step does - passes it explicitly.
    /// </summary>
    private static MeterObservation At(
        int secondsAfterNoon, MeterLevel cpu, double? recordingSeconds = null,
        string run = OneRun, double? clockStepSeconds = null) =>
        new(new MeterBar(true, 50, cpu), new MeterBar(true, 50, MeterLevel.Normal), true, false, "None",
            Noon.AddSeconds(secondsAfterNoon), recordingSeconds ?? secondsAfterNoon, run, clockStepSeconds, "{}");

    private static bool Red(MeterObservation o) => o.Cpu.Level == MeterLevel.Critical;

    [Fact]
    public void ParseDiagnoseSamples_ReadsEveryLineOldestFirst()
    {
        var path = Path.Combine(CcDirector.TestInfrastructure.RepositorySourceIndex.Root,
            "tools", "cc-devthrottle", "tests", "fixtures", "resource-recording", "samples-000000000000.jsonl");
        var lines = File.ReadAllLines(path).Where(line => line.Length > 0).Reverse().ToList();

        var samples = ProofChecks.ParseDiagnoseSamples($"{{ \"samples\": [{string.Join(",", lines)}] }}");

        Assert.Equal(lines.Count, samples.Count);
        Assert.True(samples.Zip(samples.Skip(1)).All(pair => pair.First.TakenAtUtc <= pair.Second.TakenAtUtc));
    }

    [Fact]
    public void ParseDiagnoseSamples_EmptyWindow_Throws_NeverAClean_Window()
    {
        Assert.Throws<FormatException>(() => ProofChecks.ParseDiagnoseSamples("""{ "samples": [] }"""));
    }

    [Fact]
    public void ParseDiagnoseSamples_WithoutTheSamplesFlag_Throws_SayingSo()
    {
        var error = Assert.Throws<FormatException>(() => ProofChecks.ParseDiagnoseSamples("""{ "samples": null }"""));

        Assert.Contains("--samples", error.Message);
    }

    [Fact]
    public void FreshSampleAge_IsTheWriteIntervalPlusTwoSamples()
    {
        Assert.Equal(CcDirector.Core.Machine.ResourceRecorder.WriteEvery + TimeSpan.FromSeconds(10), ProofChecks.FreshSampleAge);
    }

    // ---- the Gateway rows ----

    [Fact]
    public void ParseWindowStarts_ReadsUtcAndOffsetTimes()
    {
        var starts = ProofChecks.ParseWindowStarts("""["2026-11-06T12:00:00Z", "2026-11-06T13:05:00+01:00"]""");

        Assert.Equal([Noon, Noon.AddMinutes(5)], starts);
        Assert.All(starts, start => Assert.Equal(DateTimeKind.Utc, start.Kind));
    }

    [Fact]
    public void ParseWindowStarts_ATimeWithNoOffset_Throws_ItWouldBeReadAsLocal()
    {
        Assert.Throws<FormatException>(() => ProofChecks.ParseWindowStarts("""["2026-11-06T12:00:00"]"""));
    }

    [Fact]
    public void ParseWindowStarts_NotAnArray_Throws()
    {
        Assert.Throws<FormatException>(() => ProofChecks.ParseWindowStarts("""{ "rows": [] }"""));
    }

    // ---- the sampler's own cost ----

    [Fact]
    public void SamplerCost_OnePointTwoSecondsInTenMinutes_IsPointTwoPerCent_AndPasses()
    {
        var result = ProofChecks.SamplerCost(TimeSpan.FromSeconds(1.2), TimeSpan.FromMinutes(10));

        Assert.Equal(0.2, result.PercentOfOneCore, 6);
        Assert.True(result.Passed);
    }

    [Fact]
    public void SamplerCost_ThreeSecondsInTenMinutes_IsExactlyTheLimit_AndFails_BecauseItMustBeUnder()
    {
        var result = ProofChecks.SamplerCost(TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(10));

        Assert.False(result.Passed);
    }

    [Fact]
    public void SamplerCost_NoWallTime_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProofChecks.SamplerCost(TimeSpan.Zero, TimeSpan.Zero));
    }

    [Fact]
    public void SamplerCost_NegativeProcessorTime_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProofChecks.SamplerCost(TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(1)));
    }

    // ---- the Gateway gap ----

    [Fact]
    public void Gaps_EveryWindowStored_Passes()
    {
        var stored = Enumerable.Range(0, 6).Select(i => Noon.AddMinutes(5 * i)).ToList();

        var result = ProofChecks.Gaps(stored, Noon, Noon.AddMinutes(30));

        Assert.True(result.Passed);
        Assert.Empty(result.MissingWindowStartsUtc);
    }

    [Fact]
    public void Gaps_TwoWindowsMissingDuringTheBlock_FailsAndNamesThem()
    {
        var stored = new[] { Noon, Noon.AddMinutes(5), Noon.AddMinutes(20), Noon.AddMinutes(25) };

        var result = ProofChecks.Gaps(stored, Noon, Noon.AddMinutes(30));

        Assert.False(result.Passed);
        Assert.Equal([Noon.AddMinutes(10), Noon.AddMinutes(15)], result.MissingWindowStartsUtc);
    }

    [Fact]
    public void Gaps_NoRowsAtAll_Fails_BecauseAnEmptyResultIsABrokenInstrument()
    {
        var result = ProofChecks.Gaps([], Noon, Noon.AddMinutes(30));

        Assert.False(result.Passed);
        Assert.Contains("no rows at all", result.Summary);
    }

    [Fact]
    public void Gaps_AWindowStillOpen_IsNotOwedYet()
    {
        var stored = new[] { Noon };

        var result = ProofChecks.Gaps(stored, Noon, Noon.AddMinutes(8));

        Assert.True(result.Passed);
    }

    [Fact]
    public void Gaps_RowStampedInsideItsWindow_CountsForThatWindow()
    {
        var result = ProofChecks.Gaps([Noon.AddSeconds(3)], Noon, Noon.AddMinutes(5));

        Assert.True(result.Passed);
    }

    [Fact]
    public void AlignToWindow_RoundsDownToTheFiveMinuteBoundary()
    {
        Assert.Equal(Noon.AddMinutes(5), ProofChecks.AlignToWindow(Noon.AddMinutes(9).AddSeconds(59)));
    }

    // ---- the diagnosis ----

    private static MeterObservation Diagnosis(bool couldDiagnose, bool isSlow, string cause) =>
        new(new MeterBar(true, 50, MeterLevel.Normal), new MeterBar(true, 50, MeterLevel.Normal), couldDiagnose, isSlow, cause, Noon, 0, OneRun, null, "{}");

    [Theory]
    [InlineData("Cpu", "Cpu", true)]
    [InlineData("Cpu, Disk", "Cpu", true)]
    [InlineData("Memory", "Cpu", false)]
    [InlineData("cpu", "Cpu", true)]
    public void DiagnosisNames_MatchesAWholeCause(string cause, string expected, bool matches)
    {
        Assert.Equal(matches, ProofChecks.DiagnosisNames(Diagnosis(true, true, cause), expected));
    }

    [Fact]
    public void DiagnosisNames_CouldNotDiagnose_NeverMatches_NotKnownIsNotCpu()
    {
        Assert.False(ProofChecks.DiagnosisNames(Diagnosis(false, true, "Cpu"), "Cpu"));
    }

    [Fact]
    public void DiagnosisNames_NotSlow_NeverMatches()
    {
        Assert.False(ProofChecks.DiagnosisNames(Diagnosis(true, false, "Cpu"), "Cpu"));
    }

    // ---- the operating system's own figures ----

    [Fact]
    public void ParseFree_UsesTotalMinusAvailable()
    {
        const string free = """
                           total        used        free      shared  buff/cache   available
            Mem:     41943040000 10000000000  5000000000   100000000 26943040000 28000000000
            Swap:     8589934592           0  8589934592
            """;

        var figure = ProofChecks.ParseFreeMemoryPercent(free);

        Assert.Equal((41943040000.0 - 28000000000.0) / 41943040000.0 * 100, figure.Percent, 6);
    }

    [Fact]
    public void ParseFree_NoAvailableColumn_Throws()
    {
        Assert.Throws<FormatException>(() => ProofChecks.ParseFreeMemoryPercent("total used free\nMem: 1 2 3"));
    }

    [Fact]
    public void ParseVmstat_UsesTheLastReading_NotTheSinceBootAverage()
    {
        const string vmstat = """
            procs -----------memory---------- ---swap-- -----io---- -system-- ------cpu-----
             r  b   swpd   free   buff  cache   si   so    bi    bo   in   cs us sy id wa st
             1  0      0 500000  10000 900000    0    0     5     9  100  200  2  1 97  0  0
             9  0      0 400000  10000 900000    0    0     0     0  900 2000 80 15  5  0  0
            """;

        var figure = ProofChecks.ParseVmstatBusyPercent(vmstat);

        Assert.Equal(95, figure.Percent);
    }

    [Fact]
    public void ParseVmstat_OneReadingOnly_Throws_BecauseItIsTheSinceBootAverage()
    {
        const string vmstat = """
             r  b   swpd   free   buff  cache   si   so    bi    bo   in   cs us sy id wa st
             1  0      0 500000  10000 900000    0    0     5     9  100  200  2  1 97  0  0
            """;

        Assert.Throws<FormatException>(() => ProofChecks.ParseVmstatBusyPercent(vmstat));
    }

    // ---- the Director's diagnose output (cc-devthrottle machine diagnose --json) ----

    private const string Healthy = """
        { "newest": {
            "takenAtUtc": "2026-11-06T12:00:05Z",
            "recordingSeconds": 5.0,
            "run": "11111111-1111-1111-1111-111111111111",
            "clockStepSeconds": null,
            "cpu": { "couldRead": true, "unreadableReason": null, "totalBusyPercent": 28.0 },
            "memory": { "couldRead": true, "unreadableReason": null, "totalBytes": 1000, "availableBytes": 450 },
            "verdict": { "worst": "Tight", "resources": [
                { "kind": "Cpu", "level": "Normal", "evidence": "28% busy" },
                { "kind": "Memory", "level": "Tight", "evidence": "450 available" },
                { "kind": "Swap", "level": "Normal", "evidence": "fine" } ] },
            "diagnosis": { "couldDiagnose": true, "isSlow": true, "causes": ["Cpu", "Disk"] } } }
        """;

    [Fact]
    public void ParseDiagnose_ReadsBothBarsAndTheDiagnosis()
    {
        var observation = ProofChecks.ParseDiagnoseJson(Healthy);

        Assert.Equal(28.0, observation.Cpu.Percent);
        Assert.Equal(MeterLevel.Normal, observation.Cpu.Level);
        Assert.Equal(55.0, observation.Memory.Percent!.Value, 6);
        Assert.Equal(MeterLevel.Tight, observation.Memory.Level);
        Assert.True(observation.CouldDiagnose);
        Assert.Equal("Cpu, Disk", observation.Cause);
        Assert.True(ProofChecks.DiagnosisNames(observation, "Disk"));
    }

    [Fact]
    public void ParseDiagnose_CarriesWhenTheSampleWasTaken()
    {
        Assert.Equal(Noon.AddSeconds(5), ProofChecks.ParseDiagnoseJson(Healthy).TakenAtUtc);
    }

    [Fact]
    public void ParseDiagnose_NoRecordedVerdict_BothLevelsUnknown_NeverNormal()
    {
        var start = Healthy.IndexOf("\"verdict\"", StringComparison.Ordinal);
        var end = Healthy.IndexOf("\"diagnosis\"", StringComparison.Ordinal);
        var json = Healthy[..start] + "\"verdict\": null, " + Healthy[end..];

        var observation = ProofChecks.ParseDiagnoseJson(json);

        Assert.Equal(MeterLevel.Unknown, observation.Cpu.Level);
        Assert.Equal(MeterLevel.Unknown, observation.Memory.Level);
    }

    [Fact]
    public void ParseDiagnose_NoCauses_IsNone()
    {
        var observation = ProofChecks.ParseDiagnoseJson(Healthy.Replace("\"causes\": [\"Cpu\", \"Disk\"]", "\"causes\": []"));

        Assert.Equal("None", observation.Cause);
    }

    [Fact]
    public void ParseDiagnose_UnreadableProcessor_HasNoPercent()
    {
        var json = Healthy.Replace("\"couldRead\": true, \"unreadableReason\": null, \"totalBusyPercent\": 28.0",
            "\"couldRead\": false, \"unreadableReason\": \"first sample, no interval yet\", \"totalBusyPercent\": null");

        var observation = ProofChecks.ParseDiagnoseJson(json);

        Assert.False(observation.Cpu.CouldRead);
        Assert.Null(observation.Cpu.Percent);
    }

    [Fact]
    public void ParseDiagnose_UnreadableProcessorCarryingAFigure_Throws_ThatIsAMadeUpNumber()
    {
        var json = Healthy.Replace("\"couldRead\": true, \"unreadableReason\": null, \"totalBusyPercent\": 28.0",
            "\"couldRead\": false, \"unreadableReason\": \"gone\", \"totalBusyPercent\": 0");

        var error = Assert.Throws<FormatException>(() => ProofChecks.ParseDiagnoseJson(json));
        Assert.Contains("made-up", error.Message);
    }

    [Fact]
    public void ParseDiagnose_MemoryThatDoesNotAddUp_Throws()
    {
        var json = Healthy.Replace("\"availableBytes\": 450", "\"availableBytes\": 4500");

        Assert.Throws<FormatException>(() => ProofChecks.ParseDiagnoseJson(json));
    }

    [Fact]
    public void ParseDiagnose_NothingRecordedYet_Throws_NotAnEmptyMeter()
    {
        var error = Assert.Throws<FormatException>(() => ProofChecks.ParseDiagnoseJson("""{ "newest": null }"""));

        Assert.Contains("recorded nothing", error.Message);
    }

    [Fact]
    public void ParseDiagnose_MissingField_ThrowsNamingIt()
    {
        var error = Assert.Throws<FormatException>(() => ProofChecks.ParseDiagnoseJson("""{ "recording": {} }"""));

        Assert.Contains("\"newest\"", error.Message);
    }

    [Fact]
    public void ParseDiagnose_VerdictWithoutMemory_Throws()
    {
        var json = Healthy.Replace("{ \"kind\": \"Memory\", \"level\": \"Tight\", \"evidence\": \"450 available\" },", "");

        Assert.Throws<FormatException>(() => ProofChecks.ParseDiagnoseJson(json));
    }

    [Fact]
    public void ParseDiagnose_UnknownLevelWord_Throws()
    {
        Assert.Throws<FormatException>(() => ProofChecks.ParseDiagnoseJson(Healthy.Replace("\"level\": \"Normal\", \"evidence\": \"28% busy\"", "\"level\": \"Green\", \"evidence\": \"28% busy\"")));
    }

    [Fact]
    public void ParseDiagnose_EveryLineTheRecorderWrote_Parses_SoTheHarnessReadsTheRealFormat()
    {
        // The committed recording the recorder's golden test writes byte for byte: the real format, not a copy of it.
        var path = Path.Combine(CcDirector.TestInfrastructure.RepositorySourceIndex.Root,
            "tools", "cc-devthrottle", "tests", "fixtures", "resource-recording", "samples-000000000000.jsonl");
        var lines = File.ReadAllLines(path).Where(line => line.Length > 0).ToList();
        Assert.NotEmpty(lines);

        var observations = lines.Select(line => ProofChecks.ParseDiagnoseJson($"{{ \"newest\": {line} }}")).ToList();

        // The first line is the first sample after start-up, so its processor is unreadable; the recording's last
        // line is the devlinux disk verdict of 2026-10-15.
        Assert.False(observations[0].Cpu.CouldRead);
        Assert.Null(observations[0].Cpu.Percent);
        Assert.All(observations, observation => Assert.True(observation.Memory.CouldRead));
        Assert.True(ProofChecks.DiagnosisNames(observations[^1], "Disk"));
    }
}
