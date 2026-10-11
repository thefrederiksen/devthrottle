using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;
using CcDirector.Core.UnitTests.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.TestInfrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The prompt verb answers inside the Gateway's wait (Voice Delivery mission, phase 3). The Gateway waits 30 seconds for
/// a Director command; a send to a busy agent can take minutes. On 25 September 2026 the Gateway called such a slow
/// success a failure and retried it, and the owner's words reached the agent twice. The verb now answers "delivering"
/// at its budget and lets the send carry on.
///
/// <para>Every wait in here is on ONE FAKE CLOCK - the session's, the scripted terminal's and the verb's budget - advanced
/// by <see cref="VirtualTime.RunAsync"/> until the work is done. A two-second echo, a thirty-second watch limit and a
/// one-minute limit cost microseconds, and every "how long after" below is read off that clock, never a stopwatch. The
/// earlier version of this class ran the real clock - 43 seconds for twelve tests, and an answer measured against
/// "under 1.5 seconds" that a loaded machine cannot promise (Release Process Fix, H2).</para>
/// </summary>
public sealed class PromptAnswerBudgetTests : IDisposable
{
    private readonly FakeTimeProvider _clock = VirtualTime.NewClock();

    /// <summary>A terminal session whose agent takes <paramref name="echoDelay"/> to show typed text - a busy machine.
    /// The agent is one the Director has no conversation records for, so the send is judged by the terminal alone.</summary>
    private (Session Session, ScriptedTerminal Terminal) SlowTerminalSession(TimeSpan echoDelay)
    {
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.Clock = _clock;
        session.AgentKind = AgentKind.Gemini;
        terminal.Echo = false;
        terminal.OnWrite = text =>
        {
            if (text == "\r") return;
            var bytes = Encoding.UTF8.GetBytes(text);
            // The echo lands on the fake clock, inline when the clock reaches it, so nothing about the order of the echo
            // and the budget depends on a thread-pool scheduling race.
            _ = Task.Delay(echoDelay, _clock).ContinueWith(_ => terminal.Buffer!.Write(bytes),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        };
        return (session, terminal);
    }

    private static PromptRequest Prompt(string text) =>
        new() { Text = text, AppendEnter = true, Surface = "cockpit" };

    private static PromptResponse Body(DirectorCommandResult result)
    {
        Assert.True(result.Ok, result.Error);
        return JsonSerializer.Deserialize<PromptResponse>(result.BodyJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    /// <summary>Advance the fake clock until <paramref name="condition"/> holds; the virtual-time limits turn a condition that
    /// never holds into a failure that says so.</summary>
    private Task UntilAsync(Func<bool> condition) => VirtualTime.RunAsync(_clock, async () =>
    {
        while (!condition()) await Task.Delay(TimeSpan.FromMilliseconds(10), _clock);
    });

    [Fact]
    public async Task SendPromptAsync_SendOutlastsTheBudget_AnswersDeliveringAndTheSendStillCompletes()
    {
        // Arrange: the echo takes two seconds; the budget is a third of a second.
        var echo = TimeSpan.FromSeconds(2);
        var budget = TimeSpan.FromMilliseconds(300);
        var (session, terminal) = SlowTerminalSession(echo);
        const string text = "Token BUDGET1. Reply with exactly: ACK";
        var sentAt = _clock.GetUtcNow();

        // Act
        var result = await VirtualTime.RunAsync(_clock, () => ControlApi.SessionCommandExecutor.SendPromptAsync(
            session, Prompt(text), answerBudget: budget));
        var answeredAfter = _clock.GetUtcNow() - sentAt;

        // Assert: answered AT the budget - not before it, and no later than the one virtual-time step the clock
        // moves in, far short of the echo the send is still waiting for - accepted, "delivering", and on the wire
        // as that word. A bound of "before the echo" would pass a budget six times too long.
        var response = Body(result);
        Assert.InRange(answeredAfter, budget, budget + VirtualTime.Step);
        Assert.True(response.Accepted);
        Assert.Equal(DeliveryState.Delivering, response.DeliveryState);
        Assert.Contains("\"deliveryState\":\"delivering\"", result.BodyJson);
        Assert.Empty(terminal.Submitted);

        // ...and the send carried on after the answer and submitted the text once.
        await UntilAsync(() => terminal.Submitted.Count > 0);
        Assert.Equal(new[] { text }, terminal.Submitted);
    }

    [Fact]
    public async Task SendPromptAsync_SendFinishesWithinTheBudget_AnswersDelivered()
    {
        // Arrange
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.Clock = _clock;
        session.AgentKind = AgentKind.Gemini;

        // Act
        var response = Body(await VirtualTime.RunAsync(_clock,
            () => ControlApi.SessionCommandExecutor.SendPromptAsync(session, Prompt("Token BUDGET2. Reply: ACK"))));

        // Assert
        Assert.True(response.Accepted);
        Assert.Equal(DeliveryState.Delivered, response.DeliveryState);
        Assert.Single(terminal.Submitted);
    }

    /// <summary>A working Claude Code on the scripted agent terminal the session's own busy-send tests use.</summary>
    private (Session Session, ScriptedAgentTerminal Terminal) WorkingClaudeCode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-verb-late-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var transcript = Path.Combine(dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.ClaudeCode, transcript, dir) { Working = true, Clock = _clock };
        var session = new Session(Guid.NewGuid(), dir, dir, null, terminal, SessionBackendType.ConPty) { AgentKind = AgentKind.ClaudeCode, Clock = _clock };
        _cleanup.Add(terminal);
        _cleanup.Add(session);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        session.ApplyTerminalActivityState(ActivityState.Working);
        return (session, terminal);
    }

    private readonly List<IDisposable> _cleanup = new();

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="act"/> on the fake clock and goes on advancing it until the verb's late outcome for
    /// <paramref name="deliveryId"/> is written by the one place it is written. Answers the outcome and the virtual moment
    /// it was written.
    /// </summary>
    private async Task<(string Outcome, DateTimeOffset WrittenAt)> LateOutcomeFor(string deliveryId, Func<Task> act)
    {
        var seen = new TaskCompletionSource<(string, DateTimeOffset)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(string id, string state) { if (id == deliveryId) seen.TrySetResult((state, _clock.GetUtcNow())); }
        ControlApi.SessionCommandExecutor.LateOutcomeObserver += Observe;
        try
        {
            return await VirtualTime.RunAsync(_clock, async () =>
            {
                await act();
                return await seen.Task;
            });
        }
        finally
        {
            ControlApi.SessionCommandExecutor.LateOutcomeObserver -= Observe;
        }
    }

    [Fact]
    public async Task SendPromptAsync_WorkingClaudeCodeRecordsThePromptAfterTheWindow_AnswersDeliveringThenRecordsDelivered()
    {
        // Arrange (review finding 3, case a): the composer empties on the Enter; the records show the prompt five seconds
        // later, after the two-second records window. The send returns inside the verb's budget - but unproven.
        var (session, terminal) = WorkingClaudeCode();
        terminal.RecordDelay = TimeSpan.FromSeconds(5);
        session.ArrivalWindow = TimeSpan.FromSeconds(2);
        session.LateArrivalLimitForTests = TimeSpan.FromSeconds(30);
        const string text = "Token VERBLATE1. Reply with exactly: ACK";
        var deliveryId = Guid.NewGuid().ToString("N");
        PromptResponse? response = null;

        // Act
        var (late, _) = await LateOutcomeFor(deliveryId, async () => response = Body(
            await ControlApi.SessionCommandExecutor.SendPromptAsync(session, Recording(text, deliveryId), SendSource.Delivery, NewDeliveryRecord())));

        // Assert: answered "delivering" - never a failure, never "delivered" before the records say so - then delivered.
        Assert.True(response!.Accepted);
        Assert.Equal(DeliveryState.Delivering, response.DeliveryState);
        Assert.Equal(DeliveryStates.Delivered, late);
        Assert.Equal(new[] { text }, terminal.Recorded);
        Assert.Equal(1, terminal.EntersAccepted);
    }

    [Fact]
    public async Task SendPromptAsync_WorkingClaudeCodeNeverRecordsThePrompt_AnswersDeliveringThenUnconfirmedAtTheWatchLimit()
    {
        // Arrange (review finding 3, case b): the composer empties on the Enter; the records never show the prompt.
        var (session, terminal) = WorkingClaudeCode();
        terminal.NeverRecord = true;
        session.ArrivalWindow = TimeSpan.FromSeconds(2);
        session.LateArrivalLimitForTests = TimeSpan.FromSeconds(2);
        const string text = "Token VERBLOST1. Reply with exactly: ACK";
        var deliveryId = Guid.NewGuid().ToString("N");
        PromptResponse? response = null;

        // Act
        var (late, _) = await LateOutcomeFor(deliveryId, async () => response = Body(
            await ControlApi.SessionCommandExecutor.SendPromptAsync(session, Recording(text, deliveryId), SendSource.Delivery, NewDeliveryRecord())));

        // Assert: "delivering" at the answer - never a failure inside the first window - and "unconfirmed" when the watch
        // ends without the records showing it (the Delivery Lead's ruling: nothing stays delivering forever; issue #3484:
        // the words left the composer, so that ending proves nothing either way). The words were typed once: nothing types
        // them a second time by itself.
        Assert.Equal(DeliveryState.Delivering, response!.DeliveryState);
        Assert.Equal(DeliveryStates.Unconfirmed, late);
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(terminal.TypedText, "VERBLOST1"));
    }

    /// <summary>A delivery record in its own folder, as phase 1's tests make one; removed with the test.</summary>
    private DeliveryRecord NewDeliveryRecord()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-verb-record-" + Guid.NewGuid().ToString("N"));
        _cleanup.Add(new FolderRemoval(dir));
        return new DeliveryRecord(dir);
    }

