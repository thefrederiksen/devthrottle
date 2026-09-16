using CcDirector.Gateway.Speech;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// THE ADVERSARIAL PASS over the two rules that are heuristics rather than facts: the removal of a
/// model-invented opening name, and the sweep of a conjunction left holding nothing.
///
/// Both were written to fix a real defect and both can be wrong in the other direction - eating a real
/// sentence, or a real "and". Everything here is a case where they MUST NOT fire. They are separated
/// from the main suite deliberately: that one says what the rules do, this one says what they must
/// never do, and the second list is the one that grows when someone is tempted to widen a rule.
/// </summary>
public class SpokenForEarAdversarialTests
{
    // ================= the invented-name heuristic must not eat a real sentence =================

    [Theory]
    // A short opening sentence that genuinely starts with the session's name. The heuristic is bounded at
    // one word beyond the title's length, so each of these must be over the bound and survive whole.
    [InlineData("colors", "colors are wrong on the chart.")]
    [InlineData("Dev Manager", "Dev Manager finished the report.")]
    [InlineData("build", "build failed on the second step.")]
    [InlineData("deploy", "deploy succeeded after a retry.")]
    [InlineData("api", "api returned four hundred and three.")]
    public void ARealOpeningSentenceIsNeverMistakenForATitle(string title, string body)
    {
        var said = SpokenForEar.Assemble(title, body);
        Assert.Equal($"{SpokenForEar.SpeakableTitle(title)}. {body}", said);
    }

