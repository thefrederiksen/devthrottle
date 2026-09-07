using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// "Is this Director connected, and what is it running?" is ONE read (issue #2722).
///
/// Asking the two questions separately is a check followed by an action, and the gap between them has a
/// specific shape: a Director that disconnects in between passes the connected check and then contributes
/// no sessions, so the caller sees an EMPTY fleet. Empty is exactly what a Director that has finished
/// looks like, so "nothing was running" becomes indistinguishable from "I could not see it" - and the
/// caller here is the workspace capture, whose whole reason for refusing a disconnected Director is that
/// it must never write down the first when it means the second.
///
/// The four answers cannot be tested by racing a disconnect against a read - a timing test whose failure
/// direction is "pass" proves nothing. What IS tested is that the two facts come back together and agree,
/// on every state the store can be in.
/// </summary>
public sealed class ConnectedFleetIsOneReadTests
{
    private const string DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1";

    private static SessionDto Session(string id) => new()
    {
        SessionId = id,
        DirectorId = DirectorId,
        Agent = "ClaudeCode",
        RepoPath = @"D:\ReposFred\devthrottle_internal",
        Status = "Running",
        ActivityState = "WaitingForInput",
        LastActivityAt = DateTime.UtcNow,
    };

    [Fact]
    public void A_connected_Director_answers_connected_with_its_sessions()
    {
        var store = new PushedSessionStore();
        store.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(store.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1,
            new[] { Session("a"), Session("b") }));

        var (observation, sessions) = store.ConnectedFleet(TenantId.Local, DirectorId);

        Assert.Equal(FleetObservation.Observed, observation);
        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public void A_connected_Director_with_no_sessions_answers_connected_and_empty()
    {
        // This is the case the pair exists for. Empty here MEANS empty, because it arrives with
        // "connected" beside it from the same read - a capture may write that down.
        var store = new PushedSessionStore();
        store.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(store.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, Array.Empty<SessionDto>()));

        var (observation, sessions) = store.ConnectedFleet(TenantId.Local, DirectorId);

        Assert.Equal(FleetObservation.Observed, observation);
        Assert.Empty(sessions);
    }

    [Fact]
    public void A_disconnected_Director_answers_not_connected_rather_than_empty()
    {
        var store = new PushedSessionStore();
        store.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(store.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[] { Session("a") }));
        Assert.True(store.UnregisterConnection(TenantId.Local, DirectorId, "conn-1"));

        var (observation, sessions) = store.ConnectedFleet(TenantId.Local, DirectorId);

        // Not "no sessions" - NOT CONNECTED. The distinction is the whole point: this Director had a
        // session a moment ago and the caller must not record that it had none.
        Assert.Equal(FleetObservation.NotConnected, observation);
        Assert.Empty(sessions);
    }

    [Fact]
    public void A_Director_that_has_connected_but_never_pushed_is_not_a_fleet_answer_either()
    {
        // Registered, no snapshot yet. Reading this as "connected with nothing running" would let a
        // capture taken in the second before the first push record an empty fleet.
        var store = new PushedSessionStore();
        store.RegisterConnection(TenantId.Local, DirectorId, "conn-1");

        var (observation, sessions) = store.ConnectedFleet(TenantId.Local, DirectorId);

        // ITS OWN ANSWER, not "not connected": it IS connected, it has simply not said anything yet, and
        // collapsing the two would make a capture taken in that second a genuine zero-seat record.
        Assert.Equal(FleetObservation.ConnectedButSilent, observation);
        Assert.Empty(sessions);
    }

    [Fact]
    public void A_Director_this_Gateway_has_never_seen_answers_not_connected()
    {
        var store = new PushedSessionStore();
        var (observation, sessions) = store.ConnectedFleet(TenantId.Local, "00000000-0000-0000-0000-000000000000");

        Assert.Equal(FleetObservation.Unknown, observation);
        Assert.Empty(sessions);
    }

    [Fact]
    public void One_tenant_cannot_read_another_tenants_fleet()
    {
        var store = new PushedSessionStore();
        var mine = new TenantId("mine");
        var theirs = new TenantId("theirs");

        store.RegisterConnection(theirs, DirectorId, "conn-1");
        Assert.True(store.ApplySnapshot(theirs, DirectorId, "conn-1", 1, new[] { Session("a") }));

        var (observation, sessions) = store.ConnectedFleet(mine, DirectorId);

        Assert.Equal(FleetObservation.Unknown, observation);
        Assert.Empty(sessions);
    }
}
