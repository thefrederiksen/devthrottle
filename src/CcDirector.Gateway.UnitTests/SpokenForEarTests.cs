using CcDirector.Gateway.Speech;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// SAYING WHICH SESSION IS TALKING, AND LEAVING THE NARRATION ALONE.
///
/// The defect: on 2026-09-16 a session named "Wingman Inspector - Cockpit tab" was narrated as
/// "Wingman Inspector mobile". The listener cannot see a screen and identifies the session by name.
///
/// Nearly all of this file is about what is NOT touched. Earlier versions also stripped identifiers the
/// model should not have voiced, names it invented, and a title it had duplicated. Four review rounds
/// found defects in every one of those - real content deleted, real names corrupted - and each was
/// removed rather than patched again. The cases below are what those rounds found, kept so the
/// stripping cannot come back by accident.
/// </summary>
public class SpokenForEarTests
{
    // ================= the name comes from the record =================

    [Fact]
    public void TheRealNameIsSpokenFirst_WhateverTheModelWrote()
    {
        // The narration heard on the phone. The invented name stays in the body - it is wrong, and no
        // rule for spotting one survived review without eating real sentences - but the listener now
        // hears the right name first, from the record, which is what they needed.
        Assert.Equal(
            "Wingman Inspector Cockpit tab. Wingman Inspector mobile. Merge complete.",
            SpokenForEar.Assemble("Wingman Inspector - Cockpit tab", "Wingman Inspector mobile. Merge complete."));
    }

    [Theory]
    [InlineData("devthrottle_internal - parallel", "devthrottle internal parallel")]
    [InlineData("DevThrottleInternal::ubuntu", "DevThrottleInternal ubuntu")]
    [InlineData("Alpha ----- Beta", "Alpha Beta")]
    public void TheSeparatorsThatJoinWordsBecomePauses(string written, string said)
        => Assert.Equal(said, SpokenForEar.SpeakableTitle(written));

    [Theory]
    // Round four: a rule that kept "letters, numbers and spaces" mangled real names. Combining marks are
    // neither, so these lost characters; a symbol-only name vanished entirely and took the session's
    // identity with it.
    [InlineData("नमस्ते")]          // Devanagari with vowel signs
    [InlineData("équipe")]                                   // decomposed accent
    [InlineData("C++")]
    [InlineData("+++")]
    [InlineData("עברית")]                 // right-to-left
    public void AUnicodeOrSymbolNameIsSaidAsItIsWritten(string title)
        => Assert.Equal(title, SpokenForEar.SpeakableTitle(title));

    [Fact]
    public void WithNoTitleOnTheRecord_TheNarrationIsUnchanged()
    {
        Assert.Equal("The build finished.", SpokenForEar.Assemble(null, "The build finished."));
        Assert.Equal("The build finished.", SpokenForEar.Assemble("   ", "The build finished."));
    }

    [Fact]
    public void AnEmptyNarrationStillNamesTheSession_RatherThanSayingNothing()
        => Assert.Equal("Dev Manager.", SpokenForEar.Assemble("Dev Manager", ""));

    // ================= THE INVARIANT: the narration's words are the model's =================

    [Theory]
    // Every one of these was damaged by a rule that has since been deleted. They stand as the record of
    // what "the narration is never edited" has to mean, case by case.
    [InlineData("1000000 tests passed.")]                                  // round 1: the number went
    [InlineData("Pi is 3.1415926.")]                                       // round 1
    [InlineData("Deployed on 2026-09-15.")]                                // round 2: the date went
    [InlineData("It ran for 12345678 seconds.")]                           // round 2
    [InlineData("The condition uses AND, not XOR.")]                       // round 2: the subject went
    [InlineData("We will commit 1000000 rows tonight.")]                   // round 3
    [InlineData("Use a prior run, e.g. run 35047040578.")]                 // round 3
    [InlineData("Wait... the deploy is still running.")]                   // round 3: the ellipsis went
    [InlineData("Dev Manager found four defects.")]                        // round 3: the whole result went
    [InlineData("Go now! The deploy needs approval.")]                     // round 3
    [InlineData("Merged at commit d0630a5f2b1c.")]                         // a hash: accepted, see below
    [InlineData("Agent recommends doing issue 2905 first.")]               // a reference number: accepted
    public void TheNarrationIsNeverEdited_OnlyPrefixedWithTheName(string narration)
    {
        // The last two are things the prompt forbids and the model sometimes says anyway. They are in
        // this list deliberately: hearing a hash is the cost this design accepts, because the alternative
        // - everything above them - was deleting the answer.
        Assert.Equal($"session. {narration}", SpokenForEar.Assemble("session", narration));
    }

    [Theory]
    // Round four found the last edit still eating body text. A model that duplicates the title now simply
    // says it twice; nothing is cut looking for it.
    [InlineData("Dev Manager", "DevManager. Release complete.")]
    [InlineData("Dev", "Dev, Manager found four defects.")]
    [InlineData("Dev", "Dev—short for development—is complete.")]
    [InlineData("Dev", "Development is complete.")]
    [InlineData("Go", "Go-live failed.")]
    [InlineData("Dev Manager", "Dev Manager finished the report.")]
    [InlineData("Dev Manager", "Dev Manager. The report is done.")]
    public void NothingIsEverCutFromTheFrontOfTheBody(string title, string body)
        => Assert.Equal($"{SpokenForEar.SpeakableTitle(title)}. {body}", SpokenForEar.Assemble(title, body));

    [Fact]
    public void TheOnlyChangeToTheBodyIsWhitespaceAtItsEdges()
    {
        // Stated as the property the code actually holds. An earlier comment claimed byte-identity, which
        // the trim made false - a promise a little larger than the code, which is how a reader comes to
        // trust the wrong thing.
        foreach (var body in new[] { "  Result.  \t", "Result.", "\nResult.\n" })
            Assert.Equal("session. Result.", SpokenForEar.Assemble("session", body));
    }

    [Fact]
    public void ANarrationOfPunctuationAloneIsStillNotEdited()
        => Assert.Equal("session. ...", SpokenForEar.Assemble("session", "..."));
}
