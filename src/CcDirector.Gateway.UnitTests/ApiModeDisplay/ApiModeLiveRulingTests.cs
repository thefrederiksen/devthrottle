using CcDirector.Gateway.ApiModeDisplay;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
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
        var ruling = new ApiModeLiveRuling(new[] { stored });

        // Act
        var items = ruling.Rule(Ended(40, "t1", TurnOutcome.Failed, "Pi exited during the turn (exit code 137)", null));

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
