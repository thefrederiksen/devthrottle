using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Triggers;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// THE CONTRACT FOR A CHECK FROM A DEFINITION (Factory Definition, phase 5b, design 3.1): today's rules, plus an
/// optional <c>context</c> string of at most 8 KB and a state file of at most 64 KB. A command trigger's reading is
/// unchanged - each test that could tell the two apart reads the SAME report both ways.
/// </summary>
public sealed class TriggerCheckContractDefinitionTests
{
    private static TriggerCheckReport Exit0(string output, string? state = null, bool oversize = false)
        => new() { ExitCode = 0, Output = output, State = state, StateOversize = oversize };

    [Fact]
    public void Read_TheSameReport_BothWays_CountsTheSame_AndOnlyTheDefinitionCheckReadsItsContext()
    {
        var report = Exit0("""{"count": 3, "context": "three new threads"}""", state: "x");

        var command = TriggerCheckContract.Read(report);
        var definition = TriggerCheckContract.Read(report, definitionCheck: true);

        Assert.Equal(new TriggerCheckReading(3, null), command);
        Assert.Equal(new TriggerCheckReading(3, null, "three new threads"), definition);
    }

    [Fact]
    public void Read_AContextThatIsNotAString_FailsADefinitionCheck_AndIsIgnoredOnACommand()
    {
        var report = Exit0("""{"count": 1, "context": ["a"]}""");

        Assert.Equal(1, TriggerCheckContract.Read(report).Count);
        Assert.Equal("the context is not a string: [\"a\"]", TriggerCheckContract.Read(report, definitionCheck: true).FailureReason);
    }

    [Fact]
    public void Read_AContextOf8KB_IsKept_AndOneByteMore_Fails()
    {
        var at = new string('a', TriggerCheckContract.MaxContextBytes);
        var over = at + "a";

        Assert.Equal(at, TriggerCheckContract.Read(Exit0($$"""{"count": 1, "context": "{{at}}"}"""), true).Context);
        Assert.Equal($"the context is {TriggerCheckContract.MaxContextBytes + 1} bytes; it may be at most {TriggerCheckContract.MaxContextBytes} bytes (8 KB)",
            TriggerCheckContract.Read(Exit0($$"""{"count": 1, "context": "{{over}}"}"""), true).FailureReason);
        Assert.False(TriggerCheckContract.Read(Exit0($$"""{"count": 1, "context": "{{over}}"}""")).Failed);
    }

    [Fact]
    public void Read_TheContextLimitCountsBytes_NotCharacters()
    {
        // 4,097 two-byte characters are 8,194 bytes: over the limit though fewer than 8,192 characters.
        var wide = new string('é', 4097);

        var reading = TriggerCheckContract.Read(Exit0($$"""{"count": 1, "context": "{{wide}}"}"""), true);

        Assert.Equal("the context is 8194 bytes; it may be at most 8192 bytes (8 KB)", reading.FailureReason);
    }

    [Fact]
    public void Read_AStateTheDirectorMeasuredOversize_FailsADefinitionCheck_AndIsIgnoredOnACommand()
    {
        var report = Exit0("""{"count": 0}""", oversize: true);

        Assert.Equal(TriggerCheckContract.StateOversizeReason, TriggerCheckContract.Read(report, true).FailureReason);
        Assert.Equal(0, TriggerCheckContract.Read(report).Count);
    }

    [Fact]
    public void Read_AStateSentAboveTheLimit_FailsTheSameAsOneMeasuredOversize()
    {
        var report = Exit0("""{"count": 0}""", state: new string('s', FactoryCheckStateStore.MaxContentBytes + 1));

        Assert.Equal(TriggerCheckContract.StateOversizeReason, TriggerCheckContract.Read(report, true).FailureReason);
    }

    [Fact]
    public void Read_AStartErrorOfADefinitionCheck_IsTheFirstRule_AsForACommand()
    {
        var report = new TriggerCheckReport { StartError = "no Python on this machine (looked for python.exe on PATH)" };

        Assert.Equal("the check could not start: no Python on this machine (looked for python.exe on PATH)",
            TriggerCheckContract.Read(report, true).FailureReason);
        Assert.Equal(TriggerCheckContract.Read(report).FailureReason, TriggerCheckContract.Read(report, true).FailureReason);
    }

    [Fact]
    public void PromptFor_ADefinitionTrigger_ReplacesCountAndContext_ACommandOnlyCount()
    {
        Assert.Equal("2 new: from ada", TriggerDefinition.PromptFor("{count} new: {context}", 2, "from ada"));
        Assert.Equal("2 new: ", TriggerDefinition.PromptFor("{count} new: {context}", 2, null));
        Assert.Equal("2 new: {context}", TriggerDefinition.PromptFor("{count} new: {context}", 2));
    }
}
