using CcDirector.Gateway.Speech;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// NAMING THE SESSION, AND LEAVING EVERYTHING ELSE ALONE.
///
/// The defect this closes was heard on 2026-09-16: a session named "Wingman Inspector - Cockpit tab"
/// narrated as "Wingman Inspector mobile". The listener cannot see a screen and identifies the session
/// by that name, so a wrong one is worse than none.
///
/// MOST OF THIS FILE IS ABOUT WHAT IS NOT TOUCHED, and that is the point. An earlier version also
/// stripped identifiers the model should not have voiced and names it invented. Three review rounds
/// found fourteen defects in that stripping - every one of them real content deleted for sitting where
/// an identifier might sit - and it was removed rather than patched a fourth time. The invariant test
/// at the bottom is what keeps it removed.
/// </summary>
public class SpokenForEarTests
{
    // ================= the name comes from the record =================

    [Fact]
    public void TheRealNameIsSpokenFirst_WhateverTheModelWrote()
    {
        // The exact narration heard on the phone. The invented name is still in the body - it is wrong,
        // and no rule for spotting one survived review - but the listener now hears the right name first,
        // from the record, which is what they needed to know.
        var said = SpokenForEar.Assemble(
            "Wingman Inspector - Cockpit tab",
            "Wingman Inspector mobile. Merge complete with new table and fixes.");

        Assert.StartsWith("Wingman Inspector Cockpit tab.", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelThatWroteTheTitleCorrectly_DoesNotSayItTwice()
    {
        Assert.Equal(
            "mindzieWeb bpm video. Send the video to Ilayda for her to check.",
            SpokenForEar.Assemble("mindzieWeb - bpm video", "mindzieWeb - bpm video. Send the video to Ilayda for her to check."));
    }

    [Theory]
    [InlineData("devthrottle_internal - parallel", "devthrottle internal parallel")]
    [InlineData("DevThrottleInternal::ubuntu", "DevThrottleInternal ubuntu")]
    [InlineData("build (nightly) [v2]", "build nightly v2")]
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

    // ================= the de-duplication cuts nothing in half =================

    [Theory]
    // Each of these was mangled by a version that matched the title as a PREFIX with no boundary.
    [InlineData("Dev", "Development is complete.")]
    [InlineData("Go", "Go-live failed.")]
    [InlineData("colors", "colorspace conversion ran.")]
    public void ATitleThatIsAPrefixOfTheFirstWord_IsNotTornOutOfIt(string title, string body)
        => Assert.Equal($"{title}. {body}", SpokenForEar.Assemble(title, body));

    [Theory]
    // A real sentence that opens with the session's name keeps every word. Only a title PUNCTUATED as
    // its own clause is a duplicate; a sentence runs straight on.
    [InlineData("Dev Manager", "Dev Manager finished the report.")]
    [InlineData("Go", "Go now! The deploy needs approval.")]
    [InlineData("Dev Manager", "Dev Manager failed. Retry queued.")]
    [InlineData("Construire", "Construire maintenant! Le deploiement attend.")]
    [InlineData("colors", "colors are wrong on the chart.")]
    public void ARealOpeningSentenceIsNeverMistakenForATitle(string title, string body)
        => Assert.Equal($"{SpokenForEar.SpeakableTitle(title)}. {body}", SpokenForEar.Assemble(title, body));

    [Fact]
    public void ALongSessionTitleDoesNotLicenseEatingAShortResult()
    {
        // Found in review: a rule that compared word COUNTS deleted this entire result, because five real
        // words are fewer than the six in the title.
        var said = SpokenForEar.Assemble("Dev Manager - Inspector - 2910 round three", "Dev Manager found four defects.");
        Assert.Contains("found four defects.", said, StringComparison.Ordinal);
    }

    // ================= THE INVARIANT: the words are the model's =================

    [Theory]
    // Every one of these was damaged by the identifier stripping that used to live here. They are kept as
    // the standing proof that it is gone: whatever the narration says, it reaches the listener intact.
    [InlineData("1000000 tests passed.")]
    [InlineData("Pi is 3.1415926.")]
    [InlineData("Release 2026-09-15 shipped.")]
    [InlineData("Deployed on 2026-09-15.")]
    [InlineData("It ran for 12345678 seconds.")]
    [InlineData("The population grew from 1000000 to 2000000.")]
    [InlineData("The value is 12345678%.")]
    [InlineData("The condition uses AND, not XOR.")]
    [InlineData("The operator is OR.")]
    [InlineData("We will commit 1000000 rows tonight.")]
    [InlineData("The database will hash 1234567 records before upload.")]
    [InlineData("All 340 tests passed and 3 findings are fixed.")]
    [InlineData("Wait... the deploy is still running.")]
    [InlineData("Use a prior run, e.g. run 35047040578.")]
    [InlineData("Merged at commit d0630a5f2b1c.")]
    [InlineData("Agent recommends doing issue 2905 first.")]
    public void TheNARRATIONItselfIsNeverEdited_OnlyPrefixedWithTheName(string narration)
    {
        // The last two are things the prompt forbids and the model sometimes says anyway. They are in
        // this list on purpose: hearing a hash is a cost this design accepts, because the alternative -
        // the fourteen findings above it - was deleting the answer.
        Assert.Equal($"session. {narration}", SpokenForEar.Assemble("session", narration));
    }

    [Fact]
    public void NothingIsRemovedFromTheBodyExceptALeadingCopyOfTheTitle()
    {
        // Stated as a property rather than a list, so a future edit that starts removing something else
        // fails here even if nobody thought to add a case for it.
        const string body = "Merged deadbeef1 and 2bfaa2a24, and it took 3.5 seconds.";
        foreach (var title in new[] { "session", "Dev Manager", "a", "build nightly", "Merged" })
        {
            var said = SpokenForEar.Assemble(title, body);
            var withoutName = said[(SpokenForEar.SpeakableTitle(title).Length + 2)..];
            Assert.Equal(body, withoutName);
        }
    }
}
