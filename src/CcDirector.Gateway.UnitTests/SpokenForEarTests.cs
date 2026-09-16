using CcDirector.Gateway.Speech;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// THE NARRATIONS THE OWNER ACTUALLY HEARD, on 2026-09-16, across his twenty sessions. He called the
/// result "ridiculously bad". The first tests in each section quote one real utterance from the live
/// Gateway, against a judge prompt that already forbade exactly what the model did.
///
/// EVERY OTHER TEST HERE IS A CASE THE FIRST VERSION OF THIS CODE GOT WRONG, found by the Codex
/// inspector on pull request 2910 before it merged. That review is the reason this file is mostly
/// about what the scrub must NOT do: the first implementation treated any seven characters of digits,
/// a-f and hyphens as a hash, which is also the shape of a number, a date, a version and a decimal, so
/// it deleted the substance it exists to protect. Those cases are kept as the permanent record of what
/// "narrow" has to mean here.
/// </summary>
public class SpokenForEarTests
{
    // ================= the session's name comes from the record, not the model =================

    [Fact]
    public void TheWrongNameTheModelInvented_IsGone_NotMerelyPrecededByTheRealOne()
    {
        // Heard live: session "Wingman Inspector - Cockpit tab" narrated as "Wingman Inspector mobile."
        // The first fix prepended the real title and LEFT the invented one, so the listener heard both
        // names, one of them false - the defect surviving immediately after its own correction.
        var said = SpokenForEar.Assemble(
            "Wingman Inspector - Cockpit tab",
            "Wingman Inspector mobile. Merge complete with new table and fixes.");

        Assert.Equal("Wingman Inspector Cockpit tab. Merge complete with new table and fixes.", said);
        Assert.DoesNotContain("mobile", said, StringComparison.OrdinalIgnoreCase);
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
    public void ARealOpeningSentenceThatBeginsWithTheSessionName_IsKeptWhole()
    {
        // The guard on the heuristic above. A sentence long enough to be a sentence is not a title
        // attempt, however it starts, and losing it would cost the listener the actual news.
        var said = SpokenForEar.Assemble("Dev Manager", "Dev Manager finished the report and filed it.");

        Assert.Equal("Dev Manager. Dev Manager finished the report and filed it.", said);
    }

    [Theory]
    // Every one of these came back mangled from the first implementation, which matched title words as
    // PREFIXES with no boundary at the end.
    [InlineData("Dev", "Development is complete.", "Dev. Development is complete.")]
    [InlineData("Go", "Go-live failed.", "Go. Go-live failed.")]
    [InlineData("colors", "colorspace conversion ran.", "colors. colorspace conversion ran.")]
    public void ATitleThatIsAPrefixOfTheFirstWord_IsNotTornOutOfIt(string title, string body, string said)
        => Assert.Equal(said, SpokenForEar.Assemble(title, body));

    [Fact]
    public void ATitleWithRegexMetacharacters_IsMatchedLiterally()
    {
        var said = SpokenForEar.Assemble("build (nightly) [v2]", "build nightly v2. The build passed.");
        Assert.Equal("build nightly v2. The build passed.", said);
    }

    [Theory]
    [InlineData("devthrottle_internal - parallel", "devthrottle internal parallel")]
    [InlineData("DevThrottleInternal::ubuntu", "DevThrottleInternal ubuntu")]
    public void PunctuationInTheNameIsSpokenAsWords_NotAsSymbols(string written, string said)
        => Assert.Equal(said, SpokenForEar.SpeakableTitle(written));

    [Fact]
    public void WithNoTitleOnTheRecord_TheNarrationIsUnchanged()
    {
        Assert.Equal("The build finished.", SpokenForEar.Assemble(null, "The build finished."));
        Assert.Equal("The build finished.", SpokenForEar.Assemble("   ", "The build finished."));
    }

    [Fact]
    public void AnEmptyNarrationStillNamesTheSession_RatherThanSayingNothing()
        => Assert.Equal("Dev Manager.", SpokenForEar.Assemble("Dev Manager", ""));

    // ================= identifiers a listener cannot use =================

    [Fact]
    public void TheCommitHashHeHeardReadOut_IsGoneAndTheSentenceStillParses()
    {
        // Heard verbatim: "...confirm cutting the release at 2bfaa2a24. Say the word and it will be cut."
        Assert.Equal(
            "The agent is waiting for you to confirm cutting the release. Say the word and it will be cut.",
            SpokenForEar.ScrubIdentifiers(
                "The agent is waiting for you to confirm cutting the release at 2bfaa2a24. Say the word and it will be cut."));
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
    [InlineData("See pull request 2909 for the fix.", "See that pull request for the fix.")]
    [InlineData("See PR 2909 for the fix.", "See that pull request for the fix.")]
    [InlineData("The run failed in 35047040578.", "The run failed.")]
    [InlineData("Closed #2905 and moved on.", "Closed and moved on.")]
    [InlineData("The verdict 38203005460445138611e8ce0c5e4045 was stored.", "The verdict was stored.")]
    public void EveryShapeOfIdentifierAndReferenceNumber(string heard, string spoken)
        => Assert.Equal(spoken, SpokenForEar.ScrubIdentifiers(heard));

    [Theory]
    // The three shapes the inspector found still ungrammatical: a carrier at the START of a sentence
    // (the pattern was case-sensitive and demanded a leading space), a QUOTED identifier (the quotes
    // stayed behind, empty), and TWO bare hashes joined by a conjunction (which was left holding nothing).
    [InlineData("At commit d0630a5f2b1c, we merged.", "we merged.")]
    [InlineData("Merged at commit \"d0630a5f2b1c\".", "Merged.")]
    [InlineData("Merged d0630a5f2b1c and 2bfaa2a24.", "Merged.")]
    public void RemovingAnIdentifierNeverLeavesADanglingWord(string heard, string spoken)
        => Assert.Equal(spoken, SpokenForEar.ScrubIdentifiers(heard));

    // ================= and NOT one word more than that =================

    [Theory]
    // EVERY ONE OF THESE WAS BROKEN by the first implementation, which asked only for "contains a digit".
    // A legitimate number, a date, a version and a decimal are all digits and separators, so all four
    // matched. The rule is now "contains a hex LETTER and a digit", which no ordinary number does.
    [InlineData("1000000 tests passed.")]
    [InlineData("Pi is 3.1415926.")]
    [InlineData("Version 2.1234567 shipped.")]
    [InlineData("Release 2026-09-15 shipped.")]
    [InlineData("The value is 12345678%.")]
    [InlineData("All 340 tests passed and 3 findings are fixed.")]
    [InlineData("Version 2.1 shipped and it took 46 seconds.")]
    [InlineData("The address is 192.168.1.100 on port 7330.")]
    public void ANumberThatIsTheANSWER_SurvivesUntouched(string sentence)
        => Assert.Equal(sentence, SpokenForEar.ScrubIdentifiers(sentence));

    [Fact]
    public void OrdinaryEnglishSpelledFromTheLettersAToF_SurvivesTheScrub()
    {
        // "defaced" and "effaced" are seven hex letters each. This is why the pattern also demands a
        // digit: a rule that eats real words to catch hashes is worse than the hashes.
        var sentence = "The page was defaced and the note effaced, and the facade cabbaged.";
        Assert.Equal(sentence, SpokenForEar.ScrubIdentifiers(sentence));
    }

    [Fact]
    public void ScrubbingRunsBeforeTheTitleIsAdded_SoADigitInTheNameIsNeverEatenAsAReference()
    {
        Assert.Equal("run 2 nightly. The build finished.", SpokenForEar.Assemble("run 2 - nightly", "The build finished."));
    }
}
