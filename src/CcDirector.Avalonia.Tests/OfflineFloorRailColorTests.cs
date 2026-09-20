using Avalonia.Headless.XUnit;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
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
    // WHY [AvaloniaFact]/[AvaloniaTheory] AND NOT [Fact]/[Theory]: SessionViewModel builds
    // its brushes in a static initialiser, and building a brush is an Avalonia property write that verifies
    // it is on the dispatcher's thread. A plain [Fact] gets whatever thread xUnit hands it, so whether that
    // succeeds depends on whether another class in this assembly has already started a headless session - and
    // a static initialiser that throws once stays thrown for the rest of the process. These run ON the
    // dispatcher thread instead. Same reason as SessionRailStateTests, where the accident actually fired.
    // ----- ONLINE: render the Gateway stamp verbatim, compute nothing (unchanged behaviour) -----

    [AvaloniaTheory]
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

    [AvaloniaFact]
    public void Online_NoStamp_NotYetSettled_IsNeutralUnknown()
    {
        // The tunnel just connected and the first push has not arrived yet - the normal warm-up. Show the
        // neutral placeholder, not an alarm.
        Assert.Equal("unknown", SessionViewModel.RailColor(
            gatewayOffline: false, gatewayStamp: null, localActivity: ActivityState.Working, gatewaySettled: false));
    }

    [AvaloniaFact]
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

    [AvaloniaTheory]
    [InlineData(ActivityState.Working)]
    [InlineData(ActivityState.Starting)]
    public void Offline_Working_IsBlue(ActivityState state)
    {
        // The stale stamp says yellow (frozen "preparing voice"), but the agent is working -> blue. The offline
        // floor ignores settledness entirely (there is no live Gateway to have settled with).
        Assert.Equal("blue", SessionViewModel.RailColor(
            gatewayOffline: true, gatewayStamp: "yellow", localActivity: state, gatewaySettled: false));
    }

    [AvaloniaTheory]
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

    [AvaloniaFact]
    public void Offline_IgnoresTheStaleStampEntirely()
    {
        // Whatever the Gateway last stamped before it dropped, the offline floor is a pure function of local
        // activity - a working session is blue even if the frozen stamp was red/grey/orange.
        Assert.Equal("blue", SessionViewModel.RailColor(true, "red", ActivityState.Working, gatewaySettled: false));
        Assert.Equal("blue", SessionViewModel.RailColor(true, "grey", ActivityState.Working, gatewaySettled: false));
        Assert.Equal("red", SessionViewModel.RailColor(true, "blue", ActivityState.WaitingForInput, gatewaySettled: false));
    }

    // ----- OFFLINE FLOOR + SNOOZE: an explicit user hold survives a tunnel flap, never flattens to red -----

    [AvaloniaTheory]
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

    [AvaloniaFact]
    public void Offline_Held_NoStamp_FallsBackToGrey()
    {
        // Held but somehow no frozen stamp: still render snoozed-grey, never red.
        Assert.Equal("grey", SessionViewModel.RailColor(
            gatewayOffline: true, gatewayStamp: null, localActivity: ActivityState.Idle, gatewaySettled: false, isHeld: true));
    }

    [AvaloniaTheory]
    [InlineData(ActivityState.Working)]
    [InlineData(ActivityState.Starting)]
    public void Offline_Held_ButWorking_IsStillBlue(ActivityState state)
    {
        // Working retires a snooze (the Gateway edge), so a held session that is producing output is blue -
        // working always wins over the cached hold.
        Assert.Equal("blue", SessionViewModel.RailColor(
            gatewayOffline: true, gatewayStamp: "grey", localActivity: state, gatewaySettled: false, isHeld: true));
    }

    [AvaloniaFact]
    public void Offline_NotHeld_IdleSession_IsStillRed()
    {
        // The carve-out is ONLY for an explicit hold; an ordinary idle session with no snooze stays red.
        Assert.Equal("red", SessionViewModel.RailColor(
            gatewayOffline: true, gatewayStamp: "grey", localActivity: ActivityState.WaitingForInput, gatewaySettled: false, isHeld: false));
    }

    // ===== WHAT THE HOVER MAY SAY ABOUT A DOT THE RAIL PAINTED FOR ITSELF =====
    //
    // The floor paints a LIVE local reading of the terminal. SessionDto.StateLabel, in that same moment, is
    // FROZEN on whatever the Gateway last said before the tunnel dropped. The two are about different
    // moments, and the Session Cards mission joined them into one hover - which produced "Working: Snoozed",
    // a row contradicting itself, which is the exact defect class this mission exists to remove.
    //
    // These drive the REAL functions the view model composes - SessionViewModel.RailDotFor, then
    // SessionDotHover.For - against the REAL legend the Gateway serves. What they do NOT drive is a live
    // tunnel drop through MainWindow and GatewayConnectionMonitor: the three getters that bind the floor's
    // inputs are application-wide and not reachable here, so this proves the composition, not the wiring.

    private static string TitleOf(string colour) =>
        SessionColourLegend.Build().Entries.Single(e => e.Colour == colour).Title;

    /// <summary>
    /// THE ONE THE INSPECTOR FOUND, exactly as it found it. The Gateway's last word before the tunnel
    /// dropped was grey, "Snoozed". The agent has since started producing output, which only this Director
    /// can see, so the floor paints blue. Joining the two gave "Working: Snoozed" - a dot and its own hover
    /// naming two different states.
    ///
    /// The hover is the legend's name for the colour the rail is ACTUALLY painting, alone. That is still the
    /// Gateway's word - it is the Gateway's legend - and it is the whole of the owner's ruling: hover the
    /// colour, see what that colour means. The floor is untouched; what was wrong was pairing it with a
    /// label about another moment.
    /// </summary>
    [AvaloniaFact]
    public void Offline_AWorkingSessionWhoseFrozenStampSaysSnoozed_NeverHoversBothAtOnce()
    {
        var dot = SessionViewModel.RailDotFor(
            gatewayOffline: true, gatewayStamp: "grey", localActivity: ActivityState.Working,
            gatewaySettled: false, isHeld: true);

        var hover = SessionDotHover.For(dot, "Snoozed", SessionColourLegend.Build());

        Assert.Equal("blue", dot.Colour);
        Assert.Equal(TitleOf("blue"), hover);
        Assert.DoesNotContain("Snoozed", hover, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same thing for every state the Gateway could have been frozen on. Whatever it last said, a
    /// session the Director can see working hovers the name of the colour it is wearing and nothing else.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("grey", "Snoozed")]
    [InlineData("yellow", "Preparing voice")]
    [InlineData("purple", "Monitor fix round 2 progress")]
    [InlineData("cyan", "Done")]
    [InlineData("orange", "Waiting on a permission")]
    public void Offline_AWorkingSession_NeverHoversTheFrozenLabel(string frozenStamp, string frozenLabel)
    {
        var dot = SessionViewModel.RailDotFor(
            gatewayOffline: true, gatewayStamp: frozenStamp, localActivity: ActivityState.Working,
            gatewaySettled: false, isHeld: false);

        var hover = SessionDotHover.For(dot, frozenLabel, SessionColourLegend.Build());

        Assert.Equal(TitleOf("blue"), hover);
        Assert.DoesNotContain(frozenLabel, hover, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And the other floor colour: an idle session is painted red locally while the frozen label still says
    /// the session was carrying on by itself. "Needs you: Monitor fix round 2 progress" is the same defect
    /// wearing the other pixel.
    /// </summary>
    [AvaloniaFact]
    public void Offline_AnIdleSessionWhoseFrozenStampSaysItWasCarryingOn_HoversOnlyTheColourItWears()
    {
        var dot = SessionViewModel.RailDotFor(
            gatewayOffline: true, gatewayStamp: "purple", localActivity: ActivityState.WaitingForInput,
            gatewaySettled: false, isHeld: false);

        var hover = SessionDotHover.For(dot, "Monitor fix round 2 progress", SessionColourLegend.Build());

        Assert.Equal("red", dot.Colour);
        Assert.Equal(TitleOf("red"), hover);
        Assert.DoesNotContain("Monitor fix", hover, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE FLOOR IS NOT ALWAYS THE RAIL'S OWN WORD, and the difference is what the hover turns on. A held,
    /// idle session offline keeps the Gateway's FROZEN STAMP as its colour - the dot is the Gateway's own
    /// answer again, and the frozen label describes that same answer. So here the two DO belong in one
    /// sentence, and a fix that simply dropped the label everywhere offline would lose it.
    /// </summary>
    [AvaloniaFact]
    public void Offline_AHeldIdleSession_WearsTheGatewaysOwnStamp_SoItsLabelStillBelongsOnTheHover()
    {
        var dot = SessionViewModel.RailDotFor(
            gatewayOffline: true, gatewayStamp: "grey", localActivity: ActivityState.WaitingForInput,
            gatewaySettled: false, isHeld: true);

        Assert.Equal("grey", dot.Colour);
        Assert.True(dot.ColourIsTheGatewaysStamp);
        Assert.Equal($"{TitleOf("grey")}: Snoozed until nine",
            SessionDotHover.For(dot, "Snoozed until nine", SessionColourLegend.Build()));
    }

    /// <summary>
    /// The online dot is the Gateway's stamp by definition, so nothing above narrows what an ordinary
    /// connected row hovers.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("blue")]
    [InlineData("red")]
    [InlineData("grey")]
    [InlineData("purple")]
    public void Online_TheDotIsTheGatewaysStamp_SoItsLabelIsStillAppended(string stamp)
    {
        var dot = SessionViewModel.RailDotFor(
            gatewayOffline: false, gatewayStamp: stamp, localActivity: ActivityState.Working, gatewaySettled: true);

        Assert.True(dot.ColourIsTheGatewaysStamp);
        Assert.Equal($"{TitleOf(stamp)}: Monitor fix round 2 progress",
            SessionDotHover.For(dot, "Monitor fix round 2 progress", SessionColourLegend.Build()));
    }

    /// <summary>
    /// The sentinels and the warm-up placeholder are the rail's own pixels too - the Gateway stamped no
    /// colour at all - so no frozen label is pinned to them either.
    /// </summary>
    [AvaloniaFact]
    public void Online_TheUnstampedSentinelAndTheWarmUpPlaceholder_AreNotTheGatewaysStamp()
    {
        Assert.False(SessionViewModel.RailDotFor(false, null, ActivityState.Working, gatewaySettled: true).ColourIsTheGatewaysStamp);
        Assert.False(SessionViewModel.RailDotFor(false, null, ActivityState.Working, gatewaySettled: false).ColourIsTheGatewaysStamp);
        Assert.False(SessionViewModel.RailDotFor(true, null, ActivityState.Idle, gatewaySettled: false, isHeld: true).ColourIsTheGatewaysStamp);
    }
}
