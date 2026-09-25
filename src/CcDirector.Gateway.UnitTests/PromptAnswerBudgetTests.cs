using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;
using CcDirector.Core.UnitTests.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The prompt verb answers inside the Gateway's wait (Voice Delivery mission, phase 3). The Gateway waits 30 seconds for
/// a Director command; a send to a busy agent can take minutes. On 25 September 2026 the Gateway called such a slow
/// success a failure and retried it, and the owner's words reached the agent twice. The verb now answers "delivering"
/// at its budget and lets the send carry on.
/// </summary>
public sealed class PromptAnswerBudgetTests : IDisposable
{
    /// <summary>A terminal session whose agent takes <paramref name="echoDelay"/> to show typed text - a busy machine.
    /// The agent is one the Director has no conversation records for, so the send is judged by the terminal alone.</summary>
    private static (Session Session, ScriptedTerminal Terminal) SlowTerminalSession(TimeSpan echoDelay)
    {
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.AgentKind = AgentKind.Gemini;
        terminal.Echo = false;
        terminal.OnWrite = text =>
        {
            if (text == "\r") return;
            var bytes = Encoding.UTF8.GetBytes(text);
            _ = Task.Delay(echoDelay).ContinueWith(_ => terminal.Buffer!.Write(bytes), TaskScheduler.Default);
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

    [Fact]
    public async Task SendPromptAsync_SendOutlastsTheBudget_AnswersDeliveringAndTheSendStillCompletes()
    {
        // Arrange: the echo takes two seconds; the budget is a third of a second.
        var (session, terminal) = SlowTerminalSession(TimeSpan.FromSeconds(2));
        const string text = "Token BUDGET1. Reply with exactly: ACK";
        var sw = Stopwatch.StartNew();

        // Act
        var result = await ControlApi.SessionCommandExecutor.SendPromptAsync(
            session, Prompt(text), answerBudget: TimeSpan.FromMilliseconds(300));
        var answeredAfter = sw.Elapsed;

        // Assert: answered at the budget, accepted, "delivering" - and on the wire as that word.
        var response = Body(result);
        Assert.True(answeredAfter < TimeSpan.FromSeconds(1.5), $"answered after {answeredAfter.TotalSeconds:F1}s");
        Assert.True(response.Accepted);
        Assert.Equal(DeliveryState.Delivering, response.DeliveryState);
        Assert.Contains("\"deliveryState\":\"delivering\"", result.BodyJson);
        Assert.Empty(terminal.Submitted);

        // ...and the send carried on after the answer and submitted the text once.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (terminal.Submitted.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.Equal(new[] { text }, terminal.Submitted);
    }

    [Fact]
    public async Task SendPromptAsync_SendFinishesWithinTheBudget_AnswersDelivered()
    {
        // Arrange
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.AgentKind = AgentKind.Gemini;

        // Act
        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, Prompt("Token BUDGET2. Reply: ACK")));

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
        var terminal = new ScriptedAgentTerminal(AgentKind.ClaudeCode, transcript, dir) { Working = true };
        var session = new Session(Guid.NewGuid(), dir, dir, null, terminal, SessionBackendType.ConPty) { AgentKind = AgentKind.ClaudeCode };
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

    /// <summary>Waits for the verb's late outcome for <paramref name="deliveryId"/>, as written by the one place it is written.</summary>
    private static async Task<string> LateOutcomeFor(string deliveryId, Func<Task> act)
    {
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(string id, string state) { if (id == deliveryId) seen.TrySetResult(state); }
        ControlApi.SessionCommandExecutor.LateOutcomeObserver += Observe;
        try
        {
            await act();
            var done = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(done == seen.Task, "no late outcome was written within 30 seconds");
            return await seen.Task;
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
        var late = await LateOutcomeFor(deliveryId, async () => response = Body(
            await ControlApi.SessionCommandExecutor.SendPromptAsync(session, Recording(text, deliveryId), SendSource.Delivery, NewDeliveryRecord())));

        // Assert: answered "delivering" - never a failure, never "delivered" before the records say so - then delivered.
        Assert.True(response!.Accepted);
        Assert.Equal(DeliveryState.Delivering, response.DeliveryState);
        Assert.Equal(DeliveryStates.Delivered, late);
        Assert.Equal(new[] { text }, terminal.Recorded);
        Assert.Equal(1, terminal.EntersAccepted);
    }

    [Fact]
    public async Task SendPromptAsync_WorkingClaudeCodeNeverRecordsThePrompt_AnswersDeliveringThenNotDeliveredAtTheWatchLimit()
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
        var late = await LateOutcomeFor(deliveryId, async () => response = Body(
            await ControlApi.SessionCommandExecutor.SendPromptAsync(session, Recording(text, deliveryId), SendSource.Delivery, NewDeliveryRecord())));

        // Assert: "delivering" at the answer - never a failure inside the first window - and "not-delivered" when the watch
        // ends without the records showing it (the Delivery Lead's ruling: nothing stays delivering forever). The words
        // were typed once: nothing types them a second time by itself.
        Assert.Equal(DeliveryState.Delivering, response!.DeliveryState);
        Assert.Equal(DeliveryStates.NotDelivered, late);
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
        var late = await LateOutcomeFor(deliveryId, async () =>
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
    public async Task SendPromptAsync_WorkingClaudeCodeNeverRecordsThePrompt_DeliveryRecordIsDeliveringWhileTheWatchRunsThenNotDelivered()
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
        var late = await LateOutcomeFor(deliveryId, async () =>
        {
            Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, Recording(text, deliveryId), SendSource.Delivery, record));
            atAnswer = record.Read(session.Id, deliveryId).State;
        });

        // Assert: the record says "delivering" while the watch runs - never "delivered" without proof - and "not-delivered"
        // when the watch ends without the records showing it, with the reason the Delivery Lead ruled. Typed once.
        Assert.Equal(DeliveryState.Delivering, atAnswer);
        Assert.Equal(DeliveryStates.NotDelivered, late);
        var entry = record.Read(session.Id, deliveryId);
        Assert.Equal(DeliveryState.NotDelivered, entry.State);
        Assert.Equal("never appeared in the agent's records within 2 seconds", entry.Reason);
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(terminal.TypedText, "RECLOST1"));
    }

    [Fact]
    public async Task SendPromptAsync_SendFailsWithinTheBudget_StaysAFailure()
    {
        // Arrange: the terminal refuses the write - a failure the send knows about at once.
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.AgentKind = AgentKind.Gemini;
        terminal.OnWrite = _ => throw new InvalidOperationException("the terminal refused the write");

        // Act and Assert: the failure is thrown, as before - never answered as delivered or delivering.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ControlApi.SessionCommandExecutor.SendPromptAsync(session, Prompt("Token BUDGET3. Reply: ACK")));
        Assert.Contains("refused the write", ex.Message);
    }
}