    private sealed class FolderRemoval(string dir) : IDisposable
    {
        public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A recording spoken alone: it carries its delivery id and the spoken-turn marker, both the upload id.</summary>
    private static PromptRequest Recording(string text, string deliveryId) =>
        new() { Text = text, AppendEnter = true, Surface = "cockpit", DeliveryUploadId = deliveryId, DeliveryId = deliveryId };

    [Fact]
    public async Task SendPromptAsync_WorkingClaudeCodeRecordsThePromptAfterTheWindow_DeliveryRecordMovesFromDeliveringToDelivered()
    {
        // Arrange (round 2b): as case (a) above, with the Director's durable delivery record. The composer empties on the
        // Enter and the records show the prompt five seconds later, after the two-second window.
        var (session, terminal) = WorkingClaudeCode();
        terminal.RecordDelay = TimeSpan.FromSeconds(5);
        session.ArrivalWindow = TimeSpan.FromSeconds(2);
        session.LateArrivalLimitForTests = TimeSpan.FromSeconds(30);
        var record = NewDeliveryRecord();
        const string text = "Token RECLATE1. Reply with exactly: ACK";
        var deliveryId = Guid.NewGuid().ToString("N");
        DeliveryState? atAnswer = null;

        // Act
        var (late, _) = await LateOutcomeFor(deliveryId, async () =>
        {
            var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(
                session, Recording(text, deliveryId), SendSource.Delivery, record));
            Assert.Equal(DeliveryState.Delivering, response.DeliveryState);
            atAnswer = record.Read(session.Id, deliveryId).State;
        });

