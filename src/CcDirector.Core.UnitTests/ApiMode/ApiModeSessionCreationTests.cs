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

    // The terminal state detector's clock: a fake one, so nothing but a test's own Advance moves its readings.
    private readonly FakeTimeProvider _detectorClock = new(Start);

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
        var detector = Track(new TerminalStateDetector(manager, driveState: true, TerminalStateDetector.QuietThreshold,
            timeProvider: _detectorClock));
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
        // Windows paths are not case-sensitive, and the resolver takes the extension's casing from PATHEXT (pi.CMD).
        Assert.Equal(Path.GetFullPath(_piResolved), started.Executable, ignoreCase: OperatingSystem.IsWindows());
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
    public async Task HandBackWheelAsync_PiWorkingInTheTerminalAsTheDetectorReadsIt_IsRefused()
    {
        // Arrange: a real create with the terminal state detector running, and the user's wheel on a fake terminal.
        var pi = new ScriptedPi("plain");
        var terminals = new List<FakeTerminalBackend>();
        var (_, session, sink) = CreateApiModeSession(pi, configure: m => m.ApiModeTerminalFactory = () =>
        {
            var terminal = new FakeTerminalBackend();
            terminals.Add(terminal);
            return terminal;
        });
        await sink.WaitForAsync<ConnectionReady>();
        // A prompt first, as a user would have sent: it ends "brand new", whose start-up output the detector ignores.
        Assert.True((await session.SendTextAsync("Reply with exactly the word PONG", Door)).Confirmed);
        await sink.WaitForAsync<TurnEnded>();
        Assert.False(session.IsBrandNew);
        Assert.True((await session.TakeWheelAsync()).Done);
        var working = WaitForActivityAsync(session, ActivityState.Working);

        // Act: Pi writes to its terminal - a turn running, as the detector reads any terminal, once its settle check has
        // looked - and the user hands back. Only the detector's fake clock moves, in steps of the settle check and never as
        // far as the quiet threshold, so the reading cannot turn back to waiting before the hand back reads it.
        terminals[0].Draw("Working... reading the files\r\n");
        for (var moved = TimeSpan.Zero; !working.IsCompleted && moved <= TerminalStateDetector.MaxSettleCheckDeferral; moved += TerminalStateDetector.SettleCheckDelay)
            _detectorClock.Advance(TerminalStateDetector.SettleCheckDelay);
        await working;
        var handBack = await session.HandBackWheelAsync();

        // Assert: refused, and the terminal still holds the conversation.
        Assert.False(handBack.Done);
        Assert.Equal(ApiModeSessionBackend.TerminalMidTurnReason, handBack.Refusal);
        Assert.Equal(SessionWheelHolder.User, session.WheelHolder);
        Assert.False(terminals[0].HasExited);
    }

    [Fact]
    public async Task TakeWheelAsync_ATurnEndTheSessionHasNotHeardYet_IsHeardBeforeTheTerminalIsAttached()
    {
        // Arrange: the session's observer is held on the batch that starts a turn, so the batch that ends it is published -
        // the stream has it - but the session has not applied it. Under load that lag is real: the stream is fed under the
        // host's lock, the session from the observer loop afterwards (issue #4353).
        var pi = new ScriptedPi("plain");
        var (_, session, sink) = CreateApiModeSession(pi, configure: m => m.ApiModeTerminalFactory = () => new FakeTerminalBackend());
        await sink.WaitForAsync<ConnectionReady>();
        using var observerHeld = new ManualResetEventSlim(false);
        var backend = Assert.IsType<ApiModeSessionBackend>(Assert.IsType<UsageLimitInputGuard>(session.Backend).Inner);
        backend.EventsPublished += events =>
        {
            if (events.Any(e => e is TurnStarted))
                observerHeld.Wait(ScriptedPi.HangGuard);
        };
        // The observer is let go the moment the take waits on it, or else the moment the terminal reaches the session - a
        // take that does not wait gets there with the observer still held. No clock decides either.
        backend.Host.HeardRequested += observerHeld.Set;
        ActivityState? whenAttached = null;
        session.OnTerminalChanged += () =>
        {
            whenAttached ??= session.ActivityState;
            observerHeld.Set();
        };
        Assert.True((await session.SendTextAsync("Reply with exactly the word PONG", Door)).Confirmed);
        await sink.WaitForAsync<TurnEnded>();
        // The session applied the turn's start (its handler runs before the one holding the observer), never its end.
        await WaitForActivityAsync(session, ActivityState.Working);

        // Act: take the wheel while the turn's end is still on its way to the session.
        var taken = await ScriptedPi.WithinHangGuard(session.TakeWheelAsync(), "the take");

        // Assert: the terminal reached the session only once the session had applied the turn's end. Otherwise that stale
        // "waiting for a prompt" lands over whatever the terminal's detector reads, and a hand back reads it as not mid-turn.
        Assert.True(taken.Done);
        Assert.Equal(ActivityState.WaitingForInput, whenAttached);
    }

    [Fact]
    public async Task TakeWheelAsync_ThenHandBack_EverySessionMachineryCopesAcrossTheSwitch()
    {
        // Arrange: a real create with every per-session component running - the recorder, the session log and the
        // Wingman's watcher among them, which read a session's buffer once, at its creation, when an API-mode session has
        // none - and the user's wheel on a fake terminal. Two Pi processes: the first, and the one the hand back starts.
        var pis = new Queue<ScriptedPi>([new ScriptedPi("plain"), new ScriptedPi("plain")]);
        var terminals = new List<FakeTerminalBackend>();
        var (_, session, sink) = CreateApiModeSession(pis.Peek(), configure: m =>
        {
            m.ApiModeProcessFactory = () => pis.Dequeue().Process;
            m.ApiModeTerminalFactory = () =>
            {
                var terminal = new FakeTerminalBackend();
                terminals.Add(terminal);
                return terminal;
            };
        });
        await sink.WaitForAsync<ConnectionReady>();

        // Act: take, Pi draws in its terminal, the terminal settles idle, hand back, then a prompt over the protocol.
        Assert.True((await session.TakeWheelAsync()).Done);
        terminals[0].Draw("pi> hello from the terminal\r\n");
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        var handBack = await session.HandBackWheelAsync();
        var outcome = await session.SendTextAsync("Reply with exactly the word PONG", Door);

        // Assert: the switch went through with all of them attached, and the session carries on over the protocol.
        Assert.True(handBack.Done);
        Assert.True(outcome.Confirmed);
        Assert.Equal(SessionWheelHolder.Protocol, session.WheelHolder);
        Assert.Null(session.Buffer);
        Assert.False(session.Status is SessionStatus.Exited or SessionStatus.Failed);
    }

    [Fact]
    public async Task SessionEventSink_ChangedAfterTheSessionStarted_ReceivesItsLaterEvents()
    {
        // Arrange: a session made while the Director's sink was the first one (a Gateway configured later, say).
        var pi = new ScriptedPi("plain");
        var (manager, session, first) = CreateApiModeSession(pi);
        await first.WaitForAsync<ConnectionReady>();
        var second = new RecordingSink();

        // Act
        manager.SessionEventSink = second;
        await session.SendTextAsync("Reply with exactly the word PONG", Door);
        await second.WaitForAsync<TurnEnded>();

        // Assert: the running session's later events went where the Director's events go now.
        Assert.Single(second.Events.OfType<PromptDelivered>());
        Assert.Empty(first.Events.OfType<PromptDelivered>());
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

        // Assert: Pi exited 0 when its input closed, and the session still reads failed, with the stream's reason.
        Assert.StartsWith("Resume failed:", ended.Reason);
        Assert.Contains("messageCount 0", ended.Reason);
        Assert.EndsWith(". Pi exited (exit code 0)", ended.Reason);
        Assert.Empty(sink.Events.OfType<ConnectionReady>());
        Assert.Equal(0, session.ExitCode);
        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.True(session.Crashed);
        Assert.Equal(ended.Reason, session.EndReason);
        Assert.Equal($"failed: {ended.Reason}", session.LastStatusReason);
    }

    [Fact]
    public async Task CreateSession_ApiModePiEndsWithExitCodeZeroMidTurn_SessionFailsWithTheReason()
    {
        // Arrange: the recording of a Pi gone while its reply streamed, ending with code 0 instead of the kill.
        var pi = new ScriptedPi("exit") { ExitCode = 0 };
        var (_, session, sink) = CreateApiModeSession(pi);
        await WaitForActivityAsync(session, ActivityState.WaitingForInput);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExited += code => exited.TrySetResult(code);

        // Act
        await session.SendTextAsync("Write a long answer", Door);
        await ScriptedPi.WithinHangGuard(exited.Task, "the session's exit");

        // Assert: the turn ended failed in the stream, and the session is failed with the stream's reason, not a clean exit.
        var turn = Assert.Single(sink.Events.OfType<TurnEnded>());
        Assert.Equal(TurnOutcome.Failed, turn.Outcome);
        var ended = Assert.Single(sink.Events.OfType<ConnectionEnded>());
        Assert.Equal(0, ended.ExitCode);
        Assert.StartsWith("Pi exited during the turn (exit code 0)", ended.Reason);
        Assert.Equal(0, session.ExitCode);
        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.True(session.Crashed);
        Assert.Equal(ended.Reason, session.EndReason);
        Assert.Equal($"failed: {ended.Reason}", session.LastStatusReason);
    }

    [Fact]
    public async Task CreateSession_ApiModePiIdleEndsWithExitCodeZero_SessionExitsCleanly()
    {
        // Arrange: Pi waiting for a prompt, nothing in hand.
        var pi = new ScriptedPi("plain");
        var (_, session, sink) = CreateApiModeSession(pi);
        await WaitForActivityAsync(session, ActivityState.WaitingForInput);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExited += code => exited.TrySetResult(code);

        // Act
        pi.Process.Exit(0);
        await ScriptedPi.WithinHangGuard(exited.Task, "the session's exit");

        // Assert: an end with nothing in hand is a clean exit, as for a terminal session; the reason is still carried.
        Assert.Equal(SessionStatus.Exited, session.Status);
        Assert.False(session.Crashed);
        Assert.Equal(Assert.Single(sink.Events.OfType<ConnectionEnded>()).Reason, session.EndReason);
    }

    [Fact]
    public async Task CreateSession_ApiModePiRecordUnreadableWhileIdle_SessionFailsWithTheReason()
    {
        // Arrange: Pi's get_state answer lacks a field the dialect must read; the Director stops Pi, which exits 0.
        var pi = new ScriptedPi("plain") { EditStdout = line => line.Replace("\"command\":\"get_state\",\"success\":true,", "\"command\":\"get_state\",") };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var (_, session, sink) = CreateApiModeSession(pi);
        session.OnExited += code => exited.TrySetResult(code);
        // The session may already have ended before the handler was attached: the create returns while Pi answers.
        if (session.ExitCode is { } already)
            exited.TrySetResult(already);
        await ScriptedPi.WithinHangGuard(exited.Task, "the session's exit");

        // Assert: not a clean exit, and the reason says what went wrong.
        var ended = Assert.Single(sink.Events.OfType<ConnectionEnded>());
        Assert.StartsWith("The Director could not read Pi's record 1: ", ended.Reason);
        Assert.Equal(0, session.ExitCode);
        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.True(session.Crashed);
        Assert.Equal(ended.Reason, session.EndReason);
        Assert.Equal($"failed: {ended.Reason}", session.LastStatusReason);
    }

    [Fact]
    public async Task CreateSession_ApiModePiEndsMidTurn_ActivityGoesFromWorkingToExitedWithNoWaitBetween()
    {
        // Arrange: the issue #959 rule on its own - the exit-synthesised TurnEnded must not read as a wait for input.
        var pi = new ScriptedPi("exit") { ExitCode = 0 };
        var (_, session, _) = CreateApiModeSession(pi);
        await WaitForActivityAsync(session, ActivityState.WaitingForInput);
        var transitions = new List<(ActivityState From, ActivityState To)>();
        session.OnActivityStateChanged += (from, to) => { lock (transitions) transitions.Add((from, to)); };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExited += code => exited.TrySetResult(code);

        // Act
        await session.SendTextAsync("Write a long answer", Door);
        await ScriptedPi.WithinHangGuard(exited.Task, "the session's exit");

        // Assert
        lock (transitions)
        {
            Assert.Equal((ActivityState.Working, ActivityState.Exited), transitions[^1]);
            Assert.DoesNotContain((ActivityState.Working, ActivityState.WaitingForInput), transitions);
        }
    }
}
