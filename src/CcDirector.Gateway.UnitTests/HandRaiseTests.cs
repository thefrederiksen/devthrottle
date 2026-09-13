using System;
using System.Collections.Generic;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The raised hand (issue #2662) - the half of the supervised rule that makes the quiet safe.
///
/// Suppressing a supervised session's attention only works if something else is listening. Until this
/// existed nothing was: <c>NeedsManager</c> was declared on the wire with zero writers and zero readers, so a
/// blocked worker could reach neither the owner (by design) nor its supervisor (by omission).
/// </summary>
public sealed class HandRaiseTests
{
    private static readonly TenantId T = TenantId.Local;

    [Fact]
    public void ARaisedHandCarriesTheWorkersOwnWords()
    {
        var reg = new HandRaiseRegistry();

        reg.Raise(T, "w1", "The spec says merge on green but there is no green build for this repo - which do I follow?");

        var hand = reg.Get(T, "w1");
        Assert.NotNull(hand);
        Assert.Contains("which do I follow?", hand!.Reason);
    }

    [Fact]
    public void RaisingAgainReplacesTheReason_ratherThanStacking()
    {
        // A worker has ONE current blocker. Stacking would leave a supervisor reading a history of things
        // that are no longer true, with no way to tell which still matters.
        var reg = new HandRaiseRegistry();
        reg.Raise(T, "w1", "first blocker");

        reg.Raise(T, "w1", "second blocker");

        Assert.Equal("second blocker", reg.Get(T, "w1")!.Reason);
    }

    [Fact]
    public void ClearingAHandThatIsNotUp_isASuccess_notAnError()
    {
        // Idempotent on purpose: the fold lowers hands on its own, so a supervisor answering a worker must
        // never fail because the worker's turn ended first. A throw here would make answering a worker a
        // race the supervisor can lose.
        var reg = new HandRaiseRegistry();

        Assert.False(reg.Clear(T, "never-raised"));
        Assert.Null(reg.Get(T, "never-raised"));
    }

    [Fact]
    public void OneAccountsHandsAreInvisibleToAnother()
    {
        // Tenant partitioning, asserted rather than assumed. A raised hand names a session id, and session
        // ids are not globally unique across accounts - so a shared map would let one account read another's
        // worker problems, and clear them.
        var reg = new HandRaiseRegistry();
        var other = new TenantId("acct-" + Guid.NewGuid().ToString("N"));

        reg.Raise(T, "same-id", "mine");

        Assert.Null(reg.Get(other, "same-id"));
        Assert.False(reg.Clear(other, "same-id"));
        Assert.NotNull(reg.Get(T, "same-id"));
    }

    [Fact]
    public void PruningDropsHandsForSessionsThatAreGone_andKeepsTheLiveOnes()
    {
        var reg = new HandRaiseRegistry();
        reg.Raise(T, "alive", "still needed");
        reg.Raise(T, "gone", "long over");

        var dropped = reg.PruneNotLive(T, new HashSet<string>(StringComparer.Ordinal) { "alive" });

        Assert.Equal(1, dropped);
        Assert.NotNull(reg.Get(T, "alive"));
        Assert.Null(reg.Get(T, "gone"));
    }

    // ===================================================================================================
    // THE OWNER NEVER SEES IT. This is the rule that would decay silently: a raised hand is a signal
    // between two sessions, and the moment it gets a branch in the colour, the label or the triage bucket
    // it has put a worker's problem back in his queue - which is the whole thing the supervised rule
    // exists to stop.
    // ===================================================================================================

    [Fact]
    public void ARaisedHandChangesNothingTheOwnerLooksAt()
    {
        var worker = new SessionDto
        {
            SessionId = "w1",
            ActivityState = "Working",
            IsControlled = true,
            ControllerSessionId = "mgr",
            SessionRole = SessionRoles.Worker,
            NeedsManager = true,
            NeedsManagerReason = "I need a decision on the migration",
        };

        // Identical in every respect to the same session with its hand down.
        var handDown = new SessionDto
        {
            SessionId = "w1",
            ActivityState = "Working",
            IsControlled = true,
            ControllerSessionId = "mgr",
            SessionRole = SessionRoles.Worker,
        };

        Assert.Equal(SessionOrdering.EffectiveColor(handDown), SessionOrdering.EffectiveColor(worker));
        Assert.Equal(SessionOrdering.StateLabel(handDown), SessionOrdering.StateLabel(worker));
        Assert.Equal(SessionOrdering.Classify(handDown), SessionOrdering.Classify(worker));
    }

    [Fact]
    public void ARaisedHandOnAStoppedWorkerStillChangesNothingTheOwnerLooksAt()
    {
        // The same assertion at the other end of the ladder. A stopped supervised session is parked, and a
        // hand must not be able to un-park it - that would be a worker reaching the owner through the back
        // door, which the design says must be impossible by construction rather than by good manners.
        var stopped = new SessionDto
        {
            SessionId = "w1",
            ActivityState = "WaitingForInput",
            IsControlled = true,
            ControllerSessionId = "mgr",
            SessionRole = SessionRoles.Worker,
            HasLiveSupervisor = true,
            NeedsManager = true,
            NeedsManagerReason = "still blocked",
        };

        Assert.Equal("supporting", SessionOrdering.EffectiveColor(stopped));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(stopped));
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(stopped));
    }

    [Fact]
    public void IsWorkingSession_isTheSameAnswerTheLadderUses()
    {
        // The fold lowers a hand using this, so it must agree with the rule the whole ladder is built on.
        // A second definition of "working" is the defect class SessionOrdering's history is made of.
        var working = new SessionDto { SessionId = "a", ActivityState = "Working" };
        var stopped = new SessionDto { SessionId = "b", ActivityState = "WaitingForInput" };

        Assert.True(SessionOrdering.IsWorkingSession(working));
        Assert.False(SessionOrdering.IsWorkingSession(stopped));
        Assert.Equal("blue", SessionOrdering.EffectiveColor(working));
    }
}