        // Assert: the record said "delivering" when the verb answered - never "delivered" before the records showed the
        // prompt - and "delivered" once they did. Typed once.
        Assert.Equal(DeliveryState.Delivering, atAnswer);
        Assert.Equal(DeliveryStates.Delivered, late);
        Assert.Equal(DeliveryState.Delivered, record.Read(session.Id, deliveryId).State);
        Assert.Equal(new[] { text }, terminal.Recorded);
        Assert.Equal(1, terminal.EntersAccepted);
    }

    [Fact]
    public async Task SendPromptAsync_WorkingClaudeCodeNeverRecordsThePrompt_DeliveryRecordIsDeliveringWhileTheWatchRunsThenUnconfirmed()
    {
        // Arrange (round 2b): as case (b) above, with the durable record. The records never show the prompt.
        var (session, terminal) = WorkingClaudeCode();
        terminal.NeverRecord = true;
        session.ArrivalWindow = TimeSpan.FromSeconds(2);
        session.LateArrivalLimitForTests = TimeSpan.FromSeconds(2);
        var record = NewDeliveryRecord();
        const string text = "Token RECLOST1. Reply with exactly: ACK";
        var deliveryId = Guid.NewGuid().ToString("N");

        DeliveryState? atAnswer = null;

        // Act
        var (late, _) = await LateOutcomeFor(deliveryId, async () =>
        {
            Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, Recording(text, deliveryId), SendSource.Delivery, record));
            atAnswer = record.Read(session.Id, deliveryId).State;
        });

        // Assert: the record says "delivering" while the watch runs - never "delivered" without proof - and "unconfirmed"
        // when the watch ends without the records showing it, with the reason the Delivery Lead ruled. Typed once.
        Assert.Equal(DeliveryState.Delivering, atAnswer);
        Assert.Equal(DeliveryStates.Unconfirmed, late);
        var entry = record.Read(session.Id, deliveryId);
        Assert.Equal(DeliveryState.Unconfirmed, entry.State);
        Assert.Equal("never appeared in the agent's records within 2 seconds", entry.Reason);
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(terminal.TypedText, "RECLOST1"));
    }

    // ===== Round 2c: nothing stays delivering forever (the Tech Lead's ruling on the three open cases) =====

    /// <summary>A working Codex on the scripted agent terminal, whose composer is hidden under a menu for the whole window
    /// after the Enter: the Director has no records to watch for a Codex send mid-turn, and cannot read the composer.</summary>
    private (Session Session, ScriptedAgentTerminal Terminal) WorkingCodexUnderAMenu()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-verb-late-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _cleanup.Add(new FolderRemoval(dir));
        var transcript = Path.Combine(dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.Codex, transcript, dir) { Working = true, SwallowEnter = true, MenuAfterEnter = true, Clock = _clock };
        var session = new Session(Guid.NewGuid(), dir, dir, null, terminal, SessionBackendType.ConPty) { AgentKind = AgentKind.Codex, Clock = _clock };
        _cleanup.Add(terminal);
        _cleanup.Add(session);
        terminal.StartDrawing();
        session.ApplyTerminalActivityState(ActivityState.Working);
        return (session, terminal);
    }

    [Fact]
    public async Task SendPromptAsync_NoRecordsToWatch_DeliveryRecordIsDeliveringUntilTheLimitThenUnconfirmed()
    {
        // Arrange (case 1): a working Codex whose composer cannot be read for the whole window after the Enter. Nothing is
        // known about the words, and the Director has no records of this agent's to watch for them.
        var (session, terminal) = WorkingCodexUnderAMenu();
        session.ComposerReleaseWindowForTests = TimeSpan.FromSeconds(1);
        var limit = TimeSpan.FromSeconds(4);
        session.LateArrivalLimitForTests = limit;
        var record = NewDeliveryRecord();
        const string text = "Token NORECORDS1. Reply with exactly: ACK";
        var deliveryId = Guid.NewGuid().ToString("N");
        DeliveryState? atAnswer = null;
        DateTimeOffset answeredAt = default;

        // Act
        var (late, writtenAt) = await LateOutcomeFor(deliveryId, async () =>
        {
            var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(
                session, Recording(text, deliveryId), SendSource.Delivery, record));
            answeredAt = _clock.GetUtcNow();
            Assert.Equal(DeliveryState.Delivering, response.DeliveryState);
            atAnswer = record.Read(session.Id, deliveryId).State;
        });

        // Assert: "delivering" at the answer and for the whole limit - never an early ending - then "unconfirmed" with the
        // ruled reason: never "not-delivered", which would invite a retry that doubles the words (issue #3484). Typed once,
        // never cleared.
        Assert.Equal(DeliveryState.Delivering, atAnswer);
        Assert.Equal(DeliveryStates.Unconfirmed, late);
        Assert.True(writtenAt - answeredAt >= limit - session.ComposerReleaseWindowForTests.Value,
            $"unconfirmed was written {(writtenAt - answeredAt).TotalSeconds:F1}s after the answer, before the limit");
        var entry = record.Read(session.Id, deliveryId);
        Assert.Equal(DeliveryState.Unconfirmed, entry.State);
        Assert.Equal("could not be confirmed: the Director had no records to watch for this agent", entry.Reason);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(terminal.TypedText, "NORECORDS1"));
        Assert.Equal(0, terminal.ClearKeysPressed);
    }

    [Fact]
    public async Task SendPromptAsync_TheRecordsWatchThrows_DeliveryRecordIsDeliveringUntilTheLimitThenUnconfirmedNamingTheFailure()
    {
        // Arrange (case 2): a working Claude Code takes the Enter, the records never show the prompt, and every read of the
        // records in the late watch fails.
        var (session, terminal) = WorkingClaudeCode();
        terminal.NeverRecord = true;
        session.ArrivalWindow = TimeSpan.FromSeconds(2);
        var limit = TimeSpan.FromSeconds(4);
        session.LateArrivalLimitForTests = limit;
        session.LateWatchReadForTests = () => throw new IOException("the records file is locked");
        var record = NewDeliveryRecord();
        const string text = "Token WATCHFAIL1. Reply with exactly: ACK";
        var deliveryId = Guid.NewGuid().ToString("N");
        DeliveryState? atAnswer = null;
        DateTimeOffset answeredAt = default;

        // Act
        var (late, writtenAt) = await LateOutcomeFor(deliveryId, async () =>
        {
            var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(
                session, Recording(text, deliveryId), SendSource.Delivery, record));
            answeredAt = _clock.GetUtcNow();
            Assert.Equal(DeliveryState.Delivering, response.DeliveryState);
            atAnswer = record.Read(session.Id, deliveryId).State;
        });

        // Assert: the failure does not end the wait - "delivering" until the limit - then "unconfirmed" naming it.
        Assert.Equal(DeliveryState.Delivering, atAnswer);
        Assert.Equal(DeliveryStates.Unconfirmed, late);
        Assert.True(writtenAt - answeredAt >= limit - session.ArrivalWindow,
            $"unconfirmed was written {(writtenAt - answeredAt).TotalSeconds:F1}s after the answer, before the limit");
        var entry = record.Read(session.Id, deliveryId);
        Assert.Equal(DeliveryState.Unconfirmed, entry.State);
        Assert.Equal("could not be confirmed: the records watch failed: the records file is locked", entry.Reason);
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(terminal.TypedText, "WATCHFAIL1"));
    }

    [Fact]
    public async Task SendPromptAsync_ThePointerNamesAConversationThatDoesNotExist_DeliveryRecordIsDeliveringThenUnconfirmed()
    {
        // Arrange (issue #3484, the "cannot tell" ending of issue 3480): a working Claude Code whose conversation was
        // readable, and whose pointer then moves to a conversation file that is never written. The composer empties on
        // the Enter, so the words left it, and the records cannot say whether they arrived.
        var (session, terminal) = WorkingClaudeCode();
        terminal.NeverRecord = true;
        var missing = Path.Combine(Path.GetTempPath(), "cc-verb-missing-" + Guid.NewGuid().ToString("N"), Guid.NewGuid() + ".jsonl");
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), missing, "clear");
        session.ArrivalWindow = TimeSpan.FromSeconds(2);
        session.LateArrivalLimitForTests = TimeSpan.FromSeconds(2);
        var record = NewDeliveryRecord();
        const string text = "Token CANNOTTELL1. Reply with exactly: ACK";
        var deliveryId = Guid.NewGuid().ToString("N");
        DeliveryState? atAnswer = null;

        // Act
        var (late, _) = await LateOutcomeFor(deliveryId, async () =>
        {
            var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(
                session, Recording(text, deliveryId), SendSource.Delivery, record));
            Assert.Equal(DeliveryState.Delivering, response.DeliveryState);
            atAnswer = record.Read(session.Id, deliveryId).State;
        });

        // Assert: "delivering" while the watch runs, then "unconfirmed" - never "not-delivered", whose "Send anyway" could
        // double words the agent may hold - with the cannot-tell reason. Typed once.
        Assert.Equal(DeliveryState.Delivering, atAnswer);
        Assert.Equal(DeliveryStates.Unconfirmed, late);
        var entry = record.Read(session.Id, deliveryId);
        Assert.Equal(DeliveryState.Unconfirmed, entry.State);
        Assert.StartsWith("could not be confirmed: the records watch failed: ", entry.Reason);
        Assert.Contains("cannot be told", entry.Reason);
        Assert.Contains(missing, entry.Reason);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(terminal.TypedText, "CANNOTTELL1"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendPromptAsync_ACopyOfAnUnconfirmedDelivery_IsRefusedAndNothingIsTyped(bool senderReadsUnconfirmed)
    {
        // Arrange (issue #3484): the record says this delivery could not be confirmed - its words may be in the agent.
        var (session, terminal) = WorkingClaudeCode();
        var record = NewDeliveryRecord();
        var deliveryId = Guid.NewGuid().ToString("N");
        record.TryBeginDelivery(session.Id, deliveryId);
        record.MarkUnconfirmed(session.Id, deliveryId, DeliveryRecord.NoRecordsToWatchReason);
        var copy = Recording("Token COPY1. Reply with exactly: ACK", deliveryId);
        copy.ReadsUnconfirmed = senderReadsUnconfirmed;

        // Act
        var result = await VirtualTime.RunAsync(_clock,
            () => ControlApi.SessionCommandExecutor.SendPromptAsync(session, copy, SendSource.Delivery, record));

        // Assert: refused either way, with nothing typed and the record unchanged. A sender that reads the word is told
        // "unconfirmed"; a sender older than it (a Gateway whose reader throws on the word) gets a failure it already rules
        // on - never "unknown verb", which it would read as a Director too old to keep a record, and never a success
        // carrying "not-delivered", which it would answer with "Send anyway".
        Assert.Equal(0, terminal.TypedText.Length);
        Assert.Equal(0, terminal.EntersAccepted);
        Assert.Equal(DeliveryState.Unconfirmed, record.Read(session.Id, deliveryId).State);
        if (senderReadsUnconfirmed)
        {
            var response = Body(result);
            Assert.False(response.Accepted);
            Assert.Equal(DeliveryState.Unconfirmed, response.DeliveryState);
            Assert.Contains("could not be confirmed", response.DeliveryStateReason);
        }
        else
        {
            Assert.False(result.Ok);
            Assert.Equal(DirectorCommandStatus.Error, result.Status);
            Assert.DoesNotContain("unknown verb", result.Error);
            Assert.Null(result.BodyJson);
        }
    }

    [Fact]
    public async Task SendPromptAsync_TheSessionEndsBeforeTheLimit_DeliveryRecordIsNotDeliveredAtOnce()
    {
        // Arrange (case 3): a working Claude Code takes the Enter, the records never show the prompt, and the session ends
        // while the one-minute watch still runs.
        var (session, terminal) = WorkingClaudeCode();
        terminal.NeverRecord = true;
        session.ArrivalWindow = TimeSpan.FromSeconds(2);
        session.LateArrivalLimitForTests = TimeSpan.FromMinutes(1);
        var record = NewDeliveryRecord();
        const string text = "Token ENDED1. Reply with exactly: ACK";
        var deliveryId = Guid.NewGuid().ToString("N");
        DeliveryState? atAnswer = null;
        DateTimeOffset endedAt = default;

        // Act
        var (late, writtenAt) = await LateOutcomeFor(deliveryId, async () =>
        {
            var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(
                session, Recording(text, deliveryId), SendSource.Delivery, record));
            Assert.Equal(DeliveryState.Delivering, response.DeliveryState);
            atAnswer = record.Read(session.Id, deliveryId).State;
            session.Dispose();
            endedAt = _clock.GetUtcNow();
        });

        // Assert: "not-delivered" as soon as the session ended - a retry into an ended session cannot double anything, so
        // this provable ending stays not-delivered, never unconfirmed (issue #3484) - not a minute later at the limit.
        // "As soon as" is one poll of the watch, read off the clock the watch runs on. Typed once.
        Assert.Equal(DeliveryState.Delivering, atAnswer);
        Assert.Equal(DeliveryStates.NotDelivered, late);
        Assert.True(writtenAt - endedAt <= TimeSpan.FromSeconds(10),
            $"not-delivered came {(writtenAt - endedAt).TotalSeconds:F1}s after the session ended");
        var entry = record.Read(session.Id, deliveryId);
        Assert.Equal(DeliveryState.NotDelivered, entry.State);
        Assert.Equal("the session ended before the words appeared in its records", entry.Reason);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(terminal.TypedText, "ENDED1"));
    }

    [Fact]
    public async Task SendPromptAsync_SendFailsWithinTheBudget_StaysAFailure()
    {
        // Arrange: the terminal refuses the write - a failure the send knows about at once.
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.Clock = _clock;
        session.AgentKind = AgentKind.Gemini;
        terminal.OnWrite = _ => throw new InvalidOperationException("the terminal refused the write");

        // Act and Assert: the failure is thrown, as before - never answered as delivered or delivering.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => VirtualTime.RunAsync(_clock, () => ControlApi.SessionCommandExecutor.SendPromptAsync(session, Prompt("Token BUDGET3. Reply: ACK"))));
        Assert.Contains("refused the write", ex.Message);
    }
}
