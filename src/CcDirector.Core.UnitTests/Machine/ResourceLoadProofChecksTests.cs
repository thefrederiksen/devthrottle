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

    // ---- the warning limit, judged on the recorded sample's own time ----

    [Theory]
    [InlineData(0, true)]
    [InlineData(70, true)]
    [InlineData(71, false)]
    [InlineData(-1, false)]
    public void WithinWarningDeadline_CountsOnlySamplesTakenAfterTheChange(int sampleSecondsAfter, bool expected)
    {
        Assert.Equal(expected, ProofChecks.WithinWarningDeadline(Noon, Noon.AddSeconds(sampleSecondsAfter)));
    }

    [Fact]
    public void PollDeadline_CoversTheLimitPlusTheRecordersWriteInterval()
    {
        Assert.True(ProofChecks.PollDeadline >= ProofChecks.WarningDeadline + CcDirector.Core.Machine.ResourceRecorder.WriteEvery);
    }

    private static MeterObservation At(int secondsAfterNoon, MeterLevel cpu) =>
        new(new MeterBar(true, 50, cpu), new MeterBar(true, 50, MeterLevel.Normal), true, false, "None", Noon.AddSeconds(secondsAfterNoon), "{}");

    [Fact]
    public void FirstMeeting_ABatchWhoseNewestIsLate_IsJudgedOnItsEarliestRedLine()
    {
        // The recorder appended T+55 to T+110 at once; red was first recorded at T+65.
        var batch = Enumerable.Range(11, 12).Select(i => At(i * 5, i * 5 >= 65 ? MeterLevel.Critical : MeterLevel.Normal)).ToList();

        var first = ProofChecks.FirstMeeting(batch, Noon, o => o.Cpu.Level == MeterLevel.Critical);

        Assert.Equal(Noon.AddSeconds(65), first!.TakenAtUtc);
        Assert.True(ProofChecks.WithinWarningDeadline(Noon, first.TakenAtUtc));
        Assert.False(ProofChecks.WithinWarningDeadline(Noon, batch[^1].TakenAtUtc));
    }

    [Fact]
    public void FirstMeeting_OnlyLinesBeforeTheChangeMatch_IsNull_AStoppedDirectorNeverPasses()
    {
        var old = new[] { At(-30, MeterLevel.Critical), At(-25, MeterLevel.Critical) };

        Assert.Null(ProofChecks.FirstMeeting(old, Noon, o => o.Cpu.Level == MeterLevel.Critical));
    }

    [Fact]
    public void FirstMeeting_NoFreshSample_NeverCallsTheCondition_SoNoSafetyCheckMayLiveInIt()
    {
        var calls = 0;

        ProofChecks.FirstMeeting([At(-10, MeterLevel.Normal)], Noon, _ => { calls++; return true; });

        Assert.Equal(0, calls);
    }

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
        new(new MeterBar(true, 50, MeterLevel.Normal), new MeterBar(true, 50, MeterLevel.Normal), couldDiagnose, isSlow, cause, Noon, "{}");

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
