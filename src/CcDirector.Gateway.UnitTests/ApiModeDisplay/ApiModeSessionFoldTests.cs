using CcDirector.Core.Tenancy;
using CcDirector.Gateway.ApiModeDisplay;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.ApiModeEvents.ApiModeEventTestData;

namespace CcDirector.Gateway.Tests.ApiModeDisplay;

/// <summary>
/// <see cref="ApiModeSessionFold"/> (mission #4070, phase 2) on its own: the verdict from the receipts, the wheel and
/// the held events, with no store and no feed. The recorded fixtures' outcomes, through the real ingest and store, are
/// in <see cref="ApiModeSessionVerdictTests"/>.
/// </summary>
public sealed class ApiModeSessionFoldTests
{
    private static readonly ApiModeEventReplay NothingHeld = new(null, Array.Empty<ApiModeHeldEvent>(), Complete: false);

    private static ApiModeEventReplay HeldWhole(params SessionEvent[] events)
        => new("gen-a", events.Select(e => new ApiModeHeldEvent("gen-a", e)).ToList(), Complete: true);

    private static ApiModeVerdictReceipt Delivered(string prompt, string? turn = null, string? outcome = null, string? reason = null)
        => new(prompt, ApiModeReceiptState.Delivered, null, turn, outcome, reason);

    private static ApiModeVerdictReceipt QueuedFor(string prompt, string reason)
        => new(prompt, ApiModeReceiptState.Queued, reason, null, null, null);

    [Fact]
    public void Fold_ATerminalModeRow_HasNoVerdict()
    {
        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Terminal, WheelHolders.None, sessionEnded: false, new[] { Delivered("p1", "t1") }, NothingHeld);

