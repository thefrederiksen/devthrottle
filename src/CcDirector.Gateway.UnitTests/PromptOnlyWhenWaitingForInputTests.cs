using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director's own idle check for text the product sends by itself (the Fleet Manager mission, step 4): with
/// <see cref="PromptRequest.OnlyWhenWaitingForInput"/>, the Director types only when the session is waiting for a
/// prompt at the moment it would type. Driven through the real prompt core against a real session.
/// </summary>
public sealed class PromptOnlyWhenWaitingForInputTests
{
    private static (SessionManager Manager, Session Session) NewSession()
    {
        var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        return (manager, session);
    }

    private static PromptRequest EventPrompt(bool onlyWhenWaiting = true) => new()
    {
        Text = "[Fleet Manager events] 1 stop",
        AppendEnter = true,
        AgentDriven = true,
        OnlyWhenWaitingForInput = onlyWhenWaiting,
    };

    private static PromptResponse Body(DirectorCommandResult result)
    {
        Assert.True(result.Ok, result.Error);
        return JsonSerializer.Deserialize<PromptResponse>(result.BodyJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Theory]
    [InlineData(ActivityState.Working)]
    [InlineData(ActivityState.WaitingForPerm)]
    [InlineData(ActivityState.Starting)]
    public async Task SendPromptAsync_SessionNotWaitingForAPrompt_TypesNothingAndSaysSo(ActivityState state)
    {
        var (manager, session) = NewSession();
        try
        {
            session.ApplyTerminalActivityState(state);

            var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

            Assert.False(response.Accepted);
            Assert.True(response.RefusedBusy);
            Assert.True(response.IdleChecked);
            Assert.Equal(state.ToString(), response.ActivityState);
            Assert.Contains("nothing was typed", response.Error);
            // Nothing was submitted: no turn counted, and the state is what the owner's own turn made it.
            Assert.Equal(0, session.InputStats.Snapshot().AgentDrivenTurns);
            Assert.Equal(state, session.ActivityState);
        }
        finally { manager.Dispose(); }
    }

    [Theory]
    [InlineData(ActivityState.WaitingForInput)]
    [InlineData(ActivityState.Idle)]
    public async Task SendPromptAsync_SessionWaitingForAPrompt_TypesItAndConfirmsTheCheck(ActivityState state)
    {
        var (manager, session) = NewSession();
        try
        {
            session.ApplyTerminalActivityState(ActivityState.Working);
            session.ApplyTerminalActivityState(state);

            var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

            Assert.True(response.Accepted);
            Assert.False(response.RefusedBusy);
            Assert.True(response.IdleChecked);
            Assert.Equal(1, session.InputStats.Snapshot().AgentDrivenTurns);
        }
        finally { manager.Dispose(); }
    }

    /// <summary>Every other caller is unchanged: without the field a working session is typed into, and the answer
    /// says no check was made.</summary>
    [Fact]
    public async Task SendPromptAsync_FieldNotSet_TypesIntoAWorkingSession_AndReportsNoCheck()
    {
        var (manager, session) = NewSession();
        try
        {
            session.ApplyTerminalActivityState(ActivityState.Working);

            var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt(onlyWhenWaiting: false)));

            Assert.True(response.Accepted);
            Assert.False(response.IdleChecked);
            Assert.Equal(1, session.InputStats.Snapshot().AgentDrivenTurns);
        }
        finally { manager.Dispose(); }
    }
}
