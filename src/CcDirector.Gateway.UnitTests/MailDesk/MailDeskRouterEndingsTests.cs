using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Workspaces;
using Xunit;
using static CcDirector.Gateway.Tests.MailDesk.MailDeskRoutingRig;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// A ROUTED SESSION ENDED ON ITS HISTORY RECORD (phase 4 contract, section 3.2 step 3, as corrected on 20 October 2026).
/// A Director reaps a session whose agent exits cleanly, and a routed session is started with AutoDismiss, so its row
/// leaves the session list: the history record's ending is the evidence. <c>closed</c> (a person) and <c>finished</c>
/// (its agent) end it at once; <c>interrupted</c>, which the Gateway concludes from a silent Director and reopens when
/// the session reappears, does not - nor does <c>director-stopped</c>, nor no record at all - until 24 hours after the
/// start. A SMART RESTART's seats are read from its restart record: still owed a Bring back - not ended, the seat busy;
/// brought back - followed by the record's mapping; not to be brought back - ended at once.
/// The starter, the session list, the history and the restart records are fakes; the clock is injected.
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

    // REVIEW OF 4283, FINDING 1: a final ending is never reopened, so a late farewell from a replaced connection (every
    // session of a running Director stamped director-stopped) or a snapshot that missed a session (closed) can sit on a
    // session that is still running. A live row in the session list wins.
    [Theory]
    [InlineData(SessionHistoryEndings.Closed)]
    [InlineData(SessionHistoryEndings.Finished)]
    [InlineData(SessionHistoryEndings.DirectorStopped)]
    public async Task RouteAsync_LiveRowAndAFinalHistoryEnding_IsNotEnded_NotReroutedAndTheSeatStaysBusy(string ending)
    {
        // Arrange: the session is in the list, working, and its history record holds a stale final ending.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.SessionIs(session, alive: true);
        _rig.Endings[session] = ending;

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert: the held email stays with its session, and the new one waits.
        Assert.Equal((0, 0), (run.Rerouted, run.ToOwner));
        var row = _rig.Row(held.Id);
        Assert.Equal((session, 0, MailDeskWords.RoutedStates.Open), (row.SessionId, row.RerouteCount, row.State));
        Assert.Single(_rig.Starts);
        Assert.Equal(1, run.Waiting);
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

    // ---------- review of 4283, finding 2: a Smart Restart brings the session back under a new id ----------

    /// <summary>The session a Smart Restart reopened: a new id, the start's exact name, and the factory and seat given.</summary>
    private SessionDto Restored(string name, string factory = Books, string seat = Clerk, bool alive = true)
    {
        var restored = new SessionDto
        {
            SessionId = _rig.NextSessionId(),
            Name = name,
            MachineName = MachineA,
            Factory = factory,
            FactorySeat = seat,
            // A restore creates on the API surface, never the trigger's.
            OriginSurface = CcDirector.Core.Sessions.SessionOriginSurfaces.Api,
            ActivityState = alive ? "Working" : "Exited",
        };
        _rig.Sessions[restored.SessionId] = restored;
        return restored;
    }

    [Theory]
    [InlineData(SessionHistoryEndings.Closed)]
    [InlineData(SessionHistoryEndings.DirectorStopped)]
    public async Task RouteAsync_EndedByARestartAndALiveSessionWithTheSameNameFactoryAndSeat_IsAdoptedNotRerouted(string ending)
    {
        // Arrange
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.Endings[session] = ending;
        var restored = Restored(_rig.Starts[0].Request.Name!);

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert: the row takes the new id and stays open; nothing re-routed, no second session, the new email waits.
        Assert.Equal((1, 0, 0), (run.Followed, run.Rerouted, run.ToOwner));
        var row = _rig.Row(held.Id);
        Assert.Equal((restored.SessionId, 0, MailDeskWords.RoutedStates.Open), (row.SessionId, row.RerouteCount, row.State));
        Assert.Contains(restored.SessionId, _rig.DecisionRow(held.Id).OutcomeDetail);
        Assert.Single(_rig.Starts);
        Assert.Equal(1, run.Waiting);

        // And the restored session can now answer for the email.
        Assert.True(_rig.Routed.Done(MailDeskRoutingRig.Account, row.Id, restored.SessionId, "paid", _rig.Now).Succeeded);
    }

    [Theory]
    [InlineData(Support, Clerk)]
    [InlineData(Books, Desk)]
    public async Task RouteAsync_LiveSessionWithTheSameNameButAnotherFactoryOrSeat_IsNotAdopted_ReroutedOnce(string factory, string seat)
    {
        // Arrange
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.Endings[session] = SessionHistoryEndings.Closed;
        var other = Restored(_rig.Starts[0].Request.Name!, factory, seat);

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert
        Assert.Equal((0, 1), (run.Followed, run.Rerouted));
        var row = _rig.Row(held.Id);
        Assert.Equal(1, row.RerouteCount);
        Assert.NotEqual(other.SessionId, row.SessionId);
        Assert.Equal(2, _rig.Starts.Count);
    }

    [Fact]
    public async Task RouteAsync_SessionWithTheSameNameThatIsGone_IsNotAdopted_ReroutedOnce()
    {
        // Arrange
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.Endings[session] = SessionHistoryEndings.Closed;
        var gone = Restored(_rig.Starts[0].Request.Name!, alive: false);

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert
        Assert.Equal((0, 1), (run.Followed, run.Rerouted));
        Assert.NotEqual(gone.SessionId, _rig.Row(held.Id).SessionId);
        Assert.Equal(1, _rig.Row(held.Id).RerouteCount);
    }

    [Fact]
    public async Task RouteAsync_FinishedEndingAndALiveSessionWithTheSameName_IsNotAdopted_ReroutedOnce()
    {
        // Arrange: the agent exited on its own; only closed and director-stopped are a restart's endings (contract).
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.Endings[session] = SessionHistoryEndings.Finished;
        Restored(_rig.Starts[0].Request.Name!);

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert
        Assert.Equal((0, 1), (run.Followed, run.Rerouted));
        Assert.Equal(1, _rig.Row(held.Id).RerouteCount);
    }

    [Fact]
    public async Task RouteAsync_RestoredSessionHoldingTheRestOfAStartWhoseFirstEmailIsDone_KeepsTheSeatBusy()
    {
        // Arrange: one session took two emails and said done on one; a restart brings it back holding the other.
        _rig.AddMailDeskTrigger();
        var channel = _rig.Channel(BoxA);
        var first = _rig.Decide(channel, "k-1", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 40);
        var second = _rig.Decide(channel, "k-2", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 35);
        await _rig.RouteAsync();
        var session = _rig.Row(first.Id).SessionId!;
        Assert.True(_rig.Routed.Done(MailDeskRoutingRig.Account, _rig.Row(first.Id).Id, session, "paid", _rig.Now).Succeeded);
        _rig.Endings[session] = SessionHistoryEndings.Closed;
        var restored = Restored(_rig.Starts[0].Request.Name!);
        _rig.Decide(channel, "k-3", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 10);

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert: the open email follows the restored session; the done row still names the old id, and the seat is
        // busy all the same - the new email waits.
        Assert.Equal(1, run.Followed);
        Assert.Equal(restored.SessionId, _rig.Row(second.Id).SessionId);
        Assert.Single(_rig.Starts);
        Assert.Equal(1, run.Waiting);
    }

    [Fact]
    public async Task NewMailDeskRouter_AsGatewayHostWiresIt_FollowsARestoredSessionListedOnTheGateway()
    {
        // Arrange: the router built by GatewayHost's own factory; the restored session is listed by a Director on a real
        // registry and pushed-session store, on the API surface, and the old one's real history record is closed.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.UseGatewayHostWiring(new SessionHistoryStore(_rig.Db));
        WriteHistoryRecord(session, SessionHistoryEndings.Closed);
        var restored = Restored(_rig.Starts[0].Request.Name!);
        _rig.Sessions.Remove(restored.SessionId);
        _rig.ListOnTheGateway(restored);

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert: followed - which only happens when the host passes the lookup by name over every surface.
        Assert.Equal((1, 0), (run.Followed, run.Rerouted));
        Assert.Equal(restored.SessionId, _rig.Row(held.Id).SessionId);
        Assert.Single(_rig.Starts);
    }

    // ---------- review of the answers to 4283: an ending a Director restart wrote waits ----------

    /// <summary>The seat a Smart Restart's drain captured from <paramref name="sessionId"/>, still owed a Bring back.</summary>
    private WorkspaceSeat CapturedSeat(string sessionId, string name)
    {
        var seat = new WorkspaceSeat
        {
            SessionId = sessionId,
            Name = name,
            Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
        };
        _rig.RestartSeats[sessionId] = seat;
        return seat;
    }

    [Fact]
    public async Task RouteAsync_SmartRestart_CheckRunsBeforeTheBringBack_NotRerouted_ThenFollowedByTheRecord()
    {
        // Arrange: the drain captured the seat, then ended it - history "closed", as a person's close would be.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        var seat = CapturedSeat(session, _rig.Starts[0].Request.Name!);
        _rig.Endings[session] = SessionHistoryEndings.Closed;

        // Act: the restarted Director's Mail Desk check runs while the offer window still waits for the owner.
        _rig.Now = _rig.Now.AddMinutes(1);
        var before = await _rig.RouteAsync();

        // Assert: not re-routed, no second session, and the seat is busy - the new email waits.
        Assert.Equal((0, 0, 0), (before.Rerouted, before.Followed, before.ToOwner));
        Assert.Equal((session, 0), (_rig.Row(held.Id).SessionId, _rig.Row(held.Id).RerouteCount));
        Assert.Single(_rig.Starts);
        Assert.Equal(1, before.Waiting);

        // Act: the owner presses Bring back; the record maps the seat to its new session. The restored session has not
        // reached the list yet, and its name is not even the start's - the record's mapping is what is followed.
        var restoredId = _rig.NextSessionId();
        seat.RestoredSessionId = restoredId;
        _rig.Now = _rig.Now.AddMinutes(5);
        var after = await _rig.RouteAsync();

        // Assert
        Assert.Equal((1, 0), (after.Followed, after.Rerouted));
        var row = _rig.Row(held.Id);
        Assert.Equal((restoredId, 0, MailDeskWords.RoutedStates.Open), (row.SessionId, row.RerouteCount, row.State));
        Assert.Single(_rig.Starts);
        Assert.Equal(1, after.Waiting);
    }

    [Fact]
    public async Task RouteAsync_SmartRestart_SeatReopenedFromItsConversation_IsFollowedToTheReopenedSession()
    {
        // Arrange: a seat with no handover is reopened, not brought back - the record names the reopened session.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        var seat = CapturedSeat(session, _rig.Starts[0].Request.Name!);
        _rig.Endings[session] = SessionHistoryEndings.Finished;
        var reopened = _rig.NextSessionId();
        seat.Restore!.ReopenedAtUtc = _rig.Now;
        seat.Restore.ReopenedSessionId = reopened;

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert: followed even though its history says "finished" - the record knows it was a restart.
        Assert.Equal((1, 0), (run.Followed, run.Rerouted));
        Assert.Equal(reopened, _rig.Row(held.Id).SessionId);
    }

    [Fact]
    public async Task RouteAsync_SmartRestart_SeatMarkedNotToBeBroughtBack_IsEndedAtOnce()
    {
        // Arrange
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        CapturedSeat(session, _rig.Starts[0].Request.Name!).Restore!.Decision = WorkspaceRestoreDecisions.Close;
        _rig.Endings[session] = SessionHistoryEndings.Closed;

        // Act
        _rig.Now = _rig.Now.AddMinutes(1);
        var run = await _rig.RouteAsync();

        // Assert: re-routed once, and one new session takes both emails.
        Assert.Equal((1, 0), (run.Rerouted, run.Followed));
        Assert.Equal(1, _rig.Row(held.Id).RerouteCount);
        Assert.Equal(2, _rig.Starts.Count);
        Assert.Equal(2, _rig.Starts[1].Request.FactoryCheck!.Count);
    }

    [Fact]
    public async Task RouteAsync_SmartRestart_StillOwedAfter24Hours_IsEnded()
    {
        // Arrange: the owner never brings it back.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        CapturedSeat(session, _rig.Starts[0].Request.Name!);
        _rig.Endings[session] = SessionHistoryEndings.Closed;

        // Act
        _rig.Now = _rig.Now.Add(MailDeskRouter.UnseenSessionEndsAfter);
        var run = await _rig.RouteAsync();

        // Assert
        Assert.Equal(1, run.Rerouted);
        Assert.Equal(1, _rig.Row(held.Id).RerouteCount);
    }

    [Fact]
    public async Task RouteAsync_SmartRestart_BroughtBackButTheMarkFailedToBeWritten_IsFollowedByName()
    {
        // Arrange: the record still says owed (the Director's mark of the reopen failed), but the restored session is
        // live with the start's name, factory and seat - the fallback.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        CapturedSeat(session, _rig.Starts[0].Request.Name!);
        _rig.Endings[session] = SessionHistoryEndings.Closed;
        var restored = Restored(_rig.Starts[0].Request.Name!);

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert
        Assert.Equal((1, 0), (run.Followed, run.Rerouted));
        Assert.Equal(restored.SessionId, _rig.Row(held.Id).SessionId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(23 * 60)]     // just inside the 24 hours
    public async Task RouteAsync_DirectorStoppedEnding_IsNotEndedBefore24Hours_AndTheSeatIsBusy(int minutes)
    {
        // Arrange: a plain Director shutdown - the farewell stamps every session it takes "director-stopped".
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.Endings[session] = SessionHistoryEndings.DirectorStopped;

        // Act
        _rig.Now = _rig.Now.AddMinutes(minutes);
        var run = await _rig.RouteAsync();

        // Assert
        Assert.Equal((0, 0), (run.Rerouted, run.ToOwner));
        Assert.Equal(session, _rig.Row(held.Id).SessionId);
        Assert.Single(_rig.Starts);
        Assert.Equal(1, run.Waiting);
    }

    [Fact]
    public async Task RouteAsync_DirectorStoppedEnding_IsEndedAfter24Hours()
    {
        // Arrange
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.Endings[session] = SessionHistoryEndings.DirectorStopped;

        // Act
        _rig.Now = _rig.Now.Add(MailDeskRouter.UnseenSessionEndsAfter);
        var run = await _rig.RouteAsync();

        // Assert
        Assert.Equal(1, run.Rerouted);
        Assert.Equal(1, _rig.Row(held.Id).RerouteCount);
    }

    [Fact]
    public async Task RouteAsync_APersonClosedTheSession_NoRestartRecord_IsReroutedAtOnce()
    {
        // Arrange: closed, and no restart captured it.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.Endings[session] = SessionHistoryEndings.Closed;

        // Act
        _rig.Now = _rig.Now.AddMinutes(1);
        var run = await _rig.RouteAsync();

        // Assert
        Assert.Equal((1, 0), (run.Rerouted, run.Followed));
        Assert.Equal(1, _rig.Row(held.Id).RerouteCount);
        Assert.Equal(2, _rig.Starts.Count);
    }

    [Fact]
    public async Task RouteAsync_ManySessions_AsksForTheRestartRecordsOnceWithTheSameSessionsAndTheOldestStart()
    {
        // Arrange: two seats, each with a session; then one more email for each.
        _rig.AddMailDeskTrigger();
        var channel = _rig.Channel(BoxA);
        _rig.Decide(channel, "k-1", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 40);
        await _rig.RouteAsync();
        var firstStart = _rig.Now;
        _rig.Now = _rig.Now.AddMinutes(3);
        _rig.Decide(channel, "k-2", MailDeskWords.Decisions.Route, Support, Desk, minutesAgo: 30);
        await _rig.RouteAsync();
        _rig.Decide(channel, "k-3", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 20);
        _rig.Decide(channel, "k-4", MailDeskWords.Decisions.Route, Support, Desk, minutesAgo: 10);
        _rig.EndingsAsked.Clear();
        _rig.RestartSeatsAsked.Clear();

        // Act
        _rig.Now = _rig.Now.AddMinutes(10);
        await _rig.RouteAsync();

        // Assert: one question to each source, about the same two sessions; nothing older than the first start.
        var (ids, since) = Assert.Single(_rig.RestartSeatsAsked);
        Assert.Equal(Assert.Single(_rig.EndingsAsked).OrderBy(s => s), ids.OrderBy(s => s));
        Assert.Equal(2, ids.Count);
        Assert.Equal(firstStart, since);
    }

    [Fact]
    public async Task NewMailDeskRouter_AsGatewayHostWiresIt_FollowsTheRestartRecordInTheWorkspaceStore()
    {
        // Arrange: the router built by GatewayHost's own factory; the real restart record, in the rig's account, maps
        // the seat to its new session. No session of that name is listed anywhere - only the record can say it.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.UseGatewayHostWiring(new SessionHistoryStore(_rig.Db));
        WriteHistoryRecord(session, SessionHistoryEndings.Closed);
        var restoredId = _rig.NextSessionId();
        _rig.SaveRestartRecord("restart-1", new WorkspaceSeat
        {
            SessionId = session,
            Name = _rig.Starts[0].Request.Name!,
            Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Restore },
            RestoredSessionId = restoredId,
        });

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert: followed - which only happens when the host passes the workspace store's restart records.
        Assert.Equal((1, 0), (run.Followed, run.Rerouted));
        Assert.Equal(restoredId, _rig.Row(held.Id).SessionId);
    }

    [Fact]
    public async Task NewMailDeskRouter_AsGatewayHostWiresIt_ARestartRecordOfAnotherAccountIsNotRead()
    {
        // Arrange: the same record, saved in another account - the router reads only the routed row's own.
        var (held, session) = await SessionReapedWithAnotherEmailWaitingAsync();
        _rig.UseGatewayHostWiring(new SessionHistoryStore(_rig.Db));
        WriteHistoryRecord(session, SessionHistoryEndings.Closed);
        var doc = new WorkspaceDocument
        {
            Id = "restart-elsewhere", Name = "restart-elsewhere", Origin = WorkspaceOrigins.Captured,
            Seats = { new WorkspaceSeat { SessionId = session, Name = "x", RestoredSessionId = _rig.NextSessionId() } },
        };
        using (var ctx = _rig.Db.CreateContext(new CcDirector.Core.Tenancy.TenantId("acct-elsewhere")))
        {
            ctx.Workspaces.Add(new WorkspaceEntity
            {
                TenantId = ctx.ActiveTenant!, Id = doc.Id, Name = doc.Name, Origin = doc.Origin, SeatCount = 1,
                CreatedUtc = _rig.Now, UpdatedUtc = _rig.Now,
                DocumentJson = System.Text.Json.JsonSerializer.Serialize(doc, WorkspaceStore.DocumentJsonOptions),
            });
            ctx.SaveChanges();
        }

        // Act
        _rig.Now = _rig.Now.AddMinutes(5);
        var run = await _rig.RouteAsync();

        // Assert: closed, by a person as far as this account knows - re-routed at once.
        Assert.Equal((0, 1), (run.Followed, run.Rerouted));
        Assert.Equal(1, _rig.Row(held.Id).RerouteCount);
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
