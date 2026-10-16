using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts.ApiMode;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>
/// An API-mode session inside the Director's own <see cref="Session"/> (issue #4070, phase 1): the session's send and
/// Stop reach Pi through <see cref="ApiModeSessionBackend"/>, and their answers come from Pi's acknowledgement, never
/// from a terminal. Pi answers from the real recordings (<see cref="ScriptedPi"/>); nothing is spawned and no clock runs.
/// Also: only Pi's wrapper declares API mode, and the Director refuses it for any other agent before taking anything.
/// </summary>
public sealed class ApiModeSessionTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);
    private static readonly SubmissionProvenance Door = SubmissionProvenance.Typed("api-mode-test", "test");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "api-mode-session-" + Guid.NewGuid().ToString("N"));
    private readonly List<Session> _sessions = [];

    public ApiModeSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var session in _sessions)
            session.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private (Session Session, ApiModeSessionBackend Backend, RecordingSink Sink) StartSession(ScriptedPi pi, Dictionary<string, string>? environment = null)
    {
        var id = Guid.NewGuid();
        var clock = new FakeTimeProvider(Start);
        var sink = new RecordingSink();
        var backend = new ApiModeSessionBackend(id.ToString(), pi.AgentSessionId, resuming: false,
            new SessionEventStream(id.ToString(), sink, clock), () => pi.Process, clock);
        var session = new Session(id, _dir, _dir, null, backend, SessionBackendType.ConPty) { AgentKind = AgentKind.Pi };
        _sessions.Add(session);
        backend.Start("pi", $"--session-id {pi.AgentSessionId} --append-system-prompt \"{Path.Combine(_dir, "pre amble.md")}\"", _dir, 120, 30,
            environment ?? new Dictionary<string, string> { ["PATH"] = "/usr/bin" });
        session.MarkRunning();
        return (session, backend, sink);
    }

    [Fact]
    public async Task SendTextAsync_ApiModeSession_IsDeliveredFromPisAcknowledgement()
    {
        // Arrange
        var (session, _, sink) = StartSession(new ScriptedPi("plain"));

        // Act
        var outcome = await session.SendTextAsync("Reply with exactly the word PONG", Door);

        // Assert: confirmed by Pi's own response - the prompt went down the pipe, not into a terminal.
        Assert.True(outcome.Confirmed);
        Assert.False(outcome.NothingTyped);
        Assert.Single(sink.Events.OfType<PromptDelivered>());
        Assert.Equal(SessionConnectionMode.Api, session.ConnectionMode);
    }

    [Fact]
    public async Task SendTextAsync_PiRefuses_ReturnsRefusedWithPisReason()
    {
        // Arrange
        var (session, _, _) = StartSession(new ScriptedPi("refusal") { PromptAnswers = [2] });

        // Act
        var outcome = await session.SendTextAsync("A second prompt", Door);

        // Assert: refused, nothing in the conversation, and the reason is Pi's own words.
        Assert.False(outcome.Confirmed);
        Assert.True(outcome.NothingTyped);
        Assert.Null(outcome.LateProof);
        Assert.Equal("Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.", outcome.Reason);
    }

    [Fact]
    public async Task SendTextAsync_PiExitsBeforeAcknowledging_ReturnsRefusedByTheDirector()
    {
        // Arrange
        var (session, _, _) = StartSession(new ScriptedPi("exit-before-ack"));

        // Act
        var outcome = await session.SendTextAsync("Reply with exactly the word PONG", Door);

        // Assert
        Assert.True(outcome.NothingTyped);
        Assert.Equal("Pi exited before acknowledging", outcome.Reason);
    }

    [Fact]
    public async Task CancelTurnAsync_ApiModeSession_IsPisAbortAndWaitsForTheCancelledTurn()
    {
        // Arrange
        var pi = new ScriptedPi("abort");
        var (session, _, sink) = StartSession(pi);
        await session.SendTextAsync("Write a 2000 word essay about rivers.", Door);
        await sink.WaitForAsync<MessageText>(m => m.Channel == MessageTextChannel.Thought);

        // Act
        await session.CancelTurnAsync();

        // Assert: by the time Stop returns, Pi has confirmed the turn ended cancelled.
        Assert.Equal("abort", pi.WrittenTypes[^1]);
        Assert.Equal(TurnOutcome.Cancelled, Assert.Single(sink.Events.OfType<TurnEnded>()).Outcome);
    }

    [Fact]
    public async Task OnExited_PiExits_TheSessionLearnsTheExitCode()
    {
        // Arrange
        var (session, backend, _) = StartSession(new ScriptedPi("exit"));
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExited += code => exited.TrySetResult(code);

        // Act
        await session.SendTextAsync("Write a long answer", Door);
        var code = await exited.Task;

        // Assert: the ordinary exit path, through the backend's ProcessExited.
        Assert.Equal(137, code);
        Assert.Equal(137, session.ExitCode);
        Assert.True(backend.HasExited);
        Assert.Equal("Exited (137)", backend.Status);
    }

    [Fact]
    public void Start_ApiModeBackend_StartsPiInRpcModeWithTheSameLaunchLineAndEnvironment()
    {
        // Arrange
        var pi = new ScriptedPi("plain");
        var environment = new Dictionary<string, string>
        {
            ["CC_SESSION_ID"] = "a-session", ["CC_GATEWAY_URL"] = "https://gateway.invalid", ["PATH"] = "/usr/bin", ["NO_COLOR"] = "1",
        };

        // Act
        var (_, backend, _) = StartSession(pi, environment);

        // Assert: --mode rpc ahead of the terminal path's own arguments, the quoted preamble path kept whole, the
        // session's folder, every INHERITED CC_ variable removed, and the session's own stamped set applied after that,
        // exactly as terminal mode gives it.
        var started = pi.Process.StartedWith!;
        Assert.Same(backend.StartedWith, started);
        Assert.Equal(["--mode", "rpc", "--session-id", pi.AgentSessionId, "--append-system-prompt", Path.Combine(_dir, "pre amble.md")], started.Arguments);
        Assert.Equal("pi", started.Executable);
        Assert.Equal(_dir, started.WorkingDirectory);
        Assert.Equal(environment, started.SetVariables);
        Assert.Equal(["CC_"], started.RemoveVariablePrefixes);
    }

    [Fact]
    public void Write_ApiModeBackend_IsRefusedBecauseThereIsNoTerminal()
    {
        // Arrange
        var (_, backend, _) = StartSession(new ScriptedPi("plain"));

        // Act
        var error = Assert.Throws<InvalidOperationException>(() => backend.Write([0x1B]));

        // Assert
        Assert.Contains("is in API mode: it has no terminal", error.Message);
        Assert.Null(backend.Buffer);
    }

    [Fact]
    public void Start_ApiModeBackendWithAMissingExecutable_FailsNamingIt()
    {
        // Arrange: the real process; the path cannot exist, so it fails before anything is spawned.
        var id = Guid.NewGuid().ToString();
        var clock = new FakeTimeProvider(Start);
        var backend = new ApiModeSessionBackend(id, "agent-session", resuming: false,
            new SessionEventStream(id, new RecordingSink(), clock), () => new SystemApiModeProcess(), clock);
        var missing = Path.Combine(_dir, "not-installed", "pi");

        // Act
        var error = Assert.Throws<ApiModeStartException>(() => backend.Start(missing, "--session-id agent-session", _dir, 120, 30));

        // Assert
        Assert.Contains(missing, error.Message);
        Assert.Equal("Failed to start", backend.Status);
    }

    [Fact]
    public void ConnectionModes_EveryWrapper_OnlyPiDeclaresApiMode()
    {
        foreach (var kind in Enum.GetValues<AgentKind>())
        {
            // Arrange: the wrapper a session of this kind uses.
            var driver = AgentPluginRegistry.Contains(kind) ? AgentPluginRegistry.Get(kind).Driver : AgentDrivers.For(kind);

            // Act
            var modes = driver.ConnectionModes;

            // Assert
            if (kind == AgentKind.Pi)
                Assert.Equal([SessionConnectionMode.Terminal, SessionConnectionMode.Api], modes);
            else
                Assert.Equal([SessionConnectionMode.Terminal], modes);
        }
    }

    [Fact]
    public void ConnectionMode_TerminalBackend_IsTerminal()
    {
        // Arrange
        using var session = new Session(Guid.NewGuid(), _dir, _dir, null, new TerminalStandIn(), SessionBackendType.ConPty);

        // Act
        var mode = session.ConnectionMode;

        // Assert
        Assert.Equal(SessionConnectionMode.Terminal, mode);
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode)]
    [InlineData(AgentKind.Codex)]
    public void CreateSession_ApiModeForAnAgentWithoutIt_IsRefusedBeforeAnythingIsTaken(AgentKind kind)
    {
        // Arrange
        using var manager = new SessionManager(new AgentOptions());
        var agent = AgentPluginRegistry.CreateAgent(kind, new AgentOptions());

        // Act
        var error = Assert.Throws<InvalidOperationException>(() => manager.CreateSession(
            _dir, agent, null, SessionBackendType.ConPty, null, connectionMode: SessionConnectionMode.Api));

        // Assert
        Assert.EndsWith("cannot be started in API mode: its wrapper speaks only through the terminal.", error.Message);
        Assert.Empty(manager.ListSessions());
    }

    [Fact]
    public void CreateSession_ApiModeOnAnotherBackend_IsRefused()
    {
        // Arrange
        using var manager = new SessionManager(new AgentOptions());
        var agent = AgentPluginRegistry.CreateAgent(AgentKind.Pi, new AgentOptions());

        // Act
        var error = Assert.Throws<InvalidOperationException>(() => manager.CreateSession(
            _dir, agent, null, SessionBackendType.Pipe, null, connectionMode: SessionConnectionMode.Api));

        // Assert
        Assert.Equal("API mode replaces the terminal, so it cannot be combined with the Pipe backend.", error.Message);
        Assert.Empty(manager.ListSessions());
    }

    private sealed class TerminalStandIn : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Test";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