        // Assert
        Assert.Null(verdict);
    }

    [Fact]
    public void Fold_AnOpenTurnWhileTheProtocolHoldsTheWheel_IsWorkingWithTheTurnNumber()
    {
        // Arrange
        var receipts = new[] { Delivered("p1", "t1", "done"), Delivered("p2", "t2") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.Protocol, sessionEnded: false, receipts, HeldWhole(Ready(1)))!;

        // Assert
        Assert.Equal("working", verdict.Kind);
        Assert.Equal("green", verdict.Tone);
        Assert.Equal("API mode", verdict.ConnectionLabel);
        Assert.True(verdict.Working);
        Assert.Equal(2, verdict.TurnNumber);
        Assert.Equal("Working - turn 2", verdict.ActivityLabel);
        Assert.Null(verdict.LastTurnEnding);
        Assert.True(verdict.AgentConnected);
        Assert.Equal("Agent connected (gpt-5.6-sol)", verdict.AgentLabel);
    }

    [Fact]
    public void Fold_TheUserHasTheWheel_SaysYouHaveTheWheelAndWhyPromptsWait()
    {
        // Arrange
        var receipts = new[] { Delivered("p1", "t1", "done"), QueuedFor("p2", "userHasTheWheel"), QueuedFor("p3", "userHasTheWheel") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.User, sessionEnded: false, receipts, NothingHeld)!;

        // Assert
        Assert.Equal("userHasTheWheel", verdict.Kind);
        Assert.True(verdict.UserHasTheWheel);
        Assert.Equal("API mode - You have the wheel", verdict.ConnectionLabel);
        Assert.Equal(2, verdict.QueuedCount);
        Assert.Equal("2 prompts waiting: you have the wheel", verdict.QueueLabel);
        Assert.Equal("Idle after turn 1", verdict.ActivityLabel);
        Assert.False(verdict.AgentConnected);
        Assert.Equal("The agent runs in your terminal", verdict.AgentLabel);
    }

    [Fact]
    public void Fold_PromptsWaitingForDifferentReasons_CountsEachReason()
    {
        // Arrange
        var receipts = new[] { QueuedFor("p1", "agentStarting"), QueuedFor("p2", "turnRunning"), QueuedFor("p3", "turnRunning") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.Protocol, sessionEnded: false, receipts, HeldWhole())!;

        // Assert
        Assert.Equal("3 prompts waiting: 1 because the agent is starting, 2 because a turn is running", verdict.QueueLabel);
        Assert.Equal("Idle - no turn yet", verdict.ActivityLabel);
        Assert.Null(verdict.TurnNumber);
    }

    [Fact]
    public void Fold_OnePromptWaiting_SaysOnePrompt()
    {
        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.Protocol, sessionEnded: false, new[] { QueuedFor("p1", "turnRunning") }, HeldWhole())!;

        // Assert
        Assert.Equal(1, verdict.QueuedCount);
        Assert.Equal("1 prompt waiting: a turn is running", verdict.QueueLabel);
    }

    [Fact]
    public void Fold_AnOpenTurnWhenNoProcessHoldsTheWheel_SaysItDoesNotKnowRatherThanWorking()
    {
        // Arrange: a turn started and its end never arrived, and the Director says the protocol process is gone.
        var receipts = new[] { Delivered("p1", "t1") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.None, sessionEnded: false, receipts, NothingHeld)!;

        // Assert
        Assert.Null(verdict.Working);
        Assert.Equal("Not known: turn 1 started and its end never reached the Gateway", verdict.ActivityLabel);
        Assert.Equal("stopped", verdict.Kind);
        Assert.False(verdict.AgentConnected);
        Assert.Equal("No agent process is running", verdict.AgentLabel);
    }

    [Fact]
    public void Fold_TheWholeGenerationHeldAndNoConnectionYet_SaysTheAgentIsStarting()
    {
        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.Protocol, sessionEnded: false, new[] { QueuedFor("p1", "agentStarting") }, HeldWhole(Queued(1, "p1", PromptQueuedReason.AgentStarting)))!;

        // Assert
        Assert.False(verdict.AgentConnected);
        Assert.Equal("Agent starting", verdict.AgentLabel);
    }

    [Fact]
    public void Fold_AWheelHolderThisGatewayDoesNotKnow_SaysUnknown()
    {
        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, "somebodyElse", sessionEnded: false, Array.Empty<ApiModeVerdictReceipt>(), NothingHeld)!;

        // Assert
        Assert.Equal("unknown", verdict.Kind);
        Assert.Null(verdict.AgentConnected);
    }

    [Fact]
    public void Fold_ALaterTurnFinishedAfterAFailedOne_ReportsNoFailedEnding()
    {
        // Arrange
        var receipts = new[] { Delivered("p1", "t1", "failed", "the model errored"), Delivered("p2", "t2", "done") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.Protocol, sessionEnded: false, receipts, NothingHeld)!;

        // Assert
        Assert.Null(verdict.LastTurnEnding);
        Assert.Equal("Idle after turn 2", verdict.ActivityLabel);
    }

    [Fact]
    public void Fold_AReplayTrimmedWithNoGatewayRestart_SaysOnlyThatNoConnectionEventIsHeld()
    {
        // Arrange: the buffer still holds the session's later events but trimmed its oldest, the ConnectionReady among
        // them. The Gateway did not restart, so the words must not say it did (review finding 2).
        var trimmed = new ApiModeEventReplay("gen-a", new[] { new ApiModeHeldEvent("gen-a", Queued(40, "p9")) }, Complete: false);

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.Protocol, sessionEnded: false, new[] { QueuedFor("p9", "turnRunning") }, trimmed)!;

        // Assert
        Assert.Null(verdict.AgentConnected);
        Assert.Equal("Not known: the Gateway holds no connection event for this session. The Director reports the agent process holds the conversation", verdict.AgentLabel);
        Assert.DoesNotContain("started", verdict.AgentLabel, StringComparison.Ordinal);
    }

    // ----- the wheel button (a terminal-mode row has no verdict at all, so no button: Fold_ATerminalModeRow_HasNoVerdict) -----

    [Fact]
    public void Fold_TheProtocolHoldsTheWheelWhileATurnRuns_OffersTakeTheWheelDisabledWithTheReason()
    {
        // Arrange
        var receipts = new[] { Delivered("p1", "t1") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.Protocol, sessionEnded: false, receipts, HeldWhole(Ready(1)))!;

        // Assert
        Assert.True(verdict.Working);
        var wheel = Assert.IsType<ApiModeWheelAction>(verdict.WheelAction);
        Assert.Equal("takeTheWheel", wheel.Verb);
        Assert.Equal("Take the wheel", wheel.Label);
        Assert.False(wheel.Enabled);
        Assert.Equal("A turn is running. Stop it, or wait for it to end, before taking the wheel.", wheel.DisabledReason);
    }

    [Fact]
    public void Fold_TheProtocolHoldsTheWheelWhileIdle_OffersTakeTheWheelEnabled()
    {
        // Arrange
        var receipts = new[] { Delivered("p1", "t1", "done") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.Protocol, sessionEnded: false, receipts, HeldWhole(Ready(1)))!;

        // Assert
        var wheel = Assert.IsType<ApiModeWheelAction>(verdict.WheelAction);
        Assert.Equal("takeTheWheel", wheel.Verb);
        Assert.Equal("Take the wheel", wheel.Label);
        Assert.True(wheel.Enabled);
        Assert.Null(wheel.DisabledReason);
    }

    [Fact]
    public void Fold_TheProtocolHoldsTheWheelAndTheAgentIsDisconnected_OffersTakeTheWheelEnabledForTheDirectorToRule()
    {
        // Arrange: the agent process exited; whether the wheel can be taken now is the Director's to rule.
        var receipts = new[] { Delivered("p1", "t1", "failed", "Pi exited") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.Protocol, sessionEnded: false, receipts, HeldWhole(Ready(1), Gone(2, 137, "Pi exited")))!;

        // Assert
        Assert.False(verdict.AgentConnected);
        var wheel = Assert.IsType<ApiModeWheelAction>(verdict.WheelAction);
        Assert.Equal("takeTheWheel", wheel.Verb);
        Assert.True(wheel.Enabled);
        Assert.Null(wheel.DisabledReason);
    }

    [Fact]
    public void Fold_TheProtocolHoldsTheWheelAfterAGatewayRestart_OffersTakeTheWheelEnabled()
    {
        // Arrange: nothing held, so the agent process is not known; only a turn KNOWN to be running disables the button.
        var receipts = new[] { Delivered("p1", "t1", "done") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.Protocol, sessionEnded: false, receipts, NothingHeld)!;

        // Assert
        Assert.Null(verdict.AgentConnected);
        var wheel = Assert.IsType<ApiModeWheelAction>(verdict.WheelAction);
        Assert.Equal("takeTheWheel", wheel.Verb);
        Assert.True(wheel.Enabled);
        Assert.Null(wheel.DisabledReason);
    }

    [Fact]
    public void Fold_TheUserHasTheWheel_OffersHandBackEnabled()
    {
        // Arrange: even with a turn open in the terminal - the Director refuses a hand back mid-turn, not the Gateway.
        var receipts = new[] { Delivered("p1", "t1") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.User, sessionEnded: false, receipts, NothingHeld)!;

        // Assert
        var wheel = Assert.IsType<ApiModeWheelAction>(verdict.WheelAction);
        Assert.Equal("handBack", wheel.Verb);
        Assert.Equal("Hand back", wheel.Label);
        Assert.True(wheel.Enabled);
        Assert.Null(wheel.DisabledReason);
    }

    [Fact]
    public void Fold_NoProcessHoldsTheWheelOnALiveSession_OffersHandBackEnabled()
    {
        // Arrange: the user's terminal exited on its own and the session lives on; a hand back resumes the conversation
        // over the protocol (#4094).
        var receipts = new[] { Delivered("p1", "t1", "done") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, WheelHolders.None, sessionEnded: false, receipts, NothingHeld)!;

        // Assert
        var wheel = Assert.IsType<ApiModeWheelAction>(verdict.WheelAction);
        Assert.Equal("handBack", wheel.Verb);
        Assert.Equal("Hand back", wheel.Label);
        Assert.True(wheel.Enabled);
        Assert.Null(wheel.DisabledReason);
    }

    [Theory]
    [InlineData(WheelHolders.None)]
    [InlineData(WheelHolders.Protocol)]
    [InlineData(WheelHolders.User)]
    public void Fold_AnEndedSession_ShowsNoWheelButtonWhoeverHeldTheWheel(string wheelHolder)
    {
        // Arrange: a button on an ended session could never work (phase 1's #4088 pushes "none" once a session ends).
        var receipts = new[] { Delivered("p1", "t1", "done") };

        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, wheelHolder, sessionEnded: true, receipts, NothingHeld)!;

        // Assert
        Assert.Null(verdict.WheelAction);
    }

    [Fact]
    public void Fold_AWheelHolderThisGatewayDoesNotKnow_ShowsNoWheelButton()
    {
        // Act
        var verdict = ApiModeSessionFold.Fold(ConnectionModes.Api, "somebodyElse", sessionEnded: false, Array.Empty<ApiModeVerdictReceipt>(), NothingHeld)!;

        // Assert
        Assert.Null(verdict.WheelAction);
    }
}

