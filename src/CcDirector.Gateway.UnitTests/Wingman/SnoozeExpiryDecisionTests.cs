using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE THREE CASES OF RULING 10, as the pure function decides them (the Wingman-on-every-turn mission, slice F).
/// <see cref="SnoozeExpiryReJudgeTests"/> proves the fold reaches these answers over the real registry and the
/// real store; this file is where the rule itself is read, including the ONE PATH PRODUCTION CANNOT REACH TODAY.
///
/// THE SWITCHING DESIGN'S "turn ends since the snooze was set" IS NOT LIVE. That build has not landed, so nothing
/// on the wire carries the count and the fold passes null - every production decision today is made by comparing
/// the latest verdict's observed moment against the snooze. The count path is written, and pinned here, so that
/// the switching build changes one argument rather than re-deriving a rule. A test is the only thing that can
/// hold an unreachable path honest, which is why these exist rather than a comment saying the same.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class SnoozeExpiryDecisionTests
{
    private static readonly DateTime SnoozeSet = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private static TurnVerdictDto Verdict(DateTime observedAtUtc, bool failed = false) => new()
    {
        VerdictId = "v1",
        TurnEndObservedAtUtc = observedAtUtc,
        Failed = failed,
        Verdict = failed ? "" : "needed-you",
        Confidence = failed ? "" : "high",
    };

    // ============================================================ the live path: the verdict's own moment

    [Fact]
    public void NoVerdictAtAll_IsNothingNew()
        => Assert.Equal(SnoozeExpiryOutcome.NothingNew,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, latest: null, VerdictStates.None, turnEndsSinceSnoozeSet: null));

    [Fact]
    public void AnAcceptedVerdictOlderThanTheSnooze_StillRulesTheRow()
        // Nothing NEW happened, and the ask the owner parked is still on the screen. A clock does not answer it.
        => Assert.Equal(SnoozeExpiryOutcome.VerdictRules,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, Verdict(SnoozeSet.AddMinutes(-2)), VerdictStates.Judged, null));

    [Fact]
    public void AVerdictAtTheVeryMomentTheSnoozeWasSet_IsNotANewStop()
        // The stop the owner snoozed in response to. Strictly later is news; the same instant is the thing he
        // was answering, and a deferred hold lands at exactly the turn end that ended the work. So it is not a
        // new stop - and it still rules the row, as the verdict it is.
        => Assert.Equal(SnoozeExpiryOutcome.VerdictRules,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, Verdict(SnoozeSet), VerdictStates.Judged, null));

    [Fact]
    public void ARefusedAnswerOlderThanTheSnooze_IsNothingNew()
        // A refused answer says nothing about the stop, so there is no verdict to rule and nothing happened
        // while the snooze ran: the red this row would show is the clock's own.
        => Assert.Equal(SnoozeExpiryOutcome.NothingNew,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, Verdict(SnoozeSet.AddMinutes(-2), failed: true), VerdictStates.Failed, null));

    [Fact]
    public void AnAcceptedVerdictForAStopWhileItRan_Rules()
        => Assert.Equal(SnoozeExpiryOutcome.VerdictRules,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, Verdict(SnoozeSet.AddMinutes(5)), VerdictStates.Judged, null));

    [Fact]
    public void ARefusedAnswerForAStopWhileItRan_IsAskedAgainNow()
        // A refused answer is evidence a stop happened and is a verdict for nothing at all.
        => Assert.Equal(SnoozeExpiryOutcome.ReadRequested,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, Verdict(SnoozeSet.AddMinutes(5), failed: true), VerdictStates.Failed, null));

    [Fact]
    public void AReadAlreadyInFlight_DecidesNothing()
        // An answer is already coming: this must neither call the row calm nor ask a second time.
        => Assert.Equal(SnoozeExpiryOutcome.None,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, latest: null, VerdictStates.Reading, null));

    [Fact]
    public void ASnoozeThisGatewayNeverSawArmed_DecidesNothing()
        // No start, so no stretch of time, so no claim about it. Never "nothing happened".
        => Assert.Equal(SnoozeExpiryOutcome.None,
            SnoozeExpiryDecision.AtExpiry(armedAtUtc: null, Verdict(SnoozeSet.AddMinutes(5)), VerdictStates.Judged, null));

    // ============================================================ the switching design's count, when it lands

    [Fact]
    public void TheCount_TakesPrecedence_AndZeroTurnEndsMeansNothingHappened()
    {
        // The count says nothing ended, though a stored verdict is newer than the snooze - the detector's own
        // count wins over the inference, which is the whole reason the field exists. The verdict still rules the
        // row, because an accepted verdict always does; what the count changed is that nothing is re-read.
        Assert.Equal(SnoozeExpiryOutcome.VerdictRules,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, Verdict(SnoozeSet.AddMinutes(5)), VerdictStates.Judged, turnEndsSinceSnoozeSet: 0));
        // With nothing judged at all, the same zero count is the plain "nothing new" answer.
        Assert.Equal(SnoozeExpiryOutcome.NothingNew,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, latest: null, VerdictStates.None, turnEndsSinceSnoozeSet: 0));
    }

    [Fact]
    public void TheCount_SaysAStopHappened_ThoughNothingIsStored_SoItIsAskedNow()
    {
        // The case only the count can see: a turn ended and no verdict of any kind was ever written for it. On
        // the live path this row reads as "nothing new"; with the count it is asked.
        Assert.Equal(SnoozeExpiryOutcome.ReadRequested,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, latest: null, VerdictStates.None, turnEndsSinceSnoozeSet: 2));
    }

    [Fact]
    public void TheCount_SaysAStopHappened_AndAnOldVerdictDoesNotCoverIt()
    {
        Assert.Equal(SnoozeExpiryOutcome.ReadRequested,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, Verdict(SnoozeSet.AddMinutes(-2)), VerdictStates.Judged, turnEndsSinceSnoozeSet: 1));
    }

    [Fact]
    public void TheCount_SaysAStopHappened_AndTheVerdictForItRules()
    {
        Assert.Equal(SnoozeExpiryOutcome.VerdictRules,
            SnoozeExpiryDecision.AtExpiry(SnoozeSet, Verdict(SnoozeSet.AddMinutes(5)), VerdictStates.Judged, turnEndsSinceSnoozeSet: 1));
    }
}

