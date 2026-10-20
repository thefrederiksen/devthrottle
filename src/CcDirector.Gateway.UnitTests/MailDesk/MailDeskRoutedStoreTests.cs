using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.MailDesk;
using Xunit;
using static CcDirector.Gateway.Tests.MailDesk.MailDeskRoutingRig;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// EVERY WRITE TO A ROUTED EMAIL (Mail Desk phase 4 contract, section 3.1), store by store method: create, the start's
/// writes, the re-route, the redirect, done, return and the hand to the owner, and the reads the routes of task C build
/// on. Each refusal writes nothing.
/// </summary>
public sealed class MailDeskRoutedStoreTests : IAsyncLifetime
{
    private const string BoxA = "qa-one@example.com";
    private const string SessionOne = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string SessionTwo = "aaaaaaaa-0000-4000-8000-000000000002";
    private MailDeskRoutingRig _rig = null!;
    private MailDeskChannelEntity _channel = null!;

    public Task InitializeAsync()
    {
        _rig = new MailDeskRoutingRig();
        _channel = _rig.Channel(BoxA);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    private MailDeskRoutedStore Store => _rig.Routed;

    /// <summary>A routed row for a live route to qa-books/clerk, given to <paramref name="session"/> when named.</summary>
    private MailDeskRoutedEntity Routed(string key = "k-1", string? session = SessionOne, string decision = MailDeskWords.Decisions.Route)
    {
        var d = decision == MailDeskWords.Decisions.Route
            ? _rig.Decide(_channel, key, decision, Books, Clerk)
            : _rig.Decide(_channel, key, decision);
        Store.CreateMissing(MailDeskRoutingRig.Account, _rig.Now);
        var row = _rig.Row(d.Id);
        if (session is null) return row;
        var name = "start-" + key;
        Assert.Single(Store.BeginStart(MailDeskRoutingRig.Account, new[] { row.Id }, name, _rig.Now, _rig.Now));
        Assert.Single(Store.Take(MailDeskRoutingRig.Account, name, session, MachineA, _rig.Now));
        return Store.Get(MailDeskRoutingRig.Account, row.Id)!;
    }

    // ---------- follow a restored session (review of 4283, finding 2) ----------

    [Fact]
    public void Follow_OpenRowsOfTheOldSession_TakeTheNewIdAndStayOpen_ADoneRowIsLeftAsItIs()
    {
        // Arrange
        var open = Routed("k-1");
        var done = Routed("k-2");
        Assert.True(Store.Done(MailDeskRoutingRig.Account, done.Id, SessionOne, "paid", _rig.Now).Succeeded);

        // Act
        var followed = Store.Follow(MailDeskRoutingRig.Account, SessionOne, SessionTwo, MachineB, _rig.Now.AddMinutes(1));

        // Assert
        Assert.Equal(open.Id, Assert.Single(followed).Id);
        var row = Store.Get(MailDeskRoutingRig.Account, open.Id)!;
        Assert.Equal((SessionTwo, MailDeskWords.RoutedStates.Open, 0, _rig.Now.AddMinutes(1)),
            (row.SessionId, row.State, row.RerouteCount, row.ChangedUtc));
        Assert.Equal($"session {SessionTwo} on {MachineB} ({Books}/{Clerk})", _rig.DecisionRow(open.DecisionId).OutcomeDetail);
        Assert.Equal(SessionOne, Store.Get(MailDeskRoutingRig.Account, done.Id)!.SessionId);
    }

    // ---------- create ----------

    [Fact]
    public void CreateMissing_LiveRouteAndMailDeskAgent_GetOneQueuedRowEach_AndASecondCallCreatesNothing()
    {
        // Arrange
        var route = _rig.Decide(_channel, "k-1", MailDeskWords.Decisions.Route, Books, Clerk, minutesAgo: 20);
        var agent = _rig.Decide(_channel, "k-2", MailDeskWords.Decisions.MailDeskAgent, minutesAgo: 10);
        _rig.Decide(_channel, "k-3", MailDeskWords.Decisions.Route, Books, Clerk, mode: MailDeskWords.Modes.Observe);

        // Act
        var created = Store.CreateMissing(MailDeskRoutingRig.Account, _rig.Now);
        var again = Store.CreateMissing(MailDeskRoutingRig.Account, _rig.Now);

        // Assert
        Assert.Equal(new[] { route.Id, agent.Id }, created.Select(r => r.DecisionId).ToArray());
        Assert.Empty(again);
        var r1 = _rig.Row(route.Id);
        Assert.Equal((Books, Clerk, _channel.Id, MailDeskWords.RoutedStates.Open, (string?)null),
            (r1.Factory, r1.Seat, r1.ChannelId, r1.State, r1.SessionId));
        var r2 = _rig.Row(agent.Id);
        Assert.Equal((MailDeskFactory, MailDeskSeat), (r2.Factory, r2.Seat));
        Assert.Equal(2, _rig.Rows().Count);
    }

    // ---------- the start ----------

    [Fact]
    public void BeginStartThenTake_GivesTheSessionAndMakesTheDecisionSeatStarted()
    {
        // Arrange and act
        var row = Routed();

        // Assert
        Assert.Equal(SessionOne, row.SessionId);
        Assert.Equal("start-k-1", row.StartName);
        Assert.Equal(_rig.Now, row.StartedUtc);
        var decision = _rig.DecisionRow(row.DecisionId);
        Assert.Equal((MailDeskWords.Outcomes.SeatStarted, $"session {SessionOne} on {MachineA} ({Books}/{Clerk})", (DateTime?)_rig.Now),
            (decision.Outcome, decision.OutcomeDetail, decision.OutcomeUtc));
    }

    [Fact]
    public void BeginStart_RowAlreadyStarted_IsNotNamedAgain()
    {
        // Arrange
        var row = Routed();

        // Act
        var named = Store.BeginStart(MailDeskRoutingRig.Account, new[] { row.Id }, "another start", _rig.Now, _rig.Now);

        // Assert
        Assert.Empty(named);
        Assert.Equal("start-k-1", Store.Get(MailDeskRoutingRig.Account, row.Id)!.StartName);
    }

    [Fact]
    public void RecordStartFailure_ThirdFailure_HandsItToTheOwnerAndFailsTheDecision()
    {
        // Arrange
        var row = Routed(session: null);

        // Act
        Store.RecordStartFailure(MailDeskRoutingRig.Account, row.Id, "refused", _rig.Now);
        var second = Store.RecordStartFailure(MailDeskRoutingRig.Account, row.Id, "refused", _rig.Now)!;
        var third = Store.RecordStartFailure(MailDeskRoutingRig.Account, row.Id, "still refused", _rig.Now)!;

        // Assert
        Assert.Equal((2, MailDeskWords.RoutedStates.Open), (second.StartFailures, second.State));
        Assert.Equal((3, MailDeskWords.RoutedStates.Returned), (third.StartFailures, third.State));
        Assert.Equal("its session could not be started 3 times: still refused", third.OwnerReason);
        Assert.Equal((MailDeskWords.Outcomes.Failed, (DateTime?)_rig.Now),
            (_rig.DecisionRow(row.DecisionId).Outcome, _rig.DecisionRow(row.DecisionId).OutcomeUtc));
    }

    // ---------- the router's other writes ----------

    [Fact]
    public void Reroute_FirstTimeQueuesForTheSameSeat_SecondTimeHandsToTheOwner()
    {
        // Arrange
        var row = Routed();

        // Act
        var first = Store.Reroute(MailDeskRoutingRig.Account, row.Id, _rig.Now)!;
        var name = "start-again";
        Store.BeginStart(MailDeskRoutingRig.Account, new[] { row.Id }, name, _rig.Now, _rig.Now);
        Store.Take(MailDeskRoutingRig.Account, name, SessionTwo, MachineA, _rig.Now);
        var second = Store.Reroute(MailDeskRoutingRig.Account, row.Id, _rig.Now)!;

        // Assert
        Assert.Equal((1, (string?)null, (string?)null, Books, Clerk), (first.RerouteCount, first.SessionId, first.StartName, first.Factory, first.Seat));
        Assert.Equal((MailDeskWords.RoutedStates.Returned, MailDeskRoutedReasons.SessionEndedTwice), (second.State, second.OwnerReason));
        Assert.Equal(MailDeskWords.Outcomes.SeatStarted, _rig.DecisionRow(row.DecisionId).Outcome);
    }

    [Fact]
    public void Redirect_FromAnotherSeat_GoesToTheMailDeskSeatWithTheReason_NotCountedAsAReturn()
    {
        // Arrange
        var row = Routed(session: null);

        // Act
        var after = Store.Redirect(MailDeskRoutingRig.Account, row.Id, "no such seat", _rig.Now)!;

        // Assert
        Assert.Equal((MailDeskFactory, MailDeskSeat, "no such seat", 0, MailDeskWords.RoutedStates.Open),
            (after.Factory, after.Seat, after.ReturnNote, after.ReturnedCount, after.State));
    }

    [Fact]
    public void HandToOwner_OpenRow_IsReturnedWithTheReason_AndARowNotOpenIsLeftAsItIs()
    {
        // Arrange
        var row = Routed(session: null);
        var done = Routed("k-2");
        Assert.True(Store.Done(MailDeskRoutingRig.Account, done.Id, SessionOne, null, _rig.Now).Succeeded);

        // Act
        var handed = Store.HandToOwner(MailDeskRoutingRig.Account, row.Id, "the owner should read this", failDecision: false, _rig.Now)!;
        var untouched = Store.HandToOwner(MailDeskRoutingRig.Account, done.Id, "too late", failDecision: true, _rig.Now)!;

        // Assert
        Assert.Equal((MailDeskWords.RoutedStates.Returned, "the owner should read this"), (handed.State, handed.OwnerReason));
        Assert.Null(_rig.DecisionRow(row.DecisionId).Outcome);
        Assert.Null(_rig.DecisionRow(row.DecisionId).OutcomeUtc);
        Assert.Equal((MailDeskWords.RoutedStates.Done, (string?)null), (untouched.State, untouched.OwnerReason));
    }

    // ---------- done ----------

    [Fact]
    public void Done_ByTheHoldingSession_IsDoneWithTheNote()
    {
        // Arrange
        var row = Routed();
        var later = _rig.Now.AddMinutes(3);

        // Act
        var answer = Store.Done(MailDeskRoutingRig.Account, row.Id, SessionOne, "  paid the invoice  ", later);

        // Assert
        Assert.True(answer.Succeeded);
        var stored = Store.Get(MailDeskRoutingRig.Account, row.Id)!;
        Assert.Equal((MailDeskWords.RoutedStates.Done, "paid the invoice", later), (stored.State, stored.DoneNote, stored.ChangedUtc));
    }

    [Theory]
    [InlineData(SessionTwo)]       // another session
    [InlineData(null)]             // a person, the machine token or a Director: no session
    public void Done_ByAnyoneButTheHoldingSession_IsRefusedNotYourEmail_AndWritesNothing(string? caller)
    {
        // Arrange
        var row = Routed();

        // Act
        var answer = Store.Done(MailDeskRoutingRig.Account, row.Id, caller, "not mine", _rig.Now.AddMinutes(1));

        // Assert
        Assert.Equal(MailDeskRoutedRefusal.NotYourEmail, answer.Refusal);
        Assert.Contains(SessionOne, answer.Detail);
        Assert.Equal(row.ChangedUtc, Store.Get(MailDeskRoutingRig.Account, row.Id)!.ChangedUtc);
        Assert.Equal(MailDeskWords.RoutedStates.Open, Store.Get(MailDeskRoutingRig.Account, row.Id)!.State);
    }

    [Fact]
    public void Done_ByASessionTheRowWasWithBeforeAReroute_IsRefused()
    {
        // Arrange
        var row = Routed();
        Store.Reroute(MailDeskRoutingRig.Account, row.Id, _rig.Now);

        // Act
        var answer = Store.Done(MailDeskRoutingRig.Account, row.Id, SessionOne, null, _rig.Now);

        // Assert
        Assert.Equal(MailDeskRoutedRefusal.NotYourEmail, answer.Refusal);
        Assert.Contains("queued for qa-books/clerk", answer.Detail);
    }

    [Fact]
    public void Done_RowNotOpen_IsRefusedNotOpen_AndNoRow_IsRefusedNoSuchRoutedEmail()
    {
        // Arrange
        var row = Routed();
        Assert.True(Store.Done(MailDeskRoutingRig.Account, row.Id, SessionOne, null, _rig.Now).Succeeded);

        // Act
        var again = Store.Done(MailDeskRoutingRig.Account, row.Id, SessionOne, null, _rig.Now);
        var none = Store.Done(MailDeskRoutingRig.Account, Guid.NewGuid(), SessionOne, null, _rig.Now);

        // Assert
        Assert.Equal((MailDeskRoutedRefusal.NotOpen, "This email is already done."), (again.Refusal, again.Detail));
        Assert.Equal(MailDeskRoutedRefusal.NoSuchRoutedEmail, none.Refusal);
    }

    // ---------- return ----------

    [Fact]
    public void Return_FirstTimeFromAnotherSeat_QueuesItForTheMailDeskSeatWithTheNote()
    {
        // Arrange
        var row = Routed();

        // Act
        var answer = Store.Return(MailDeskRoutingRig.Account, row.Id, SessionOne, "this is a support question", _rig.Now);

        // Assert
        Assert.True(answer.Succeeded);
        var after = Store.Get(MailDeskRoutingRig.Account, row.Id)!;
        Assert.Equal((MailDeskFactory, MailDeskSeat, (string?)null, MailDeskWords.RoutedStates.Open, 1, "this is a support question"),
            (after.Factory, after.Seat, after.SessionId, after.State, after.ReturnedCount, after.ReturnNote));
        Assert.Null(after.StartName);
    }

    [Fact]
    public void Return_SecondTime_HandsItToTheOwner()
    {
        // Arrange: returned once to the Mail Desk seat, then given to a session of another seat again by hand of the router.
        var row = Routed();
        Store.Return(MailDeskRoutingRig.Account, row.Id, SessionOne, "not for me", _rig.Now);
        using (var ctx = _rig.Db.CreateContext(MailDeskRoutingRig.Account))
        {
            var r = ctx.MailDeskRouted.Single(x => x.Id == row.Id);
            r.Factory = Books;
            r.Seat = Clerk;
            ctx.SaveChanges();
        }
        Store.BeginStart(MailDeskRoutingRig.Account, new[] { row.Id }, "second", _rig.Now, _rig.Now);
        Store.Take(MailDeskRoutingRig.Account, "second", SessionTwo, MachineA, _rig.Now);

        // Act
        var answer = Store.Return(MailDeskRoutingRig.Account, row.Id, SessionTwo, "still not for me", _rig.Now);

        // Assert
        var after = answer.Row!;
        Assert.Equal((MailDeskWords.RoutedStates.Returned, 2, "returned twice: still not for me"), (after.State, after.ReturnedCount, after.OwnerReason));
    }

    [Fact]
    public void Return_ByTheMailDeskAgent_HandsItToTheOwner()
    {
        // Arrange
        var row = Routed(decision: MailDeskWords.Decisions.MailDeskAgent);

        // Act
        var answer = Store.Return(MailDeskRoutingRig.Account, row.Id, SessionOne, "I cannot tell who this is for", _rig.Now);

        // Assert
        Assert.Equal((MailDeskWords.RoutedStates.Returned, "the Mail Desk agent returned it: I cannot tell who this is for"),
            (answer.Row!.State, answer.Row.OwnerReason));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Return_WithoutANote_IsRefusedNoteRequired_AndWritesNothing(string? note)
    {
        // Arrange
        var row = Routed();

        // Act
        var answer = Store.Return(MailDeskRoutingRig.Account, row.Id, SessionOne, note, _rig.Now.AddMinutes(1));

        // Assert
        Assert.Equal(MailDeskRoutedRefusal.NoteRequired, answer.Refusal);
        var after = Store.Get(MailDeskRoutingRig.Account, row.Id)!;
        Assert.Equal((0, row.ChangedUtc, Books), (after.ReturnedCount, after.ChangedUtc, after.Factory));
    }

    [Fact]
    public void Return_ByAnotherSession_IsRefusedNotYourEmail()
    {
        // Arrange
        var row = Routed();

        // Act
        var answer = Store.Return(MailDeskRoutingRig.Account, row.Id, SessionTwo, "mine now", _rig.Now);

        // Assert
        Assert.Equal(MailDeskRoutedRefusal.NotYourEmail, answer.Refusal);
        Assert.Equal(0, Store.Get(MailDeskRoutingRig.Account, row.Id)!.ReturnedCount);
    }

    // ---------- the reads ----------

    [Fact]
    public void List_EveryFilterApplies_NewestFirst_WithTheTotalOfEverythingMatching()
    {
        // Arrange: one with a session, one queued, one done, one at the Mail Desk seat.
        var withSession = Routed("k-1");
        _rig.Now = _rig.Now.AddMinutes(1);
        var queued = Routed("k-2", session: null);
        _rig.Now = _rig.Now.AddMinutes(1);
        var done = Routed("k-3", session: SessionTwo);
        Store.Done(MailDeskRoutingRig.Account, done.Id, SessionTwo, null, _rig.Now);
        _rig.Now = _rig.Now.AddMinutes(1);
        var agent = Routed("k-4", session: null, decision: MailDeskWords.Decisions.MailDeskAgent);

        // Act and assert
        Assert.Equal(new[] { agent.Id, done.Id, queued.Id, withSession.Id }, Store.List(MailDeskRoutingRig.Account, new MailDeskRoutedFilter()).Routed.Select(r => r.Id));
        Assert.Equal(new[] { done.Id }, Store.List(MailDeskRoutingRig.Account, new MailDeskRoutedFilter(State: MailDeskWords.RoutedStates.Done)).Routed.Select(r => r.Id));
        Assert.Equal(new[] { agent.Id, queued.Id }, Store.List(MailDeskRoutingRig.Account, new MailDeskRoutedFilter(Queued: true)).Routed.Select(r => r.Id));
        Assert.Equal(new[] { agent.Id }, Store.List(MailDeskRoutingRig.Account, new MailDeskRoutedFilter(Factory: MailDeskFactory)).Routed.Select(r => r.Id));
        Assert.Equal(new[] { queued.Id }, Store.List(MailDeskRoutingRig.Account, new MailDeskRoutedFilter(Queued: true, Seat: Clerk)).Routed.Select(r => r.Id));
        var page = Store.List(MailDeskRoutingRig.Account, new MailDeskRoutedFilter(N: 1));
        Assert.Equal((1, 4), (page.Routed.Count, page.Total));
        Assert.Equal((0, 0), (Store.List(MailDeskRoutingRig.Account, new MailDeskRoutedFilter(Factory: "nobody")).Routed.Count, Store.List(MailDeskRoutingRig.Account, new MailDeskRoutedFilter(Factory: "nobody")).Total));
    }

    [Fact]
    public void List_UnknownStateOrOutOfRangeCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => Store.List(MailDeskRoutingRig.Account, new MailDeskRoutedFilter(State: "lost")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Store.List(MailDeskRoutingRig.Account, new MailDeskRoutedFilter(N: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Store.List(MailDeskRoutingRig.Account, new MailDeskRoutedFilter(N: MailDeskRoutedStore.MaxList + 1)));
    }

    [Fact]
    public void Routed_HasNoBodyColumn()
    {
        // NO BODY TEXT, EVER: the routed row has no column that could hold an email's body or preview.
        var names = typeof(MailDeskRoutedEntity).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("Body", StringComparison.OrdinalIgnoreCase) || n.Contains("Preview", StringComparison.OrdinalIgnoreCase));
    }
}
