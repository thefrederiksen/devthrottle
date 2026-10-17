using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
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
    public async Task SendTextAsync_ApiModeSessionWithAGatewayDeliveryId_CarriesItAsThePromptId()
    {
        // Arrange
        var (session, _, sink) = StartSession(new ScriptedPi("plain"));

        // Act: the send path the Gateway's prompt verb takes, with the recording's delivery id.
        var outcome = await session.SendTextAsync("Reply with exactly the word PONG", Door, deliveryId: "upload-3c2e");
        var turn = await sink.WaitForAsync<TurnStarted>();

        // Assert
        Assert.True(outcome.Confirmed);
        Assert.Equal("upload-3c2e", Assert.Single(sink.Events.OfType<PromptDelivered>()).PromptId);
        Assert.Equal(["upload-3c2e"], turn.PromptIds);
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
        var code = await ScriptedPi.WithinHangGuard(exited.Task, "the session's exit");

        // Assert: the ordinary exit path, through the backend's ProcessExited.
        Assert.Equal(137, code);
        Assert.Equal(137, session.ExitCode);
        Assert.True(backend.HasExited);
        Assert.Equal("Exited (137)", backend.Status);
    }

    [Fact]
    public async Task ActivityState_ApiModeSession_IsStartingUntilPiNamesItsConversation()
    {
        // Arrange: Pi answers get_state only when the test says so.
        var pi = new ScriptedPi("plain") { AutoRespond = false };
        var (session, _, _) = StartSession(pi);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnActivityStateChanged += (_, now) => { if (now == ActivityState.WaitingForInput) waiting.TrySetResult(); };
        await pi.WaitForWrittenAsync("get_state");

        // Act
        var before = session.ActivityState;
        pi.RespondToNext();
        await ScriptedPi.WithinHangGuard(waiting.Task, "the session to wait for input");

        // Assert: a session never claims to wait for a prompt its agent cannot take yet.
        Assert.Equal(ActivityState.Starting, before);
        Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);
    }

    [Fact]
    public async Task RingAsync_ApiModeSessionWaiting_SendsTheDoorbellLineToPiAndIsRung()
    {
        // Arrange
        var pi = new ScriptedPi("plain");
        var (session, _, sink) = StartSession(pi);
        await sink.WaitForAsync<ConnectionReady>();

        // Act
        var answer = await FleetDoorbellRinger.RingAsync(session, 2);

        // Assert: rung on Pi's own acknowledgement of the one doorbell line, sent as an ordinary prompt.
        Assert.Equal(FleetRingOutcomes.Rung, answer.Outcome);
        Assert.Equal(["get_state", "prompt"], pi.WrittenTypes);
        Assert.Equal(FleetDoorbellLine.For(2), System.Text.Json.JsonDocument.Parse(pi.Written[1]).RootElement.GetProperty("message").GetString());
        Assert.Single(sink.Events.OfType<PromptDelivered>());
    }

    [Fact]
    public async Task RingAsync_ApiModeSessionBusy_IsDeferredWorkingAndSendsNothing()
    {
        // Arrange: a prompt is in flight and Pi has not answered it.
        var pi = new ScriptedPi("plain") { AutoRespond = false };
        var (session, _, sink) = StartSession(pi);
        await pi.WaitForWrittenAsync("get_state");
        pi.RespondToNext();
        await sink.WaitForAsync<ConnectionReady>();
        var first = session.SendTextAsync("First", Door);
        await pi.WaitForWrittenAsync("prompt");

        // Act
        var answer = await FleetDoorbellRinger.RingAsync(session, 1);

        // Assert
        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal(FleetRingDeferReasons.Working, answer.Reason);
        Assert.Equal(["get_state", "prompt"], pi.WrittenTypes);
        pi.RespondToNext();
        Assert.True((await ScriptedPi.WithinHangGuard(first, "the first prompt's receipt")).Confirmed);
    }

    [Fact]
    public async Task RingAsync_ApiModeSessionThatEnded_IsDeferredExited()
    {
        // Arrange
        var pi = new ScriptedPi("exit-before-ack");
        var (session, _, _) = StartSession(pi);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExited += code => exited.TrySetResult(code);
        await session.SendTextAsync("Reply with exactly the word PONG", Door);
        await ScriptedPi.WithinHangGuard(exited.Task, "the session's exit");

        // Act
        var answer = await FleetDoorbellRinger.RingAsync(session, 1);

        // Assert
        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal(FleetRingDeferReasons.Exited, answer.Reason);
    }

    [Fact]
    public async Task DeliverPrePromptAsync_ApiModeSession_IsQueuedAtOnceAndWrittenRightAfterPiNamesItsConversation()
    {
        // Arrange: Pi answers only when the test says so. The wait given is far longer than the tests' hang guard, so a
        // session that sat out the wait instead of handing the prompt over fails here rather than passing late.
        var pi = new ScriptedPi("plain") { AutoRespond = false };
        var (session, _, sink) = StartSession(pi);
        await pi.WaitForWrittenAsync("get_state");

        // Act: the first prompt is handed over before Pi is ready...
        var delivering = session.DeliverPrePromptAsync("Reply with exactly the word PONG", TimeSpan.FromMinutes(10));
        var queued = await sink.WaitForAsync<PromptQueued>();
        // ...and goes the moment Pi names its conversation, not after any wait.
        pi.RespondToNext();
        var written = await pi.WaitForWrittenAsync("prompt");
        pi.RespondToNext();
        await ScriptedPi.WithinHangGuard(delivering, "the first prompt's delivery");

        // Assert
        Assert.Equal(PromptQueuedReason.AgentStarting, queued.Reason);
        Assert.Equal("Reply with exactly the word PONG", System.Text.Json.JsonDocument.Parse(written).RootElement.GetProperty("message").GetString());
        Assert.Single(sink.Events.OfType<PromptDelivered>());
    }

    // ---------------------------------------------------------------- the prompt's text, from every source (ruling 15)

    /// <summary>A prompt whose newlines and leading and trailing spaces must reach every event unchanged.</summary>
    private const string SpacedText = "  Reply with exactly the word PONG\n\n  then stop.\r\n  ";

    private static string MessageOf(string line) => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("message").GetString()!;

    [Theory]
    [InlineData(SendSource.UserInput, null)]          // the owner typing in the desktop
    [InlineData(SendSource.UserInput, "upload-7f3a")] // the Gateway's prompt route, with its delivery id
    [InlineData(SendSource.Delivery, "upload-91bc")]  // a dictation's own delivery through the Gateway
    [InlineData(SendSource.Agent, null)]              // a fleet message
    [InlineData(SendSource.Framework, null)]          // the framework's own text
    public async Task SendTextAsync_EverySource_DeliveredCarriesTheTextExactlyAsWrittenToPi(SendSource source, string? deliveryId)
    {
        // Arrange
        var pi = new ScriptedPi("plain");
        var (session, _, sink) = StartSession(pi);

        // Act
        var outcome = await session.SendTextAsync(SpacedText, Door, source, deliveryId: deliveryId);

        // Assert
        Assert.True(outcome.Confirmed);
        var delivered = Assert.Single(sink.Events.OfType<PromptDelivered>());
        Assert.Equal(SpacedText, MessageOf(pi.Written[1]));
        Assert.Equal(SpacedText, delivered.PromptText);
        if (deliveryId is not null)
            Assert.Equal(deliveryId, delivered.PromptId);
    }

    [Fact]
    public async Task SendTextOnlyWhenWaitingForInputAsync_GuardedSend_DeliveredCarriesTheTextExactlyAsWrittenToPi()
    {
        // Arrange
        var pi = new ScriptedPi("plain");
        var (session, _, sink) = StartSession(pi);
        await sink.WaitForAsync<ConnectionReady>();

        // Act: the guarded send the Gateway's prompt route and the Fleet Manager use.
        await session.SendTextOnlyWhenWaitingForInputAsync(SpacedText, Door, SendSource.Agent, null, appendEnter: true, deliveryId: "upload-3c2e");

        // Assert
        var delivered = Assert.Single(sink.Events.OfType<PromptDelivered>());
        Assert.Equal("upload-3c2e", delivered.PromptId);
        Assert.Equal(SpacedText, MessageOf(pi.Written[1]));
        Assert.Equal(SpacedText, delivered.PromptText);
    }

    [Fact]
    public async Task RingAsync_FleetDoorbell_DeliveredCarriesTheDoorbellLineExactly()
    {
        // Arrange
        var pi = new ScriptedPi("plain");
        var (session, _, sink) = StartSession(pi);
        await sink.WaitForAsync<ConnectionReady>();

        // Act
        await FleetDoorbellRinger.RingAsync(session, 3);

        // Assert
        Assert.Equal(FleetDoorbellLine.For(3), MessageOf(pi.Written[1]));
        Assert.Equal(FleetDoorbellLine.For(3), Assert.Single(sink.Events.OfType<PromptDelivered>()).PromptText);
    }

    [Fact]
    public async Task DeliverPrePromptAsync_PrePrompt_QueuedAndDeliveredCarryTheTextExactlyAsWrittenToPi()
    {
        // Arrange
        var pi = new ScriptedPi("plain") { AutoRespond = false };
        var (session, _, sink) = StartSession(pi);
        await pi.WaitForWrittenAsync("get_state");

        // Act: handed over before Pi is ready, so it waits in the Director's queue first.
        var delivering = session.DeliverPrePromptAsync(SpacedText, TimeSpan.FromMinutes(10));
        var queued = await sink.WaitForAsync<PromptQueued>();
        pi.RespondToNext();
        var written = await pi.WaitForWrittenAsync("prompt");
        pi.RespondToNext();
        await ScriptedPi.WithinHangGuard(delivering, "the pre-prompt's delivery");

        // Assert
        Assert.Equal(SpacedText, MessageOf(written));
        Assert.Equal(SpacedText, queued.PromptText);
        Assert.Equal(SpacedText, Assert.Single(sink.Events.OfType<PromptDelivered>()).PromptText);
    }

    [Fact]
    public async Task SendTextAsync_PiRefuses_RefusedCarriesTheText()
    {
        // Arrange
        var (session, _, sink) = StartSession(new ScriptedPi("refusal") { PromptAnswers = [2] });

        // Act
        await session.SendTextAsync(SpacedText, Door);

        // Assert
        Assert.Equal(SpacedText, Assert.Single(sink.Events.OfType<PromptRefused>()).PromptText);
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
