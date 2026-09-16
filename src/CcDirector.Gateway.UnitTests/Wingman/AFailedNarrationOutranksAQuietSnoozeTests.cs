using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// A FAILED NARRATION OUTRANKS A QUIET SNOOZE (the Architect's ruling, 2026-09-16).
///
/// TWO CHANGES THAT WERE EACH RIGHT ALONE, AND FALSE TOGETHER. Pull request 2908 ended the yellow voice hold
/// when a narration is never coming, so such a row falls through to red and asks for the owner - his own
/// amendment, "make sure that all sessions get close to needs-you if transcription fails". Slice F added an
/// arm that turns a raw-red row cyan when its snooze ran out with nothing new. Both arms describe the SAME
/// raw-red row, both had passing tests, and neither knew about the other: the collision only existed once
/// they met in a rebase, which is why nothing either side wrote could have caught it.
///
/// THE RULE THAT SETTLES IT, and it is the reason this file is named for the rule rather than for the bug: a
/// TERMINAL verdict must never outrank an ACTIONABLE one. "Snooze ended, nothing new" is the end of the
/// story and there is nothing to do about it. A narration that never arrived is something the owner can act
/// on - he was promised he would hear this session, he did not, and now he has to go and look.
///
/// AND IT WAS NOT MERELY OUTRANKED - IT WAS UNREACHABLE. The snooze arm returned cyan for the colour, and the
/// voice words are consulted only inside the RED branch of the label fold. With cyan winning the colour,
/// "Voice did not arrive after 48m" could not be reached by any row at all.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class AFailedNarrationOutranksAQuietSnoozeTests
{
    private const string GaveUpWords = "Voice did not arrive after 48m";

    /// <summary>A stopped session whose snooze ran out with nothing new AND whose narration never came. Both
    /// facts on one row, which is the whole point: either one alone was already proved by its own slice.</summary>
    private static SessionDto BothFacts() => new()
    {
        SessionId = "s1",
        DirectorId = "dir-1",
        Agent = "TestAgent",
        RepoPath = "repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        VoiceMode = true,
        VoiceAudioReady = false,
        VoiceDisplay = new VoiceDisplay { Kind = VoiceDisplayKinds.GaveUp, Label = GaveUpWords },
        SnoozeExpired = true,
        SnoozeEndedNothingNew = true,
        VerdictState = VerdictStates.None,
    };

    [Fact]
    public void ARowCarryingBothFacts_IsRed_AndSaysWhyTheOwnerIsBeingAsked()
    {
        var s = BothFacts();

        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal(GaveUpWords, SessionOrdering.StateLabel(s));
        // Named explicitly, because these are what the row said before the arms were ordered.
        Assert.NotEqual(SessionOrdering.SnoozeEndedNothingNewColor, SessionOrdering.EffectiveColor(s));
        Assert.NotEqual(SessionOrdering.SnoozeEndedNothingNewLabel, SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void ARowCarryingBothFacts_AsksForTheOwner()
    {
        // The colour is not the point on its own - the bucket is what puts the row in front of him.
        var s = BothFacts();

        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(s));
        Assert.False(SessionOrdering.IsInCalmBand(s));
    }

    [Fact]
    public void AQuietSnoozeWithNoVoiceAtAll_IsStillCyan_AndStillSaysNothingNew()
    {
        // THE RANKING IS NARROW. Only the two terminal voice verdicts outrank the snooze arm; a row with no
        // voice trouble is untouched by this ordering, so slice F's whole point survives it. Voice mode is off
        // here, which is the ordinary row - a session nobody asked to be narrated.
        var s = BothFacts();
        s.VoiceMode = false;
        s.VoiceDisplay = null;

        Assert.Equal(SessionOrdering.SnoozeEndedNothingNewColor, SessionOrdering.EffectiveColor(s));
        Assert.Equal(SessionOrdering.SnoozeEndedNothingNewLabel, SessionOrdering.StateLabel(s));
    }

    [Theory]
    [InlineData(VoiceDisplayKinds.GaveUp)]
    [InlineData(VoiceDisplayKinds.NotNarrated)]
    public void BothTerminalVoiceVerdicts_OutrankTheSnoozeArm(string kind)
    {
        // Both of the verdicts that mean "nothing is coming", not just the one that was found.
        var s = BothFacts();
        s.VoiceDisplay = new VoiceDisplay { Kind = kind, Label = "Turn not narrated" };

        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Turn not narrated", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void ACalmVoiceVerdict_DoesNotTurnTheRowRed()
    {
        // THE OTHER SIDE OF "NARROW": a voice that is still trying has broken no promise, so it must not drag
        // this row to red. That is 2908's own rule - every calm verdict still holds yellow, so nothing flashes
        // red between attempts - asserted here where the two arms meet rather than only where 2908 asserted it.
        //
        // IT IS YELLOW, NOT CYAN, AND THAT IS CORRECT: the voice hold sits ABOVE both of the arms this file is
        // about, so while a narration is still promised the row says so and neither arm speaks. The claim that
        // belongs to this file is only that the row does not go red.
        //
        // The kind is a literal because VoiceDisplayKinds deliberately names only the two verdicts a rule
        // outside the voice fold reads; spelling a third one there would be a second name for the fold's own
        // word, which is what that class exists to prevent.
        var s = BothFacts();
        s.VoiceDisplay = new VoiceDisplay { Kind = "preparing", Label = "Preparing voice" };

        Assert.NotEqual("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void TheWingmansOwnWords_StillOutrankTheVoicesWords()
    {
        // The ranking does not stop at this pair. When the Wingman managed to say what the session needs, that
        // is the more useful sentence and it stays on top - a failed narration is the reason he is reading it
        // at all, not the thing he has to do. 2908 put VoiceGaveUpLabel below JudgedLabel for exactly this, and
        // ordering the arms above must not have quietly reversed it.
        var s = BothFacts();
        s.SnoozeEndedNothingNew = false;
        s.VerdictState = VerdictStates.Judged;
        s.VerdictLabel = "Approve the release";
        s.TurnVerdict = new TurnVerdictDto
        {
            VerdictId = "v1",
            Verdict = "needed-you",
            Label = "Approve the release",
            Confidence = "high",
            Model = "devthrottle/wingman-fast",
            ContractVersion = "v2",
            PackageKind = "agent-reply",
            ScreenHash = "hash",
        };

        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Approve the release", SessionOrdering.StateLabel(s));
    }
}
