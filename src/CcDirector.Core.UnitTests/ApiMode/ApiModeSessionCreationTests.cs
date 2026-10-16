using CcDirector.Core.Agents;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Pi;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts.ApiMode;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>
/// An API-mode Pi session made the way the Director makes every session - <see cref="SessionManager.CreateSession(string, IAgent, string?, SessionBackendType, string?, Guid?, string?, string?, Func{Guid, string}?, Guid?, Action{Session}?, Git.PooledWorktree?, FactoryKitRequest?, bool, SessionConnectionMode)"/>
/// - with the machinery that runs for every session attached first: state detection, the Wingman's status and
/// auto-resume watchers, the terminal recorder, the turn review log, the session log, the records watcher, the
/// pending-interaction watcher and Pi's rebinder. An API-mode session has no terminal and so no buffer; these tests prove
/// none of it throws or misreads such a session, and that the launch is terminal mode's own (issue #4070).
///
/// The process is the scripted Pi (<see cref="ScriptedPi"/>): nothing is spawned. The storage root is a throwaway folder
/// (the ConfigEnvSerial collection, so no other class races the process-wide root), because the create writes Pi's
/// preamble file under it.
/// </summary>
[Collection("ConfigEnvSerial")]
public sealed class ApiModeSessionCreationTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);
    private static readonly SubmissionProvenance Door = SubmissionProvenance.Typed("api-mode-test", "test");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "api-mode-create-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
    private readonly string _repo;
    // What the agent's executable path is configured as, and the file the Director must resolve it to.
    private readonly string _piExecutable;
    private readonly string _piResolved;
    private readonly List<IDisposable> _disposables = [];

    public ApiModeSessionCreationTests()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        // A Pi installed the way npm installs it: an extensionless script, and on Windows a pi.cmd beside it, which is the
        // one a Windows Director must start (it never runs the script). The Director resolves the configured path the same
        // way for a terminal and an API-mode session. Never run - the scripted Pi stands in.
        _piExecutable = Path.Combine(_root, "bin", "pi");
        Directory.CreateDirectory(Path.GetDirectoryName(_piExecutable)!);
        File.WriteAllText(_piExecutable, "never run");
        _piResolved = _piExecutable;
        if (OperatingSystem.IsWindows())
        {
            _piResolved = _piExecutable + ".cmd";
            File.WriteAllText(_piResolved, "never run");
        }
    }

    public void Dispose()
    {
        for (var i = _disposables.Count - 1; i >= 0; i--)
            _disposables[i].Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        try { Directory.Delete(_root, recursive: true); } catch { /* scratch folder; best effort */ }
    }

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    /// <summary>A manager with every per-session component a Director runs started on it, then one API-mode Pi session.</summary>
    private (SessionManager Manager, Session Session, RecordingSink Sink) CreateApiModeSession(ScriptedPi pi, string? resumeSessionId = null,
        Action<SessionManager>? configure = null)
    {
        var sink = new RecordingSink();
        var manager = Track(new SessionManager(new AgentOptions { PiPath = _piExecutable })
        {
            ApiModeProcessFactory = () => pi.Process,
            SessionEventSink = sink,
            ApiModeClock = new FakeTimeProvider(Start),
        });
        configure?.Invoke(manager);
        var detector = Track(new TerminalStateDetector(manager, driveState: true));
        detector.Start();
        var autoResume = Track(new TransientErrorAutoResume(manager));
        autoResume.Start();
        var recorder = Track(new TerminalSessionRecorder(manager, Path.Combine(_root, "recordings"), captureEnabled: true));
        recorder.Start();
        var wingman = Track(new SessionStatusWingman(manager));
        wingman.Start();
        var review = new TurnReviewLogger(manager);
        review.Start();
        var logs = Track(new SessionLogManager(manager));
        logs.Start();
        var records = Track(new SessionRecordsWatcher(manager));
        records.Start();
        var pending = Track(new PendingInteractionWatcher(manager));
        pending.Start();
        var rebinder = Track(new PiSessionRebinder(manager));
        rebinder.Start();

        var agent = new PiAgent(new AgentOptions { PiPath = _piExecutable });
        var session = manager.CreateSession(_repo, agent, null, SessionBackendType.ConPty, resumeSessionId,
            connectionMode: SessionConnectionMode.Api);
        return (manager, session, sink);
    }

    private static Task<ActivityState> WaitForActivityAsync(Session session, ActivityState wanted)
    {
        var reached = new TaskCompletionSource<ActivityState>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnActivityStateChanged += (_, now) => { if (now == wanted) reached.TrySetResult(now); };
        if (session.ActivityState == wanted)
            reached.TrySetResult(wanted);
        return ScriptedPi.WithinHangGuard(reached.Task, $"the session to reach {wanted}");
    }

    [Fact]
    public async Task CreateSession_ApiModePi_StartsPiInRpcModeWithTheTerminalPathsLaunchLineAndEnvironment()
    {
        // Arrange
        var pi = new ScriptedPi("plain");

        // Act
        var (manager, session, _) = CreateApiModeSession(pi);
        await WaitForActivityAsync(session, ActivityState.WaitingForInput);

        // Assert: Pi was started as terminal mode would start it - same executable, same folder, same --session-id
        // and preamble file - with --mode rpc ahead, the inherited CC_ variables removed and the session's own applied.
        var started = pi.Process.StartedWith!;
        Assert.Equal(Path.GetFullPath(_piResolved), started.Executable);
        Assert.Equal(_repo, started.WorkingDirectory);
        Assert.Equal(["--mode", "rpc", "--session-id"], started.Arguments.Take(3));
        Assert.Equal(session.ClaudeSessionId, started.Arguments[3]);
        Assert.Equal("--append-system-prompt", started.Arguments[4]);
        var preamble = started.Arguments[5];
        Assert.Equal(Path.Combine(CcStorage.PiPreamble(), Path.GetFileName(preamble)), preamble);
        Assert.True(File.Exists(preamble), "the fleet preamble file the terminal path writes for Pi exists");
        Assert.Contains(session.Id.ToString(), File.ReadAllText(preamble));
        Assert.Equal(6, started.Arguments.Count);
        Assert.Equal(session.Id.ToString(), started.SetVariables["CC_SESSION_ID"]);
        Assert.Equal(["CC_"], started.RemoveVariablePrefixes);
        Assert.Equal(SessionConnectionMode.Api, session.ConnectionMode);
        Assert.Same(session, Assert.Single(manager.ListSessions()));
        Assert.Equal(SessionStatus.Running, session.Status);
    }

    [Fact]
    public async Task CreateSession_ApiModePi_MintsAndStampsItsGatewaySessionKeyAsATerminalSessionDoes()
    {
        // Arrange: a Director connected to a Gateway, whose session key source records which sessions asked.
        const string gatewayUrl = "https://gateway.invalid";
        var asked = new List<Guid>();
        void Connect(SessionManager manager)
        {
            manager.GatewayUrl = gatewayUrl;
            manager.GatewaySessionCredentialSource = id => { lock (asked) asked.Add(id); return $"key-for-{id}"; };
        }
        var pi = new ScriptedPi("plain");

        // Act: the same Pi agent created on the terminal path (the Studio backend records the environment it was
        // started with and runs nothing) and in API mode.
        var terminalManager = Track(new SessionManager(new AgentOptions { PiPath = _piExecutable }));
        Connect(terminalManager);
        var terminal = terminalManager.CreateSession(_repo, new PiAgent(new AgentOptions { PiPath = _piExecutable }), null,
            SessionBackendType.Studio, null);
        var terminalEnvironment = Assert.IsType<StudioBackend>(terminal.Backend).StartedEnvironment!;
        var (_, api, sink) = CreateApiModeSession(pi, configure: Connect);
        await sink.WaitForAsync<ConnectionReady>();

        // Assert: each create asked the key source once, for its own session, and Pi got the pair in its environment
        // exactly as the terminal path gives it (the key row the Gateway keeps is registered by that source).
        Assert.Equal([terminal.Id, api.Id], asked);
        var apiEnvironment = pi.Process.StartedWith!.SetVariables;
        Assert.Equal(gatewayUrl, terminalEnvironment["CC_GATEWAY_URL"]);
        Assert.Equal($"key-for-{terminal.Id}", terminalEnvironment["CC_GATEWAY_SESSION_KEY"]);
        Assert.Equal(gatewayUrl, apiEnvironment["CC_GATEWAY_URL"]);
        Assert.Equal($"key-for-{api.Id}", apiEnvironment["CC_GATEWAY_SESSION_KEY"]);
        Assert.Equal(api.Id.ToString(), apiEnvironment["CC_SESSION_ID"]);
    }

    [Fact]
    public async Task CreateSession_ApiModePi_EverySessionMachineryReadsItWithoutATerminal()
    {
        // Arrange
        var pi = new ScriptedPi("plain");
        var (_, session, sink) = CreateApiModeSession(pi);
        await WaitForActivityAsync(session, ActivityState.WaitingForInput);
        var states = new List<ActivityState>();
        var turnSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnActivityStateChanged += (_, now) =>
        {
            lock (states)
            {
                states.Add(now);
                if (states is [.., ActivityState.Working, ActivityState.WaitingForInput])
                    turnSettled.TrySetResult();
            }
        };

        // Act: a prompt and its whole turn.
        var outcome = await session.SendTextAsync("Reply with exactly the word PONG", Door);
        await ScriptedPi.WithinHangGuard(turnSettled.Task, "the turn's working-then-waiting activity");

        // Assert: delivered by Pi's acknowledgement; the activity came from Pi's events (working for the turn, waiting
        // after it) - not from a screen, which there is none of.
        Assert.True(outcome.Confirmed);
        lock (states)
            Assert.Equal([ActivityState.Working, ActivityState.WaitingForInput], states);
        Assert.Null(session.Buffer);
        // Every screen read answers "nothing on screen" rather than throwing or inventing a screen.
        Assert.Empty(session.SnapshotScreenRowsWithCursor().Rows);
        Assert.Empty(session.SnapshotLiveScreen().Rows);
        Assert.Empty(session.SnapshotScreenColoredRows());
        Assert.Empty(session.GetTerminalSnapshot().Snapshot);
        Assert.Equal("", session.GetHtmlSnapshot());
        Assert.Equal(0, session.GetHtmlSnapshotSplit().ScrollbackCount);
        Assert.False(session.IsAlternateScreen);
        Assert.Empty(pi.Unexpected);
    }

    [Fact]
    public async Task SendTextOnlyWhenWaitingForInputAsync_ApiModePiWaiting_SendsThroughPi()
    {
        // Arrange
        var pi = new ScriptedPi("plain");
        var (_, session, sink) = CreateApiModeSession(pi);
        await WaitForActivityAsync(session, ActivityState.WaitingForInput);

        // Act: the guarded send the prompt verb's "only when waiting" mode uses, and the API-mode doorbell rides on.
        var sent = await session.SendTextOnlyWhenWaitingForInputAsync("Reply with exactly the word PONG", Door, SendSource.UserInput, null, appendEnter: true);

        // Assert
        Assert.True(sent.Accepted, sent.Reason);
        Assert.Single(sink.Events.OfType<PromptDelivered>());
        Assert.Equal(["get_state", "prompt"], pi.WrittenTypes);
    }

    [Fact]
    public async Task SendTextOnlyWhenWaitingForInputAsync_ApiModePiBusy_IsRefusedBusyAndSendsNothing()
    {
        // Arrange: Pi has a prompt in flight that it has not answered.
        var pi = new ScriptedPi("plain") { AutoRespond = false };
        var (_, session, _) = CreateApiModeSession(pi);
        await pi.WaitForWrittenAsync("get_state");
        pi.RespondToNext();
        await WaitForActivityAsync(session, ActivityState.WaitingForInput);
        var first = session.SendTextAsync("First", Door);
        await pi.WaitForWrittenAsync("prompt");

        // Act
        var sent = await session.SendTextOnlyWhenWaitingForInputAsync("Second", Door, SendSource.Agent, null, appendEnter: true);

        // Assert: refused, not queued, and nothing more was written to Pi.
        Assert.False(sent.Accepted);
        Assert.True(sent.RefusedBusy);
        Assert.Equal(["get_state", "prompt"], pi.WrittenTypes);
        pi.RespondToNext();
        Assert.True((await first).Confirmed);
    }

    [Fact]
    public async Task CreateSession_ApiModePiResumed_ResumesPisConversationById()
    {
        // Arrange: the conversation the resume recording's Pi answered from.
        var pi = new ScriptedPi("resume");

        // Act
        var (_, session, sink) = CreateApiModeSession(pi, resumeSessionId: pi.AgentSessionId);
        var ready = await sink.WaitForAsync<ConnectionReady>();

        // Assert: the same --session-id, the conversation found (messageCount 5), and the session linked to it.
        Assert.Equal(pi.AgentSessionId, pi.Process.StartedWith!.Arguments[3]);
        Assert.Equal(pi.AgentSessionId, ready.AgentSessionId);
        Assert.Equal(pi.AgentSessionId, session.ClaudeSessionId);
    }

    [Fact]
    public async Task CreateSession_ApiModePiResumeFindsNothing_SessionEndsWithTheReason()
    {
        // Arrange: a resume whose Pi has no messages under the id.
        var pi = new ScriptedPi("resume") { EditStdout = line => line.Replace("\"messageCount\":5", "\"messageCount\":0") };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var (_, session, sink) = CreateApiModeSession(pi, resumeSessionId: pi.AgentSessionId);
        session.OnExited += code => exited.TrySetResult(code);
        // The session may already have ended before the handler was attached: the create returns while Pi answers.
        if (session.ExitCode is { } already)
            exited.TrySetResult(already);
        var ended = await sink.WaitForAsync<ConnectionEnded>();
        await ScriptedPi.WithinHangGuard(exited.Task, "the session's exit");

        // Assert
        Assert.StartsWith("Resume failed:", ended.Reason);
        Assert.Contains("messageCount 0", ended.Reason);
        Assert.Empty(sink.Events.OfType<ConnectionReady>());
        Assert.True(session.Status is SessionStatus.Exited or SessionStatus.Failed);
    }
}
