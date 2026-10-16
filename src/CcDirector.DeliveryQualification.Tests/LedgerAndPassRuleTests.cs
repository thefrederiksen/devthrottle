using CcDirector.DeliveryQualification;
using Xunit;

namespace CcDirector.DeliveryQualification.Tests;

/// <summary>
/// The delivery rig's rate arithmetic and its scenario pass rules (review of pull request 4069). A rate is only as good
/// as these: a pass rule that lets a destroyed draft through, or a count that drops a lost prompt from the denominator,
/// reports a fix that never happened.
/// </summary>
public sealed class LedgerAndPassRuleTests
{
    [Theory]
    [InlineData(24, 30, 0.63, 0.90)]
    [InlineData(3, 3, 0.44, 1.00)]
    [InlineData(0, 3, 0.00, 0.56)]
    [InlineData(10, 15, 0.42, 0.85)]
    [InlineData(1, 1, 0.21, 1.00)]
    public void Wilson_KnownCounts_MatchesTheStandardInterval(int pass, int n, double low, double high)
    {
        var (lo, hi) = Ledger.Wilson(pass, n);

        Assert.Equal(low, lo, 2);
        Assert.Equal(high, hi, 2);
    }

    [Fact]
    public void Wilson_NoAttempts_IsTheWholeRange()
    {
        Assert.Equal((0.0, 1.0), Ledger.Wilson(0, 0));
    }

    private static LedgerEntry Entry(string director, bool pass) =>
        new("run", new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), "abc", "m", "os", "claude", "v", "idle-short", 10, director, pass ? "ARRIVED" : "LOST", pass, 1, "", null);

    [Fact]
    public void Counted_SetupFailure_IsLeftOutButAnErrorAfterTheSendIsCounted()
    {
        var counted = Ledger.Counted([Entry("OK", true), Entry("SETUP-FAILED", false), Entry("RIG-ERROR", false)]);

        Assert.Equal(["OK", "RIG-ERROR"], counted.Select(e => e.Director));
    }

    [Fact]
    public void Rates_OneCommit_ShowsPassesOverCountedAttempts()
    {
        var report = Ledger.Rates([Entry("OK", true), Entry("FAILED", false), Entry("SETUP-FAILED", false)]);

        Assert.Contains("2 attempts counted (1 setup failures not counted)", report);
        Assert.Contains("claude   idle-short             1    2    50%", report);
    }

    [Fact]
    public void LeftInComposer_OwnerDraftRefusedAndStillThere_Passes()
    {
        Assert.Equal((true, "LOST"), PassRules.LeftInComposer(ownerTyped: true, "LOST", "FAILED", leftSubmitted: false, leftOnScreen: true));
    }

    [Fact]
    public void LeftInComposer_OwnerDraftDestroyedAndPromptArrivedAlone_Fails()
    {
        Assert.Equal((false, "DRAFT-DESTROYED"), PassRules.LeftInComposer(ownerTyped: true, "ARRIVED", "OK", leftSubmitted: false, leftOnScreen: false));
    }

    [Fact]
    public void LeftInComposer_DraftAndPromptInOneTurn_FailsAsWelded()
    {
        Assert.Equal((false, "WELDED"), PassRules.LeftInComposer(ownerTyped: true, "DOUBLED", "OK", leftSubmitted: false, leftOnScreen: false));
        Assert.Equal((false, "WELDED"), PassRules.LeftInComposer(ownerTyped: false, "ARRIVED", "OK", leftSubmitted: true, leftOnScreen: false));
    }

    [Fact]
    public void LeftInComposer_ProductsOwnLineClearedAndPromptArrivedAlone_Passes()
    {
        Assert.Equal((true, "ARRIVED"), PassRules.LeftInComposer(ownerTyped: false, "ARRIVED", "OK", leftSubmitted: false, leftOnScreen: false));
    }

    [Fact]
    public void MenuOpen_RefusedThenNextArrives_Passes()
    {
        Assert.Equal((true, "LOST"), PassRules.MenuOpen("LOST", "FAILED", firstPass: false, firstArrivedLate: false, "ARRIVED", nextPass: true));
    }

    [Fact]
    public void MenuOpen_FirstArrivesLateAfterBeingReportedFailed_FailsAsLate()
    {
        Assert.Equal((false, "LATE"), PassRules.MenuOpen("LOST", "FAILED", firstPass: false, firstArrivedLate: true, "ARRIVED", nextPass: true));
    }

    [Fact]
    public void MenuOpen_NextPromptRefusedToo_FailsNamingTheNextPrompt()
    {
        Assert.Equal((false, "NEXT-LOST"), PassRules.MenuOpen("LOST", "FAILED", firstPass: false, firstArrivedLate: false, "LOST", nextPass: false));
    }

    [Fact]
    public void MenuOpen_FirstLostButReportedDelivered_Fails()
    {
        Assert.Equal((false, "LOST"), PassRules.MenuOpen("LOST", "OK", firstPass: false, firstArrivedLate: false, "ARRIVED", nextPass: true));
    }
}