    [Fact]
    public void ATitleWhoseWordsRepeat_DoesNotOverMatch()
    {
        var said = SpokenForEar.Assemble("build build", "build build build ran three times.");
        Assert.Contains("ran three times.", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AMultiClauseOpening_IsNotSwallowedByItsFirstClause()
    {
        // The heuristic cuts at the first sentence end. A comma is not one, so a long opening clause that
        // begins with the name stays whole rather than being read as a title.
        var said = SpokenForEar.Assemble(
            "colors", "colors, fonts and spacing were all changed in this pass.");
        Assert.Contains("fonts and spacing were all changed", said, StringComparison.Ordinal);
    }

    [Fact]
    public void NonEnglishOpening_IsLeftAlone()
    {
        var said = SpokenForEar.Assemble("rapport", "Le rapport est termine et envoye.");
        Assert.Equal("rapport. Le rapport est termine et envoye.", said);
    }

    [Theory]
    // The second review's cases. Each is a SHORT REAL SENTENCE that the earlier "at most one word over the
    // title" bound deleted, because a two-word imperative or status line clears a one- or two-word title.
    [InlineData("Go", "Go now! The deploy needs approval.", "Go now!")]
    [InlineData("Dev Manager", "Dev Manager failed. Retry queued.", "Dev Manager failed.")]
    [InlineData("Construire", "Construire maintenant! Le deploiement attend.", "Construire maintenant!")]
    public void AShortRealSentenceIsNotATitleAttempt(string title, string body, string mustSurvive)
        => Assert.Contains(mustSurvive, SpokenForEar.Assemble(title, body), StringComparison.Ordinal);

    [Fact]
    public void ATitleWrittenAsALabelWithAColon_IsStillRemoved()
    {
        // The colon shape the second review found surviving: the listener heard the real name and then the
        // false one, which is the defect this helper exists to close.
        var said = SpokenForEar.Assemble(
            "Wingman Inspector - Cockpit tab", "Wingman Inspector mobile: Merge complete with fixes.");

        Assert.Equal("Wingman Inspector Cockpit tab. Merge complete with fixes.", said);
        Assert.DoesNotContain("mobile", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheInventedNameIsStillRemovedWhenItShouldBe()
    {
        // The positive case, kept beside the negatives so a future narrowing of the bound cannot quietly
        // turn the rule off and leave this file still green.
        Assert.Equal(
            "Wingman Inspector Cockpit tab. Merge complete.",
            SpokenForEar.Assemble("Wingman Inspector - Cockpit tab", "Wingman Inspector mobile. Merge complete."));
    }

    // ================= the conjunction sweep must not eat a real "and" =================

    [Theory]
    // An "and" or "or" that legitimately ends a clause, with nothing removed anywhere near it.
    [InlineData("It is a question of when, and of how.")]
    [InlineData("The list is long and, frankly, unread.")]
    [InlineData("Ask whether it merged, and whether it deployed.")]
    [InlineData("Closed the issue and moved on.")]
    [InlineData("Closed #2905 and moved on.")]
    [InlineData("He looked it over and over.")]
    [InlineData("The choice is deploy or wait.")]
    public void ARealConjunctionSurvives(string sentence)
    {
        var said = SpokenForEar.ScrubIdentifiers(sentence);
        Assert.Contains(sentence.Contains(" or ") ? "or" : "and", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheOrphanedConjunctionIsStillSweptWhenItShouldBe()
    {
        Assert.Equal("Merged.", SpokenForEar.ScrubIdentifiers("Merged d0630a5f2b1c and 2bfaa2a24."));
    }

    [Theory]
    // The second review's cases: "and" and "or" as the SUBJECT of the sentence, in narration holding no
    // identifier at all. The sweep used to run unconditionally, so it deleted them on position alone.
    [InlineData("The condition uses AND, not XOR.")]
    [InlineData("The operator is OR.")]
    [InlineData("The two operators are AND and OR.")]
    public void AConjunctionInTextWithNoIdentifierAtAll_IsNeverTouched(string sentence)
        => Assert.Equal(sentence, SpokenForEar.ScrubIdentifiers(sentence));

    // ================= a general preposition is not evidence =================

    [Theory]
    // The second review's cases. A preposition carrier was treated as proof that the value behind it was
    // an identifier, so ordinary dates, durations and quantities were deleted whole.
    [InlineData("Deployed on 2026-09-15.")]
    [InlineData("It ran for 12345678 seconds.")]
    [InlineData("The population grew from 1000000 to 2000000.")]
    [InlineData("Scheduled for 20260916 at 0900.")]
    public void APrepositionDoesNotMakeANumberAnIdentifier(string sentence)
        => Assert.Equal(sentence, SpokenForEar.ScrubIdentifiers(sentence));

    [Theory]
    // ...but a noun that NAMES the thing an identifier does, and that is the line between them.
    [InlineData("Merged at commit 1234567.", "Merged.")]
    [InlineData("Rolled back to revision 9876543.", "Rolled back.")]
    [InlineData("Deployed at 2bfaa2a24.", "Deployed.")]
    public void ANamingNounDoes(string heard, string spoken)
        => Assert.Equal(spoken, SpokenForEar.ScrubIdentifiers(heard));

    // ================= things that look like identifiers and are not =================

    [Theory]
    [InlineData("The port is 7330 and the build is 1000000.")]
    [InlineData("It took 3.5 seconds and used 2048 megabytes.")]
    [InlineData("Coverage is 87.5% across 1234567 lines.")]
    [InlineData("The date was 2026-09-16 and the time 23:07.")]
    [InlineData("Revenue was 5254730 dollars.")]
    [InlineData("The ratio is 16-9 and the size 1920-1080.")]
    public void NumbersAndDatesThatCarryMeaning_AreNeverTakenForHashes(string sentence)
        => Assert.Equal(sentence, SpokenForEar.ScrubIdentifiers(sentence));

    [Theory]
    [InlineData("deadbeef1", true)]   // letters AND digits: a hash
    [InlineData("1234567", false)]    // digits only: a number
    [InlineData("defaced", false)]    // letters only: a word
    public void TheEvidenceRuleForABareIdentifier(string token, bool removed)
    {
        var said = SpokenForEar.ScrubIdentifiers($"The value {token} was recorded.");
        Assert.Equal(removed, !said.Contains(token, StringComparison.Ordinal));
    }
}