/// <summary>
/// The verdict the roster carries for an API-mode session (mission #4070, phase 2), built the way production builds it:
/// the recorded fixtures' event sequences go through the REAL ingest into the REAL receipt store (over the harness's
/// SQLite file) and the REAL event buffer, and <see cref="ApiModeRowStamp"/> folds the row from what they hold. A
/// Gateway restart is a NEW, empty buffer over the SAME store - exactly what a restarted process has.
/// </summary>
public sealed class ApiModeSessionVerdictTests : IDisposable
{
    private static readonly TenantId Alice = new("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly TenantId Bob = new("bbbbbbbb-0000-4000-8000-00000000000b");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly AsyncLocalTenantContext _ambient = new();
    private readonly HostedTenantBoundary _boundary;
    private readonly GatewayDatabase _db;
    private readonly ApiModeReceiptStore _receipts;
    private ApiModeEventBuffer _buffer = new();

    public ApiModeSessionVerdictTests()
    {
        var path = _harness.LegacyPath("devices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _boundary = new HostedTenantBoundary(_ambient, new DeviceRegistry(_harness.Open(), path), hosted: false);
        _db = _harness.Open(_ambient);
        _receipts = new ApiModeReceiptStore(_db, () => T0.UtcDateTime);
    }

    public void Dispose() => _harness.Dispose();

    private ApiModeDisplayFacts Facts() => new(_buffer, new ApiModeVerdictReceipts(_db));

    private void Push(TenantId tenant, params SessionEvent[] events)
        => Assert.NotNull(new ApiModeEventIngest(_buffer, _receipts, _boundary).Accept(tenant, Director, A(events)));

    private void Push(params SessionEvent[] events) => Push(Alice, events);

    /// <summary>The Gateway restarts: the held events are gone, the receipts are not.</summary>
    private void RestartTheGateway() => _buffer = new ApiModeEventBuffer();

    private ApiModeSessionDisplay? Verdict(string wheel = WheelHolders.Protocol, TenantId? tenant = null, string mode = ConnectionModes.Api)
    {
        var row = new SessionDto { SessionId = Sid, ConnectionMode = mode, WheelHolder = wheel };
        ApiModeRowStamp.Stamp(new[] { row }, Facts(), tenant ?? Alice);
        return row.ApiMode;
    }

    // ----- the recorded fixtures' outcomes -----

    [Fact]
    public void Stamp_DeliveredThenDone_IsIdleAfterTurnOneAndConnected()
    {
        // Arrange
        Push(DeliveredThenDone());

        // Act
        var verdict = Verdict()!;

        // Assert
        Assert.Equal("idle", verdict.Kind);
        Assert.False(verdict.Working);
        Assert.Equal(1, verdict.TurnNumber);
        Assert.Equal("Idle after turn 1", verdict.ActivityLabel);
        Assert.Null(verdict.LastTurnEnding);
        Assert.Equal(0, verdict.QueuedCount);
        Assert.Null(verdict.QueueLabel);
        Assert.True(verdict.AgentConnected);
        Assert.Equal("Agent connected (gpt-5.6-sol)", verdict.AgentLabel);
    }

    [Fact]
    public void Stamp_RefusedByTheAgent_TheRefusalIsNoTurnAndNothingWaits()
    {
        // Arrange
        Push(RefusedByTheAgent());

        // Act
        var verdict = Verdict()!;

        // Assert
        Assert.Equal(1, verdict.TurnNumber);
        Assert.Equal("Idle after turn 1", verdict.ActivityLabel);
        Assert.Equal(0, verdict.QueuedCount);
    }

    [Fact]
    public void Stamp_RefusedBeforeAcknowledgement_NoTurnAndTheAgentIsGone()
    {
        // Arrange
        Push(RefusedByTheDirectorBeforeAcknowledgement());

        // Act
        var verdict = Verdict(WheelHolders.None)!;

        // Assert
        Assert.Equal("stopped", verdict.Kind);
        Assert.Null(verdict.TurnNumber);
        Assert.Equal("Idle - no turn yet", verdict.ActivityLabel);
        Assert.False(verdict.AgentConnected);
        Assert.Equal("Agent disconnected: Pi exited (exit code 137)", verdict.AgentLabel);
    }

    [Fact]
    public void Stamp_FailedMidTurn_SaysTheLastTurnFailedWithItsReason()
    {
        // Arrange
        Push(DeliveredThenFailedMidTurn());

        // Act
        var verdict = Verdict(WheelHolders.None)!;

        // Assert
        Assert.Equal("The last turn failed: Pi exited during the turn (exit code 137)", verdict.LastTurnEnding);
        Assert.Equal("red", verdict.Tone);
        Assert.False(verdict.Working);
        Assert.False(verdict.AgentConnected);
        Assert.Equal("Agent disconnected: Pi exited during the turn (exit code 137)", verdict.AgentLabel);
    }

    [Fact]
    public void Stamp_Cancelled_SaysTheLastTurnWasStopped()
    {
        // Arrange
        Push(DeliveredThenCancelled());

        // Act
        var verdict = Verdict()!;

        // Assert
        Assert.Equal("The last turn was stopped: stopped by the user", verdict.LastTurnEnding);
        Assert.Equal("idle", verdict.Kind);
    }

    [Fact]
    public void Stamp_QueuedWhileTheUserHasTheWheel_SaysYouHaveTheWheelAndWhyItWaits()
    {
        // Arrange
        Push(Queued(1, "p1", PromptQueuedReason.UserHasTheWheel));

        // Act
        var verdict = Verdict(WheelHolders.User)!;

        // Assert
        Assert.Equal("userHasTheWheel", verdict.Kind);
        Assert.Equal("API mode - You have the wheel", verdict.ConnectionLabel);
        Assert.Equal(1, verdict.QueuedCount);
        Assert.Equal("1 prompt waiting: you have the wheel", verdict.QueueLabel);
    }

    [Fact]
    public void Stamp_ATurnRunning_IsWorkingWithItsNumber()
    {
        // Arrange: turn 1 done, turn 2 running.
        Push(DeliveredThenDone());
        Push(Delivered(8, "p2", PlainAck), Started(9, "t2", "p2"));

        // Act
        var verdict = Verdict()!;

        // Assert
        Assert.Equal("working", verdict.Kind);
        Assert.True(verdict.Working);
        Assert.Equal("Working - turn 2", verdict.ActivityLabel);
    }

    // ----- a Gateway restart: the held events are gone, the receipts are not -----

    [Fact]
    public void Stamp_AfterAGatewayRestart_TheTurnAndItsEndingAreRebuiltFromTheReceiptsAndTheConnectionIsNotKnown()
    {
        // Arrange
        Push(DeliveredThenFailedMidTurn());
        RestartTheGateway();

        // Act
        var verdict = Verdict()!;

        // Assert: what the receipts support survives...
        Assert.Equal(1, verdict.TurnNumber);
        Assert.Equal("The last turn failed: Pi exited during the turn (exit code 137)", verdict.LastTurnEnding);
        Assert.False(verdict.Working);
        // ...and what only the events said is honestly not known.
        Assert.Null(verdict.AgentConnected);
        Assert.Equal("Not known: the Gateway holds no connection event for this session. The Director reports the agent process holds the conversation", verdict.AgentLabel);
    }

    [Fact]
    public void Stamp_AfterAGatewayRestartMidTurn_TheRunningTurnIsStillWorkingFromTheReceipts()
    {
        // Arrange
        Push(Ready(1), Delivered(2, "p1", PlainAck), Started(3, "t1", "p1"), Queued(4, "p2"));
        RestartTheGateway();

        // Act
        var verdict = Verdict()!;

        // Assert
        Assert.True(verdict.Working);
        Assert.Equal("Working - turn 1", verdict.ActivityLabel);
        Assert.Equal("1 prompt waiting: a turn is running", verdict.QueueLabel);
        Assert.Null(verdict.AgentConnected);
    }

    [Fact]
    public void Stamp_AfterAGatewayRestartWithNothingStored_IsIdleWithNoTurnAndNotAConfidentConnection()
    {
        // Arrange
        RestartTheGateway();

        // Act
        var verdict = Verdict()!;

        // Assert
        Assert.Null(verdict.TurnNumber);
        Assert.Null(verdict.AgentConnected);
    }

    // ----- tenant isolation -----

    [Fact]
    public void Stamp_AnotherAccountsSessionWithTheSameId_FoldsNothingOfIt()
    {
        // Arrange: Alice's session ran a failed turn and has a prompt waiting.
        Push(DeliveredThenFailedMidTurn());
        Push(Queued(7, "p2"));

        // Act: Bob's Director reports a row with the same session id.
        var bobs = Verdict(tenant: Bob)!;
        var alices = Verdict()!;

        // Assert
        Assert.Null(bobs.TurnNumber);
        Assert.Null(bobs.LastTurnEnding);
        Assert.Equal(0, bobs.QueuedCount);
        Assert.Null(bobs.AgentConnected);
        Assert.Equal(1, alices.TurnNumber);
        Assert.Equal(1, alices.QueuedCount);
        Assert.NotNull(alices.LastTurnEnding);
    }

    // ----- what the stamp does to every row -----

    [Fact]
    public void Stamp_ATerminalModeRowCarryingAStaleVerdict_IsClearedAndReadsNothing()
    {
        // Arrange
        Push(DeliveredThenDone());
        var row = new SessionDto { SessionId = Sid, ConnectionMode = ConnectionModes.Terminal, ApiMode = new ApiModeSessionDisplay { Kind = "working" } };

        // Act
        ApiModeRowStamp.Stamp(new[] { row }, Facts(), Alice);

        // Assert
        Assert.Null(row.ApiMode);
    }

    [Fact]
    public void Stamp_NoFactsGiven_StampsNoVerdictRatherThanOneFoldedFromNothing()
    {
        // Arrange
        var row = new SessionDto { SessionId = Sid, ConnectionMode = ConnectionModes.Api, WheelHolder = WheelHolders.Protocol };

        // Act
        ApiModeRowStamp.Stamp(new[] { row }, facts: null, Alice);

        // Assert
        Assert.Null(row.ApiMode);
    }
}
