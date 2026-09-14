using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// WHAT THIS FILE ACTUALLY PROVES: that the shared fold is a FUNCTION - one set of inputs yields one
/// answer, whoever calls it. Despite the name, it does NOT prove that the desktop and the Gateway agree,
/// and it cannot: it lives in Gateway.Tests, imports only <c>CcDirector.Gateway.Contracts</c>, and never
/// touches a line of desktop code. It builds its inputs by hand and asks the fold what it says.
///
/// READ THAT AGAIN BEFORE CITING THIS FILE AS PROOF OF AGREEMENT. Two surfaces agree only if they call the
/// same function AND feed it the same INPUTS. This file pins the first half. The second half is where every
/// bug in this mission actually lived - <see cref="ALiveWorker_IsRecessive_NotRed_OnEveryScreen"/> hand-sets
/// <c>SessionRole = Worker</c>, which is precisely the field the desktop's real producer did not populate
/// (defect 5). So that test was GREEN while the desktop showed RED for the same session, and it had no way
/// to notice: it supplied the missing input itself. A test that injects the value production forgets is the
/// shape this codebase keeps shipping - see the auto-drain, green for fourteen months on an injected state.
///
/// The other half now exists, next door: <see cref="DesktopRoleStampWireProofTests"/> drives a real
/// <c>Session</c> through the real <c>set-resolved-role</c> verb, the real <c>ControlEndpoints.Map</c> and
/// this same fold, hand-setting nothing. Read the two as a pair: HERE is the answer the fold must give;
/// THERE is the desktop actually reaching it through the real wire. Neither is sufficient alone.
///
/// The history that produced these rows: the desktop rail and the phone showed DIFFERENT states for the
/// same session at the same instant - six of thirteen sessions disagreed when measured on the live fleet on
/// 14 July 2026. The desktop hand-rolled three private folds, and two of them had never heard of the hold
/// flag, so a snoozed session rendered a grey strip next to a red "Your Turn" and an hours-long nagging
/// clock, while the phone correctly showed "Snoozed". The rows below are the ACTUAL diverging sessions from
/// that measurement. Anything that re-introduces a second READING of these inputs fails here.
///
/// Design: docs/new_architecture/sessions.html
/// </summary>
public sealed class DesktopGatewayFoldAgreementTests
{
    /// <summary>A snoozed session as the Director reports it: the wingman's raw colour still says "red"
    /// (the session genuinely IS at a turn end), which is exactly what the old rail read.</summary>
    private static SessionDto Snoozed(string activityState) => new()
    {
        SessionId = "s",
        ActivityState = activityState,
        OnHold = true,
        StatusColor = "red",
    };

