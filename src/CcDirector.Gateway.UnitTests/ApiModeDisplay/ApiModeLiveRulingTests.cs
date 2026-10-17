using CcDirector.Gateway.ApiModeDisplay;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Receipts;
using Xunit;
using static CcDirector.Gateway.Tests.ApiModeEvents.ApiModeEventTestData;

namespace CcDirector.Gateway.Tests.ApiModeDisplay;

/// <summary>
/// <see cref="ApiModeLiveRuling"/> (mission #4070, phase 2): the words and items a client renders verbatim for each
/// live event and each receipt. The event sequences are the ones the event model's Pi mapping makes of the recorded
/// fixtures (<c>src/CcDirector.Core.Tests/TestData/pi-rpc/</c>), from <see cref="Tests.ApiModeEvents.ApiModeEventTestData"/>.
/// Pure: no store, no clock, no feed.
/// </summary>
public sealed class ApiModeLiveRulingTests
{
    private static List<ApiModeLiveItem> RuleAll(ApiModeLiveRuling ruling, IEnumerable<SessionEvent> events)
        => events.SelectMany(ruling.Rule).ToList();

    private static List<ApiModeLiveItem> RuleAll(IEnumerable<SessionEvent> events) => RuleAll(new ApiModeLiveRuling(), events);

    /// <summary>A receipt as the store answers it to a reader starting from it: last changed by the event at
    /// <paramref name="lastSequence"/> of generation A.</summary>
    private static ApiModeStoredReceipt Stored(ApiModeReceiptDto receipt, long lastSequence)
        => new(receipt, new ApiModeEventPosition("gen-a", GenAStarted, lastSequence));

    /// <summary>An event as the feed hands it to a reader: held in generation A.</summary>
    private static ApiModeHeldEvent HeldA(SessionEvent e) => new("gen-a", GenAStarted, e);

    private static List<string> ReceiptWordsOf(IEnumerable<ApiModeLiveItem> items, string promptId)
        => items.Where(i => i.ItemId == "receipt:" + promptId).Select(i => i.Text).ToList();

    private static ToolCallStarted Tool(long seq, string turn, string id, string name = "bash", string input = """{"command":"ls"}""")
        => new() { SessionId = Sid, Sequence = seq, At = T0, TurnId = turn, ToolCallId = id, ToolName = name, Kind = ToolCallKind.Execute, Input = input };

    private static ToolCallFinished ToolDone(long seq, string turn, string id, ToolCallOutcome outcome)
        => new() { SessionId = Sid, Sequence = seq, At = T0, TurnId = turn, ToolCallId = id, Outcome = outcome, Output = "", DurationMilliseconds = 12 };

    // ----- the recorded fixtures' outcomes -----

    [Fact]
    public void Rule_DeliveredThenDone_ConnectsDeliversStreamsTheReplyAndEndsTheTurn()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();

        // Act
        var items = RuleAll(ruling, DeliveredThenDone());

