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
    /// <summary>Every event as the feed hands it, held in generation A: a connection line is named by its generation.</summary>
    private static List<ApiModeLiveItem> RuleAll(ApiModeLiveRuling ruling, IEnumerable<SessionEvent> events)
        => events.SelectMany(e => ruling.Rule(HeldA(e))).ToList();

    private static List<ApiModeLiveItem> RuleAll(IEnumerable<SessionEvent> events) => RuleAll(new ApiModeLiveRuling(), events);

    /// <summary>A receipt as the store answers it to a reader starting from it: last changed by the event at
    /// <paramref name="lastSequence"/> of generation A.</summary>
    private static ApiModeStoredReceipt Stored(ApiModeReceiptDto receipt, long lastSequence)
        => new(receipt, new ApiModeEventPosition("gen-a", GenAStarted, lastSequence));

    /// <summary>An event as the feed hands it to a reader: held in generation A.</summary>
    private static ApiModeHeldEvent HeldA(SessionEvent e) => new("gen-a", GenAStarted, e);

    /// <summary>An event as the feed hands it to a reader: held in generation B, which started after A (a Director
    /// restart).</summary>
    private static ApiModeHeldEvent HeldB(SessionEvent e) => new("gen-b", GenBStarted, e);

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
        Assert.Equal(new[] { "Queued, 1st in line: you have the wheel", "Delivered" }, ReceiptWordsOf(items, "p1"));
        Assert.Equal("neutral", items.First(i => i.ItemId == "receipt:p1").Tone);
    }

    [Theory]
    [InlineData(PromptQueuedReason.TurnRunning, "Queued, 1st in line: a turn is running")]
    [InlineData(PromptQueuedReason.AgentStarting, "Queued, 1st in line: the agent is starting")]
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
        Assert.Equal(new[] { "Queued, 1st in line: a turn is running" }, ReceiptWordsOf(items, "gw-1"));
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
        Assert.Equal(new[] { "Queued, 1st in line: a turn is running" }, ReceiptWordsOf(queued, "gw-1"));
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

    // ----- a turn whose end was lost in a hole between two generations (item 7) -----

    /// <summary>A ruling rebuilt from the store after the Director moved to generation B: p1 delivered and carried by
    /// t1, whose last stored event was at 3 of generation A, and whose TurnEnded never arrived.</summary>
    private static ApiModeLiveRuling RulingWithTurnOneOpenInGenerationA()
        => new(new[] { Stored(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Delivered, Disposition = "started", TurnId = "t1" }, 3) });

    public static TheoryData<SessionEvent> Proofs => new()
    {
        Started(2, "t2", "p2"),
        Ready(1),
        Gone(1, 137, "Pi exited (exit code 137)"),
    };

    [Theory]
    [MemberData(nameof(Proofs))]
    public void Rule_AProofInALaterGeneration_RulesTheStoredOpenTurnLost(SessionEvent proof)
    {
        // Arrange
        var ruling = RulingWithTurnOneOpenInGenerationA();

        // Act
        var items = ruling.Rule(HeldB(proof));

        // Assert
        var receipt = Assert.Single(items, i => i.ItemId == "receipt:p1");
        Assert.Equal("Delivered - the turn's end was lost when the Director reconnected", receipt.Text);
        Assert.Equal("neutral", receipt.Tone);
        Assert.Equal("t1", receipt.TurnId);
    }

    [Fact]
    public void Rule_TheSameTurnContinuingInALaterGeneration_RulesNothingAndItsRealEndStands()
    {
        // Arrange
        var ruling = RulingWithTurnOneOpenInGenerationA();

        // Act: the same Pi process carries on with t1 after the hole, then ends it.
        var items = new List<ApiModeLiveItem>();
        items.AddRange(ruling.Rule(HeldB(Text(1, "t1", "NG"))));
        items.AddRange(ruling.Rule(HeldB(Ended(2, "t1", TurnOutcome.Done, null, "stop"))));

        // Assert
        Assert.Empty(ReceiptWordsOf(items, "p1")); // Delivered, as the store already shows it
        Assert.Contains(items, i => i.ItemId == "turn:t1" && i.Text == "Turn finished");
    }

    [Fact]
    public void Rule_AProofInTheSameGenerationAsTheStoredReceipt_RulesNothing()
    {
        // Act
        var items = RulingWithTurnOneOpenInGenerationA().Rule(HeldA(Gone(9, 0, "Pi exited (exit code 0)")));

        // Assert
        Assert.Empty(ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_AProofWithoutItsPositionWhileAStoredTurnIsOpen_Throws()
    {
        // Act + Assert: without the position the ruling cannot tell whether a hole lies between, and it does not guess.
        Assert.Throws<InvalidOperationException>(() => RulingWithTurnOneOpenInGenerationA().Rule(Ready(1)));
    }

    [Fact]
    public void Rule_TheRealTurnEndedAfterTheLostEnding_ReplacesIt()
    {
        // Arrange: ruled lost at generation B's ConnectionReady.
        var ruling = RulingWithTurnOneOpenInGenerationA();
        ruling.Rule(HeldB(Ready(1)));

        // Act: t1's own ending, of generation A, arrives after all.
        var items = ruling.Rule(HeldA(Ended(4, "t1", TurnOutcome.Failed, "the model errored", null)));

        // Assert
        Assert.Equal(new[] { "Delivered - the turn failed: the model errored" }, ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_AStoredLostEndingThenTheRealTurnEnded_ReplacesIt()
    {
        // Arrange: the store had already ruled t1 lost at 1 of generation B.
        var ruling = new ApiModeLiveRuling(new[]
        {
            new ApiModeStoredReceipt(
                new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Delivered, TurnId = "t1", TurnOutcome = ApiModeLostTurnEnd.Outcome },
                new ApiModeEventPosition("gen-b", GenBStarted, 1)),
        });

        // Act
        var items = ruling.Rule(HeldB(Ended(2, "t1", TurnOutcome.Cancelled, "stopped by the user", null)));

        // Assert
        Assert.Equal(new[] { "Delivered - stopped" }, ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_ADifferentTurnUnderTheSameId_EndsItsOwnReceiptAndLeavesTheLostEnding()
    {
        // Arrange: t1 ruled lost; then a t1 that is a different turn carries p2 (turn ids are unique per Director
        // process only; phase 2 review, observation C).
        var ruling = RulingWithTurnOneOpenInGenerationA();
        ruling.Rule(HeldB(Ready(1)));
        ruling.Rule(HeldB(Delivered(2, "p2", PlainAck)));
        ruling.Rule(HeldB(Started(3, "t1", "p2")));

        // Act
        var items = ruling.Rule(HeldB(Ended(4, "t1", TurnOutcome.Failed, "the model errored", null)));

        // Assert
        Assert.Empty(ReceiptWordsOf(items, "p1"));
        Assert.Equal(new[] { "Delivered - the turn failed: the model errored" }, ReceiptWordsOf(items, "p2"));
    }

    // ----- a prompt whose turn's start was lost too (review of pull request #4179, low finding) -----

    /// <summary>A ruling rebuilt from the store after the Director moved to generation B: p1 delivered at 3 of generation
    /// A, and both the TurnStarted that carried it and its TurnEnded fell in the hole.</summary>
    private static ApiModeLiveRuling RulingWithADeliveredPromptAndNoTurn()
        => new(new[] { Stored(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Delivered, Disposition = "started" }, 3) });

    [Theory]
    [MemberData(nameof(Proofs))]
    public void Rule_NoTurnAndAProofInALaterGeneration_RulesTheTurnLost(SessionEvent proof)
    {
        // Arrange
        var ruling = RulingWithADeliveredPromptAndNoTurn();

        // Act
        var items = ruling.Rule(HeldB(proof));

        // Assert
        var receipt = Assert.Single(items, i => i.ItemId == "receipt:p1");
        Assert.Equal("Delivered - the turn's end was lost when the Director reconnected", receipt.Text);
        Assert.Equal("neutral", receipt.Tone);
        Assert.Null(receipt.TurnId);
    }

    [Fact]
    public void Rule_NoTurnAndTheTurnThatNamesThePromptStarts_RulesNothingAndLinksIt()
    {
        // Arrange
        var ruling = RulingWithADeliveredPromptAndNoTurn();

        // Act: generation B's TurnStarted names p1 - its own turn starting; then that turn ends.
        var items = new List<ApiModeLiveItem>();
        items.AddRange(ruling.Rule(HeldB(Started(1, "t1", "p1"))));
        items.AddRange(ruling.Rule(HeldB(Ended(2, "t1", TurnOutcome.Failed, "the model errored", null))));

        // Assert
        Assert.Equal(new[] { "Delivered - the turn failed: the model errored" }, ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_NoTurnAndAProofInTheSameGeneration_RulesNothing()
    {
        // Act
        var items = RulingWithADeliveredPromptAndNoTurn().Rule(HeldA(Gone(9, 0, "Pi exited (exit code 0)")));

        // Assert
        Assert.Empty(ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_NoTurnRuledLostThenItsOlderRealTurnStarted_LinksItAndKeepsTheLostEnding()
    {
        // Arrange: ruled lost at generation B's ConnectionReady.
        var ruling = RulingWithADeliveredPromptAndNoTurn();
        ruling.Rule(HeldB(Ready(1)));

        // Act: the TurnStarted that carried p1, of generation A, arrives after all - older than the ruling, so it is an
        // earlier turn the proof already showed over (re-review of pull request #4179, finding 1).
        var items = ruling.Rule(HeldA(Started(4, "t1", "p1")));

        // Assert
        var receipt = Assert.Single(items, i => i.ItemId == "receipt:p1");
        Assert.Equal(ApiModeLiveRuling.LostWords, receipt.Text);
        Assert.Equal("t1", receipt.TurnId);
    }

    [Fact]
    public void Rule_NoTurnRuledLostThenItsOlderRealTurnStartedAndEnded_ShowsTheRealOutcome()
    {
        // Arrange: ruled lost at generation B; the TurnStarted naming p1 arrives late, older than the ruling.
        var ruling = RulingWithADeliveredPromptAndNoTurn();
        ruling.Rule(HeldB(Ready(1)));
        ruling.Rule(HeldA(Started(4, "t1", "p1")));

        // Act: the turn's real TurnEnded follows it. The store's twin is
        // RecordTurnEnded_AfterAnOlderRealStartLinkedALostReceiptWithNoTurn_TheRealEndReplacesTheLostEnding.
        var items = ruling.Rule(HeldA(Ended(5, "t1", TurnOutcome.Failed, "the model errored", null)));

        // Assert
        Assert.Equal(new[] { "Delivered - the turn failed: the model errored" }, ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_AStoredLostEndingWithNoTurnThenItsRealTurnStarted_ReplacesIt()
    {
        // Arrange: the store had already ruled p1 lost, with no turn, at 1 of generation B.
        var ruling = new ApiModeLiveRuling(new[]
        {
            new ApiModeStoredReceipt(
                new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Delivered, TurnOutcome = ApiModeLostTurnEnd.Outcome },
                new ApiModeEventPosition("gen-b", GenBStarted, 1)),
        });

        // Act
        var items = ruling.Rule(HeldB(Started(2, "t1", "p1")));

        // Assert
        Assert.Equal(new[] { "Delivered" }, ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_AStoredReceiptTouchedInThisGeneration_IsNotRuledLostByALaterProof()
    {
        // Arrange: p1 taken from the store at generation A, linked to t1 by generation B's TurnStarted; the ruling's
        // last position for it is now of generation B, as the store's row says too.
        var ruling = RulingWithADeliveredPromptAndNoTurn();
        ruling.Rule(HeldB(Started(1, "t1", "p1")));

        // Act: generation B's agent exits; its turn ending fell in no hole, so nothing is lost.
        var items = ruling.Rule(HeldB(Gone(2, 0, "Pi exited (exit code 0)")));

        // Assert
        Assert.Empty(ReceiptWordsOf(items, "p1"));
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
    public void Rule_ConnectionLinesOfTwoGenerations_AreNamedByTheirGenerationSoTheyStayApart()
    {
        // Arrange: the Director restarted, and the new generation's sequence started again at 1.
        var ruling = new ApiModeLiveRuling();

        // Act
        var first = Assert.Single(ruling.Rule(HeldA(Ready(1))));
        var second = Assert.Single(new ApiModeLiveRuling().Rule(new ApiModeHeldEvent("gen-b", GenBStarted, Gone(1, 0, "Pi exited (exit code 0)"))));

        // Assert
        Assert.Equal(("connection:gen-a:1", "connection:gen-b:1"), (first.ItemId, second.ItemId));
    }

    [Fact]
    public void Rule_AConnectionEventWithoutItsPosition_Throws()
    {
        // Act + Assert: the generation names the line, so the event must be handed as a held event.
        var refused = Assert.Throws<InvalidOperationException>(() => new ApiModeLiveRuling().Rule(Ready(1)));
        Assert.Contains("held event", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_StartedFromAStoredRunningToolCard_TheFinishKeepsItsTitleAndInput()
    {
        // Arrange: the stored conversation holds the card still running; the start is not in any held event.
        var stored = new ApiModeLiveItem { ItemId = "tool:c1", Kind = ApiModeLiveItemKinds.ToolCard, Mode = ApiModeLiveItemModes.Replace, Text = "read", Status = ApiModeLiveRuling.ToolRunningStatus, Detail = "README.md", Tone = "neutral", TurnId = "t1" };
        var ruling = new ApiModeLiveRuling(Array.Empty<ApiModeStoredReceipt>(), new[] { stored });

        // Act
        var remembers = ruling.Remembers(ToolDone(9, "t1", "c1", ToolCallOutcome.Failed));
        var card = Assert.Single(ruling.Rule(HeldA(ToolDone(9, "t1", "c1", ToolCallOutcome.Failed))));

        // Assert
        Assert.True(remembers);
        Assert.Equal(("read", "failed", "README.md", "red"), (card.Text, card.Status, card.Detail, card.Tone));
    }

    [Fact]
    public void Rule_StartedFromAStoredFinishedToolCard_DoesNotTreatItAsRunning()
    {
        // Arrange
        var stored = new ApiModeLiveItem { ItemId = "tool:c1", Kind = ApiModeLiveItemKinds.ToolCard, Mode = ApiModeLiveItemModes.Replace, Text = "read", Status = "done", Tone = "green", TurnId = "t1" };
        var ruling = new ApiModeLiveRuling(Array.Empty<ApiModeStoredReceipt>(), new[] { stored });

        // Act + Assert
        Assert.False(ruling.Remembers(ToolDone(9, "t1", "c1", ToolCallOutcome.Succeeded)));
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
    [InlineData(ApiModeReceiptState.Delivered, null, "lost", null, null, "Delivered - the turn's end was lost when the Director reconnected")]
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

    // ----- a queued receipt's place in line -----

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(10, "10th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(22, "22nd")]
    [InlineData(23, "23rd")]
    [InlineData(101, "101st")]
    [InlineData(111, "111th")]
    [InlineData(112, "112th")]
    [InlineData(113, "113th")]
    [InlineData(1000, "1000th")]
    public void Ordinal_EachPlace_IsTheEnglishOrdinal(int place, string expected)
    {
        // Act
        var words = ApiModeLiveRuling.Ordinal(place);

        // Assert
        Assert.Equal(expected, words);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ordinal_APlaceBelowOne_Throws(int place)
    {
        // Act and Assert: a place in line counts from 1, so anything less is a defect, never words.
        Assert.Throws<ArgumentOutOfRangeException>(() => ApiModeLiveRuling.Ordinal(place));
    }

    [Theory]
    [InlineData(1, "turnRunning", "Queued, 1st in line: a turn is running")]
    [InlineData(2, "userHasTheWheel", "Queued, 2nd in line: you have the wheel")]
    [InlineData(13, "agentStarting", "Queued, 13th in line: the agent is starting")]
    public void ReceiptWords_AQueuedReceiptWithAPlace_SaysItsPlaceInLine(int place, string reason, string expected)
    {
        // Arrange
        var receipt = new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Queued, QueueReason = reason, QueuePosition = place };

        // Act
        var words = ApiModeLiveRuling.ReceiptWords(receipt);

        // Assert
        Assert.Equal(expected, words);
    }

    [Fact]
    public void ReceiptWords_AQueuedReceiptWithNoPlaceKnown_SaysTheOldWordsAndGuessesNoNumber()
    {
        // Act
        var words = ApiModeLiveRuling.ReceiptWords(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Queued, QueueReason = "turnRunning", QueuePosition = null });

        // Assert
        Assert.Equal("Queued: a turn is running", words);
    }

    [Fact]
    public void Rule_AFreshQueue_EachPlaceIsTheDirectorsOwnPosition()
    {
        // Arrange: the Director's PromptQueued.position counts from 1 (the contract; it sends its queue's count after
        // adding the prompt), and on a fresh queue the place the Gateway derives must say the same.
        var events = new PromptQueued[]
        {
            Queued(1, "p1", position: 1),
            Queued(2, "p2", position: 2),
            Queued(3, "p3", position: 3),
        };
        var ruling = new ApiModeLiveRuling();

        // Act
        var words = events.Select(q => Assert.Single(ruling.Rule(q)).Text).ToList();

        // Assert
        Assert.Equal(events.Select(q => $"Queued, {ApiModeLiveRuling.Ordinal(q.Position)} in line: a turn is running"), words);
    }

    [Fact]
    public void Rule_TheOneAheadIsDelivered_EveryWaitingPromptMovesUp()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();
        RuleAll(ruling, new[] { Queued(1, "p1"), Queued(2, "p2"), Queued(3, "p3") });

        // Act
        var items = ruling.Rule(Delivered(4, "p1", PlainAck));

        // Assert: the delivery first, then each prompt still waiting, at its new place, under the delivery's sequence.
        Assert.Equal(new[]
        {
            ("receipt:p1", "Delivered", 4L),
            ("receipt:p2", "Queued, 1st in line: a turn is running", 4L),
            ("receipt:p3", "Queued, 2nd in line: a turn is running", 4L),
        }, items.Select(i => (i.ItemId, i.Text, i.Sequence)));
    }

    [Fact]
    public void Rule_TheOneAheadIsRefused_TheWaitingPromptMovesUp()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling();
        RuleAll(ruling, new[] { Queued(1, "p1"), Queued(2, "p2") });

        // Act
        var items = ruling.Rule(Refused(3, "p1", PiNotRunning, PromptRefusedBy.Director));

        // Assert
        Assert.Equal(new[] { "Refused: " + PiNotRunning }, ReceiptWordsOf(items, "p1"));
        Assert.Equal(new[] { "Queued, 1st in line: a turn is running" }, ReceiptWordsOf(items, "p2"));
    }

    [Fact]
    public void Rule_APromptBehindTheWaitingOnesIsDelivered_NoWaitingPromptIsSentAgain()
    {
        // Arrange: p1 and p2 wait; p3 was never queued and goes straight in.
        var ruling = new ApiModeLiveRuling();
        RuleAll(ruling, new[] { Queued(1, "p1"), Queued(2, "p2") });

        // Act
        var delivered = ruling.Rule(Delivered(3, "p3", PlainAck));
        var turnEnded = ruling.Rule(Ended(4, "t9", TurnOutcome.Done, null, "stop"));

        // Assert: no place moved, so the changed-words check sends neither waiting receipt again.
        Assert.Equal(new[] { "receipt:p3" }, delivered.Select(i => i.ItemId));
        Assert.Equal(new[] { "turn:t9" }, turnEnded.Select(i => i.ItemId));
    }

    [Fact]
    public void Rule_ARefusedPromptRetried_JoinsTheEndOfTheLine()
    {
        // Arrange: p2 was refused, and p3 queued after it.
        var ruling = new ApiModeLiveRuling();
        RuleAll(ruling, new SessionEvent[] { Queued(1, "p1"), Queued(2, "p2"), Refused(3, "p2", PiNotRunning, PromptRefusedBy.Director), Queued(4, "p3") });

        // Act: the retry of p2 arrives now, behind p3, exactly as the receipt store reads it.
        var items = ruling.Rule(Queued(5, "p2"));

        // Assert
        Assert.Equal(new[] { "Queued, 3rd in line: a turn is running" }, ReceiptWordsOf(items, "p2"));
    }

    [Fact]
    public void Rule_SeededFromStoredReceipts_TheWaitingPromptMovesUpWhenTheOneAheadIsDelivered()
    {
        // Arrange: a reader that starts from the store, which holds p1 and p2 queued.
        var stored = new[]
        {
            Stored(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Queued, QueueReason = "turnRunning" }, 1),
            Stored(new ApiModeReceiptDto { PromptId = "p2", State = ApiModeReceiptState.Queued, QueueReason = "turnRunning" }, 2),
        };
        ApiModeLiveRuling.PlaceInLine(stored);
        var opening = stored.Select(s => ApiModeLiveRuling.ReceiptItem(s.Receipt, 0).Text).ToList();
        var ruling = new ApiModeLiveRuling(stored);

        // Act
        var items = ruling.Rule(HeldA(Delivered(3, "p1", PlainAck)));

        // Assert
        Assert.Equal(new[] { "Queued, 1st in line: a turn is running", "Queued, 2nd in line: a turn is running" }, opening);
        Assert.Equal(new[] { "Delivered" }, ReceiptWordsOf(items, "p1"));
        Assert.Equal(new[] { "Queued, 1st in line: a turn is running" }, ReceiptWordsOf(items, "p2"));
    }

    [Fact]
    public void PlaceInLine_DeliveredAndRefusedReceipts_HaveNoPlaceAndAreNotCountedAhead()
    {
        // Arrange
        var stored = new[]
        {
            Stored(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Delivered }, 1),
            Stored(new ApiModeReceiptDto { PromptId = "p2", State = ApiModeReceiptState.Queued, QueueReason = "turnRunning" }, 2),
            Stored(new ApiModeReceiptDto { PromptId = "p3", State = ApiModeReceiptState.Refused, RefusalReason = PiNotRunning }, 3),
            Stored(new ApiModeReceiptDto { PromptId = "p4", State = ApiModeReceiptState.Queued, QueueReason = "turnRunning" }, 4),
        };

        // Act
        ApiModeLiveRuling.PlaceInLine(stored);

        // Assert
        Assert.Equal(new int?[] { null, 1, null, 2 }, stored.Select(s => s.Receipt.QueuePosition));
    }

    [Fact]
    public void PlaceInLine_AReceiptStillQueuedFromAnOlderGeneration_HasNoPlaceAndIsNotCountedAhead()
    {
        // Arrange: p1 was queued by a Director that has since restarted (generation A); p2 was queued by the new one.
        var stored = new[]
        {
            Stored(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Queued, QueueReason = "turnRunning" }, 7),
            new ApiModeStoredReceipt(new ApiModeReceiptDto { PromptId = "p2", State = ApiModeReceiptState.Queued, QueueReason = "turnRunning" }, new ApiModeEventPosition("gen-b", GenBStarted, 1)),
        };

        // Act
        ApiModeLiveRuling.PlaceInLine(stored);

        // Assert: the old Director's queue is gone, so p1's place cannot be known and is not guessed.
        Assert.Equal(new int?[] { null, 1 }, stored.Select(s => s.Receipt.QueuePosition));
        Assert.Equal("Queued: a turn is running", ApiModeLiveRuling.ReceiptWords(stored[0].Receipt));
    }

    [Fact]
    public void Rule_TheFirstPromptQueuedByANewerGeneration_TheOlderQueuedReceiptLosesItsPlace()
    {
        // Arrange: a reader that starts from the store, which holds p1 queued in generation A.
        var ruling = new ApiModeLiveRuling(new[] { Stored(new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Queued, QueueReason = "turnRunning" }, 7) });

        // Act: the restarted Director queues p2 in generation B.
        var items = ruling.Rule(HeldB(Queued(1, "p2")));

        // Assert
        Assert.Equal(new[] { "Queued, 1st in line: a turn is running" }, ReceiptWordsOf(items, "p2"));
        Assert.Equal(new[] { "Queued: a turn is running" }, ReceiptWordsOf(items, "p1"));
    }
    // ----- a queued prompt whose ending was lost in a hole between two generations (item 9) -----

    private const string UnknownWords = "Not known - the Director reconnected without this prompt in its queue or any record of sending it";
    private const string CarriedWords = "Not known - the record of its delivery was lost when the Director reconnected; a turn carrying it started";

    /// <summary>A queued receipt as the store answers it: queued by the event at <paramref name="lastSequence"/> of
    /// generation A, with its text.</summary>
    private static ApiModeStoredReceipt StoredQueued(string promptId, long lastSequence)
        => Stored(new ApiModeReceiptDto { PromptId = promptId, PromptText = $"text of {promptId}", State = ApiModeReceiptState.Queued, QueueReason = "turnRunning" }, lastSequence);

    /// <summary>A receipt the store already ruled Unknown at <paramref name="ruledAt"/> of generation B.</summary>
    private static ApiModeStoredReceipt StoredUnknown(string promptId, long ruledAt)
        => new(new ApiModeReceiptDto { PromptId = promptId, PromptText = $"text of {promptId}", State = ApiModeReceiptState.Unknown, QueueReason = "turnRunning" },
            new ApiModeEventPosition("gen-b", GenBStarted, ruledAt));

    public static TheoryData<SessionEvent> QueueLeftProofs => new()
    {
        Started(1, "t1", "p9"),
        Ready(1),
        Gone(1, 137, "Pi exited (exit code 137)"),
        Delivered(1, "p9", "ok"),
        Refused(1, "p9", PiNotRunning, PromptRefusedBy.Director),
        Ended(1, "t0", TurnOutcome.Done, null, "stop"),
    };

    [Theory]
    [MemberData(nameof(QueueLeftProofs))]
    public void Rule_AProofInALaterGenerationThatDidNotReannounceIt_RulesTheStoredQueuedReceiptUnknown(SessionEvent proof)
    {
        // Arrange: p1 was queued in generation A; generation B's first event is not a re-announcement of it.
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 7) });

        // Act
        var items = ruling.Rule(HeldB(proof));

        // Assert
        var receipt = Assert.Single(items, i => i.ItemId == "receipt:p1");
        Assert.Equal(UnknownWords, receipt.Text);
        Assert.Equal("neutral", receipt.Tone);
        Assert.Equal("text of p1", receipt.PromptText);
    }

    [Fact]
    public void Rule_TheReceiptReannouncedThenAProof_IsNotRuledUnknown()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 7) });

        // Act: generation B re-announces p1 first, then a turn starts.
        var items = ruling.Rule(HeldB(Queued(1, "p1"))).Concat(ruling.Rule(HeldB(Ready(2)))).ToList();

        // Assert: p1 keeps the words it opened with (1st in line), so nothing is sent for it.
        Assert.Empty(ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_ATurnStartedThatCarriesTheQueuedPrompt_RulesItUnknownLinkedToTheTurnAndNeverDelivered()
    {
        // Arrange: p1 was queued in generation A; its delivery fell in the hole, and generation B opens with the turn
        // that carries it. The Gateway holds no acknowledgement, so it may not say Delivered (the Delivery Lead's ruling
        // on pull request #4209).
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 7) });

        // Act: the turn's start, then its end.
        var atTheStart = ruling.Rule(HeldB(Started(1, "t1", "p1")));
        var atTheEnd = ruling.Rule(HeldB(Ended(2, "t1", TurnOutcome.Failed, "the model refused", "error")));

        // Assert: Unknown in its own words, linked to t1, as the store rules it; the turn's end does not change them.
        var carried = Assert.Single(atTheStart, i => i.ItemId == "receipt:p1");
        Assert.Equal((CarriedWords, "neutral", "t1"), (carried.Text, carried.Tone, carried.TurnId));
        Assert.Empty(ReceiptWordsOf(atTheEnd, "p1"));
    }

    [Fact]
    public void Rule_ATurnStartedThatCarriesOneQueuedPrompt_GivesEachUnknownItsOwnWords()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 6), StoredQueued("p2", 7) });

        // Act
        var items = ruling.Rule(HeldB(Started(1, "t1", "p1")));

        // Assert
        Assert.Equal(new[] { CarriedWords }, ReceiptWordsOf(items, "p1"));
        Assert.Equal(new[] { UnknownWords }, ReceiptWordsOf(items, "p2"));
    }

    [Fact]
    public void Rule_ACarriedPrompt_LeavesTheLineSoAPromptQueuedAfterItIsFirst()
    {
        // Arrange: p1 ruled Unknown by the turn that carries it; that turn ends.
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 7) });
        ruling.Rule(HeldB(Started(1, "t1", "p1")));
        ruling.Rule(HeldB(Ended(2, "t1", TurnOutcome.Done, null, "stop")));

        // Act: the user queues p2 behind the next turn.
        var items = ruling.Rule(HeldB(Queued(3, "p2", position: 1)));

        // Assert: the Director's line holds p2 alone, and so does the Gateway's.
        Assert.Equal(new[] { "Queued, 1st in line: a turn is running" }, ReceiptWordsOf(items, "p2"));
        Assert.Empty(ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_TheRealDeliveryAfterACarriedPromptWasRuledUnknown_TurnsItDeliveredWithItsTurnsOutcome()
    {
        // Arrange: p1 ruled Unknown by t1's start, and t1 failed.
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 7) });
        ruling.Rule(HeldB(Started(1, "t1", "p1")));
        ruling.Rule(HeldB(Ended(2, "t1", TurnOutcome.Failed, "the model refused", "error")));

        // Act: the prompt's own PromptDelivered, should it ever arrive in the ruling's generation.
        var items = ruling.Rule(HeldB(Delivered(3, "p1", "ok")));

        // Assert: a true Delivered now, with the outcome of the turn it stayed linked to; not a second ending.
        Assert.Equal(0, ruling.SecondEndingsLogged);
        var delivered = Assert.Single(items, i => i.ItemId == "receipt:p1");
        Assert.Equal(("Delivered - the turn failed: the model refused", "red", "t1"), (delivered.Text, delivered.Tone, delivered.TurnId));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("text")]
    public void Rule_APromptQueuedOrAReplysTextInALaterGeneration_RulesNothing(string kind)
    {
        // Arrange
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 7) });
        SessionEvent e = kind == "queued" ? Queued(1, "p2", position: 1) : Text(1, "t0", "hello");

        // Act
        var items = ruling.Rule(HeldB(e));

        // Assert: a PromptQueued may be a re-announcement, and text is not taken as a proof.
        Assert.DoesNotContain(UnknownWords, ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_AProofInTheSameGenerationAsTheQueuedReceipt_RulesNothing()
    {
        // Arrange: no hole lies between them.
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 7) });

        // Act
        var items = ruling.Rule(HeldA(Ready(8)));

        // Assert
        Assert.Empty(ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_AProofThatEndsTheQueuedPromptItself_IsItsOwnEndingNotUnknown()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 7) });

        // Act
        var items = ruling.Rule(HeldB(Delivered(1, "p1", "ok")));

        // Assert
        Assert.Equal(new[] { "Delivered" }, ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_AProofWithoutItsPositionWhileAStoredReceiptIsQueued_Throws()
    {
        // Arrange: without the position the ruling cannot tell whether the proof is of a later generation.
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 7) });

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => ruling.Rule(Ready(1)));
    }

    public static TheoryData<SessionEvent, string> LateRealEvents => new()
    {
        { Queued(1, "p1", position: 1), "Queued, 1st in line: a turn is running" },
        { Delivered(1, "p1", "ok"), "Delivered" },
        { Refused(1, "p1", PiNotRunning, PromptRefusedBy.Director), "Refused: " + PiNotRunning },
    };

    [Theory]
    [MemberData(nameof(LateRealEvents))]
    public void Rule_ARealEventOlderThanTheRulingInItsGeneration_ReplacesTheUnknownRuling(SessionEvent late, string expected)
    {
        // Arrange: the ruling was made at sequence 5 of generation B.
        var ruling = new ApiModeLiveRuling(new[] { StoredUnknown("p1", 5) });

        // Act: the prompt's own event, at sequence 1 of the same generation, arrives after it.
        var items = ruling.Rule(HeldB(late));

        // Assert
        Assert.Equal(new[] { expected }, ReceiptWordsOf(items, "p1"));
        Assert.Equal("text of p1", Assert.Single(items, i => i.ItemId == "receipt:p1").PromptText);
    }

    [Fact]
    public void Rule_ARulingThenTheRealDeliveryInTheSameReader_ReplacesIt()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 7) });
        Assert.Equal(new[] { UnknownWords }, ReceiptWordsOf(ruling.Rule(HeldB(Ready(1))), "p1"));

        // Act
        var items = ruling.Rule(HeldB(Delivered(2, "p1", "ok")));

        // Assert
        Assert.Equal(new[] { "Delivered" }, ReceiptWordsOf(items, "p1"));
    }

    [Fact]
    public void Rule_AnEventOfAnOlderGenerationThanTheRuling_IsAReplayAndChangesNothing()
    {
        // Arrange
        var ruling = new ApiModeLiveRuling(new[] { StoredUnknown("p1", 5) });

        // Act
        var items = ruling.Rule(HeldA(Queued(7, "p1")));

        // Assert
        Assert.Empty(items);
    }

    [Fact]
    public void Rule_ThreeQueuedAndTheLaterGenerationReannouncesTwo_TheFirstIsRuledUnknownAndThePlacesMoveUp()
    {
        // Arrange: p1, p2 and p3 queued in generation A, 1st, 2nd and 3rd.
        var ruling = new ApiModeLiveRuling(new[] { StoredQueued("p1", 1), StoredQueued("p2", 2), StoredQueued("p3", 3) });

        // Act: generation B re-announces p2 and p3 (p1 left the queue in the hole), then the agent connects.
        var items = new[] { Queued(1, "p2", position: 1), Queued(2, "p3", position: 2), (SessionEvent)Ready(3) }
            .SelectMany(e => ruling.Rule(HeldB(e))).ToList();

        // Assert: the last words each receipt was sent with.
        string Last(string p) => ReceiptWordsOf(items, p).Last();
        Assert.Equal(UnknownWords, Last("p1"));
        Assert.Equal("Queued, 1st in line: a turn is running", Last("p2"));
        Assert.Equal("Queued, 2nd in line: a turn is running", Last("p3"));
    }

    [Fact]
    public void PlaceInLine_AnUnknownReceipt_HasNoPlaceAndIsNotCountedAhead()
    {
        // Arrange: both in generation B; p1, ahead of p2, was ruled Unknown.
        var stored = new[]
        {
            StoredUnknown("p1", 3),
            new ApiModeStoredReceipt(new ApiModeReceiptDto { PromptId = "p2", State = ApiModeReceiptState.Queued, QueueReason = "turnRunning" }, new ApiModeEventPosition("gen-b", GenBStarted, 1)),
        };

        // Act
        ApiModeLiveRuling.PlaceInLine(stored);

        // Assert
        Assert.Equal(new int?[] { null, 1 }, stored.Select(s => s.Receipt.QueuePosition));
    }

    [Fact]
    public void ReceiptWords_AnUnknownReceipt_SaysNeitherDeliveredNorRefusedInANeutralTone()
    {
        // Arrange
        var receipt = new ApiModeReceiptDto { PromptId = "p1", State = ApiModeReceiptState.Unknown, QueueReason = "turnRunning" };

        // Act + Assert
        Assert.Equal(UnknownWords, ApiModeLiveRuling.ReceiptWords(receipt));
        Assert.Equal("neutral", ApiModeLiveRuling.ReceiptTone(receipt));
    }
}
