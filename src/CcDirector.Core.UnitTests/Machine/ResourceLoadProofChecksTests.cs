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
        new(new MeterBar(true, 50, MeterLevel.Normal), new MeterBar(true, 50, MeterLevel.Normal), couldDiagnose, isSlow, cause, "{}");

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
