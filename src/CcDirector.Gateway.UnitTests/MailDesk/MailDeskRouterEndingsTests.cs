using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.MailDesk;
using Xunit;
using static CcDirector.Gateway.Tests.MailDesk.MailDeskRoutingRig;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// A ROUTED SESSION ENDED ON ITS HISTORY RECORD (phase 4 contract, section 3.2 step 3, as corrected on 20 October 2026).
/// A Director reaps a session whose agent exits cleanly, and a routed session is started with AutoDismiss, so its row
/// leaves the session list: the history record's ending is the evidence. <c>closed</c>, <c>finished</c> and
/// <c>director-stopped</c> end it at once; <c>interrupted</c>, which the Gateway concludes from a silent Director and
/// reopens when the session reappears, does not - nor does no record at all - until 24 hours after the start.
/// The starter, the session list and the history are fakes; the clock is injected.
/// </summary>
public sealed class MailDeskRouterEndingsTests : IAsyncLifetime
{
    private const string BoxA = "qa-one@example.com";
    private MailDeskRoutingRig _rig = null!;

    public Task InitializeAsync()
    {
        _rig = new MailDeskRoutingRig();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    /// <summary>One email routed to qa-books/clerk and given to a session, which then leaves the session list (reaped),
    /// and a second email for the same seat arriving after. Returns the first decision and its session.</summary>
    private async Task<(MailDeskDecisionEntity Held, string Session)> SessionReapedWithAnotherEmailWaitingAsync()
    {
        _rig.AddMailDeskTrigger();
        var channel = _rig.Channel(BoxA);
        var held = _rig.Decide(channel, "k-1", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 30);
        await _rig.RouteAsync();
        var session = _rig.Row(held.Id).SessionId!;
        Assert.False(_rig.Sessions.ContainsKey(session));
        _rig.Decide(channel, "k-2", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 20);
        return (held, session);
    }

    [Theory]
    [InlineData(SessionHistoryEndings.Closed)]
    [InlineData(SessionHistoryEndings.Finished)]
    [InlineData(SessionHistoryEndings.DirectorStopped)]
    public async Task RouteAsync_NoRowAndAFinalHistoryEnding_IsEndedAtOnce_ReroutedOnceAndTheSeatIsFree(string ending)
    {
        // Arrange
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.Endings[session] = ending;

        // Act: five minutes later, far inside the 24 hours.
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert: re-routed once, and one new session takes both emails in the same run.
        Assert.Equal((1, 0), (run.Rerouted, run.ToOwner));
        Assert.Equal(2, _rig.Starts.Count);
        Assert.Equal(2, _rig.Starts[1].Request.FactoryCheck!.Count);
        var row = _rig.Row(held.Id);
        Assert.Equal((1, MailDeskWords.RoutedStates.Open), (row.RerouteCount, row.State));
        Assert.NotEqual(session, row.SessionId);

        // Act: the next run, the new session still running (no row yet, no history record).
        _rig.Now = _rig.Now.AddMinutes(5);
        var next = await _rig.RouteAsync();

        // Assert: once only - nothing more is re-routed and no third session is started.
        Assert.Equal((0, 0), (next.Rerouted, next.ToOwner));
        Assert.Equal(2, _rig.Starts.Count);
        Assert.Equal(1, _rig.Row(held.Id).RerouteCount);
    }

    [Theory]
    [InlineData(SessionHistoryEndings.Closed)]
    [InlineData(SessionHistoryEndings.Finished)]
    [InlineData(SessionHistoryEndings.DirectorStopped)]
    public async Task RouteAsync_SeatsLastSessionSaidDoneThenWasReaped_WithAFinalHistoryEnding_StartsTheNewEmailAtOnce(string ending)
    {
        // Arrange: the session says done on its only email, so no open row names it, and the Director reaps it.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        Assert.True(_rig.Routed.Done(MailDeskRoutingRig.Account, _rig.Row(held.Id).Id, session, "paid", _rig.Now).Succeeded);
        _rig.Endings[session] = ending;

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert: the seat is free, so the waiting email is started now, not after 24 hours.
        Assert.Equal(0, run.Waiting);
        Assert.Equal(2, _rig.Starts.Count);
        Assert.Equal(1, _rig.Starts[1].Request.FactoryCheck!.Count);
    }

    [Theory]
    [InlineData(6)]           // just past the start grace
    [InlineData(23 * 60)]     // just inside the 24 hours
    public async Task RouteAsync_NoRowAndAnInterruptedEnding_IsNotEndedBefore24Hours(int minutes)
    {
        // Arrange: the Gateway concluded "interrupted" from a silent Director - a backup takeover looks the same.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.Endings[session] = SessionHistoryEndings.Interrupted;

        // Act
        _rig.Now = _rig.Now.AddMinutes(minutes);
        var run = await _rig.RouteAsync();

        // Assert: the held email stays with its session, and the new one waits.
        Assert.Equal((0, 0), (run.Rerouted, run.ToOwner));
        var row = _rig.Row(held.Id);
        Assert.Equal((session, 0, MailDeskWords.RoutedStates.Open), (row.SessionId, row.RerouteCount, row.State));
        Assert.Single(_rig.Starts);
        Assert.Equal(1, run.Waiting);
    }

    [Fact]
    public async Task RouteAsync_NoRowAndAnInterruptedEnding_IsEndedAfter24Hours()
    {
        // Arrange
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.Endings[session] = SessionHistoryEndings.Interrupted;

        // Act
        _rig.Now = _rig.Now.Add(MailDeskRouter.UnseenSessionEndsAfter);
        var run = await _rig.RouteAsync();

        // Assert
        Assert.Equal(1, run.Rerouted);
        Assert.Equal(2, _rig.Starts.Count);
        Assert.Equal(1, _rig.Row(held.Id).RerouteCount);
    }

    [Fact]
    public async Task RouteAsync_NoRowAndAHistoryRecordWithNoEnding_IsNotEndedBefore24Hours()
    {
        // Arrange: the record exists and the session has not ended - only the list lost sight of it.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.Endings[session] = null;

        // Act
        _rig.Now = _rig.Now.AddMinutes(30);
        var run = await _rig.RouteAsync();

        // Assert
        Assert.Equal(0, run.Rerouted);
        Assert.Equal(session, _rig.Row(held.Id).SessionId);
        Assert.Single(_rig.Starts);
    }

    [Fact]
    public async Task RouteAsync_ManySessionsAndRows_AsksForTheHistoryEndingsOnceForEverySessionItReads()
    {
        // Arrange: qa-books/clerk holds two emails on one session, qa-support/desk one on another; then one more email
        // arrives for each seat (the busy-seat rule reads each seat's newest session).
        _rig.AddMailDeskTrigger();
        var channel = _rig.Channel(BoxA);
        _rig.Decide(channel, "k-1", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 40);
        _rig.Decide(channel, "k-2", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 35);
        _rig.Decide(channel, "k-3", MailDeskWords.Decisions.Route, Support, Desk, minutesAgo: 30);
        await _rig.RouteAsync();
        var sessions = _rig.Rows().Select(r => r.SessionId!).Distinct().ToList();
        Assert.Equal(2, sessions.Count);
        _rig.Decide(channel, "k-4", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 20);
        _rig.Decide(channel, "k-5", MailDeskWords.Decisions.Route, Support, Desk, minutesAgo: 10);
        _rig.EndingsAsked.Clear();

        // Act
        _rig.Now = _rig.Now.AddMinutes(10);
        await _rig.RouteAsync();

        // Assert: one question, holding both sessions.
        var asked = Assert.Single(_rig.EndingsAsked);
        Assert.Equal(sessions.OrderBy(s => s), asked.OrderBy(s => s));
    }

    [Fact]
    public async Task NewMailDeskRouter_AsGatewayHostWiresIt_ReadsTheEndingFromTheSessionHistoryStore()
    {
        // Arrange: the router built by GatewayHost's own factory, over the real history store on the rig's database.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.UseGatewayHostWiring(new SessionHistoryStore(_rig.Db));
        WriteHistoryRecord(session, SessionHistoryEndings.Finished);

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert: ended on the record - which only happens when the host passes the store's endings to the router.
        Assert.Equal(1, run.Rerouted);
        Assert.Equal(1, _rig.Row(held.Id).RerouteCount);
        Assert.Equal(2, _rig.Starts.Count);
    }

    [Fact]
    public async Task NewMailDeskRouter_AsGatewayHostWiresIt_AnInterruptedRecordDoesNotEndTheSession()
    {
        // Arrange: the control for the test above - the same wiring, an ending that is not evidence.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.UseGatewayHostWiring(new SessionHistoryStore(_rig.Db));
        WriteHistoryRecord(session, SessionHistoryEndings.Interrupted);

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert
        Assert.Equal(0, run.Rerouted);
        Assert.Equal(session, _rig.Row(held.Id).SessionId);
        Assert.Single(_rig.Starts);
    }

    /// <summary>The session's history record in the rig's account, as the Gateway keeps it, with this ending.</summary>
    private void WriteHistoryRecord(string sessionId, string ending)
    {
        using var ctx = _rig.Db.CreateContext(MailDeskRoutingRig.Account);
        ctx.SessionHistory.Add(new SessionHistoryEntity
        {
            TenantId = ctx.ActiveTenant!,
            SessionId = sessionId,
            DirectorId = DirectorA,
            MachineName = MachineA,
            StartedAtUtc = _rig.Now.AddMinutes(-30),
            LastSeenUtc = _rig.Now,
            EndingKind = ending,
            EndedAtUtc = _rig.Now,
        });
        ctx.SaveChanges();
    }
}