/// <summary>
/// The fold arm itself (slice F): what <see cref="SessionOrdering"/> does with the stamp, and the one gate that
/// keeps it off a row it must never touch.
/// </summary>
public sealed class SnoozeEndedNothingNewFoldArmTests
{
    private static SessionDto Row(string activity, bool stamped = true) => new()
    {
        SessionId = "s1",
        ActivityState = activity,
        StatusColor = "red",
        SnoozeEndedNothingNew = stamped,
    };

    [Fact]
    public void AStoppedRow_IsCyanWithTheWords()
    {
        var s = Row("WaitingForInput");
        Assert.Equal("cyan", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Snooze ended, nothing new", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(s));
    }

    [Theory]
    [InlineData("Working")]
    [InlineData("Starting")]
    public void AWorkingRow_IsBlue_NothingOutranksWorking(string activity)
    {
        var s = Row(activity);
        Assert.False(SessionOrdering.IsSnoozeEndedNothingNew(s));
        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Working", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void AnExitedRow_IsNotQuietenedByIt()
    {
        var s = Row("Exited");
        Assert.False(SessionOrdering.IsSnoozeEndedNothingNew(s));
        Assert.Equal("Exited", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void ACrashedRow_IsNeverQuietenedByIt()
    {
        // The one thing on the whole ladder nobody may ever quieten. A crashed session is "Exited" like any
        // other - the crash is the separate Crashed fact - which is the shape the fold actually sees.
        var s = Row("Exited");
        s.Crashed = true;
        Assert.False(SessionOrdering.IsSnoozeEndedNothingNew(s));
        Assert.Equal("error", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Crashed", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void AJudgedVerdictOutranksIt()
    {
        // A RED JUDGED ROW IS A REAL ASK, and this arm must never paint it calm. The fold never stamps both -
        // a judged stop is case 2 and stamps nothing - so this is the ladder holding it as a property rather
        // than trusting the producer. It caught a real hole: without the verdict gate this row went cyan.
        var s = Row("WaitingForInput");
        s.VerdictState = VerdictStates.Judged;
        s.VerdictLabel = "Choose whether to run the migration";
        s.TurnVerdict = new TurnVerdictDto { Verdict = "needed-you", Confidence = "high", Label = s.VerdictLabel };
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Choose whether to run the migration", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void ASnoozeStillRunningOutranksIt()
    {
        var s = Row("WaitingForInput");
        s.OnHold = true;
        Assert.Equal("grey", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void WithoutTheStamp_TheRowIsTheRedItAlwaysWas()
    {
        var s = Row("WaitingForInput", stamped: false);
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(s));
    }
}
