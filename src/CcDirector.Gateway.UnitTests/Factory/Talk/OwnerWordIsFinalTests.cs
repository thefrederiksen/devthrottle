using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Talk;

/// <summary>
/// THE OWNER'S WORD IS FINAL (the owner's ruling of 24 October 2026: "If I say so, directly, it can ask me for approval.
/// But if I approve, it has to do it."). One standing paragraph the Gateway gives every seat, so no factory's author can
/// leave it out. Its words are pinned here once; every other prompt test expects them by name.
/// </summary>
[Trait("Category", "FactoryTalk")]
public sealed class OwnerWordIsFinalTests
{
    [Fact]
    public void OwnerWordIsFinal_IsTheSentenceEverySeatReads()
    {
        Assert.Equal(
            "THE OWNER'S WORD IS FINAL. When the owner tells you directly to do something, do it. If a permission rule, a grant "
            + "or your brief stands in the way, do not refuse and do not tell the owner to do it themselves: put the approval "
            + "prompt in front of the owner - run the step, so the permission question appears for the owner to answer - and once "
            + "the owner says yes, carry it out. Never write a deny rule into a settings file: restrict with an ask rule, so the "
            + "owner can always approve.",
            FactorySeatOpening.OwnerWordIsFinal);
    }

    [Fact]
    public void WithOwnerWord_AddsTheParagraphLast_AfterOneBlankLine_KeepingTheTextAsItWas()
    {
        // Only line ends at the very end are dropped: a prompt that ends in a space keeps it.
        Assert.Equal("Go.\n\n" + FactorySeatOpening.OwnerWordIsFinal, FactorySeatOpening.WithOwnerWord("Go.\n\n"));
        Assert.Equal("New mail: 1. \n\n" + FactorySeatOpening.OwnerWordIsFinal, FactorySeatOpening.WithOwnerWord("New mail: 1. "));
        Assert.Equal("A.\r\n\r\n" + FactorySeatOpening.OwnerWordIsFinal, FactorySeatOpening.WithOwnerWord("A.\r\n", "\r\n"));
    }

    [Fact]
    public void WithOwnerWord_OnATextThatAlreadyHoldsIt_ChangesNothing()
    {
        // A talk's text is passed through the seat start too: the paragraph is said once, never twice.
        var once = FactorySeatOpening.WithOwnerWord("Talk to the owner.");

        Assert.Equal(once, FactorySeatOpening.WithOwnerWord(once));
    }

    [Fact]
    public void ATalk_AScheduledRunOfASeat_AndAProblemTalk_EachEndWithTheParagraph_ExactlyOnce()
    {
        var f = FactorySeatOpeningTests.WarmForward();
        var brief = new FactoryGivenBrief(3, FactorySeatOpeningTests.BriefOne, FactoryBriefVersionKinds.Definition);

        var talk = FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", FactorySeatOpeningTests.MorningRun, brief).ReplaceLineEndings("\n");
        var run = ScheduledRunOpening.PromptFor(FactorySeatOpeningTests.MorningRun, "morning", "cj_a721e6",
            new ScheduledRunSeat(f, f.Seats[0], brief));

        Assert.EndsWith("\n\n" + FactorySeatOpening.OwnerWordIsFinal + "\n", talk);
        Assert.EndsWith("\n\n" + FactorySeatOpening.OwnerWordIsFinal, run);
        Assert.Equal(1, Occurrences(talk));
        Assert.Equal(1, Occurrences(run));
    }

    [Fact]
    public void AScheduleInNoFactory_IsNotGivenTheParagraph()
    {
        // The paragraph is for a seat. A schedule in no factory is given its own prompt and the closing instruction, as
        // it always was.
        var run = ScheduledRunOpening.PromptFor(FactorySeatOpeningTests.MorningRun, "morning", "cj_a721e6", seat: null);

        Assert.Equal(0, Occurrences(run));
    }

    private static int Occurrences(string text)
        => (text.Length - text.Replace(FactorySeatOpening.OwnerWordIsFinal, "", StringComparison.Ordinal).Length)
           / FactorySeatOpening.OwnerWordIsFinal.Length;
}
