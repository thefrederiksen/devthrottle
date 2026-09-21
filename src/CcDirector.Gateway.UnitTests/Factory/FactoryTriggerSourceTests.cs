using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Factory;

/// <summary>
/// The trigger service's DTO adapted for the Factory Agents fold: the status is the service's verdict, carried over
/// and never re-decided, and every run outcome has words on the pages.
/// </summary>
public sealed class FactoryTriggerSourceTests
{
    private static TriggerDto Dto(string status, string text, string? lastOutcome = TriggerRunOutcome.NothingToDo, bool paused = false) => new()
    {
        Id = "0f5c1a52-5d7b-4a8e-9a38-6f1f2a0a7c11",
        Name = "New business mail",
        Factory = "website-business",
        FactoryAgent = "front-desk",
        IntervalSeconds = 300,
        Paused = paused,
        LastCheckUtc = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc),
        LastOutcome = lastOutcome,
        Status = status,
        StatusText = text,
    };

    [Theory]
    [InlineData("no checks ran")]
    [InlineData("check failed: the command exited 1")]
    [InlineData("start failed: no Director is connected on that machine")]
    public void Facts_ARedTrigger_CarriesTheServicesWordsVerbatim(string text)
    {
        var facts = FactoryTriggerSource.Facts(Dto(TriggerStatusKind.Red, text));

        Assert.True(facts.Red);
        Assert.Equal(text, facts.StatusText);
    }

    [Fact]
    public void Facts_AnOkTrigger_IsNotRed_AndCarriesNoStatusSentence()
    {
        var facts = FactoryTriggerSource.Facts(Dto(TriggerStatusKind.Ok, "OK", paused: true));

        Assert.False(facts.Red);
        Assert.Null(facts.StatusText);
        Assert.True(facts.Paused);
        Assert.Equal("nothing to do", facts.LastResult);
        Assert.Equal(300, facts.IntervalSeconds);
    }

    [Theory]
    [InlineData(TriggerRunOutcome.NothingToDo, "nothing to do")]
    [InlineData(TriggerRunOutcome.Started, "started a session")]
    [InlineData(TriggerRunOutcome.Paused, "work waiting, paused")]
    [InlineData(TriggerRunOutcome.SkippedRunning, "work waiting, its last session still running")]
    [InlineData(TriggerRunOutcome.Failed, "failed")]
    public void LastResult_EveryRunOutcomeHasWords(string outcome, string words)
    {
        Assert.Equal(words, FactoryTriggerSource.LastResult(outcome));
    }

    [Fact]
    public void LastResult_NeverChecked_IsNull_AndAnUnknownOutcomeFailsLoud()
    {
        Assert.Null(FactoryTriggerSource.LastResult(null));
        Assert.Throws<InvalidOperationException>(() => FactoryTriggerSource.LastResult("exploded"));
    }
}
