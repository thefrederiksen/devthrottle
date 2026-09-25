using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The prompt verb answers inside the Gateway's wait (Voice Delivery mission, phase 3). The Gateway waits 30 seconds for
/// a Director command; a send to a busy agent can take minutes. On 25 September 2026 the Gateway called such a slow
/// success a failure and retried it, and the owner's words reached the agent twice. The verb now answers "delivering"
/// at its budget and lets the send carry on.
/// </summary>
public sealed class PromptAnswerBudgetTests
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

    private static PromptRequest Prompt(string text) => new() { Text = text, AppendEnter = true, Surface = "cockpit" };

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