        // Assert
        Assert.Collection(items,
            i => { Assert.Equal(ApiModeLiveItemKinds.Connection, i.Kind); Assert.Equal("Agent connected (gpt-5.6-sol)", i.Text); Assert.Equal("green", i.Tone); },
            i => { Assert.Equal("receipt:p1", i.ItemId); Assert.Equal("Delivered", i.Text); Assert.Equal("green", i.Tone); Assert.Equal("p1", i.PromptId); },
            i => { Assert.Equal("reply:m1", i.ItemId); Assert.Equal(ApiModeLiveItemModes.Append, i.Mode); Assert.Equal("PO", i.Text); },
            i => { Assert.Equal("reply:m1", i.ItemId); Assert.Equal(ApiModeLiveItemModes.Append, i.Mode); Assert.Equal("NG", i.Text); },
            i => { Assert.Equal("reply:m1", i.ItemId); Assert.Equal(ApiModeLiveItemModes.Replace, i.Mode); Assert.Equal("PONG", i.Text); },
            i => { Assert.Equal("turn:t1", i.ItemId); Assert.Equal(ApiModeLiveItemKinds.TurnEnded, i.Kind); Assert.Equal("Turn finished", i.Text); Assert.Equal("neutral", i.Tone); });
    }

    [Fact]
    public void Rule_RefusedByTheAgent_TheReceiptIsRefusedInTheAgentsOwnWords()
    {
        // Act
        var items = RuleAll(RefusedByTheAgent());

        // Assert
        var refused = Assert.Single(items, i => i.ItemId == "receipt:p2");
        Assert.Equal("Refused: " + RefusalBusy, refused.Text);
        Assert.Equal("red", refused.Tone);
        Assert.Equal(new[] { "Delivered" }, ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_RefusedBeforeAcknowledgement_TheReceiptIsRefusedAndTheAgentIsGone()
    {
        // Act
        var items = RuleAll(RefusedByTheDirectorBeforeAcknowledgement());

        // Assert
        Assert.Equal(new[] { "Refused: Pi exited before acknowledging" }, ReceiptWordsOf(items, "p1"));
        var gone = items.Last();
        Assert.Equal(ApiModeLiveItemKinds.Connection, gone.Kind);
        Assert.Equal("Agent disconnected: Pi exited (exit code 137)", gone.Text);
        Assert.Equal("Exit code 137", gone.Detail);
        Assert.Equal("red", gone.Tone);
        Assert.DoesNotContain(items, i => i.Kind == ApiModeLiveItemKinds.TurnEnded);
    }

    [Fact]
    public void Rule_FailedMidTurn_TheReceiptShowsTheFailedTurnBesideDelivered()
    {
        // Act
        var items = RuleAll(DeliveredThenFailedMidTurn());

        // Assert: rule 5 - never Delivered alone once the turn failed.
        Assert.Equal(new[] { "Delivered", "Delivered - the turn failed: Pi exited during the turn (exit code 137)" }, ReceiptWordsOf(items, "p1"));
        Assert.Equal("red", items.Last(i => i.ItemId == "receipt:p1").Tone);
        var turn = Assert.Single(items, i => i.ItemId == "turn:t1");
        Assert.Equal("Turn failed: Pi exited during the turn (exit code 137)", turn.Text);
        Assert.Equal("red", turn.Tone);
    }

    [Fact]
    public void Rule_Cancelled_TheThoughtIsHiddenAndTheReceiptReadsDeliveredStopped()
    {
        // Act
        var items = RuleAll(DeliveredThenCancelled());

        // Assert
        Assert.DoesNotContain(items, i => i.Text.Contains("Considering rivers", StringComparison.Ordinal));
        Assert.DoesNotContain(items, i => i.Kind == ApiModeLiveItemKinds.ReplyText);
        Assert.Equal(new[] { "Delivered", "Delivered - stopped" }, ReceiptWordsOf(items, "p1"));
        Assert.Equal("yellow", items.Last(i => i.ItemId == "receipt:p1").Tone);
        Assert.Equal("Turn stopped: stopped by the user", Assert.Single(items, i => i.ItemId == "turn:t1").Text);
    }

    [Fact]
    public void Rule_QueuedWhileTheUserHasTheWheel_TheReceiptSaysWhyThenDelivered()
    {
        // Arrange
        var events = new SessionEvent[]
        {
            Queued(1, "p1", PromptQueuedReason.UserHasTheWheel),
            Ready(2),
            Delivered(3, "p1", PlainAck),
        };

        // Act
        var items = RuleAll(events);

        // Assert
        Assert.Equal(new[] { "Queued: you have the wheel", "Delivered" }, ReceiptWordsOf(items, "p1"));
        Assert.Equal("neutral", items.First(i => i.ItemId == "receipt:p1").Tone);
    }

    [Theory]
    [InlineData(PromptQueuedReason.TurnRunning, "Queued: a turn is running")]
    [InlineData(PromptQueuedReason.AgentStarting, "Queued: the agent is starting")]
    public void Rule_QueuedForAnotherReason_SaysThatReason(PromptQueuedReason reason, string expected)
    {
        // Act
        var item = Assert.Single(new ApiModeLiveRuling().Rule(Queued(1, "p1", reason)));

        // Assert
        Assert.Equal(expected, item.Text);
    }

    [Fact]
    public void Rule_QueuedAgainWithTheSameReason_ShowsNothingNew()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Queued(1, "p1"));

        // Act
        var items = ruling.Rule(Queued(2, "p1"));

        // Assert
        Assert.Empty(items);
    }

    // ----- tool cards -----

    [Theory]
    [InlineData(ToolCallOutcome.Succeeded, "done", "green")]
    [InlineData(ToolCallOutcome.Failed, "failed", "red")]
    [InlineData(ToolCallOutcome.Cancelled, "stopped", "yellow")]
    public void Rule_AToolStartsThenFinishes_TheCardRunsThenShowsItsStatusWords(ToolCallOutcome outcome, string status, string tone)
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();

        // Act
        var running = Assert.Single(ruling.Rule(Tool(1, "t1", "c1")));
        var finished = Assert.Single(ruling.Rule(ToolDone(2, "t1", "c1", outcome)));

        // Assert
        Assert.Equal("tool:c1", running.ItemId);
        Assert.Equal(ApiModeLiveItemKinds.ToolCard, running.Kind);
        Assert.Equal("bash", running.Text);
        Assert.Equal("running", running.Status);
        Assert.Equal("""{"command":"ls"}""", running.Detail);
        Assert.Equal("tool:c1", finished.ItemId);
        Assert.Equal(ApiModeLiveItemModes.Replace, finished.Mode);
        Assert.Equal("bash", finished.Text);
        Assert.Equal(status, finished.Status);
        Assert.Equal(tone, finished.Tone);
        Assert.Equal("""{"command":"ls"}""", finished.Detail);
    }

    [Fact]
    public void Rule_AToolFinishesWhoseStartThisReaderMissed_SaysSoAndGuessesNoTitle()
    {
        // Act
        var finished = Assert.Single(new ApiModeLiveRuling().Rule(ToolDone(9, "t1", "c1", ToolCallOutcome.Succeeded)));

        // Assert
        Assert.Equal("A tool that started before this view began", finished.Text);
        Assert.Equal("done", finished.Status);
        Assert.Null(finished.Detail);
    }

    // ----- a reader that starts from the stored receipts -----

    [Fact]
    public void Rule_SeededFromStoredReceipts_ATurnFailureRewritesTheStoredReceipt()
    {
        // Arrange: after an incomplete replay the reader starts from the store, which says p1 is delivered and carried
        // by t1, still running.
        var stored = new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Delivered, Disposition = "started", TurnId = "t1" };
        var ruling = new ApiModeLiveRuling(new[] { Stored(stored, 30) });

        // Act
        var items = ruling.Rule(HeldA(Ended(40, "t1", TurnOutcome.Failed, "Pi exited during the turn (exit code 137)", null)));

        // Assert
        Assert.Equal(new[] { "Delivered - the turn failed: Pi exited during the turn (exit code 137)" }, ReceiptWordsOf(items, "p1"));
        Assert.Null(stored.TurnOutcome); // the caller's receipt is not changed
    }

    [Fact]
    public void ReceiptItem_AStoredReceipt_IsWordedAsTheLiveRulingWordsIt()
    {
        // Arrange
        var stored = new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Delivered, TurnId = "t1", TurnOutcome = "cancelled", TurnOutcomeReason = "stopped by the user" };

        // Act
        var item = ApiModeLiveRuling.ReceiptItem(stored, 7);

        // Assert
        Assert.Equal("receipt:p1", item.ItemId);
        Assert.Equal("Delivered - stopped", item.Text);
        Assert.Equal("yellow", item.Tone);
        Assert.Equal(7, item.Sequence);
    }

    // ----- rule 1: one ending per prompt -----

    [Fact]
    public void Rule_ARefusalAfterDelivery_TheFirstEndingStands()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Delivered(1, "p1", PlainAck));

        // Act
        var items = ruling.Rule(Refused(2, "p1", "too late", PromptRefusedBy.Director));

        // Assert
        Assert.Empty(items);
    }

    [Fact]
    public void Rule_TheEndingAStoredReceiptWasBuiltFrom_IsNotLoggedAsASecondEnding()
    {
        // Arrange: a reader started from the store, which wrote p1's receipt from the very delivery now replayed to it
        // (review of pull request #4083, finding 2).
        var ruling = new ApiModeLiveRuling(new[] { Stored(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Delivered, Disposition = "started" }, 5) });

        // Act
        var items = ruling.Rule(HeldA(Delivered(5, "p1", PlainAck)));

        // Assert
        Assert.Empty(items);
        Assert.Equal(0, ruling.SecondEndingsLogged);
    }

    [Fact]
    public void Rule_AnEndingAfterTheOneAStoredReceiptWasBuiltFrom_IsLoggedAsASecondEnding()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling(new[] { Stored(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Delivered, Disposition = "started" }, 5) });
        ruling.Rule(HeldA(Delivered(5, "p1", PlainAck)));

        // Act: the replay of the stored ending is quiet; a refusal after it is a real second ending.
        var items = ruling.Rule(HeldA(Refused(6, "p1", "too late", PromptRefusedBy.Director)));

        // Assert
        Assert.Empty(items);
        Assert.Equal(1, ruling.SecondEndingsLogged);
    }

    [Fact]
    public void Rule_AStoredDeliveryThenTheRefusalItSuperseded_ChangesNothingAndIsNotLogged()
    {
        // Arrange: the store says Delivered - a retry's delivery that superseded a refusal - and the reader is handed
        // the events the store has already ruled on, the superseded refusal first.
        var ruling = new ApiModeLiveRuling(new[] { Stored(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Delivered, Disposition = "started" }, 5) });

        // Act
        var refusal = ruling.Rule(HeldA(Refused(1, "p1", "Pi is not running: the process exited", PromptRefusedBy.Director)));
        var delivery = ruling.Rule(HeldA(Delivered(5, "p1", PlainAck)));

        // Assert
        Assert.Empty(refusal);
        Assert.Empty(delivery);
        Assert.Equal(0, ruling.SecondEndingsLogged);
        Assert.Equal(0, ruling.SupersedesLogged);
    }

    [Fact]
    public void Rule_ASecondEndingWithNoStoredReceipt_IsLogged()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Delivered(1, "p1", PlainAck));

        // Act
        ruling.Rule(Delivered(2, "p1", PlainAck));

        // Assert
        Assert.Equal(1, ruling.SecondEndingsLogged);
    }

    [Fact]
    public void Rule_ADeliveryAfterADelivery_TheFirstStandsAndItIsLogged()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Delivered(1, "p1", PlainAck));

        // Act
        var items = ruling.Rule(Delivered(2, "p1", RefusalFirstAck));

        // Assert
        Assert.Empty(items);
        Assert.Equal(1, ruling.SecondEndingsLogged);
        Assert.Equal(0, ruling.SupersedesLogged);
    }

    // ----- a retry of a delivery that was not delivered reuses its Gateway id: the later receipt stands -----
    // (event model section 2, "What a promptId is"; phase 2 phase review, finding 1)

    private const string PiNotRunning = "Pi is not running: the process exited";

    [Fact]
    public void Rule_RefusedThenDeliveredOnRetry_TheReceiptReadsDelivered()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();
        var refused = ruling.Rule(Refused(1, "gw-1", PiNotRunning, PromptRefusedBy.Director));

        // Act
        var delivered = ruling.Rule(Delivered(5, "gw-1", PlainAck));

        // Assert
        Assert.Equal(new[] { "Refused: " + PiNotRunning }, ReceiptWordsOf(refused, "gw-1"));
        var item = Assert.Single(delivered);
        Assert.Equal("receipt:gw-1", item.ItemId);
        Assert.Equal("Delivered", item.Text);
        Assert.Equal("green", item.Tone);
        Assert.Equal(5, item.Sequence);
        Assert.Equal(1, ruling.SupersedesLogged);
        Assert.Equal(0, ruling.SecondEndingsLogged);
    }

    [Fact]
    public void Rule_RefusedThenDeliveredOnRetryThenTheTurnFailed_TheReceiptShowsTheFailedTurn()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();

        // Act
        var items = RuleAll(ruling, new SessionEvent[]
        {
            Refused(1, "gw-1", PiNotRunning, PromptRefusedBy.Director),
            Delivered(5, "gw-1", PlainAck),
            Started(6, "t1", "gw-1"),
            Ended(9, "t1", TurnOutcome.Failed, "Pi exited during the turn (exit code 137)", null),
        });

        // Assert
        Assert.Equal(
            new[] { "Refused: " + PiNotRunning, "Delivered", "Delivered - the turn failed: Pi exited during the turn (exit code 137)" },
            ReceiptWordsOf(items, "gw-1"));
    }

    [Fact]
    public void Rule_RefusedThenRefusedAgainOnRetry_TheLaterReasonStands()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Refused(1, "gw-1", PiNotRunning, PromptRefusedBy.Director));

        // Act
        var items = ruling.Rule(Refused(4, "gw-1", RefusalBusy, PromptRefusedBy.Agent));

        // Assert
        Assert.Equal(new[] { "Refused: " + RefusalBusy }, ReceiptWordsOf(items, "gw-1"));
        Assert.Equal(1, ruling.SupersedesLogged);
    }

    [Fact]
    public void Rule_RefusedThenQueuedOnRetry_TheReceiptReadsQueued()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Refused(1, "gw-1", PiNotRunning, PromptRefusedBy.Director));

        // Act
        var items = ruling.Rule(Queued(3, "gw-1", PromptQueuedReason.TurnRunning));

        // Assert
        Assert.Equal(new[] { "Queued: a turn is running" }, ReceiptWordsOf(items, "gw-1"));
    }

    [Fact]
    public void Rule_AnOlderRefusalHandedOverAfterTheRetrysDelivery_ChangesNothing()
    {
        // Arrange: refused at 1, delivered on retry at 5.
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Refused(1, "gw-1", PiNotRunning, PromptRefusedBy.Director));
        ruling.Rule(Delivered(5, "gw-1", PlainAck));

        // Act: the first refusal again, and a refusal with a lower sequence than the delivery.
        var replayed = ruling.Rule(Refused(1, "gw-1", PiNotRunning, PromptRefusedBy.Director));
        var older = ruling.Rule(Refused(3, "gw-1", "an earlier attempt", PromptRefusedBy.Director));

        // Assert: neither overwrites, and neither is a second ending - they are older, not later.
        Assert.Empty(replayed);
        Assert.Empty(older);
        Assert.Equal(0, ruling.SecondEndingsLogged);
    }

    [Fact]
    public void Rule_AStoredRefusalThenTheRetrysDelivery_TheReceiptReadsDelivered()
    {
        // Arrange: a reader started from the store, which holds the refusal; the retry is acknowledged after the read.
        var ruling = new ApiModeLiveRuling(new[] { Stored(new ApiModeReceiptDto { PromptId = "gw-1", State = ApiModeReceiptState.Refused, RefusalReason = PiNotRunning, RefusedBy = "director" }, 1) });

        // Act
        var items = ruling.Rule(HeldA(Delivered(5, "gw-1", PlainAck)));

        // Assert
        Assert.Equal(new[] { "Delivered" }, ReceiptWordsOf(items, "gw-1"));
        Assert.Equal(1, ruling.SupersedesLogged);
    }

    [Fact]
    public void Rule_AStoredRefusalThenTheRetryQueuedAndRefusedLive_ShowsTheRetrysReason()
    {
        // Arrange: the review of pull request #4142, finding 1. Refused at 40 while the Director restarted Pi; the reader
        // starts from the store, which holds that refusal; the user presses again and the retry arrives live.
        var ruling = new ApiModeLiveRuling(new[] { Stored(new ApiModeReceiptDto { PromptId = "gw-1", State = ApiModeReceiptState.Refused, RefusalReason = PiNotRunning, RefusedBy = "director" }, 40) });

        // Act
        var queued = ruling.Rule(HeldA(Queued(41, "gw-1")));
        var refused = ruling.Rule(HeldA(Refused(42, "gw-1", RefusalBusy, PromptRefusedBy.Agent)));

        // Assert: each is the retry, newer than what the store held, so the stream shows the second reason - the one the
        // receipt store now holds and the receipts route answers.
        Assert.Equal(new[] { "Queued: a turn is running" }, ReceiptWordsOf(queued, "gw-1"));
        Assert.Equal(new[] { "Refused: " + RefusalBusy }, ReceiptWordsOf(refused, "gw-1"));
        Assert.Equal(1, ruling.SupersedesLogged);
    }

    [Fact]
    public void Rule_TheEventsAStoredRefusalWasBuiltFromReplayed_AreQuietAndLogNoSupersede()
    {
        // Arrange: refused twice - reason A at 2, then the retry's reason B at 6 - and the store holds the second. The
        // reader starting from it is handed every one of those events again (the review's note 2).
        var ruling = new ApiModeLiveRuling(new[] { Stored(new ApiModeReceiptDto { PromptId = "gw-1", State = ApiModeReceiptState.Refused, RefusalReason = RefusalBusy, RefusedBy = "agent" }, 6) });

        // Act
        var items = new SessionEvent[]
        {
            Queued(1, "gw-1"),
            Refused(2, "gw-1", PiNotRunning, PromptRefusedBy.Director),
            Queued(4, "gw-1"),
            Refused(6, "gw-1", RefusalBusy, PromptRefusedBy.Agent),
        }.SelectMany(e => ruling.Rule(HeldA(e))).ToList();

        // Assert: nothing is shown, nothing is a supersede, nothing is a second ending.
        Assert.Empty(items);
        Assert.Equal(0, ruling.SupersedesLogged);
        Assert.Equal(0, ruling.SecondEndingsLogged);
    }

    [Fact]
    public void Rule_AStoredRefusalThenTheRetryInALaterGeneration_IsTheRetryWhateverItsSequence()
    {
        // Arrange: refused at 40 of generation A; the Director restarted, and the retry is sequence 3 of generation B.
        var ruling = new ApiModeLiveRuling(new[] { Stored(new ApiModeReceiptDto { PromptId = "gw-1", State = ApiModeReceiptState.Refused, RefusalReason = PiNotRunning, RefusedBy = "director" }, 40) });

        // Act
        var items = ruling.Rule(new ApiModeHeldEvent("gen-b", GenBStarted, Delivered(3, "gw-1", PlainAck)));

        // Assert
        Assert.Equal(new[] { "Delivered" }, ReceiptWordsOf(items, "gw-1"));
    }

    [Fact]
    public void Rule_AReceiptEventForAStoredPromptWithoutItsPosition_Throws()
    {
        // Arrange: without the position, the ruling cannot tell a replay from the retry, and it does not guess.
        var ruling = new ApiModeLiveRuling(new[] { Stored(new ApiModeReceiptDto { PromptId = "gw-1", State = ApiModeReceiptState.Refused, RefusalReason = PiNotRunning, RefusedBy = "director" }, 40) });

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => ruling.Rule(Refused(42, "gw-1", RefusalBusy, PromptRefusedBy.Agent)));
    }

    // ----- what the ruling remembers (review of pull request #4083, finding 1) -----

    [Fact]
    public void Remembers_AToolFinishWhoseStartItSaw_IsTrue()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Tool(3, "t1", "c1"));

        // Act + Assert
        Assert.True(ruling.Remembers(ToolDone(4, "t1", "c1", ToolCallOutcome.Succeeded)));
    }

    [Fact]
    public void Remembers_AToolFinishWhoseStartItNeverSaw_IsFalse()
    {
        // Act + Assert
        Assert.False(new ApiModeLiveRuling().Remembers(ToolDone(4, "t1", "c1", ToolCallOutcome.Succeeded)));
    }

    [Fact]
    public void Remembers_ATurnEndWhoseStartAndDeliveriesItSaw_IsTrue()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();
        RuleAll(ruling, new SessionEvent[] { Delivered(1, "p1", PlainAck), Started(2, "t1", "p1") });

        // Act + Assert
        Assert.True(ruling.Remembers(Ended(3, "t1", TurnOutcome.Failed, "the model errored", null)));
    }

    [Fact]
    public void Remembers_ATurnEndWhoseStartItNeverSaw_IsFalse()
    {
        // Act + Assert
        Assert.False(new ApiModeLiveRuling().Remembers(Ended(3, "t1", TurnOutcome.Failed, "the model errored", null)));
    }

    [Fact]
    public void Remembers_ATurnEndWhoseStartItSawButNotADelivery_IsFalse()
    {
        // Arrange: the delivery of p1 was let go before the turn's start.
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Started(2, "t1", "p1"));

        // Act + Assert
        Assert.False(ruling.Remembers(Ended(3, "t1", TurnOutcome.Failed, "the model errored", null)));
    }

    [Fact]
    public void Remembers_ATurnEndOfATurnInTheStoredReceipts_IsTrue()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling(new[] { Stored(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Delivered, TurnId = "t1" }, 2) });

        // Act + Assert
        Assert.True(ruling.Remembers(Ended(3, "t1", TurnOutcome.Failed, "the model errored", null)));
    }

    [Fact]
    public void Remembers_AnEventThatBuildsOnNothingEarlier_IsTrue()
    {
        // Act + Assert
        Assert.True(new ApiModeLiveRuling().Remembers(Delivered(1, "p1", PlainAck)));
    }

    // ----- the words of a stored receipt -----

    [Theory]
    [InlineData(ApiModeReceiptState.Queued, "userHasTheWheel", null, null, null, "Queued: you have the wheel")]
    [InlineData(ApiModeReceiptState.Delivered, null, null, null, null, "Delivered")]
    [InlineData(ApiModeReceiptState.Delivered, null, "done", null, null, "Delivered")]
    [InlineData(ApiModeReceiptState.Delivered, null, "failed", "the model errored", null, "Delivered - the turn failed: the model errored")]
    [InlineData(ApiModeReceiptState.Delivered, null, "cancelled", "stopped by the user", null, "Delivered - stopped")]
    [InlineData(ApiModeReceiptState.Refused, null, null, null, "Pi exited before acknowledging", "Refused: Pi exited before acknowledging")]
    public void ReceiptWords_EachState_IsTheEventModelsWords(ApiModeReceiptState state, string? queueReason, string? outcome, string? outcomeReason, string? refusal, string expected)
    {
        // Arrange
        var receipt = new ApiModeReceiptDto { PromptId = "p1", State = state, QueueReason = queueReason, TurnOutcome = outcome, TurnOutcomeReason = outcomeReason, RefusalReason = refusal };

        // Act
        var words = ApiModeLiveRuling.ReceiptWords(receipt);

        // Assert
        Assert.Equal(expected, words);
    }

    [Fact]
    public void ReceiptWords_AQueueReasonThisGatewayDoesNotKnow_SaysSoRatherThanGuessing()
    {
        // Act
        var words = ApiModeLiveRuling.ReceiptWords(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Queued, QueueReason = "rateLimited" });

        // Assert
        Assert.Equal("Queued: the Director gave a reason this Gateway does not know (rateLimited)", words);
    }

    [Fact]
    public void ShowsThoughts_TheGatewaysRuling_IsHidden()
    {
        // Act
        var items = new ApiModeLiveRuling().Rule(Text(1, "t1", "thinking", channel: MessageTextChannel.Thought));

        // Assert
        Assert.False(ApiModeLiveRuling.ShowsThoughts);
        Assert.Empty(items);
    }
}
