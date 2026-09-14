using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The gateway-offline floor (owner's ruling, 2026-07-19). The desktop rail renders the Gateway's stamped
/// colour while the tunnel is Connected, but when the Gateway is unreachable the stamp is frozen stale, so
/// the Director - the one surface that HOSTS the session and sees its terminal - paints the single fact it
/// owns firsthand: blue when the agent is producing output, red when it is idle. Two colours only; it never
/// runs the Gateway fold locally (which would wedge every voice-mode session yellow, since VoiceAudioReady is
/// a Gateway-only fact the Director cannot know). Design: docs/new_architecture/sessions.html.
/// </summary>
public sealed class OfflineFloorRailColorTests
{
    // ----- ONLINE: render the Gateway stamp verbatim, compute nothing (unchanged behaviour) -----

    [Theory]
    [InlineData("blue")]
    [InlineData("red")]
    [InlineData("yellow")]
    [InlineData("grey")]
    [InlineData("orange")]
    public void Online_RendersGatewayStampVerbatim_RegardlessOfLocalActivity(string stamp)
    {
        // Even a locally-working session shows the Gateway's stamp when online - e.g. yellow "preparing
        // voice", which the Director could never compute for itself. A present stamp renders verbatim whether
        // or not the tunnel has settled.
        Assert.Equal(stamp, SessionViewModel.RailColor(
            gatewayOffline: false, gatewayStamp: stamp, localActivity: ActivityState.Working, gatewaySettled: true));
        Assert.Equal(stamp, SessionViewModel.RailColor(
            gatewayOffline: false, gatewayStamp: stamp, localActivity: ActivityState.Working, gatewaySettled: false));
    }

    [Fact]
    public void Online_NoStamp_NotYetSettled_IsNeutralUnknown()
    {
        // The tunnel just connected and the first push has not arrived yet - the normal warm-up. Show the
        // neutral placeholder, not an alarm.
        Assert.Equal("unknown", SessionViewModel.RailColor(
            gatewayOffline: false, gatewayStamp: null, localActivity: ActivityState.Working, gatewaySettled: false));
    }

    [Fact]
    public void Online_NoStamp_Settled_IsTheMagentaUnstampedSentinel()
    {
        // Connected and settled past the grace, yet still no stamp: the push seam is not delivering (issue
        // #1966). Fail LOUD with the magenta sentinel, never a grey that reads as "parked".
        Assert.Equal(SessionViewModel.UnstampedSentinel, SessionViewModel.RailColor(
            gatewayOffline: false, gatewayStamp: null, localActivity: ActivityState.Working, gatewaySettled: true));
        // Independent of local activity - the desktop is not computing a colour, it is raising an alarm.
        Assert.Equal(SessionViewModel.UnstampedSentinel, SessionViewModel.RailColor(
            gatewayOffline: false, gatewayStamp: null, localActivity: ActivityState.WaitingForInput, gatewaySettled: true));
    }

    // ----- OFFLINE FLOOR: blue when working, red otherwise, ignoring the stale stamp -----

    [Theory]
    [InlineData(ActivityState.Working)]
    [InlineData(ActivityState.Starting)]
    public void Offline_Working_IsBlue(ActivityState state)
    {
        // The stale stamp says yellow (frozen "preparing voice"), but the agent is working -> blue. The offline
        // floor ignores settledness entirely (there is no live Gateway to have settled with).
        Assert.Equal("blue", SessionViewModel.RailColor(
            gatewayOffline: true, gatewayStamp: "yellow", localActivity: state, gatewaySettled: false));
    }

    [Theory]
    [InlineData(ActivityState.WaitingForInput)]
    [InlineData(ActivityState.WaitingForPerm)]
    [InlineData(ActivityState.Idle)]
    [InlineData(ActivityState.Exited)]
    public void Offline_NotWorking_IsRed(ActivityState state)
    {
        // Stale stamp says yellow; the agent is idle/waiting -> red. Never yellow: the Director cannot know
        // VoiceAudioReady, so it must not paint "preparing voice".
        Assert.Equal("red", SessionViewModel.RailColor(
            gatewayOffline: true, gatewayStamp: "yellow", localActivity: state, gatewaySettled: false));
    }

    [Fact]
    public void Offline_IgnoresTheStaleStampEntirely()
    {
        // Whatever the Gateway last stamped before it dropped, the offline floor is a pure function of local
        // activity - a working session is blue even if the frozen stamp was red/grey/orange.
        Assert.Equal("blue", SessionViewModel.RailColor(true, "red", ActivityState.Working, gatewaySettled: false));
        Assert.Equal("blue", SessionViewModel.RailColor(true, "grey", ActivityState.Working, gatewaySettled: false));
        Assert.Equal("red", SessionViewModel.RailColor(true, "blue", ActivityState.WaitingForInput, gatewaySettled: false));
    }

    // ----- OFFLINE FLOOR + SNOOZE: an explicit user hold survives a tunnel flap, never flattens to red -----

    [Theory]
    [InlineData(ActivityState.WaitingForInput)]
    [InlineData(ActivityState.WaitingForPerm)]
    [InlineData(ActivityState.Idle)]
    public void Offline_Held_IdleSession_StaysSnoozedGrey_NotRed(ActivityState state)
    {
        // The user snoozed this session; the Director caches that as Session.OnHold (a Gateway-owned fact),
        // and the Gateway last stamped it snoozed-grey. When the tunnel flaps, the floor must KEEP the snooze,
        // not repaint it red - otherwise the snooze "does not stick" every time the tunnel reconnects.
        Assert.Equal("grey", SessionViewModel.RailColor(
            gatewayOffline: true, gatewayStamp: "grey", localActivity: state, gatewaySettled: false, isHeld: true));
    }

    [Fact]
    public void Offline_Held_NoStamp_FallsBackToGrey()
    {
        // Held but somehow no frozen stamp: still render snoozed-grey, never red.
        Assert.Equal("grey", SessionViewModel.RailColor(
            gatewayOffline: true, gatewayStamp: null, localActivity: ActivityState.Idle, gatewaySettled: false, isHeld: true));
    }

    [Theory]
    [InlineData(ActivityState.Working)]
    [InlineData(ActivityState.Starting)]
    public void Offline_Held_ButWorking_IsStillBlue(ActivityState state)
    {
        // Working retires a snooze (the Gateway edge), so a held session that is producing output is blue -
        // working always wins over the cached hold.
        Assert.Equal("blue", SessionViewModel.RailColor(
            gatewayOffline: true, gatewayStamp: "grey", localActivity: state, gatewaySettled: false, isHeld: true));
    }

    [Fact]
    public void Offline_NotHeld_IdleSession_IsStillRed()
    {
        // The carve-out is ONLY for an explicit hold; an ordinary idle session with no snooze stays red.
        Assert.Equal("red", SessionViewModel.RailColor(
            gatewayOffline: true, gatewayStamp: "grey", localActivity: ActivityState.WaitingForInput, gatewaySettled: false, isHeld: false));
    }
}
