using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Receipts;

/// <summary>
/// The API-mode receipt store (mission #4070, phase 2; <c>docs/missions/api-mode-pi-2026-10-16/event-model.md</c>).
/// These pin what a receipt must say for each way a prompt can go - delivered and done, refused by the agent, refused
/// by the Director before an acknowledgement, delivered then failed mid-turn, cancelled - and the properties the ingest
/// leans on: a duplicate or late event changes nothing, a receipt never moves backwards, one ending per prompt (rule 1),
/// the turn's outcome beside Delivered (rule 5), tenant isolation, retention on the Gateway's injected clock, and a
/// malformed change refused whole.
///
/// The acknowledgement and reason strings are the recorded Pi wire's own words
/// (<c>src/CcDirector.Core.Tests/TestData/pi-rpc/</c>), so a receipt is checked against what Pi really wrote.
/// </summary>
public sealed class ApiModeReceiptStoreTests : IDisposable
{
    private const string Director = "dir-devlinux";
    private const string Sid = "0b6c2d9e-4f1a-4c3b-9d2e-7a8b9c0d1e2f";

    private const string StartedAck = """{"id":"req-96c7b87f","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""";
    private const string BusyRefusal = "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.";

    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private DateTime _clock = new(2026, 10, 16, 12, 0, 5, DateTimeKind.Utc);

    public void Dispose() => _harness.Dispose();

    private ApiModeReceiptStore Store(ITenantContext? tenant = null) => new(_harness.Open(tenant), () => _clock);

    private static ApiModeReceiptDto Only(ApiModeReceiptStore store) => Assert.Single(store.Read(Sid));

    /// <summary>The Director's first generation of this session's event stream.</summary>
    private static readonly DateTime GenAStarted = new(2026, 10, 16, 11, 0, 0, DateTimeKind.Utc);

    /// <summary>The generation after a Director restart: started later, and its sequence starts again.</summary>
    private static readonly DateTime GenBStarted = new(2026, 10, 16, 12, 30, 0, DateTimeKind.Utc);

    private static ApiModeEventPosition At(long sequence) => new("gen-a", GenAStarted, sequence);

    private static ApiModeEventPosition AfterRestart(long sequence) => new("gen-b", GenBStarted, sequence);

    // ----- the five ways a prompt goes, as the recorded Pi fixtures show them -----

