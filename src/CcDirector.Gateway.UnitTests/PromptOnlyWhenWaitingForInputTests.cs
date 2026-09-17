using System.Text;
using System.Text.Json;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
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

    // ===== The owner typing between the check and the Enter (inspection round 2, finding 1) =====

    /// <summary>
    /// A terminal session over a scripted terminal: it echoes what is typed, answers an Enter with a turn's worth of
    /// output, and runs <see cref="OnWrite"/> as each write lands - which is how a test puts the owner's keystroke in the
    /// middle of the send.
    /// </summary>
    private sealed class ScriptedTerminal : ISessionBackend
    {
        public List<string> Writes { get; } = new();
        public Action<string>? OnWrite { get; set; }
        public int ProcessId => 0;
        public string Status => "scripted";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new(1 << 16);
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }

        public void Write(byte[] data)
        {
            var text = Encoding.UTF8.GetString(data);
            Writes.Add(text);
            Buffer!.Write(data);
            // An Enter starts a turn: the agent streams well past what the submit check waits for.
            if (text == "\r") Buffer.Write(Encoding.UTF8.GetBytes(new string('.', 4096)));
            OnWrite?.Invoke(text);
        }

        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private const string EventText = "[Fleet Manager events] 1 stop";

    private static (Session Session, ScriptedTerminal Terminal) NewTerminalSession()
    {
        var terminal = new ScriptedTerminal();
        var session = new Session(Guid.NewGuid(), Path.GetTempPath(), Path.GetTempPath(), null, terminal, SessionBackendType.ConPty);
        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        return (session, terminal);
    }

    private static void OwnerTypes(Session session, string keys) =>
        session.SendInput(Encoding.UTF8.GetBytes(keys), CcDirector.Core.Sessions.InputOrigin.DesktopTyped, SessionTestDoors.TestDoor);

    [Fact]
    public async Task SendPromptAsync_TheOwnerSubmitsAfterTheCheck_TypesNothingAndSaysSo()
    {
        var (session, terminal) = NewTerminalSession();
        // The owner presses Enter on their own prompt in the gap between the idle check and the first typed byte.
        session.AfterInputCheckForTests = () => OwnerTypes(session, "carry on\r");

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.False(response.Accepted);
        Assert.True(response.RefusedBusy);
        Assert.True(response.IdleChecked);
        Assert.Contains("nothing was typed", response.Error);
        Assert.Equal(new[] { "carry on\r" }, terminal.Writes);
        Assert.Equal(0, session.InputStats.Snapshot().AgentDrivenTurns);
    }

    [Fact]
    public async Task SendPromptAsync_TheOwnerTypesBetweenTheTextAndTheEnter_EnterIsNeverPressed()
    {
        var (session, terminal) = NewTerminalSession();
        // The owner's keystroke lands the moment the event text is in the composer, before the Enter.
        terminal.OnWrite = written =>
        {
            if (written.Contains(EventText, StringComparison.Ordinal)) OwnerTypes(session, "y");
        };

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.False(response.Accepted);
        Assert.True(response.RefusedBusy);
        Assert.Contains("Enter was not pressed", response.Error);
        // The text, then the owner's key, and nothing of ours after it - no Enter, no nudge.
        Assert.Equal(new[] { EventText, "y" }, terminal.Writes);
        Assert.Equal(0, session.InputStats.Snapshot().AgentDrivenTurns);
    }

    [Fact]
    public async Task SendPromptAsync_NobodyTypes_TheTerminalGetsTheTextThenTheEnter()
    {
        var (session, terminal) = NewTerminalSession();

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.True(response.Accepted);
        Assert.True(response.IdleChecked);
        Assert.Equal(new[] { EventText, "\r" }, terminal.Writes);
        Assert.Equal(1, session.InputStats.Snapshot().AgentDrivenTurns);
    }
}
