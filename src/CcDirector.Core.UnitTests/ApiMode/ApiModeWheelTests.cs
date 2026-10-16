using CcDirector.Core.Agents;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts.ApiMode;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>
/// TAKE THE WHEEL AND HAND BACK, the Director side (API mode mission, issue #4070, phase 4). An API-mode session's
/// conversation moves from Pi's protocol process to Pi's real terminal and back, and exactly one process holds it at any
/// moment. Pi answers from the real recordings (<see cref="ScriptedPi"/>); the terminal is <see cref="FakeTerminalBackend"/>.
/// Nothing is spawned, no port is opened, and no real clock decides anything: the only clock is a fake one.
/// </summary>
public sealed class ApiModeWheelTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);
    private static readonly SubmissionProvenance Door = SubmissionProvenance.Typed("api-mode-wheel-test", "test");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "api-mode-wheel-" + Guid.NewGuid().ToString("N"));
    private readonly List<Session> _sessions = [];

    public ApiModeWheelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var session in _sessions)
            session.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>One API-mode session and everything a test reads about it.</summary>
    private sealed class Rig
    {
        public required Session Session { get; init; }
        public required ApiModeSessionBackend Backend { get; init; }
        public required RecordingSink Sink { get; init; }
        public required FakeTimeProvider Clock { get; init; }
        public List<string> Timeline { get; } = [];
        public List<ScriptedPi> PisStarted { get; } = [];
        public List<FakeTerminalBackend> Terminals { get; } = [];
    }

    /// <param name="pis">The Pi processes the session gets, in order: the first at start, the next at each hand back.</param>
    private Rig StartSession(params ScriptedPi[] pis)
    {
        var id = Guid.NewGuid();
        var clock = new FakeTimeProvider(Start);
        var sink = new RecordingSink();
        var waiting = new Queue<ScriptedPi>(pis);
        Rig? rig = null;
        var backend = new ApiModeSessionBackend(id.ToString(), pis[0].AgentSessionId, resuming: false,
            new SessionEventStream(id.ToString(), sink, clock),
            () =>
            {
                // Never two processes: every terminal has exited before a Pi process starts.
                rig!.Timeline.Add($"pi started (every terminal exited: {rig.Terminals.All(t => t.HasExited)})");
                var pi = waiting.Dequeue();
                rig.PisStarted.Add(pi);
                return pi.Process;
            },
            () =>
            {
                // ...and every Pi process has exited before a terminal starts.
                rig!.Timeline.Add($"terminal made (every pi exited: {rig.PisStarted.All(p => p.Process.HasExited)})");
                var terminal = new FakeTerminalBackend(rig.Timeline.Add);
                rig.Terminals.Add(terminal);
                return terminal;
            },
            clock);
        var session = new Session(id, _dir, _dir, null, backend, SessionBackendType.ConPty) { AgentKind = AgentKind.Pi };
        _sessions.Add(session);
        rig = new Rig { Session = session, Backend = backend, Sink = sink, Clock = clock };
        backend.Start("/usr/local/bin/pi", $"--session-id {pis[0].AgentSessionId} --append-system-prompt \"{Path.Combine(_dir, "pre amble.md")}\"",
            _dir, 100, 40, new Dictionary<string, string> { ["CC_SESSION_ID"] = id.ToString(), ["PATH"] = "/usr/bin" });
        session.MarkRunning();
        return rig;
    }

    private static Task WhenTerminalChanges(Session session)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnTerminalChanged += () => changed.TrySetResult();
        return ScriptedPi.WithinHangGuard(changed.Task, "the session's terminal to change");
    }

    [Fact]
    public async Task TakeWheelAsync_ThenHandBack_KeepsTheConversationAndEndsWithTheProtocol()
    {
        // Arrange
        var rig = StartSession(new ScriptedPi("plain"), new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        var take = await rig.Session.TakeWheelAsync();
        var holderWhileHeld = rig.Session.WheelHolder;
        var handBack = await rig.Session.HandBackWheelAsync();
        await rig.Sink.WaitForAsync<ConnectionReady>(c => c.Sequence > 1);

        // Assert: both switches done, the user held it in between, and the protocol holds it at the end.
        Assert.True(take.Done);
        Assert.True(handBack.Done);
        Assert.Equal(SessionWheelHolder.User, holderWhileHeld);
        Assert.Equal(SessionWheelHolder.Protocol, rig.Session.WheelHolder);
        // The same conversation all the way: the terminal and both Pi processes were started on the same id, in the same folder.
        var terminal = Assert.Single(rig.Terminals);
        Assert.Contains($"--session-id {rig.PisStarted[0].AgentSessionId}", terminal.StartedArguments);
        Assert.Equal(_dir, terminal.StartedWorkingDirectory);
        Assert.Equal(rig.PisStarted[0].Process.StartedWith!.Arguments, rig.PisStarted[1].Process.StartedWith!.Arguments);
        Assert.Equal(rig.PisStarted[0].Process.StartedWith!.WorkingDirectory, rig.PisStarted[1].Process.StartedWith!.WorkingDirectory);
        Assert.False(rig.Session.Status is SessionStatus.Exited or SessionStatus.Failed);
    }

    [Fact]
    public async Task TakeWheelAsync_ThenHandBack_NeverHasTwoProcessesOnTheConversation()
    {
        // Arrange
        var rig = StartSession(new ScriptedPi("plain"), new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        await rig.Session.TakeWheelAsync();
        await rig.Session.HandBackWheelAsync();

        // Assert: each process had exited before the next one started.
        Assert.Equal(
        [
            "pi started (every terminal exited: True)",
            "terminal made (every pi exited: True)",
            "terminal started",
            "terminal exited",
            "pi started (every terminal exited: True)",
        ], rig.Timeline);
    }

    [Fact]
    public async Task TakeWheelAsync_PiSlowToExit_TheTerminalStartsOnlyAfterPiHasExited()
    {
        // Arrange: a Pi that does not exit when its input closes until the test says so.
        var slow = new ScriptedPi("plain", exitsWhenInputCloses: false);
        var rig = StartSession(slow);
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act: the take closes Pi's input; while Pi is still running, nothing else may start.
        var taking = rig.Session.TakeWheelAsync();
        await ScriptedPi.WithinHangGuard(slow.Process.InputClosedTask, "the take to close Pi's input");
        var terminalsBeforePiExited = rig.Terminals.Count;
        slow.Process.Exit(0);
        var take = await ScriptedPi.WithinHangGuard(taking, "the take");

        // Assert
        Assert.Equal(0, terminalsBeforePiExited);
        Assert.True(take.Done);
        Assert.Equal(["pi started (every terminal exited: True)", "terminal made (every pi exited: True)", "terminal started"], rig.Timeline);
    }

    [Fact]
    public async Task TakeWheelAsync_AfterTheTurnStartBoundRestartedPi_StopsTheRestartedPiBeforeTheTerminalOpens()
    {
        // Arrange: Pi acknowledged a prompt and never began its turn; the bound runs out and Pi is restarted on the
        // conversation (the turn-start ruling).
        var stuck = new ScriptedPi("plain") { AcknowledgeOnly = true };
        var rig = StartSession(stuck, new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();
        Assert.True((await rig.Session.SendTextAsync("Reply with exactly the word PONG", Door)).Confirmed);
        // A send that cannot go now takes the host's lock and sends nothing, so the step that armed the bound has finished.
        Assert.Null(await rig.Backend.Host.TrySendPromptNowAsync("not sent", CancellationToken.None));
        rig.Clock.Advance(ApiModeSessionDefaults.TurnStartBound);
        var failed = await rig.Sink.WaitForAsync<TurnEnded>();
        await rig.Sink.WaitForAsync<ConnectionReady>(c => c.Sequence > failed.Sequence);

        // Act: the user takes the wheel from the restarted Pi.
        var take = await rig.Session.TakeWheelAsync();

        // Assert: the restarted Pi had exited before the terminal was made; at no point two processes.
        Assert.True(take.Done);
        Assert.Equal(
        [
            "pi started (every terminal exited: True)",
            "pi started (every terminal exited: True)",
            "terminal made (every pi exited: True)",
            "terminal started",
        ], rig.Timeline);
        Assert.Equal(1, rig.Backend.Host.Restarts);
    }

    [Fact]
    public async Task TakeWheelAsync_StartsPisTerminalWithTheTerminalPathsLaunch()
    {
        // Arrange
        var rig = StartSession(new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        await rig.Session.TakeWheelAsync();

        // Assert: the same executable, the launch line without --mode rpc (the preamble kept), the folder, the
        // environment and the session's terminal size.
        var terminal = Assert.Single(rig.Terminals);
        Assert.Equal("/usr/local/bin/pi", terminal.StartedExecutable);
        Assert.Equal($"--session-id {rig.PisStarted[0].AgentSessionId} --append-system-prompt \"{Path.Combine(_dir, "pre amble.md")}\"", terminal.StartedArguments);
        Assert.DoesNotContain("--mode", terminal.StartedArguments);
        Assert.Equal(rig.Session.Id.ToString(), terminal.StartedEnvironment!["CC_SESSION_ID"]);
        Assert.Equal(((short)100, (short)40), terminal.StartedSize);
    }

    [Fact]
    public async Task TakeWheelAsync_Twice_TheSecondIsRefusedAndNothingChanges()
    {
        // Arrange
        var rig = StartSession(new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();
        await rig.Session.TakeWheelAsync();

        // Act
        var second = await rig.Session.TakeWheelAsync();

        // Assert
        Assert.False(second.Done);
        Assert.Equal(ApiModeSessionBackend.AlreadyHeldReason, second.Refusal);
        Assert.Single(rig.Terminals);
        Assert.Equal(SessionWheelHolder.User, rig.Session.WheelHolder);
    }

    [Fact]
    public async Task HandBackWheelAsync_WhileTheTerminalIsMidTurn_IsRefusedWithTheReason()
    {
        // Arrange: the user has the wheel and Pi is working in the terminal (the activity the terminal state detector
        // reports while the user holds the wheel).
        var rig = StartSession(new ScriptedPi("plain"), new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();
        await rig.Session.TakeWheelAsync();
        rig.Session.ApplyTerminalActivityState(ActivityState.Working);

        // Act
        var handBack = await rig.Session.HandBackWheelAsync();

        // Assert: refused, the terminal still runs, and no Pi process was started beside it.
        Assert.False(handBack.Done);
        Assert.Equal(ApiModeSessionBackend.TerminalMidTurnReason, handBack.Refusal);
        Assert.False(rig.Terminals[0].HasExited);
        Assert.Single(rig.PisStarted);
        Assert.Equal(SessionWheelHolder.User, rig.Session.WheelHolder);
    }

    [Fact]
    public async Task HandBackWheelAsync_WhileTheProtocolHoldsIt_IsRefused()
    {
        // Arrange
        var rig = StartSession(new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        var handBack = await rig.Session.HandBackWheelAsync();

        // Assert
        Assert.False(handBack.Done);
        Assert.Equal(ApiModeSessionBackend.NotHeldReason, handBack.Refusal);
        Assert.Empty(rig.Terminals);
        Assert.Single(rig.PisStarted);
    }

    [Fact]
    public async Task SendTextAsync_WhileTheUserHoldsTheWheel_IsQueuedThenDeliveredOnHandBackWithItsReceipt()
    {
        // Arrange
        var rig = StartSession(new ScriptedPi("plain"), new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();
        await rig.Session.TakeWheelAsync();

        // Act: a prompt while the user has the wheel...
        var sending = rig.Session.SendTextAsync("Reply with exactly the word PONG", Door);
        var queued = await rig.Sink.WaitForAsync<PromptQueued>();
        var typedIntoTheTerminal = rig.Terminals[0].Written.Count;
        // ...then the wheel is handed back.
        await rig.Session.HandBackWheelAsync();
        var outcome = await ScriptedPi.WithinHangGuard(sending, "the queued prompt's receipt");

        // Assert: queued with the reason, never typed into the terminal, written to the new Pi and delivered.
        Assert.Equal(PromptQueuedReason.UserHasTheWheel, queued.Reason);
        Assert.Equal(0, typedIntoTheTerminal);
        Assert.True(outcome.Confirmed);
        Assert.Equal(["get_state", "prompt"], rig.PisStarted[1].WrittenTypes);
        Assert.Equal(queued.PromptId, rig.Sink.Events.OfType<PromptDelivered>().Single().PromptId);
    }

    [Fact]
    public async Task TakeWheelAsync_ThenTheTerminalExitsOnItsOwn_LeavesNoHolderAndTheNextHandBackStartsPi()
    {
        // Arrange
        var rig = StartSession(new ScriptedPi("plain"), new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();
        await rig.Session.TakeWheelAsync();
        var detached = WhenTerminalChanges(rig.Session);

        // Act: the user quits Pi in the terminal.
        rig.Terminals[0].Exit(0);
        await detached;
        var holderAfterExit = rig.Session.WheelHolder;
        var sending = rig.Session.SendTextAsync("Reply with exactly the word PONG", Door);
        var queued = await rig.Sink.WaitForAsync<PromptQueued>();
        var handBack = await rig.Session.HandBackWheelAsync();
        var outcome = await ScriptedPi.WithinHangGuard(sending, "the queued prompt's receipt");

        // Assert: the session lived on with no process, the prompt waited, and the hand back started Pi with nothing to
        // stop.
        Assert.Equal(SessionWheelHolder.None, holderAfterExit);
        Assert.Equal(PromptQueuedReason.UserHasTheWheel, queued.Reason);
        Assert.True(handBack.Done);
        Assert.True(outcome.Confirmed);
        Assert.Equal(SessionWheelHolder.Protocol, rig.Session.WheelHolder);
        Assert.False(rig.Session.Status is SessionStatus.Exited or SessionStatus.Failed);
    }

    [Fact]
    public async Task TakeWheelAsync_AfterTheTerminalExitedOnItsOwn_OpensTheTerminalAgainWithoutStartingPi()
    {
        // Arrange
        var rig = StartSession(new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();
        await rig.Session.TakeWheelAsync();
        var detached = WhenTerminalChanges(rig.Session);
        rig.Terminals[0].Exit(0);
        await detached;

        // Act
        var take = await rig.Session.TakeWheelAsync();

        // Assert
        Assert.True(take.Done);
        Assert.Equal(2, rig.Terminals.Count);
        Assert.Single(rig.PisStarted);
        Assert.Equal(SessionWheelHolder.User, rig.Session.WheelHolder);
    }

    [Fact]
    public async Task TakeWheelAsync_WhilePiOwesATurn_TheTurnStartRestartNeverRacesIt()
    {
        // Arrange: Pi acknowledged a prompt and has not begun its turn, so the turn-start bound is armed (and abort is
        // answered from the refusal recording's idle abort).
        var rig = StartSession(new ScriptedPi("refusal") { AcknowledgeOnly = true });
        await rig.Sink.WaitForAsync<ConnectionReady>();
        Assert.True((await rig.Session.SendTextAsync("A prompt", Door)).Confirmed);

        // Act: the user takes the wheel, then the bound's time passes.
        var take = await rig.Session.TakeWheelAsync();
        rig.Clock.Advance(ApiModeSessionDefaults.TurnStartBound * 2);

        // Assert: no Pi was restarted beside the terminal.
        Assert.True(take.Done);
        Assert.Single(rig.PisStarted);
        Assert.Equal(0, rig.Backend.Host.Restarts);
        Assert.Equal(SessionWheelHolder.User, rig.Session.WheelHolder);
    }

    [Fact]
    public async Task TakeWheelAsync_ThenHandBack_TheEventStreamKeepsItsGenerationAndKeepsCounting()
    {
        // Arrange
        var rig = StartSession(new ScriptedPi("plain"), new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        await rig.Session.TakeWheelAsync();
        await rig.Session.HandBackWheelAsync();
        await rig.Sink.WaitForAsync<ConnectionReady>(c => c.Sequence > 1);

        // Assert: one generation, a sequence rising by one, the take shown as the protocol's end with its reason, then the
        // protocol back.
        Assert.Single(rig.Sink.Batches.Select(b => b.Generation).Distinct());
        Assert.Equal(Enumerable.Range(1, rig.Sink.Events.Count).Select(i => (long)i), rig.Sink.Events.Select(e => e.Sequence));
        Assert.Equal(
            ["ConnectionReady", "ConnectionEnded", "ConnectionReady"],
            rig.Sink.Events.Select(e => e.GetType().Name));
        Assert.Equal(ApiModeSessionHost.UserTookTheWheelReason, rig.Sink.Events.OfType<ConnectionEnded>().Single().Reason);
    }

    [Fact]
    public async Task TakeWheelAsync_ThenHandBack_TheSessionsScreenIsTheTerminalsOnlyWhileTheUserHoldsIt()
    {
        // Arrange
        var rig = StartSession(new ScriptedPi("plain"), new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        await rig.Session.TakeWheelAsync();
        rig.Terminals[0].Draw("pi> hello from the terminal");
        var rowsWhileHeld = rig.Session.SnapshotScreenRowsWithCursor().Rows;
        var bufferWhileHeld = rig.Session.Buffer;
        await rig.Session.HandBackWheelAsync();

        // Assert
        Assert.Same(rig.Terminals[0].Buffer, bufferWhileHeld);
        Assert.Contains(rowsWhileHeld, row => row.Contains("hello from the terminal"));
        Assert.Null(rig.Session.Buffer);
        Assert.Empty(rig.Session.SnapshotScreenRowsWithCursor().Rows);
    }

    [Fact]
    public async Task Start_ASecondApiModeSessionOnAConversationAnotherHolds_IsRefused()
    {
        // Arrange: one session holds the conversation.
        var first = StartSession(new ScriptedPi("plain"));
        await first.Sink.WaitForAsync<ConnectionReady>();

        // Act: a second session tries to resume the same conversation in the same folder.
        var id = Guid.NewGuid().ToString();
        var clock = new FakeTimeProvider(Start);
        var second = new ApiModeSessionBackend(id, first.PisStarted[0].AgentSessionId, resuming: true,
            new SessionEventStream(id, new RecordingSink(), clock), () => new ScriptedPi("resume").Process, () => new FakeTerminalBackend(), clock);
        var error = Assert.Throws<InvalidOperationException>(() =>
            second.Start("pi", $"--session-id {first.PisStarted[0].AgentSessionId}", _dir, 120, 30));

        // Assert
        Assert.Contains($"already held by session {first.Session.Id}", error.Message);
        Assert.Contains("two processes may never run on one conversation", error.Message);
    }

    [Fact]
    public async Task TakeWheelAsync_TerminalSession_IsRefused()
    {
        // Arrange
        using var session = new Session(Guid.NewGuid(), _dir, _dir, null, new FakeTerminalBackend(), SessionBackendType.ConPty);

        // Act
        var take = await session.TakeWheelAsync();
        var handBack = await session.HandBackWheelAsync();

        // Assert
        Assert.Equal(Session.NoWheelReason, take.Refusal);
        Assert.Equal(Session.NoWheelReason, handBack.Refusal);
        Assert.Equal(SessionWheelHolder.None, session.WheelHolder);
    }
}
