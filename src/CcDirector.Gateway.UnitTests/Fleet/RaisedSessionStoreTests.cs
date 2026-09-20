using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// The list of raised sessions (the Fleet Manager Improvement mission, phase 1), against a real database file: who
/// is raised, that the list survives a restart of the store, that raised follows the Fleet Manager mark when it moves
/// and when it clears, and that an entry ends with its session.
///
/// The mark is a variable the test moves, read through the same delegate production hands the store - so "the mark
/// moved" here is exactly what the store sees when the account's setting changes. The routes and the guard that
/// consult this list are proven through a booted Gateway in <c>RaisedSessionHostTests</c> (the parked suite).
/// </summary>
public sealed class RaisedSessionStoreTests : IDisposable
{
    private static readonly TenantId TenantA = new("acct-raised-a");
    private static readonly TenantId TenantB = new("acct-raised-b");
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string First = "bbbbbbbb-0000-4000-8000-000000000001";
    private const string Second = "bbbbbbbb-0000-4000-8000-000000000002";
    private const string Owner = "device browser dev-1";

    private readonly GatewayDbTestHarness _harness = new();
    private string? _markA;

    public void Dispose() => _harness.Dispose();

    private RaisedSessionStore Open() => new(_harness.Open(), tenant => tenant == TenantA ? _markA : null);

    [Fact]
    public void IsRaised_NothingStored_IsFalse()
    {
        Assert.False(Open().IsRaised(TenantA, First));
        Assert.Empty(Open().RaisedIds(TenantA));
    }

    [Fact]
    public void Raise_ByTheOwner_IsRaised_InThatAccountOnly_AndSurvivesARestartOfTheStore()
    {
        var stored = Open().Raise(TenantA, First.ToUpperInvariant(), Owner, Now);

        Assert.Equal(new RaisedSession(First, RaisedSessionSources.Owner, Owner, Now), stored);

        // A NEW store over the same database file: nothing is held in memory, so this is the restart.
        var reopened = Open();
        Assert.True(reopened.IsRaised(TenantA, First));
        Assert.Equal(new[] { First }, reopened.RaisedIds(TenantA));
        Assert.Equal(new[] { stored }, reopened.List(TenantA));
        // The same session id asked about from another account is not raised: the list is the account's.
        Assert.False(reopened.IsRaised(TenantB, First));
        Assert.Empty(reopened.List(TenantB));
    }

    [Fact]
    public void Lower_RemovesTheEntry_AndASecondLowerRemovesNothing()
    {
        var store = Open();
        store.Raise(TenantA, First, Owner, Now);

        Assert.True(store.Lower(TenantA, First));

        Assert.False(store.IsRaised(TenantA, First));
        Assert.Empty(store.List(TenantA));
        Assert.False(store.Lower(TenantA, First));
    }

    [Fact]
    public void Raise_NotASessionId_Throws()
    {
        Assert.Throws<ArgumentException>(() => Open().Raise(TenantA, "not-an-id", Owner, Now));
        Assert.False(Open().IsRaised(TenantA, "not-an-id"));
    }

    [Fact]
    public void FollowMark_SetByTheOwner_RaisesTheMarkedSession()
    {
        var store = Open();
        _markA = First;

        var change = store.FollowMark(TenantA, First, raise: true, Owner, Now);

        Assert.Equal(First, change.Raised);
        Assert.Empty(change.Lowered);
        Assert.True(store.IsRaised(TenantA, First));
        Assert.Equal(RaisedSessionSources.FleetManagerMark, Assert.Single(store.List(TenantA)).Source);
    }

    /// <summary>The mark moves to another session by the owner's hand: the first is lowered, the second raised.</summary>
    [Fact]
    public void FollowMark_MarkMovesByTheOwner_LowersTheOldAndRaisesTheNew()
    {
        var store = Open();
        _markA = First;
        store.FollowMark(TenantA, First, raise: true, Owner, Now);

        _markA = Second;
        var change = store.FollowMark(TenantA, Second, raise: true, Owner, Now.AddMinutes(1));

        Assert.Equal(new[] { First }, change.Lowered);
        Assert.Equal(Second, change.Raised);
        Assert.False(store.IsRaised(TenantA, First));
        Assert.True(store.IsRaised(TenantA, Second));
        Assert.Equal(new[] { Second }, store.List(TenantA).Select(r => r.SessionId));
    }

    [Fact]
    public void FollowMark_MarkCleared_LowersTheMarkedSession()
    {
        var store = Open();
        _markA = First;
        store.FollowMark(TenantA, First, raise: true, Owner, Now);

        _markA = null;
        var change = store.FollowMark(TenantA, null, raise: false, Owner, Now.AddMinutes(1));

        Assert.Equal(new[] { First }, change.Lowered);
        Assert.Null(change.Raised);
        Assert.False(store.IsRaised(TenantA, First));
        Assert.Empty(store.List(TenantA));
    }

