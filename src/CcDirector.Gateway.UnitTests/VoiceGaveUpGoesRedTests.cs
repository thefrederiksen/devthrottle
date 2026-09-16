using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// THE WEDGE, CLOSED: a voice session whose narration is not coming stops being yellow and asks for the owner.
///
/// What this is about. The voice hold turns a stopped voice session yellow so the owner is not shown red
/// before he can hear anything, and it held until audio arrived - with no other way out. A narration that
/// never arrived therefore held the row forever. On 2026-09-16 a session sat yellow for 48 minutes carrying
/// the label "Voice did not arrive after 48m": the Gateway's own voice verdict had given up at three
/// minutes, said so in words on that same row, and the colour never heard about it.
///
/// The rule that was supposed to prevent this already existed - VoiceDisplayFold.GaveUpAfter - and
/// SessionOrdering's own summary named it as the reason the wedge could not happen. It was wired to the
/// WORDS and never to the DOT, so that sentence was true of the label and false of the colour. These tests
/// are the wiring, and the first of them is the row that was actually on the fleet.
///
/// Owner's amendment, 2026-09-15: "make sure that all sessions get close to 'need you' if transcription
/// fails." This narrows the ruling of 2026-07-19 ("never red until the voice is available") to the window
/// in which a voice is still genuinely coming.
/// </summary>
public class VoiceGaveUpGoesRedTests
{
    /// <summary>A stopped voice session with no audio, carrying the given voice verdict.</summary>
    private static SessionDto VoiceRow(string kind, string label) => new()
    {
        SessionId = "v",
        StatusColor = "red",
        ActivityState = "WaitingForInput",
        VoiceMode = true,
        VoiceGenerating = false,
        VoiceAudioReady = false,
        VoiceDisplay = new VoiceDisplay { Kind = kind, Label = label },
    };

    // ---------- the hold ENDS when nothing is coming ----------

    [Fact]
    public void TheFortyEightMinuteRow_IsRed_NotYellow()
    {
        // Session c80e7a36 on the live fleet, 2026-09-16 00:33Z, field for field.
        var s = VoiceRow(VoiceDisplayKinds.GaveUp, "Voice did not arrive after 48m");

        Assert.False(SessionOrdering.IsVoicePreparing(s));
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(s));
    }

    [Fact]
    public void AnAbandonedNarration_IsRed_NotYellow()
    {
        var s = VoiceRow(VoiceDisplayKinds.NotNarrated, "Turn not narrated");

        Assert.False(SessionOrdering.IsVoicePreparing(s));
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
    }

    // ---------- and ONLY then. Every calm verdict still holds the yellow ----------

    /// <summary>
    /// The 2026-07-19 ruling, still in force everywhere it was aimed. These are the states where a narration
    /// really may still arrive, or where the row already carries a better sentence of its own, and a red dot
    /// in the gaps between attempts is exactly what that ruling exists to prevent.
    ///
    /// "nothingToNarrate" is in this list on purpose and is not an oversight: a session parked on a menu was
    /// never going to be narrated, so calling it a voice failure reports something that never happened.
    /// "blocked" and "serviceDown" are here because they already say what to DO, and "needs you" would
    /// replace an actionable sentence with a symptom.
    /// </summary>
    [Theory]
    [InlineData("preparing")]
    [InlineData("retrying")]
    [InlineData("notReady")]
    [InlineData("nothingToNarrate")]
    [InlineData("blocked")]
    [InlineData("serviceDown")]
    public void EveryOtherVoiceVerdict_StillHoldsYellow(string kind)
    {
        var s = VoiceRow(kind, "some label");

        Assert.True(SessionOrdering.IsVoicePreparing(s));
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void NoVoiceVerdictOnTheRow_HoldsYellow_AsItDidBefore()
    {
        // VoiceDisplay is null on every Director-local response. This rule must not read a shape that is not
        // there, and the behaviour where it is absent is exactly what it was. The Gateway roster path always
        // stamps it - proven by GatewayStampsAVoiceVerdictOnEveryRowTests - so the surface the owner reads is
        // never the one taking this arm.
        var s = VoiceRow(VoiceDisplayKinds.GaveUp, "x");
        s.VoiceDisplay = null;

        Assert.True(SessionOrdering.IsVoicePreparing(s));
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));
    }

    // ---------- going red must not cost the words ----------

    [Fact]
    public void TheRedRowKeepsTheVoiceWords_NotABareNeedsYou()
    {
        var s = VoiceRow(VoiceDisplayKinds.GaveUp, "Voice did not arrive after 48m");

        // Both halves, together. The words alone passed before this fix too - the yellow hold rendered the
        // same string - so asserting them without the colour would be a test that cannot fail for the reason
        // it is here. What is new is that they survive the row going RED.
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Voice did not arrive after 48m", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void AJudgedLineOutranksTheVoiceWords()
    {
        // When the Wingman managed to say what the session needs, that is the more useful sentence. The
        // failed narration is the reason he is reading it rather than hearing it, not the headline.
        var s = VoiceRow(VoiceDisplayKinds.GaveUp, "Voice did not arrive after 48m");
        s.VerdictState = "judged";
        s.VerdictLabel = "Needs the database password";

        Assert.Equal("Needs the database password", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void ABlankVoiceLabelFallsBackToNeedsYou_NeverToTheEmptyString()
    {
        var s = VoiceRow(VoiceDisplayKinds.GaveUp, "   ");

        Assert.Equal("Needs you", SessionOrdering.StateLabel(s));
    }

    // ---------- the arms above this one are untouched ----------

    [Fact]
    public void AWorkingSession_IsStillBlue_WhateverItsVoiceSays()
    {
        var s = VoiceRow(VoiceDisplayKinds.GaveUp, "Voice did not arrive after 48m");
        s.ActivityState = "Working";
        s.StatusColor = "blue";

        Assert.False(SessionOrdering.IsVoicePreparing(s));
        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Working", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void ASupervisedSession_StillRecedes_RatherThanAskingTheOwner()
    {
        // A session with a live owning session is not the owner's to be asked for, and a failed narration
        // does not change whose it is.
        var s = VoiceRow(VoiceDisplayKinds.GaveUp, "Voice did not arrive after 48m");
        s.IsControlled = true;
        s.HasLiveSupervisor = true;

        Assert.Equal("supporting", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(s));
    }
}
