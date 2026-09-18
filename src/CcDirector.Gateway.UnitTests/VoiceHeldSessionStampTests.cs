using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// VOICE MODE NARRATES THE SESSIONS YOU OWN, AND THE SCREEN NOW SAYS SO.
///
/// The enrolment sweep has always refused a session a live session owns - <c>VoiceModeAllSweep.Plan</c> skips a
/// supervised session, and <c>PlanOff</c> switches one back off - because those are read by their owner, not by the
/// user. Nothing SAID it. On the owner's twenty-session fleet on 2026-09-17, eleven sessions were correctly not voice
/// sessions, and each of their Voice tabs drew the phone's own card: "Voice mode is off for this session", with a
/// "Switch to voice mode" button, directly under a banner reading "Every session on the Gateway narrates its turns".
///
/// Two contradictory sentences on one screen, and a button that could not succeed: on an unreachable computer it
/// answered 503 and the failure was wiped off the screen by the next three-second poll, and on a reachable one the
/// sweep is meant to undo it. That is how a feature that was working read as "half the time it doesn't work".
///
/// These pin the fix at the ONE place the answer exists. <see cref="SessionDto.HasLiveSupervisor"/> is resolved inside
/// <see cref="GatewayEndpoints.StampFleetRolesAndFold"/> - the push store nulls the resolved facts at ingest so only
/// this Gateway decides them - which is AFTER both callers have folded their voice verdict. So the stamp replaces that
/// verdict with <see cref="VoiceDisplayFold.HeldDisplay"/>, and these drive the stamp rather than the fold, because a
/// parameter on the fold would be passed a default false by everything in the product.
/// </summary>
public sealed class VoiceHeldSessionStampTests
{
    private static (SessionDto Owner, SessionDto Held) Pair(string heldActivity = "WaitingForInput")
    {
        var owner = new SessionDto
        {
            SessionId = "architect-1",
            ActivityState = "WaitingForInput",
            VoiceMode = true,
            VoiceAudioReady = true,
        };
        var held = new SessionDto
        {
            SessionId = "worker-1",
            ActivityState = heldActivity,
            IsControlled = true,
            ControllerSessionId = owner.SessionId,
        };
        return (owner, held);
    }

    private static void Stamp(params SessionDto[] sessions)
    {
        var all = new List<SessionDto>(sessions);
        GatewayEndpoints.StampFleetRolesAndFold(all, all);
    }

    [Fact]
    public void ASessionALiveSessionOwns_IsStampedHeld_AndOffersNothing()
    {
        var (owner, held) = Pair();
        // The verdict the earlier fold would have produced for a non-voice session: the "off" card whose whole content
        // is the Switch button. This is what the stamp has to replace.
        held.VoiceDisplay = VoiceDisplayFold.Fold(voiceMode: false, agentWorking: false, hasAudio: false,
            generating: false, unavailable: null, nothingToNarrate: false);
        Assert.Equal("off", held.VoiceDisplay.Kind);

        Stamp(owner, held);

        Assert.True(held.HasLiveSupervisor);
        Assert.Equal(VoiceDisplayKinds.Held, held.VoiceDisplay!.Kind);
        Assert.False(held.VoiceDisplay.CanPlay);
        Assert.False(held.VoiceDisplay.CanGenerate);
        Assert.NotEqual("", held.VoiceDisplay.Label);
        Assert.NotEqual("", held.VoiceDisplay.Message);
    }

    [Fact]
    public void TheOwnersOwnSession_IsUntouched()
    {
        // THE NEGATIVE CONTROL. Without it this whole change could be stamping "held" over the entire fleet and every
        // assertion above would still pass. The Architect nobody holds keeps its playable clip.
        var (owner, held) = Pair();
        owner.VoiceDisplay = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true,
            generating: false, unavailable: null, nothingToNarrate: false);

        Stamp(owner, held);

        Assert.False(owner.HasLiveSupervisor);
        Assert.Equal("ready", owner.VoiceDisplay!.Kind);
        Assert.True(owner.VoiceDisplay.CanPlay);
    }

    [Fact]
    public void HeldOutranksAPlayableClip()
    {
        // The one place the voice screen deliberately takes audio away from a listener. A held session that was
        // hand-marked into voice mode - which the product still allows, and which the off-direction sweep is supposed
        // to undo - has a clip made from a turn the user never asked to hear. Saying who owns the session beats
        // offering him that clip.
        var (owner, held) = Pair();
        held.VoiceMode = true;
        held.VoiceAudioReady = true;
        held.VoiceDisplay = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true,
            generating: false, unavailable: null, nothingToNarrate: false);
        Assert.Equal("ready", held.VoiceDisplay.Kind);

        Stamp(owner, held);

        Assert.Equal(VoiceDisplayKinds.Held, held.VoiceDisplay!.Kind);
        Assert.False(held.VoiceDisplay.CanPlay);
    }

    [Fact]
    public void WhenTheOwningSessionHasExited_TheSessionIsTheUsersAgain_AndIsNotHeld()
    {
        // Supervision is read from LIVENESS, not from the role stamp (SessionOrdering.IsSupervised), so this needs no
        // repair step: the moment the owning session exits, the same worker becomes the user's, is enrolled by the
        // next sweep, and its Voice tab stops showing the held card.
        var (owner, held) = Pair();
        owner.ActivityState = "Exited";
        held.VoiceDisplay = VoiceDisplayFold.Fold(voiceMode: false, agentWorking: false, hasAudio: false,
            generating: false, unavailable: null, nothingToNarrate: false);

        Stamp(owner, held);

        Assert.False(held.HasLiveSupervisor);
        Assert.NotEqual(VoiceDisplayKinds.Held, held.VoiceDisplay!.Kind);
    }
}
