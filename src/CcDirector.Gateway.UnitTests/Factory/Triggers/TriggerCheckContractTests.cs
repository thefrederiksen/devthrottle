using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Triggers;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// The check contract (the Website Business Factory mission, product track): exit 0 and JSON with an integer count
/// is a count; everything else is a FAILED check with the reason in words. Pure, so each broken shape is named here.
/// </summary>
public sealed class TriggerCheckContractTests
{
    private static TriggerCheckReport Exit0(string output) => new() { ExitCode = 0, Output = output };

    [Theory]
    [InlineData("{\"count\": 0}", 0)]
    [InlineData("{\"count\":2}", 2)]
    [InlineData("  {\"count\": 7, \"threads\": [\"a\"]}\r\n", 7)]
    public void Read_ExitZeroWithAnIntegerCount_IsThatCount(string output, int expected)
    {
        var reading = TriggerCheckContract.Read(Exit0(output));

        Assert.False(reading.Failed);
        Assert.Equal(expected, reading.Count);
    }

    [Fact]
    public void Read_ExitOne_FailsWithTheExitCodeAndWhatItSaid()
    {
        var reading = TriggerCheckContract.Read(new TriggerCheckReport { ExitCode = 1, ErrorOutput = "gmail: not signed in\r\n" });

        Assert.True(reading.Failed);
        Assert.Equal("exit code 1: gmail: not signed in", reading.FailureReason);
        Assert.Null(reading.Count);
    }

    [Fact]
    public void Read_ExitOneSayingNothing_FailsWithTheExitCode()
    {
        Assert.Equal("exit code 1", TriggerCheckContract.Read(new TriggerCheckReport { ExitCode = 1 }).FailureReason);
    }

    [Fact]
    public void Read_NotJson_Fails()
    {
        var reading = TriggerCheckContract.Read(Exit0("3 new mails"));
        Assert.Equal("the output is not JSON: 3 new mails", reading.FailureReason);
    }

    [Fact]
    public void Read_JsonWithNoCount_Fails()
    {
        var reading = TriggerCheckContract.Read(Exit0("{\"waiting\": 3}"));
        Assert.Equal("the output has no count: {\"waiting\": 3}", reading.FailureReason);
    }

    [Theory]
    [InlineData("{\"count\": \"2\"}", "the count is not an integer: \"2\"")]
    [InlineData("{\"count\": 2.5}", "the count is not an integer: 2.5")]
    [InlineData("{\"count\": null}", "the count is not an integer: null")]
    [InlineData("{\"count\": -1}", "the count is negative: -1")]
    [InlineData("[1, 2]", "the output is JSON but not an object with a count: [1, 2]")]
    [InlineData("{\"Count\": 2}", "the output has no count: {\"Count\": 2}")]
    [InlineData("", "the check printed nothing; it must print JSON with an integer count")]
    public void Read_ACountThatIsNotAWholeNumberOfThings_Fails(string output, string reason)
    {
        Assert.Equal(reason, TriggerCheckContract.Read(Exit0(output)).FailureReason);
    }

    [Fact]
    public void Read_Timeout_Fails_EvenWithOutputThatWouldHaveCounted()
    {
        var reading = TriggerCheckContract.Read(new TriggerCheckReport { TimedOut = true, Output = "{\"count\": 3}" });
        Assert.Equal("the check timed out", reading.FailureReason);
    }

    [Fact]
    public void Read_CouldNotStart_FailsWithWhy()
    {
        var reading = TriggerCheckContract.Read(new TriggerCheckReport { StartError = "the folder 'X:\\gone' does not exist on this machine" });
        Assert.Equal("the check could not start: the folder 'X:\\gone' does not exist on this machine", reading.FailureReason);
    }

    [Fact]
    public void Read_NoExitCode_Fails()
    {
        Assert.Equal("the check reported no exit code",
            TriggerCheckContract.Read(new TriggerCheckReport { Output = "{\"count\": 1}" }).FailureReason);
    }

    [Fact]
    public void Read_QuotesAtMostTwoHundredCharacters_OnOneLine()
    {
        var reading = TriggerCheckContract.Read(Exit0(new string('x', 500) + "\n" + "tail"));
        Assert.Equal("the output is not JSON: " + new string('x', 200) + "...", reading.FailureReason);
    }
}