    /// <summary>
    /// THE BACKSTOP. The mark is written in six places, and one of them may forget to tell this store. A mark entry
    /// counts only while its session IS the mark, so a writer that forgets leaves the old session NOT raised - the
    /// row is still there, and it grants nothing.
    /// </summary>
    [Fact]
    public void IsRaised_MarkMovedWithoutTellingTheStore_TheOldMarkEntryGrantsNothing()
    {
        var store = Open();
        _markA = First;
        store.FollowMark(TenantA, First, raise: true, Owner, Now);

        _markA = Second;

        Assert.Single(store.List(TenantA));
        Assert.False(store.IsRaised(TenantA, First));
        Assert.False(store.IsRaised(TenantA, Second));
        Assert.Empty(store.RaisedIds(TenantA));
    }

    /// <summary>
    /// A SESSION CANNOT RAISE ITSELF BY MARKING ITSELF. Any session key may set the mark; the caller passes
    /// <c>raise: false</c> for every caller but the owner's own device, and then the newly marked session gets no
    /// entry - and the entry the mark had granted the old one is removed, so marking the old one AGAIN later does not
    /// bring it back to life.
    /// </summary>
    [Fact]
    public void FollowMark_SetByASessionKey_RaisesNobody_AndAnOldEntryNeverComesBack()
    {
        var store = Open();
        _markA = First;
        store.FollowMark(TenantA, First, raise: true, Owner, Now);

        _markA = Second;
        var moved = store.FollowMark(TenantA, Second, raise: false, "session " + Second, Now.AddMinutes(1));
        Assert.Null(moved.Raised);
        Assert.Equal(new[] { First }, moved.Lowered);
        Assert.False(store.IsRaised(TenantA, Second));

        _markA = First;
        var back = store.FollowMark(TenantA, First, raise: false, "session " + First, Now.AddMinutes(2));
        Assert.Null(back.Raised);
        Assert.False(store.IsRaised(TenantA, First));
        Assert.Empty(store.List(TenantA));
    }

    /// <summary>An entry the owner made himself does not depend on the mark, and the mark moving does not remove it.</summary>
    [Fact]
    public void FollowMark_LeavesAnEntryTheOwnerMadeHimself()
    {
        var store = Open();
        store.Raise(TenantA, First, Owner, Now);
        _markA = Second;

        var change = store.FollowMark(TenantA, Second, raise: true, Owner, Now.AddMinutes(1));

        Assert.Empty(change.Lowered);
        Assert.True(store.IsRaised(TenantA, First));
        Assert.True(store.IsRaised(TenantA, Second));
    }

    /// <summary>
    /// A RESTART OR A MOVE: the new Fleet Manager is given its entry when it is started, and it counts for nothing
    /// until the mark actually moves to it. Raised is carried, never multiplied: once the mark has moved, the old one
    /// is not raised.
    /// </summary>
    [Fact]
    public void CarryToSuccessor_FromARaisedFleetManager_CountsOnlyOnceTheMarkHasMoved()
    {
        var store = Open();
        _markA = First;
        store.FollowMark(TenantA, First, raise: true, Owner, Now);

        Assert.True(store.CarryToSuccessor(TenantA, First, Second, Now.AddMinutes(1)));

        Assert.True(store.IsRaised(TenantA, First));
        Assert.False(store.IsRaised(TenantA, Second));

        _markA = Second;

        Assert.False(store.IsRaised(TenantA, First));
        Assert.True(store.IsRaised(TenantA, Second));
    }

    [Fact]
    public void CarryToSuccessor_FromAFleetManagerThatIsNotRaised_CarriesNothing()
    {
        var store = Open();
        _markA = First;

        Assert.False(store.CarryToSuccessor(TenantA, First, Second, Now));

        _markA = Second;
        Assert.False(store.IsRaised(TenantA, Second));
        Assert.Empty(store.List(TenantA));
    }

    [Fact]
    public void EndWithSession_RemovesTheEntry_WhateverGrantedIt()
    {
        var store = Open();
        store.Raise(TenantA, First, Owner, Now);
        _markA = Second;
        store.FollowMark(TenantA, Second, raise: true, Owner, Now);

        Assert.True(store.EndWithSession(TenantA, First));
        Assert.True(store.EndWithSession(TenantA, Second.ToUpperInvariant()));

        Assert.Empty(store.List(TenantA));
        Assert.False(store.IsRaised(TenantA, First));
        Assert.False(store.IsRaised(TenantA, Second));
        Assert.False(store.EndWithSession(TenantA, First));
    }

    [Theory]
    [InlineData(RaisedSessionSources.Owner, null, true)]
    [InlineData(RaisedSessionSources.Owner, Second, true)]
    [InlineData(RaisedSessionSources.FleetManagerMark, First, true)]
    [InlineData(RaisedSessionSources.FleetManagerMark, Second, false)]
    [InlineData(RaisedSessionSources.FleetManagerMark, null, false)]
    [InlineData("a-source-nobody-wrote", First, false)]
    public void IsRaised_TheOneAnswer(string source, string? marked, bool expected)
    {
        var entry = new RaisedSession(First, source, Owner, Now);

        Assert.Equal(expected, RaisedSessions.IsRaised(entry, marked));
        Assert.False(RaisedSessions.IsRaised(null, marked));
    }
}
