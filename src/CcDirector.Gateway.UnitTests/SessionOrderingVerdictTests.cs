using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE WINGMAN'S ARMS ON THE FOLD (the Wingman-on-every-turn mission, slice D): the calm colours, the reading
/// yellow, the label, the bucket, and the calm band - folded once in <see cref="SessionOrdering"/> and rendered
/// verbatim everywhere.
///
/// ONE TEST PER CALM GATE, WITH EVERY OTHER GATE SATISFIED. A fold that ignored one gate must not be able to pass
/// because another gate happened to fail in the same case, so each gate below is broken alone on a row that is
/// otherwise calm - and the control, with nothing broken, is green.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class SessionOrderingVerdictTests
{
    private const string ReportLabel = "Pushed the branch and opened the pull request";

    /// <summary>A stopped session whose row carries an accepted verdict that passes every calm gate, unless a
    /// parameter breaks one.</summary>
    private static SessionDto Calm(
        string verdict = SessionOrdering.VerdictFinished,
        string confidence = SessionOrdering.ConfidenceHigh,
        string activity = "WaitingForInput",
        string state = VerdictStates.Judged,
        string? label = ReportLabel,
        bool withVerdict = true) => new()
    {
        SessionId = "calm",
        ActivityState = activity,
        VerdictState = state,
        VerdictLabel = label,
        TurnVerdict = withVerdict
            ? new TurnVerdictDto { VerdictId = "v1", Verdict = verdict, Confidence = confidence, Label = label ?? "" }
            : null,
    };

    // ================================================================= the words

    [Fact]
    public void VerdictWords_OnTheFold_AreTheVocabularysOwnWords()
    {
        Assert.Equal(TurnVerdictVocabulary.Finished, SessionOrdering.VerdictFinished);
        Assert.Equal(TurnVerdictVocabulary.ContinuesAlone, SessionOrdering.VerdictContinuesAlone);
        Assert.Contains(SessionOrdering.ConfidenceHigh, TurnVerdictVocabulary.Confidences);
        Assert.NotEqual("ambiguous", SessionOrdering.ConfidenceHigh);
    }

    // ================================================================= the control: every gate satisfied

    [Fact]
    public void EffectiveColor_EveryGateSatisfied_Finished_IsGreenWithTheWingmansLabel_AndNotCounted()
    {
        var s = Calm();

        Assert.True(SessionOrdering.IsCalmVerdict(s));
        Assert.Equal("green", SessionOrdering.EffectiveColor(s));
        Assert.Equal(ReportLabel, SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(s));
    }

    [Fact]
    public void EffectiveColor_EveryGateSatisfied_ContinuesAlone_IsPurpleWithTheWingmansLabel_AndNotCounted()
    {
        var s = Calm(verdict: SessionOrdering.VerdictContinuesAlone, label: "Watching the nightly build");

        Assert.Equal("purple", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Watching the nightly build", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(s));
    }

    [Theory]
    [InlineData("WaitingForInput")]
    [InlineData("WaitingForPerm")]
    [InlineData("Idle")]
    public void EffectiveColor_EveryRawRedActivity_IsCalmed(string activity)
    {
        Assert.Equal("green", SessionOrdering.EffectiveColor(Calm(activity: activity)));
    }

    // ================================================================= one gate broken at a time

    [Fact]
    public void EffectiveColor_AmbiguousConfidence_EveryOtherGateSatisfied_StaysRedAndCounted()
    {
        var s = Calm(confidence: "ambiguous");

        Assert.False(SessionOrdering.IsCalmVerdict(s));
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(s));
        // A judged row's label is the Wingman's line whatever its colour.
        Assert.Equal(ReportLabel, SessionOrdering.StateLabel(s));
    }

    [Theory]
    [InlineData("needed-you")]
    [InlineData("stuck-recoverable")]
    [InlineData("stuck-needs-person")]
    [InlineData("cannot-tell")]
    [InlineData("not-a-turn-end")]
    [InlineData("")]
    [InlineData("Finished")]
    public void EffectiveColor_VerdictWordThatIsNotAReport_EveryOtherGateSatisfied_StaysRed(string verdict)
    {
        var s = Calm(verdict: verdict);

        Assert.False(SessionOrdering.IsCalmVerdict(s));
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(s));
    }

    [Theory]
    [InlineData("Working", "blue", "Working")]
    [InlineData("Starting", "blue", "Working")]
    [InlineData("Exited", "grey", "Exited")]
    public void EffectiveColor_NotRawRed_EveryOtherGateSatisfied_IsNeverCalmed(string activity, string colour, string label)
    {
        var s = Calm(activity: activity);

        Assert.False(SessionOrdering.IsCalmVerdict(s));
        Assert.Equal(colour, SessionOrdering.EffectiveColor(s));
        Assert.Equal(label, SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void EffectiveColor_CrashedWithACalmVerdict_IsTheCrashRed()
    {
        var s = Calm(activity: "Exited");
        s.Crashed = true;

        Assert.Equal("error", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Crashed", SessionOrdering.StateLabel(s));
    }

    [Theory]
    [InlineData(VerdictStates.None)]
    [InlineData(VerdictStates.Failed)]
    [InlineData("Judged")]
    [InlineData("")]
    public void EffectiveColor_StateNotJudged_EveryOtherGateSatisfied_StaysRedReadingNeedsYou(string state)
    {
        var s = Calm(state: state);

        Assert.False(SessionOrdering.IsCalmVerdict(s));
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        // The label is rendered only from an ACCEPTED verdict: a row carrying a stale label under any other state
        // still reads the detector's words.
        Assert.Equal("Needs you", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(s));
    }

    [Fact]
    public void EffectiveColor_StateJudgedButNoVerdictOnTheRow_StaysRed()
    {
        var s = Calm(withVerdict: false);

        Assert.False(SessionOrdering.IsCalmVerdict(s));
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
    }

    // ================================================================= the reading yellow

    [Fact]
    public void EffectiveColor_Reading_OnARawRedRow_IsYellowWingmanReading_AndNotCounted()
    {
        var s = Calm(state: VerdictStates.Reading);

        Assert.True(SessionOrdering.IsVerdictReading(s));
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Wingman reading", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(s));
    }

    [Fact]
    public void EffectiveColor_Reading_OnAWorkingRow_IsBlue()
    {
        var s = Calm(state: VerdictStates.Reading, activity: "Working");

        Assert.False(SessionOrdering.IsVerdictReading(s));
        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void StateLabel_ReadingOnAVoiceSessionWithNoAudio_KeepsPreparingVoice_BecauseReadingSitsBelowIt()
    {
        var s = Calm(state: VerdictStates.Reading);
        s.VoiceMode = true;
        s.VoiceAudioReady = false;

        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Preparing voice", SessionOrdering.StateLabel(s));
    }

    // ================================================================= where the calm arm sits on the ladder

    [Fact]
    public void EffectiveColor_SnoozedWithACalmVerdict_IsGreySnoozed()
    {
        var s = Calm();
        s.OnHold = true;

        Assert.Equal("grey", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(s));
    }

    [Fact]
    public void EffectiveColor_SupervisedWithACalmVerdict_IsSupportingSnoozed()
    {
        var s = Calm();
        s.HasLiveSupervisor = true;

        Assert.Equal("supporting", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(s));
    }

    [Fact]
    public void EffectiveColor_DictationInFlightWithACalmVerdict_IsOrange()
    {
        var s = Calm();
        s.DictationStatus = "Transcribing";

        Assert.Equal("orange", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Transcribing", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void EffectiveColor_DirectorBriefingWithACalmVerdict_IsYellowWingmanReading()
    {
        var s = Calm();
        s.BriefingState = "Briefing";

        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Wingman reading", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void EffectiveColor_VoicePreparingWithACalmVerdict_IsYellowPreparingVoice()
    {
        var s = Calm();
        s.VoiceMode = true;
        s.VoiceAudioReady = false;

        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Preparing voice", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void EffectiveColor_CalmVerdictAboveTheBaseColour_ABrandNewRowWithOneReadsTheVerdict()
    {
        // The base colour's green "Ready" sits below the calm arm, so the verdict's words win where both apply.
        var s = Calm();
        s.IsBrandNew = true;

        Assert.Equal("green", SessionOrdering.EffectiveColor(s));
        Assert.Equal(ReportLabel, SessionOrdering.StateLabel(s));
    }

    // ================================================================= the label

    [Fact]
    public void StateLabel_CalmRowWithNoLabelOfItsOwn_ReadsDoneOrCarryingOn_NeverBlank()
    {
        Assert.Equal("Done", SessionOrdering.StateLabel(Calm(label: null)));
        Assert.Equal("Done", SessionOrdering.StateLabel(Calm(label: "   ")));
        Assert.Equal("Carrying on", SessionOrdering.StateLabel(Calm(verdict: SessionOrdering.VerdictContinuesAlone, label: null)));
    }

    [Fact]
    public void StateLabel_RedRowJudgedNeededYou_ReadsTheAskVerbatim()
    {
        var s = Calm(verdict: "needed-you", label: "Choose whether to run the migration now");

        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Choose whether to run the migration now", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void StateLabel_ARowNobodyJudged_ReadsNeedsYou()
    {
        var s = new SessionDto { SessionId = "plain", ActivityState = "WaitingForInput" };

        Assert.Equal(VerdictStates.None, s.VerdictState);
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(s));
    }

    // ================================================================= the calm band

    [Fact]
    public void InWaitingOrder_CalmRowsFollowEveryRedRow_AndOnlyJudgedCalmRowsAreInTheBand()
    {
        var redLate = new SessionDto { SessionId = "red-late", ActivityState = "WaitingForInput", NeedsYouSince = new DateTime(2026, 9, 15, 10, 5, 0, DateTimeKind.Utc) };
        var redEarly = new SessionDto { SessionId = "red-early", ActivityState = "WaitingForInput", NeedsYouSince = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc) };
        var done = Calm();
        done.SessionId = "done";
        done.CreatedAt = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc);
        var carrying = Calm(verdict: SessionOrdering.VerdictContinuesAlone);
        carrying.SessionId = "carrying";
        carrying.CreatedAt = new DateTime(2026, 9, 15, 7, 0, 0, DateTimeKind.Utc);
        var snoozed = Calm();
        snoozed.SessionId = "snoozed";
        snoozed.OnHold = true;
        var fresh = new SessionDto { SessionId = "fresh", ActivityState = "WaitingForInput", IsBrandNew = true };
        var working = new SessionDto { SessionId = "working", ActivityState = "Working" };

        var order = SessionOrdering.InWaitingOrder(new[] { done, redLate, snoozed, carrying, fresh, redEarly, working });

        Assert.Equal(new[] { "red-early", "red-late", "carrying", "done" }, order.Select(s => s.SessionId));
        Assert.Equal("green", SessionOrdering.EffectiveColor(fresh));
        Assert.False(SessionOrdering.IsInCalmBand(fresh));
        Assert.False(SessionOrdering.IsInCalmBand(snoozed));
    }
}
