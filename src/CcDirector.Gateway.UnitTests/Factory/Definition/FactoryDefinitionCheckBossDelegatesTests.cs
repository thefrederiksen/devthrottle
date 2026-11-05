using CcDirector.Gateway.Factory.Definition;
using Xunit;
using static CcDirector.Gateway.Tests.Factory.Definition.DefinitionFixture;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A BOSS ONLY DELEGATES (the owner, 4 November 2026, Factory Inbox mission): "The boss is just a delegator ... It can
/// add a to-do item to do later. It can spin up a session, do it right away. But it can't be doing it itself." The check
/// flags a Boss seat's brief that tells the Boss to do the work itself, as a notice on the brief's line - and leaves
/// every other seat's brief, and a Boss's own reviews and strategy, alone.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionCheckBossDelegatesTests
{
    private const string BossBriefLine = "  Run the factory.";

    private static List<string> DelegateNotices(IReadOnlyDictionary<string, string> content)
        => Check(content).Findings.Where(f => f.Message.StartsWith("This brief tells the Boss to do the work itself", StringComparison.Ordinal))
            .Select(f => $"{f.File}:{f.Line}:{f.Severity}").ToList();

    [Fact]
    public void Check_ABossBriefThatTellsTheBossToDoTheWorkItself_GetsTheNoticeOnTheBriefLine_AndNoError()
    {
        var content = Edit("seats/boss.yaml", BossBriefLine, "  Run the factory. Write the morning article yourself.");

        var result = Check(content);

        Assert.Equal(new[] { "seats/boss.yaml:4:notice" }, DelegateNotices(content));
        AssertNotice(result, "seats/boss.yaml", 4, "\"Write the morning article yourself\"", "A Boss only delegates",
            "adds a to-do to be done later, or starts a session to do it now");
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Check_TheValidDefinition_HasNoNotice()
    {
        Assert.Empty(DelegateNotices(Valid()));
    }

    [Fact]
    public void Check_TheSameWordsInAnotherSeatsBrief_GetNoNotice()
    {
        // The writer is the seat that does the work: being told to do it itself is its job.
        var content = Edit("seats/writer.yaml", "brief: Write the posts.", "brief: Write the posts yourself, and do not delegate.");

        Assert.Empty(DelegateNotices(content));
    }

    [Fact]
    public void Check_ASeatWithTheRoleBoss_ThatFactoryYamlDoesNotName_IsCheckedToo()
    {
        var content = Edit("seats/writer.yaml", "role: Writer\nbrief: Write the posts.", "role: Boss\nbrief: Do the work yourself.");

        Assert.Contains("seats/writer.yaml:4:notice", DelegateNotices(content));
    }

    [Fact]
    public void Check_APhraseWrappedAcrossLines_IsStillFound()
    {
        var content = Edit("seats/boss.yaml", BossBriefLine, "  Run the factory, and do not\n  delegate the morning round.");

        Assert.Equal(new[] { "seats/boss.yaml:4:notice" }, DelegateNotices(content));
    }

    [Theory]
    [InlineData("Do the work yourself.", "Do the work yourself")]
    [InlineData("Handle it on your own.", "Handle it on your own")]
    [InlineData("Complete the task in this session.", "Complete the task in this session")]
    [InlineData("Fix the broken link yourself.", "Fix the broken link yourself")]
    [InlineData("Send the email yourself.", "Send the email yourself")]
    [InlineData("You do all of the work here.", "You do all of the work")]
    [InlineData("Never delegate the outreach.", "Never delegate")]
    [InlineData("Answer every mail without delegating.", "without delegating")]
    [InlineData("Don't spawn sessions for this.", "Don't spawn sessions")]
    [InlineData("Do not start any seats.", "Do not start any seats")]
    public void BossDoesTheWorkPhrase_FindsEachWayOfTellingTheBossToDoTheWork(string brief, string phrase)
    {
        Assert.Equal(phrase, FactoryDefinitionCheck.BossDoesTheWorkPhrase(brief));
    }

    [Theory]
    [InlineData("Run the factory.")]
    [InlineData("Review the article yourself before it goes out.")]
    [InlineData("Decide yourself which prospect comes first.")]
    [InlineData("Set the strategy and delegate the rest.")]
    [InlineData("Start a session to write the article.")]
    [InlineData("Judge every request and push back on the owner when it is a mistake.")]
    public void BossDoesTheWorkPhrase_LeavesABossesOwnWorkAndDelegationAlone(string brief)
    {
        Assert.Null(FactoryDefinitionCheck.BossDoesTheWorkPhrase(brief));
    }

    /// <summary>
    /// A PROHIBITION IS NOT AN ORDER (phase 0 review, finding 1): the exact sentences the review found flagged - the live
    /// Marketing Boss brief's limit on secrets, and briefs that restate the rule itself.
    /// </summary>
    [Theory]
    [InlineData("cc-secrets. Never send or request it yourself.")]
    [InlineData("Never do it yourself; delegate.")]
    [InlineData("You do not do the work yourself - you start a session.")]
    [InlineData("You never do the work yourself.")]
    public void BossDoesTheWorkPhrase_ASentenceThatForbidsTheWork_IsNotFlagged(string brief)
    {
        Assert.Null(FactoryDefinitionCheck.BossDoesTheWorkPhrase(brief));
    }

    [Fact]
    public void BossDoesTheWorkPhrase_AnOrderInTheNextClause_AfterAProhibition_IsStillFlagged()
    {
        Assert.Equal("send the report yourself",
            FactoryDefinitionCheck.BossDoesTheWorkPhrase("Never send or request it yourself. Then send the report yourself."));
        Assert.Equal("do it yourself", FactoryDefinitionCheck.BossDoesTheWorkPhrase("Ask nobody; do it yourself."));
    }

    [Fact]
    public void Check_TheLiveMarketingBossLimit_GetsNoNotice()
    {
        var content = Edit("seats/boss.yaml", BossBriefLine,
            "  Run the factory.\n  The key lives in cc-secrets. Never send or request it yourself.");

        Assert.Empty(DelegateNotices(content));
    }

    [Fact]
    public void BossDoesTheWorkPhrase_ReturnsTheFirstPhraseInTheBrief()
    {
        Assert.Equal("Do it yourself", FactoryDefinitionCheck.BossDoesTheWorkPhrase("Do it yourself. Never delegate."));
    }

    [Fact]
    public void BossBriefDoesTheWork_IsTheSentenceAPersonReads()
    {
        Assert.Equal(
            "This brief tells the Boss to do the work itself (\"Do it yourself\"). A Boss only delegates: it adds a to-do to be "
            + "done later, or starts a session to do it now, and never does the work itself. Reword the brief to say what gets "
            + "done and let the Boss hand it on.",
            FactoryDefinitionCheck.BossBriefDoesTheWork("Do it yourself"));
    }
}
