using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// WHAT THE OWNER HEARS WHEN THE CARRYING-ON CLOCK RUNS OUT.
///
/// Voice narrates the Wingman's output and nothing else (owner's ruling, 2026-09-15), so a verdict with no
/// content produces a narration with no content. The clock's replacement verdict dropped everything the
/// original held and spoke nine fixed words. On 2026-09-16 the owner played one on the phone: a two-second
/// clip saying "It said it would continue, and it did not", which does not say what the session had been
/// doing - the only thing that would make the interruption worth acting on.
///
/// The distinction these tests pin: the original verdict's ANSWER is dropped, because it claimed the session
/// needed nothing and that is what turned out to be wrong. Its DESCRIPTION is kept, because what the session
/// was doing is still true and is the reason the owner is being spoken to at all.
///
/// SAID ONCE, NOT TWICE (issue 2243, the owner's ruling of 2026-09-23). The line this file used to pin opened
/// with "It said it would continue, and it did not." and then replayed the old reading behind "It had said:" -
/// two sentences in a row opening "It said / It had said" - and the owner heard the stop narrated two times, on
/// every expiry, on the live fleet. The body now opens with the correction once, and the description follows
/// behind "Its last reading was:", attributed to the Wingman's own earlier reading rather than restated as
/// another sentence about the promise.
///
/// ONE BODY, READ OR HEARD (contract v3, owner's ruling of 2026-09-18). Summary, Spoken and Narration hold
/// the SAME text on every stored reading, a clock-written one included - a screen that shows one field while
/// the ear hears another is the defect the v3 redesign removed. These tests pin all three to the one body.
/// </summary>
public sealed class ExpiredVerdictSaysWhatTheSessionWasDoingTests
{
    private static readonly DateTime JudgedAt = new(2026, 9, 16, 1, 0, 0, DateTimeKind.Utc);

    private static TurnVerdictDto CarryingOn(string? summary) => new()
    {
        VerdictId = "carry-1",
        JudgedAtUtc = JudgedAt,
        TurnEndObservedAtUtc = JudgedAt.AddSeconds(-30),
        ScreenHash = "hash-1",
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v2",
        PackageKind = "agent-reply",
        Verdict = TurnVerdictVocabulary.ContinuesAlone,
        Confidence = "high",
        Evidence = "the two Workers are the only thing between here and the next deploy",
        Label = "In progress: slices F and G building",
        Summary = summary!,
        AnswerVia = "reply",
        Risk = "none",
        Spoken = "Slices F and G are mid-build and nothing is committed yet.",
        NextScheduledWakeUtc = null,
    };

    [Fact]
    public void TheCorrectionOpens_TheOldReadingFollows_AttributedToTheLastReading()
    {
        var expired = TurnVerdictWatchdog.Expire(CarryingOn("Slices F and G are mid-build."), JudgedAt.AddMinutes(11));

        Assert.Equal(
            "It said it would continue, and it did not. Its last reading was: Slices F and G are mid-build.",
            expired.Spoken);
    }

    [Fact]
    public void ThePromiseIsStatedOnce_NoSecondItSaidReplayingTheOldReading()
    {
        // The description is one the old shape quoted behind "It had said:", so this pins the doubling is gone
        // on the very case that produced it.
        var expired = TurnVerdictWatchdog.Expire(
            CarryingOn("Everything is fine, and it will keep going by itself."), JudgedAt.AddMinutes(11));

        // The correction opens the line - the news is never delayed behind the description - and the old
        // reading arrives exactly once, behind a frame that is not another sentence about the promise.
        Assert.StartsWith(TurnVerdictWatchdog.ExpiredCorrection, expired.Spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("It had said:", expired.Spoken, StringComparison.Ordinal);
        Assert.Equal(1, Count(expired.Spoken, TurnVerdictWatchdog.ExpiredReadingFrame));
        Assert.Equal(1, Count(expired.Spoken, "It said"));
        Assert.Equal(1, Count(expired.Spoken, "Everything is fine, and it will keep going by itself."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNothingToCarry_ItIsTheCorrectionAlone_NeverADanglingFrame(string? summary)
    {
        var expired = TurnVerdictWatchdog.Expire(CarryingOn(summary), JudgedAt.AddMinutes(11));

        Assert.Equal(TurnVerdictWatchdog.ExpiredCorrection, expired.Spoken);
        Assert.DoesNotContain(TurnVerdictWatchdog.ExpiredReadingFrame, expired.Spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void OneBody_ReadOrHeard_SummarySpokenAndNarrationHoldTheSameText()
    {
        var expired = TurnVerdictWatchdog.Expire(CarryingOn("Slices F and G are mid-build."), JudgedAt.AddMinutes(11));

        Assert.Equal(expired.Narration, expired.Spoken);
        Assert.Equal(expired.Narration, expired.Summary);
        // The row's own one line is the label; the body is what the panel shows and the ear hears.
        Assert.Equal(TurnVerdictWatchdog.ExpiredLabel, expired.Label);
    }

    [Fact]
    public void TheFalsifiedANSWERIsStillDropped()
    {
        // The half that must NOT survive. It said the session needed nothing, and the clock exists because
        // that was wrong; carrying it would re-offer the owner an answer the evidence has just refuted.
        var expired = TurnVerdictWatchdog.Expire(CarryingOn("Slices F and G are mid-build."), JudgedAt.AddMinutes(11));

        Assert.Equal(TurnVerdictVocabulary.NeededYou, expired.Verdict);
        Assert.Null(expired.AgentRecommends);
        Assert.Null(expired.Menu);
        Assert.Empty(expired.Options);
        Assert.Null(expired.NextScheduledWakeUtc);
    }

    /// <summary>Honest counting, not a Contains that one hit or three would satisfy equally.</summary>
    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = 0; (at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0; at += needle.Length)
            count++;
        return count;
    }
}
