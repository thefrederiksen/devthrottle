using CcDirector.Gateway.Speech;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// THE NARRATIONS THE OWNER ACTUALLY HEARD, on 2026-09-16, across his twenty sessions. He called the
/// result "ridiculously bad". Each of the first three tests is one real utterance, quoted from the live
/// Gateway, against a judge prompt that already forbade exactly what the model did.
///
/// The point of pinning the REAL strings rather than invented ones: a rule the model breaks is a rule the
/// code has to keep, and the only proof that it keeps it is the sentence that got through.
/// </summary>
public class SpokenForEarTests
{
    // ---------- the session's name comes from the record, not the model ----------

    [Fact]
    public void TheWrongNameTheModelInvented_IsReplacedByTheRealOne()
    {
        // Session "Wingman Inspector - Cockpit tab" was narrated "Wingman Inspector mobile." Not mangled
        // punctuation - a different name. The listener cannot see a screen and identifies the session by it.
        var said = SpokenForEar.Assemble(
            "Wingman Inspector - Cockpit tab",
            "Wingman Inspector mobile. Merge complete with new table and fixes.");

        Assert.StartsWith("Wingman Inspector Cockpit tab.", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelThatWroteTheTitleCorrectly_DoesNotSayItTwice()
    {
        var said = SpokenForEar.Assemble(
            "mindzieWeb - bpm video",
            "mindzieWeb - bpm video. Send the video to Ilayda for her to check.");

        Assert.Equal("mindzieWeb bpm video. Send the video to Ilayda for her to check.", said);
    }

    [Fact]
    public void PunctuationInTheNameIsSpoken_AsWordsNotAsSymbols()
    {
        Assert.Equal("devthrottle internal parallel", SpokenForEar.SpeakableTitle("devthrottle_internal - parallel"));
        Assert.Equal("DevThrottleInternal ubuntu", SpokenForEar.SpeakableTitle("DevThrottleInternal::ubuntu"));
    }

    [Fact]
    public void WithNoTitleOnTheRecord_TheNarrationIsUnchanged()
    {
        Assert.Equal("The build finished.", SpokenForEar.Assemble(null, "The build finished."));
        Assert.Equal("The build finished.", SpokenForEar.Assemble("   ", "The build finished."));
    }

    // ---------- identifiers a listener cannot use ----------

    [Fact]
    public void TheCommitHashHeHeardReadOut_IsGoneAndTheSentenceStillParses()
    {
        // Heard verbatim: "...confirm cutting the release at 2bfaa2a24. Say the word and it will be cut."
        var said = SpokenForEar.ScrubIdentifiers(
            "The agent is waiting for you to confirm cutting the release at 2bfaa2a24. Say the word and it will be cut.");

        Assert.Equal(
            "The agent is waiting for you to confirm cutting the release. Say the word and it will be cut.",
            said);
    }

    [Fact]
    public void TheIssueNumberHeHeard_BecomesTheWorkWithoutTheNumber()
    {
        // Heard verbatim: "Agent recommends doing issue 2905 first."
        Assert.Equal(
            "Agent recommends doing that issue first.",
            SpokenForEar.ScrubIdentifiers("Agent recommends doing issue 2905 first."));
    }

    [Theory]
    [InlineData("Merged at commit d0630a5f2b1c.", "Merged.")]
    [InlineData("The run failed in 35047040578.", "The run failed.")]
    [InlineData("See pull request 2909 for the fix.", "See that pull request for the fix.")]
    [InlineData("See PR 2909 for the fix.", "See that pull request for the fix.")]
    [InlineData("Closed #2905 and moved on.", "Closed and moved on.")]
    [InlineData("The verdict 38203005460445138611e8ce0c5e4045 was stored.", "The verdict was stored.")]
    public void EveryShapeOfIdentifierAndReferenceNumber(string heard, string spoken)
        => Assert.Equal(spoken, SpokenForEar.ScrubIdentifiers(heard));

    // ---------- and NOT one word more than that ----------

    [Fact]
    public void OrdinaryEnglishSpelledFromTheLettersAToF_SurvivesTheScrub()
    {
        // "defaced" and "effaced" are seven hex characters each. This is why the pattern demands a digit:
        // a rule that eats real words to catch hashes is worse than the hashes.
        var sentence = "The page was defaced and the note effaced, and the facade cabbaged.";
        Assert.Equal(sentence, SpokenForEar.ScrubIdentifiers(sentence));
    }

    [Fact]
    public void NumBERSThatAreTheANSWER_AreKept()
    {
        // The prompt keeps a number when it IS the answer, and the scrub must not undo that. Only a
        // number attached to a REFERENCE noun - issue, pull request, run - is one the listener cannot use.
        var sentence = "All 340 tests passed and 3 findings are fixed.";
        Assert.Equal(sentence, SpokenForEar.ScrubIdentifiers(sentence));
    }

    [Fact]
    public void AVersionOrATimeIsNotAnIdentifier()
    {
        var sentence = "Version 2.1 shipped and it took 46 seconds.";
        Assert.Equal(sentence, SpokenForEar.ScrubIdentifiers(sentence));
    }

    [Fact]
    public void ScrubbingRunsBeforeTheTitleIsAdded_SoADigitInTheNameIsNeverEatenAsAReference()
    {
        var said = SpokenForEar.Assemble("run 2 - nightly", "The build finished.");
        Assert.Equal("run 2 nightly. The build finished.", said);
    }

    [Fact]
    public void AnEmptyNarrationStillNamesTheSession_RatherThanSayingNothing()
        => Assert.Equal("Dev Manager.", SpokenForEar.Assemble("Dev Manager", ""));
}