    [Theory]
    [InlineData("WaitingForInput")] // "Enrichment Facade Eval - Architect", "cc-consult - Perry", ...
    [InlineData("WaitingForPerm")]
    [InlineData("Idle")]
    public void ASnoozedSession_ReadsSnoozed_AndIsNotRed_OnEveryScreen(string activityState)
    {
        var s = Snoozed(activityState);

        // The raw fact the old rail folded on its own, and got wrong:
        Assert.Equal("red", s.StatusColor);

        // The one fold every screen now calls - one answer.
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(s));
        Assert.Equal("grey", SessionOrdering.EffectiveColor(s));
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(s));

        // ...and because the "waiting 10h" nag is gated on the FOLD being red rather than the raw colour,
        // a snoozed session no longer nags. This is the assertion that pins the rail's third fold.
        Assert.NotEqual("red", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void ALiveWorker_IsRecessive_NotRed_OnEveryScreen()
    {
        // The "Tunnel-Only Review - Reviewer 2 (Codex)" row: a Worker parked at a turn end. The Gateway
        // keeps Workers quiet so they surface to their manager rather than the human; the rail painted it
        // red because it cannot see the role. Phase 2b pushes the role down so the rail reaches this same
        // answer - this test pins what that answer must be.
        //
        // NOTE WHAT THIS TEST CANNOT SEE, because it is the whole of defect 5: SessionRole is HAND-SET
        // below. That is the one field the desktop's real producer (ControlEndpoints.Map) did not carry, so
        // this test was green while the rail rendered red for this very session. Supplying the missing input
        // yourself proves the fold, never the pipeline. The pipeline is proved in
        // DesktopRoleStampWireProofTests, which sets nothing by hand - if you change this test, change that
        // one, or you are back to trusting an injected value.
        var codex = new SessionDto
        {
            SessionId = "codex",
            ActivityState = "WaitingForInput",
            StatusColor = "red",
            IsControlled = true,
            ControllerSessionId = "manager",
            SessionRole = SessionRoles.Worker,
            // The resolver's answer, set by hand here because this is a FOLD test. Since 2026-09-13 the seat
            // does not imply this - a Worker whose supervisor has died is the owner's - so the fold has to be
            // told the supervisor is alive, which is precisely the fact the roster row above represents.
            HasLiveSupervisor = true,
        };

        Assert.Equal("supporting", SessionOrdering.EffectiveColor(codex));
        // The label is "Snoozed" (was "Sub-agent") since the owner ruled on 2026-09-02 that a supervised
        // session goes to on-hold when it is not working; the slate dot is unchanged. See the supervised arm
        // in SessionOrdering.EffectiveColor.
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(codex));
    }

    [Fact]
    public void AWorkingSession_ReadsWorking_OnEveryScreen()
    {
        var s = new SessionDto { SessionId = "s", ActivityState = "Working", StatusColor = "blue" };

        Assert.Equal("Working", SessionOrdering.StateLabel(s));
        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void AControlledWorkingSession_IsBlueAndReadsWorking_OnEveryScreen()
    {
        // The "Stable Release - Manager" row (session 107): driven by its Architect and 23 minutes into a
        // real turn, yet the rail painted it slate and labelled it "Sub-agent" - indistinguishable from
        // on-hold or exited, so the owner read a busy session as parked. Owner's ruling, 2026-07-14: if a
        // session is working it is BLUE, no matter what. Nothing outranks working. Who DRIVES a session
        // travels on the rail's role badge; colour says what it is DOING and never who owns it.
        var manager = new SessionDto
        {
            SessionId = "manager-107",
            ActivityState = "Working",
            StatusColor = "blue",
            IsControlled = true,
            ControllerSessionId = "architect",
            SessionRole = SessionRoles.Worker,
        };

        Assert.Equal("blue", SessionOrdering.EffectiveColor(manager));
        Assert.Equal("Working", SessionOrdering.StateLabel(manager));
        Assert.NotEqual(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(manager));
    }

    [Fact]
    public void AHeldSessionThatIsWorking_ReadsWorking()
    {
        // Flipped 14 July 2026, and worth reading closely, because this test contained BOTH the law and
        // the defect at once. It used to be called "AHeldSession_CanNeverAlsoReadWorking" and asserted
        // that a parked-and-working session does NOT read "Working" - while its own comment said, in
        // parentheses: "a session the user snoozed WHILE working ... reads as working, which is correct:
        // it is still working." The comment was right. The assertion was wrong. They sat one line apart
        // and the suite was green.
        //
        // The old test defended itself with "a state the machine cannot produce - belt and braces". That
        // is the trap: if the input really were unreachable the assertion would be untestable either way,
        // so the belt-and-braces bought nothing and cost the law. And the input IS reachable - a snoozed
        // session whose agent starts producing bytes again is exactly this DTO.
        //
        // THE LAW: if it is working, it is blue and it reads "Working". Snooze says "do not nag me about
        // this one when it stops"; it cannot un-work a session that is running right now.
        var held = Snoozed("Working");

        Assert.Equal("blue", SessionOrdering.EffectiveColor(held));
        Assert.Equal("Working", SessionOrdering.StateLabel(held));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(held));
    }

    [Fact]
    public void AHeldSessionThatIsNotWorking_StillReadsSnoozed()
    {
        // The other half: hoisting working to the top must not weaken snooze for the case it was built
        // for - a session that has stopped and that the user has explicitly deferred.
        var held = Snoozed("WaitingForInput");

        Assert.Equal("grey", SessionOrdering.EffectiveColor(held));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(held));
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(held));
    }
}
