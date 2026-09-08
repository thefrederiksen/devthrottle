using CcDirector.ControlApi.Drain;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// The block a drained seat declares: the four facts a file's existence cannot say.
/// </summary>
public class DrainReportBlockTests
{
    [Fact]
    public void Parse_ADocumentWithNoBlockIsNotAnError()
    {
        // THE ORDINARY CASE NEEDS NO BLOCK. A document at the seat's own path IS a handover; the block is
        // required only for what existence cannot say. A missing block must never read as a missing
        // handover.
        Assert.Null(DrainReportBlock.Parse("## What I was doing\n\nSome work.\n"));
        Assert.Null(DrainReportBlock.Parse(""));
        Assert.Null(DrainReportBlock.Parse(null));
    }

    [Fact]
    public void Parse_ReadsEveryDeclaredFact()
    {
        var doc = """
            ## Handover

            Some prose.

            <!-- drain-report
            state: drained
            restore: yes
            why: The packaging work is half done and the next action is named above.
            covered: 08c7bba5 | Read its brief, wrote no code; the Manager re-seats it.
            covered: b8d8124d | Same.
            question: Deploy the merged ring change (pull request 2718)? It needs your go.
            -->
            """;

        var block = DrainReportBlock.Parse(doc)!;

        Assert.Equal("drained", block.State);
        Assert.True(block.Restore);
        Assert.StartsWith("The packaging work", block.Why);
        Assert.Equal(2, block.Covered.Count);
        Assert.Equal("08c7bba5", block.Covered[0].SessionId);
        Assert.Contains("re-seats it", block.Covered[0].Note);
        Assert.Equal("Deploy the merged ring change (pull request 2718)? It needs your go.",
            Assert.Single(block.Questions));
        Assert.Empty(block.UnparsedLines);
    }

    [Fact]
    public void Parse_TheLASTBlockWins()
    {
        // A session that amends its handover after it was read adds a block rather than editing in place.
        // The later one is the author's latest word, and the earlier one is history.
        var doc = """
            <!-- drain-report
            state: drained
            restore: yes
            why: There is still work here.
            -->

            Later, after the branch was merged:

            <!-- drain-report
            state: drained
            restore: no
            why: Merged to main; there is nothing left to do.
            -->
            """;

        var block = DrainReportBlock.Parse(doc)!;

        Assert.False(block.Restore);
        Assert.Equal("Merged to main; there is nothing left to do.", block.Why);
    }

    [Fact]
    public void Parse_BlockedCarriesTheSeatsOwnWords()
    {
        var block = DrainReportBlock.Parse("""
            <!-- drain-report
            state: blocked
            blocked-reason: A release is being published and stopping now would leave a half-pushed tag.
            -->
            """)!;

        Assert.Equal("blocked", block.State);
        Assert.Contains("half-pushed tag", block.BlockedReason);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("true", true)]
    [InlineData("restore", true)]
    [InlineData("no", false)]
    [InlineData("false", false)]
    [InlineData("close", false)]
    public void Parse_AcceptsThePlainWordsASeatWouldActuallyWrite(string value, bool expected)
    {
        var block = DrainReportBlock.Parse($"<!-- drain-report\nrestore: {value}\n-->")!;
        Assert.Equal(expected, block.Restore);
    }

    [Fact]
    public void Parse_AnAnswerItDoesNotUnderstandIsNOTGuessed()
    {
        // "restore: probably" is a seat that meant something. Reading it as either answer would put a
        // wrong decision in the one field a stranger acts on, so it stays undecided and is reported.
        var block = DrainReportBlock.Parse("<!-- drain-report\nrestore: probably\n-->")!;

        Assert.Null(block.Restore);
        Assert.Contains(block.UnparsedLines, l => l.Contains("probably"));
    }

    [Fact]
    public void Parse_AMisspelledKeyIsKeptRatherThanDropped()
    {
        // A seat that wrote "resore: yes" meant to be restored. Silently ignoring the line loses that,
        // and nothing anywhere would ever say so.
        var block = DrainReportBlock.Parse("<!-- drain-report\nstate: drained\nresore: yes\n-->")!;

        Assert.Equal("drained", block.State);
        Assert.Contains(block.UnparsedLines, l => l.Contains("resore"));
    }

    [Fact]
    public void Parse_ACoveredLineWithNoNoteStillNamesTheSeat()
    {
        var block = DrainReportBlock.Parse("<!-- drain-report\ncovered: 08c7bba5\n-->")!;

        var claim = Assert.Single(block.Covered);
        Assert.Equal("08c7bba5", claim.SessionId);
        Assert.Equal("", claim.Note);
    }

    [Fact]
    public void Parse_ACoveredLineWithNoSeatIsReportedRatherThanSilentlySkipped()
    {
        var block = DrainReportBlock.Parse("<!-- drain-report\ncovered:\n-->")!;

        Assert.Empty(block.Covered);
        Assert.Contains(block.UnparsedLines, l => l.StartsWith("covered:"));
    }

    [Fact]
    public void Parse_TheBlockSurvivesWindowsLineEndingsAndOddSpacing()
    {
        var block = DrainReportBlock.Parse("<!--   drain-report\r\n  STATE :  drained  \r\n-->")!;
        Assert.Equal("drained", block.State);
    }

    [Fact]
    public void TheDrainMessageNamesEVERYKeyTheParserUnderstands()
    {
        // The instruction that produces a block and the parser that reads it are two halves of one
        // protocol living in different files. A key added to one and not the other is a fact the drain
        // silently loses - and there used to be a comment claiming they could not disagree, above nothing
        // that made it so. This is what makes it so.
        var message = DrainMessages.Drain(
            "TestDirector", "C:/dir/x.md", "C:/dir",
            new[] { ("A Worker", "C:/dir/w.md") }, reason: "a reason");

        foreach (var key in DrainReportBlock.Keys)
            Assert.Contains(key, message);
    }

    [Fact]
    public void EveryKeyTheDrainMessageNamesIsONEThisParserAccepts()
    {
        // The other direction: a block written exactly as the message describes must parse with nothing
        // left unrecognised. Both directions, because either one alone passes while the pair is broken.
        var block = string.Join("\n", new[]
        {
            "<!-- drain-report",
            "state: blocked",
            "restore: no",
            "why: a reason",
            "covered: 08c7bba5 | a note",
            "question: a question?",
            "blocked-reason: a blocker",
            "-->",
        });

        var parsed = DrainReportBlock.Parse(block)!;

        Assert.Empty(parsed.UnparsedLines);
        Assert.Equal("blocked", parsed.State);
        Assert.False(parsed.Restore);
        Assert.Equal("a reason", parsed.Why);
        Assert.Single(parsed.Covered);
        Assert.Single(parsed.Questions);
        Assert.Equal("a blocker", parsed.BlockedReason);
    }

    [Fact]
    public void Parse_AQuestionKeepsThePunctuationAndTheColonsInsideIt()
    {
        var block = DrainReportBlock.Parse(
            "<!-- drain-report\nquestion: Which do you want: the desk figure, or the total?\n-->")!;

        Assert.Equal("Which do you want: the desk figure, or the total?", Assert.Single(block.Questions));
    }
}
