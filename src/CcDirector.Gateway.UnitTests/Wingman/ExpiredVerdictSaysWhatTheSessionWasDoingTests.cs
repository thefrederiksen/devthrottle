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
    public void TheSpokenLineSaysWhatTheSessionHadBeenDoing()
    {
        var expired = TurnVerdictWatchdog.Expire(CarryingOn("Slices F and G are mid-build."), JudgedAt.AddMinutes(11));

        Assert.Equal(
            "It said it would continue, and it did not. It had said: Slices F and G are mid-build.",
            expired.Spoken);
    }

    [Fact]
    public void TheCorrectionLeads_SoItIsNeverHeardAsTheSessionStillClaimingIt()
    {
        var expired = TurnVerdictWatchdog.Expire(CarryingOn("Everything is fine."), JudgedAt.AddMinutes(11));

        Assert.StartsWith(TurnVerdictWatchdog.ExpiredSpokenLead, expired.Spoken, StringComparison.Ordinal);
        // The past-tense attribution is what stops "Everything is fine" being heard as a live claim.
        Assert.Contains("It had said:", expired.Spoken, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNothingToCarry_ItIsTheLeadAlone_NeverATrailingColon(string? summary)
    {
        var expired = TurnVerdictWatchdog.Expire(CarryingOn(summary), JudgedAt.AddMinutes(11));

        Assert.Equal(TurnVerdictWatchdog.ExpiredSpokenLead, expired.Spoken);
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
}