    [Fact]
    public void RecordDelivered_ThenTurnEndedDone_ReceiptIsDeliveredWithTheAcknowledgementVerbatimAndDone()
    {
        var store = Store();

        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);
        store.RecordTurnStarted(Director, Sid, "t1", new[] { "p1" }, At(2));
        store.RecordTurnEnded(Director, Sid, "t1", "done", null, At(9), T0.AddSeconds(2));

        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal("started", r.Disposition);
        Assert.Equal(StartedAck, r.Acknowledgement);
        Assert.Equal("t1", r.TurnId);
        Assert.Equal("done", r.TurnOutcome);
        Assert.Null(r.TurnOutcomeReason);
        Assert.Equal(T0, r.DeliveredAtUtc);
        Assert.Equal(T0.AddSeconds(2), r.TurnEndedAtUtc);
        Assert.Null(r.RefusalReason);
    }

    [Fact]
    public void RecordRefused_ByTheAgent_ReceiptShowsTheAgentsOwnWordsAndWhoRefused()
    {
        var store = Store();

        store.RecordQueued(Director, Sid, "p2", "turnRunning", At(3), T0);
        store.RecordRefused(Director, Sid, "p2", BusyRefusal, "agent", At(4), T0.AddSeconds(1));

        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Refused, r.State);
        Assert.Equal(BusyRefusal, r.RefusalReason);
        Assert.Equal("agent", r.RefusedBy);
        Assert.Equal("turnRunning", r.QueueReason);
        Assert.Equal(T0, r.QueuedAtUtc);
        Assert.Equal(T0.AddSeconds(1), r.RefusedAtUtc);
        Assert.Null(r.Acknowledgement);
        Assert.Null(r.TurnOutcome);
    }

    [Fact]
    public void RecordRefused_ByTheDirectorBeforeAnAcknowledgement_ReceiptIsRefusedNeverDelivered()
    {
        var store = Store();

        store.RecordRefused(Director, Sid, "p1", "Pi exited before acknowledging", "director", At(1), T0);
        // No turn ever starts for it; a turn end that names some other turn cannot reach it.
        store.RecordTurnEnded(Director, Sid, "t-other", "failed", "Pi exited during the turn (exit code 137)", At(2), T0);

        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Refused, r.State);
        Assert.Equal("director", r.RefusedBy);
        Assert.Equal("Pi exited before acknowledging", r.RefusalReason);
        Assert.Null(r.Disposition);
        Assert.Null(r.TurnOutcome);
    }

    [Fact]
    public void RecordTurnEnded_FailedMidTurn_ReceiptShowsFailedBesideDelivered()
    {
        var store = Store();

        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);
        store.RecordTurnStarted(Director, Sid, "t1", new[] { "p1" }, At(2));
        store.RecordTurnEnded(Director, Sid, "t1", "failed", "Pi exited during the turn (exit code 137)", At(7), T0.AddSeconds(3));

        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);   // Delivered stays: Pi acknowledged it
        Assert.Equal("failed", r.TurnOutcome);                   // and the failed turn is beside it (rule 5)
        Assert.Equal("Pi exited during the turn (exit code 137)", r.TurnOutcomeReason);
    }

    [Fact]
    public void RecordTurnEnded_Cancelled_ReceiptShowsCancelledBesideDelivered()
    {
        var store = Store();

        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);
        store.RecordTurnStarted(Director, Sid, "t1", new[] { "p1" }, At(2));
        store.RecordTurnEnded(Director, Sid, "t1", "cancelled", "stopped by the user", At(5), T0.AddSeconds(1));

        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal("cancelled", r.TurnOutcome);
        Assert.Equal("stopped by the user", r.TurnOutcomeReason);
    }

    // ----- forward only -----

    [Fact]
    public void RecordDelivered_SentTwice_ChangesNothingTheSecondTime()
    {
        var store = Store();
        Assert.True(store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0));
        var first = Only(store);

        _clock = _clock.AddMinutes(1);
        Assert.False(store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0));

        Assert.Equal(first.UpdatedAtUtc, Only(store).UpdatedAtUtc);
    }

    [Fact]
    public void RecordQueued_ArrivingLate_CannotMoveADeliveredReceiptBackwards()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "p1", "agentStarting", At(1), T0);
        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(3), T0.AddSeconds(1));

        // The same queued event re-sent, and a queued event with a later sequence (a Director defect): neither moves it.
        Assert.False(store.RecordQueued(Director, Sid, "p1", "agentStarting", At(1), T0));
        Assert.False(store.RecordQueued(Director, Sid, "p1", "turnRunning", At(4), T0.AddSeconds(2)));

        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal("agentStarting", r.QueueReason);
    }

    [Fact]
    public void RecordQueued_AgainWhileQueued_RefreshesTheReason()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "p1", "agentStarting", At(1), T0);

        Assert.True(store.RecordQueued(Director, Sid, "p1", "userHasTheWheel", At(2), T0.AddSeconds(1)));

        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Queued, r.State);
        Assert.Equal("userHasTheWheel", r.QueueReason);
        Assert.Equal(T0, r.QueuedAtUtc);   // first queued time kept
    }

    [Fact]
    public void RecordRefused_AfterDelivered_IsRefusedAndTheFirstEndingStands()
    {
        var store = Store();
        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);

        Assert.False(store.RecordRefused(Director, Sid, "p1", "Pi exited before acknowledging", "director", At(2), T0));

        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Null(r.RefusalReason);
    }

    [Fact]
    public void RecordDelivered_Twice_TheFirstDeliveryStands()
    {
        // Arrange
        var store = Store();
        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);

        // Act: a delivered prompt is never sent again, so a second delivery is a Director defect.
        var changed = store.RecordDelivered(Director, Sid, "p1", "queuedByAgent", "a different acknowledgement", At(2), T0.AddSeconds(1));

        // Assert
        Assert.False(changed);
        var r = Only(store);
        Assert.Equal("started", r.Disposition);
        Assert.Equal(StartedAck, r.Acknowledgement);
        Assert.Equal(T0, r.DeliveredAtUtc);
    }

    // ----- a retry of a delivery that was not delivered reuses its Gateway id: the later receipt stands -----
    // (event model section 2, "What a promptId is"; phase 2 phase review, finding 1)

    private const string PiNotRunning = "Pi is not running: the process exited";

    [Fact]
    public void RecordDelivered_AfterRefused_TheRetrysDeliveryStandsWithItsAcknowledgement()
    {
        // Arrange: the first press was refused by the Director while it restarted Pi.
        var store = Store();
        store.RecordQueued(Director, Sid, "gw-1", "agentStarting", At(1), T0);
        store.RecordRefused(Director, Sid, "gw-1", PiNotRunning, "director", At(2), T0.AddSeconds(1));

        // Act: the Gateway's re-press, under the same id, is acknowledged.
        var changed = store.RecordDelivered(Director, Sid, "gw-1", "started", StartedAck, At(5), T0.AddSeconds(4));

        // Assert: the receipt reads as the retry alone - nothing of the refused attempt is left on it.
        Assert.True(changed);
        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal("started", r.Disposition);
        Assert.Equal(StartedAck, r.Acknowledgement);
        Assert.Equal(T0.AddSeconds(4), r.DeliveredAtUtc);
        Assert.Null(r.RefusalReason);
        Assert.Null(r.RefusedBy);
        Assert.Null(r.RefusedAtUtc);
        Assert.Null(r.QueueReason);
        Assert.Null(r.QueuedAtUtc);
    }

    [Fact]
    public void RecordTurnStarted_ForARetryDeliveredAfterARefusal_LinksItAndTheTurnEndsOnIt()
    {
        // Arrange
        var store = Store();
        store.RecordRefused(Director, Sid, "gw-1", PiNotRunning, "director", At(1), T0);
        store.RecordDelivered(Director, Sid, "gw-1", "started", StartedAck, At(5), T0);

        // Act
        var linked = store.RecordTurnStarted(Director, Sid, "t1", new[] { "gw-1" }, At(6));
        var ended = store.RecordTurnEnded(Director, Sid, "t1", "failed", "Pi exited during the turn (exit code 137)", At(9), T0);

        // Assert
        Assert.Equal(1, linked);
        Assert.Equal(1, ended);
        var r = Only(store);
        Assert.Equal("t1", r.TurnId);
        Assert.Equal("failed", r.TurnOutcome);
    }

    [Fact]
    public void RecordRefused_AfterRefused_TheLaterReasonStands()
    {
        // Arrange
        var store = Store();
        store.RecordRefused(Director, Sid, "gw-1", PiNotRunning, "director", At(1), T0);

        // Act: the retry is refused too, by the agent this time.
        var changed = store.RecordRefused(Director, Sid, "gw-1", BusyRefusal, "agent", At(4), T0.AddSeconds(3));

        // Assert
        Assert.True(changed);
        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Refused, r.State);
        Assert.Equal(BusyRefusal, r.RefusalReason);
        Assert.Equal("agent", r.RefusedBy);
        Assert.Equal(T0.AddSeconds(3), r.RefusedAtUtc);
    }

    [Fact]
    public void RecordQueued_AfterRefused_TheRetryIsQueuedWithItsOwnReason()
    {
        // Arrange
        var store = Store();
        store.RecordRefused(Director, Sid, "gw-1", PiNotRunning, "director", At(1), T0);

        // Act
        var changed = store.RecordQueued(Director, Sid, "gw-1", "turnRunning", At(3), T0.AddSeconds(2));

        // Assert
        Assert.True(changed);
        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Queued, r.State);
        Assert.Equal("turnRunning", r.QueueReason);
        Assert.Equal(T0.AddSeconds(2), r.QueuedAtUtc);
        Assert.Null(r.RefusalReason);
    }

    [Fact]
    public void RecordRefused_AnOlderRefusalReplayedAfterTheRetrysDelivery_DoesNotOverwrite()
    {
        // Arrange: refused at 1, delivered on retry at 5.
        var store = Store();
        store.RecordRefused(Director, Sid, "gw-1", PiNotRunning, "director", At(1), T0);
        store.RecordDelivered(Director, Sid, "gw-1", "started", StartedAck, At(5), T0);

        // Act: the first refusal re-sent, and a refusal with a lower sequence than the delivery.
        var replayed = store.RecordRefused(Director, Sid, "gw-1", PiNotRunning, "director", At(1), T0);
        var older = store.RecordRefused(Director, Sid, "gw-1", "an earlier attempt", "director", At(3), T0);

        // Assert
        Assert.False(replayed);
        Assert.False(older);
        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal(StartedAck, r.Acknowledgement);
        Assert.Null(r.RefusalReason);
    }

    [Fact]
    public void RecordRefused_FromAnOlderGenerationAfterTheRetrysDelivery_DoesNotOverwrite()
    {
        // Arrange: the retry was delivered in the generation after a Director restart.
        var store = Store();
        store.RecordRefused(Director, Sid, "gw-1", PiNotRunning, "director", At(1), T0);
        store.RecordDelivered(Director, Sid, "gw-1", "started", StartedAck, AfterRestart(2), T0);

        // Act: a late refusal from before the restart, with a higher sequence of its own.
        var changed = store.RecordRefused(Director, Sid, "gw-1", BusyRefusal, "agent", At(500), T0);

        // Assert
        Assert.False(changed);
        Assert.Equal(ApiModeReceiptState.Delivered, Only(store).State);
    }

    [Fact]
    public void Read_ARetryDeliveredAfterAnotherPrompt_IsReadAfterIt()
    {
        // Arrange: gw-1 refused, then p2 delivered, then gw-1's retry delivered. Turns are numbered in the order receipts
        // are read, so the retry must read as the later prompt.
        var store = Store();
        store.RecordRefused(Director, Sid, "gw-1", PiNotRunning, "director", At(1), T0);
        store.RecordDelivered(Director, Sid, "p2", "started", StartedAck, At(2), T0);

        // Act
        store.RecordDelivered(Director, Sid, "gw-1", "started", StartedAck, At(5), T0);

        // Assert
        Assert.Equal(new[] { "p2", "gw-1" }, store.Read(Sid).Select(r => r.PromptId));
    }

    [Fact]
    public void RecordTurnEnded_Twice_TheOutcomeIsWrittenOnce()
    {
        var store = Store();
        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);
        store.RecordTurnStarted(Director, Sid, "t1", new[] { "p1" }, At(2));
        Assert.Equal(1, store.RecordTurnEnded(Director, Sid, "t1", "cancelled", "stopped by the user", At(5), T0));

        Assert.Equal(0, store.RecordTurnEnded(Director, Sid, "t1", "failed", "a later, different ending", At(6), T0));

        Assert.Equal("cancelled", Only(store).TurnOutcome);
    }

    [Fact]
    public void RecordTurnStarted_NamingAPromptWithNoReceiptOrNotDelivered_LinksNothing()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "queued", "userHasTheWheel", At(1), T0);
        store.RecordRefused(Director, Sid, "refused", BusyRefusal, "agent", At(2), T0);

        Assert.Equal(0, store.RecordTurnStarted(Director, Sid, "t1", new[] { "missing", "queued", "refused" }, At(3)));

        Assert.All(store.Read(Sid), r => Assert.Null(r.TurnId));
    }

    [Fact]
    public void RecordTurnStarted_ForAPromptAnotherTurnCarries_LeavesItWithTheFirstTurn()
    {
        var store = Store();
        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);
        store.RecordTurnStarted(Director, Sid, "t1", new[] { "p1" }, At(2));

        Assert.Equal(0, store.RecordTurnStarted(Director, Sid, "t2", new[] { "p1" }, At(3)));

        Assert.Equal("t1", Only(store).TurnId);
    }

    [Fact]
    public void RecordTurnEnded_ForATurnCarryingTwoPrompts_WritesTheOutcomeOnBoth()
    {
        var store = Store();
        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);
        store.RecordDelivered(Director, Sid, "p2", "queuedByAgent", StartedAck, At(2), T0);
        Assert.Equal(2, store.RecordTurnStarted(Director, Sid, "t1", new[] { "p1", "p2" }, At(3)));

        Assert.Equal(2, store.RecordTurnEnded(Director, Sid, "t1", "done", null, At(4), T0));

        Assert.All(store.Read(Sid), r => Assert.Equal("done", r.TurnOutcome));
    }

    // ----- generations: a Director restart starts the sequence again -----

    [Fact]
    public void RecordTurnEnded_FromALaterGenerationWithALowerSequence_IsApplied()
    {
        var store = Store();
        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(40), T0);
        store.RecordTurnStarted(Director, Sid, "t1", new[] { "p1" }, At(41));

        // The Director restarted mid-turn: its new generation counts from 1 again, and closes the turn as failed.
        Assert.Equal(1, store.RecordTurnEnded(Director, Sid, "t1", "failed", "the Director restarted during the turn", AfterRestart(1), T0));

        Assert.Equal("failed", Only(store).TurnOutcome);
    }

    [Fact]
    public void RecordRefused_FromAnOlderGeneration_IsRefusedWhateverItsSequence()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "p1", "agentStarting", AfterRestart(1), T0);

        // A late batch from before the restart, with a much higher sequence of its own: it is older, so it moves nothing.
        Assert.False(store.RecordRefused(Director, Sid, "p1", BusyRefusal, "agent", At(500), T0));

        Assert.Equal(ApiModeReceiptState.Queued, Only(store).State);
    }

    [Fact]
    public void ApiModeEventPosition_TwoGenerationsStartedTogether_AreOrderedByTheirIds()
    {
        var b = new ApiModeEventPosition("gen-b", GenAStarted, 1);

        Assert.True(b.IsAfter("gen-a", GenAStarted, 99));
        Assert.False(new ApiModeEventPosition("gen-a", GenAStarted, 99).IsAfter("gen-b", GenAStarted, 1));
    }

    [Fact]
    public void Read_AnswersReceiptsOfALaterGenerationAfterTheEarlierOnes()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "after-restart", "turnRunning", AfterRestart(1), T0);
        store.RecordQueued(Director, Sid, "before-restart", "turnRunning", At(30), T0);

        Assert.Equal(new[] { "before-restart", "after-restart" }, store.Read(Sid).Select(r => r.PromptId));
    }

    // ----- reading -----

    [Fact]
    public void Read_AnswersOldestPromptFirst_AndOnlyThisSession()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "zz-first", "turnRunning", At(1), T0);
        store.RecordQueued(Director, Sid, "aa-second", "turnRunning", At(2), T0);
        store.RecordQueued(Director, "another-session", "p9", "turnRunning", At(3), T0);

        Assert.Equal(new[] { "zz-first", "aa-second" }, store.Read(Sid).Select(r => r.PromptId));
    }

    [Fact]
    public void ReadWithPositions_EachReceipt_CarriesThePositionOfTheLastEventTheStoreApplied()
    {
        var store = Store();
        store.RecordRefused(Director, Sid, "gw-1", "Pi is not running: the process exited", "director", At(1), T0);
        store.RecordDelivered(Director, Sid, "p2", "started", StartedAck, At(2), T0);
        store.RecordQueued(Director, Sid, "gw-1", "turnRunning", At(3), T0);
        store.RecordRefused(Director, Sid, "gw-1", BusyRefusal, "agent", At(4), T0);
        // A second ending the store refuses: it applies nothing, so the position does not move.
        store.RecordRefused(Director, Sid, "p2", "too late", "director", At(5), T0);

        var positioned = store.ReadWithPositions(Sid);

        Assert.Equal(store.Read(Sid).Select(r => (r.PromptId, r.State, r.RefusalReason)), positioned.Select(r => (r.Receipt.PromptId, r.Receipt.State, r.Receipt.RefusalReason)));
        Assert.Equal(At(2), positioned.Single(r => r.Receipt.PromptId == "p2").LastPosition);
        Assert.Equal(At(4), positioned.Single(r => r.Receipt.PromptId == "gw-1").LastPosition);
    }

    [Fact]
    public void Read_ASessionWithNoReceipts_IsEmpty()
    {
        Assert.Empty(Store().Read(Sid));
    }

    [Fact]
    public void RecordDelivered_StampsTheGatewayTimesFromTheInjectedClock()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "p1", "agentStarting", At(1), T0);
        _clock = _clock.AddSeconds(30);

        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(2), T0);

        Assert.Equal(_clock, Only(store).UpdatedAtUtc);
    }

    // ----- tenant isolation -----

    [Fact]
    public void Read_AnotherTenant_SeesNoneOfTheReceipts()
    {
        var mine = Store(new FixedTenantContext(TenantId.Local));
        mine.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);

        var theirs = Store(new FixedTenantContext(new TenantId("11111111-1111-1111-1111-111111111111")));

        Assert.Empty(theirs.Read(Sid));
    }

    [Fact]
    public void RecordRefused_InAnotherTenantWithTheSameIds_NeitherTouchesTheOther()
    {
        var mine = Store(new FixedTenantContext(TenantId.Local));
        var theirs = Store(new FixedTenantContext(new TenantId("11111111-1111-1111-1111-111111111111")));
        mine.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);

        // Same session id, same prompt id, a refusal: in the other account it is a receipt of its own.
        Assert.True(theirs.RecordRefused("dir-elsewhere", Sid, "p1", BusyRefusal, "agent", At(5), T0));
        Assert.Equal(0, theirs.RecordTurnStarted("dir-elsewhere", Sid, "t1", new[] { "p1" }, At(6)));

        Assert.Equal(ApiModeReceiptState.Delivered, Only(mine).State);
        Assert.Null(Only(mine).TurnId);
        Assert.Equal(ApiModeReceiptState.Refused, Assert.Single(theirs.Read(Sid)).State);
    }

    [Fact]
    public void PurgeOlderThan_InOneTenant_LeavesAnotherTenantsReceipts()
    {
        var mine = Store(new FixedTenantContext(TenantId.Local));
        var theirs = Store(new FixedTenantContext(new TenantId("11111111-1111-1111-1111-111111111111")));
        mine.RecordQueued(Director, Sid, "p1", "turnRunning", At(1), T0);
        theirs.RecordQueued(Director, Sid, "p1", "turnRunning", At(1), T0);

        Assert.Equal(1, mine.PurgeOlderThan(_clock.AddDays(1)));

        Assert.Empty(mine.Read(Sid));
        Assert.Single(theirs.Read(Sid));
    }

    // ----- retention -----

    [Fact]
    public void PurgeOlderThan_RemovesOnlyReceiptsLastChangedBeforeTheCutoff()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "old", "turnRunning", At(1), T0);
        _clock = _clock.AddDays(100);
        store.RecordQueued(Director, Sid, "new", "turnRunning", At(2), T0);

        var removed = store.PurgeOlderThan(_clock - ApiModeReceiptStore.Retention);

        Assert.Equal(1, removed);
        Assert.Equal("new", Only(store).PromptId);
    }

    // ----- a malformed change is refused whole -----

    [Theory]
    [InlineData("empty-session")]
    [InlineData("long-prompt-id")]
    [InlineData("negative-sequence")]
    [InlineData("no-generation")]
    [InlineData("unset-time")]
    [InlineData("oversized-acknowledgement")]
    [InlineData("long-disposition")]
    public void RecordDelivered_AMalformedChange_IsRefusedAndNothingIsWritten(string fault)
    {
        var store = Store();
        var session = fault == "empty-session" ? "" : Sid;
        var prompt = fault == "long-prompt-id" ? new string('p', ApiModeReceiptStore.MaxIdLength + 1) : "p1";
        var at = fault == "unset-time" ? default : T0;
        var ack = fault == "oversized-acknowledgement" ? new string('x', ApiModeReceiptStore.MaxVerbatimLength + 1) : StartedAck;
        var disposition = fault == "long-disposition" ? new string('d', ApiModeReceiptStore.MaxWordLength + 1) : "started";

        Assert.ThrowsAny<ArgumentException>(() =>
        {
            var position = fault switch
            {
                "negative-sequence" => At(-1),
                "no-generation" => default,
                _ => At(1),
            };
            store.RecordDelivered(Director, session, prompt, disposition, ack, position, at);
        });

        Assert.Empty(store.Read(Sid));
    }

    [Fact]
    public void RecordRefused_AReasonAtTheLimit_IsStoredWholeNeverCut()
    {
        var store = Store();
        var reason = new string('r', ApiModeReceiptStore.MaxVerbatimLength);

        store.RecordRefused(Director, Sid, "p1", reason, "director", At(1), T0);

        Assert.Equal(reason, Only(store).RefusalReason);
    }
}
