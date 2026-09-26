using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.Core.UnitTests.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director's answer to a Gateway command says so when it runs long (Voice Delivery mission, phase 6). On 25
/// September 2026 the prompt verb answered 32 seconds after receipt and the delivery-state verb 61 seconds after, and
/// the only lines were the command's receipt and its result. The phase 3 rule now covers these waits too: past a few
/// seconds a wait writes what it waits for and its limit, and when it ends, how long it took and how it ended.
/// </summary>
public sealed class VerbWaitNoticeTests : IDisposable
{
    private readonly List<string> _lines = new();

    public VerbWaitNoticeTests() => SendWaitNotice.LineObserver += Observe;

    public void Dispose() => SendWaitNotice.LineObserver -= Observe;

    private void Observe(string line) { lock (_lines) _lines.Add(line); }

    private List<string> Lines(string about) { lock (_lines) return _lines.Where(l => l.Contains(about, StringComparison.Ordinal)).ToList(); }

    [Fact]
    public async Task SendPromptAsync_SendOutlastsALongBudget_TheBudgetWaitSaysWhatItWaitsForAndHowItEnded()
    {
        // Arrange: typed text echoes after thirty seconds; the budget is six, so the wait runs past the notice.
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.AgentKind = AgentKind.Gemini;
        terminal.Echo = false;
        terminal.OnWrite = text =>
        {
            if (text == "\r") return;
            var bytes = Encoding.UTF8.GetBytes(text);
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => terminal.Buffer!.Write(bytes), TaskScheduler.Default);
        };

        // Act
        await ControlApi.SessionCommandExecutor.SendPromptAsync(session,
            new PromptRequest { Text = "Token VERBWAIT1. Reply: ACK", AppendEnter = true, Surface = "cockpit" },
            answerBudget: TimeSpan.FromSeconds(6));

        // Assert
        var lines = Lines(session.Id.ToString());
        Assert.Contains(lines, l => l.Contains("[SessionCommandExecutor] WAITING") && l.Contains("the send to finish before the prompt verb answers") && l.Contains("limit 6s"));
        Assert.Contains(lines, l => l.Contains("[SessionCommandExecutor] WAIT ENDED") && l.Contains("still sending") && l.Contains("delivering"));
    }

    [Fact]
    public void DescribeBudgetEnd_TheBudgetTimerFiredLate_SaysHowLateAndWhatThatMeans()
    {
        // Act: the case2f numbers - a 20-second budget that ended 32 seconds in.
        var said = ControlApi.SessionCommandExecutor.DescribeBudgetEnd(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(32));

        // Assert
        Assert.Contains("12.0s late", said);
        Assert.Contains("processor", said);
    }

    [Fact]
    public void DescribeBudgetEnd_TheBudgetTimerFiredOnTime_SaysNothingAboutLateness()
    {
        // Act
        var said = ControlApi.SessionCommandExecutor.DescribeBudgetEnd(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20.3));

        // Assert
        Assert.DoesNotContain("late", said);
    }

    [Fact]
    public async Task AnswerAsync_TheAnswerTakesLong_SaysWhatItIsAnsweringAndHowLongItTook()
    {
        // Arrange
        var command = new DirectorCommand { CommandId = "cmd-" + Guid.NewGuid().ToString("N"), Verb = "delivery-state", SessionId = Guid.NewGuid().ToString() };

        // Act
        var result = await ControlApi.CommandAnswerTiming.AnswerAsync(command, async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            return DirectorCommandResult.Success();
        });

        // Assert
        Assert.True(result.Ok);
        var lines = Lines(command.CommandId);
        Assert.Contains(lines, l => l.Contains("[GatewayStreamClient] WAITING") && l.Contains("delivery-state") && l.Contains("30s"));
        Assert.Contains(lines, l => l.Contains("[GatewayStreamClient] WAIT ENDED") && l.Contains("Ok"));
    }

    [Fact]
    public async Task AnswerAsync_TheAnswerIsQuick_WritesNothing()
    {
        // Arrange
        var command = new DirectorCommand { CommandId = "cmd-" + Guid.NewGuid().ToString("N"), Verb = "delivery-state", SessionId = Guid.NewGuid().ToString() };

        // Act
        await ControlApi.CommandAnswerTiming.AnswerAsync(command, () => Task.FromResult(DirectorCommandResult.Success()));

        // Assert
        Assert.Empty(Lines(command.CommandId));
    }
}
