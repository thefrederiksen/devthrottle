using System.Text.Json;
using CcDirector.Core.ApiMode;
using CcDirector.Core.ApiMode.Pi;
using CcDirector.Gateway.Contracts.ApiMode;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>
/// The API-mode session host end to end: the transport, Pi's dialect and the Director's prompt queue, driven by a Pi that
/// answers from the real recordings in TestData/pi-rpc (<see cref="ScriptedPi"/>). Each test sends what a session sends
/// - a prompt, a Stop - and asserts the receipt the send returns and the session events the stream published, per
/// docs/missions/api-mode-pi-2026-10-16/event-model.md. No Pi, no process, no port, no sleep, no real clock: every wait
/// is on an event the host published or a line it wrote.
/// </summary>
public sealed class ApiModeSessionHostTests
{
    private const string SessionId = "session-under-test";

    private static readonly DateTimeOffset Start = new(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);

    private static readonly ApiModeProcessStartInfo PiStart = new()
    {
        Executable = "pi",
        Arguments = ["--mode", "rpc", "--session-id", "from-the-recording"],
        WorkingDirectory = "/work",
    };

    private sealed class Rig(ScriptedPi pi, RecordingSink sink, ApiModeSessionHost host, SessionEventStream stream, FakeTimeProvider clock) : IAsyncDisposable
    {
        public ScriptedPi Pi { get; } = pi;
        public FakeTimeProvider Clock { get; } = clock;
        public RecordingSink Sink { get; } = sink;
        public ApiModeSessionHost Host { get; } = host;
        public SessionEventStream Stream { get; } = stream;

        public Task<ConnectionEnded> ShutdownAsync() => Host.ShutdownAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    /// <param name="pi">The Pi process the host starts.</param>
    /// <param name="restarts">The Pi processes the host gets when it restarts Pi, in order.</param>
    private static Rig StartHost(ScriptedPi pi, bool resuming = false, SessionEventStream? stream = null, RecordingSink? sink = null, params ScriptedPi[] restarts)
    {
        var clock = new FakeTimeProvider(Start);
        sink ??= new RecordingSink();
        stream ??= new SessionEventStream(SessionId, sink, clock);
        var processes = new Queue<ScriptedPi>([pi, .. restarts]);
        var host = new ApiModeSessionHost(SessionId, pi.AgentSessionId, resuming, () => processes.Dequeue().Process, stream, clock);
        host.Start(PiStart);
        return new Rig(pi, sink, host, stream, clock);
    }

    private static string MessageOf(string line) => JsonDocument.Parse(line).RootElement.GetProperty("message").GetString()!;

    [Fact]
    public async Task Start_AnyRecording_WritesGetStateFirstAndReportsConnectionReady()
    {
        // Arrange
        await using var rig = StartHost(new ScriptedPi("plain"));

        // Act
        var ready = await rig.Sink.WaitForAsync<ConnectionReady>();

        // Assert: Pi named its conversation from get_state, before any prompt could go.
        Assert.Equal("get_state", rig.Pi.WrittenTypes[0]);
        Assert.Equal("311ffc3b-7a39-45cf-bcb5-596c494cdbbf", ready.AgentSessionId);
        Assert.Equal("gpt-5.6-sol", ready.Model);
        Assert.Equal(1, ready.Sequence);
    }

    [Fact]
    public async Task SendPromptAsync_PlainPrompt_IsDeliveredAndTheTurnEndsDone()
    {
        // Arrange
        await using var rig = StartHost(new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        var receipt = await rig.Host.SendPromptAsync("Reply with exactly the word PONG", CancellationToken.None);
        await rig.Sink.WaitForAsync<TurnEnded>();
        var ended = await rig.ShutdownAsync();

        // Assert: the receipt is Pi's own acknowledgement, and the turn streamed the reply and ended done.
        Assert.Equal(new ApiModePromptReceipt("prompt-1", true, null, null), receipt);
        Assert.Equal(
        [
            "ConnectionReady 311ffc3b-7a39-45cf-bcb5-596c494cdbbf gpt-5.6-sol",
            "PromptDelivered prompt-1 Started",
            "TurnStarted turn-1 [prompt-1]",
            "MessageText turn-1-message-1 Reply Delta \"P\"",
            "MessageText turn-1-message-1 Reply Delta \"ONG\"",
            "MessageText turn-1-message-1 Reply Final \"PONG\"",
            "TurnEnded turn-1 Done reason= stop=stop",
            "ConnectionEnded 0: Pi exited (exit code 0) []",
        ], rig.Sink.Lines());
        Assert.Equal(["get_state", "prompt"], rig.Pi.WrittenTypes);
        Assert.Equal("Reply with exactly the word PONG", MessageOf(rig.Pi.Written[1]));
        Assert.Equal(0, ended.ExitCode);
        Assert.Empty(rig.Host.Defects);
        Assert.Empty(rig.Pi.Unexpected);
    }

    [Fact]
    public async Task SendPromptAsync_PlainPrompt_AcknowledgementIsPisLineWithTheHostsCommandId()
    {
        // Arrange
        await using var rig = StartHost(new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        await rig.Host.SendPromptAsync("Reply with exactly the word PONG", CancellationToken.None);

        // Assert: rule 4 - the receipt is Pi's response line verbatim, answering the id the host wrote.
        var delivered = await rig.Sink.WaitForAsync<PromptDelivered>();
        var promptId = JsonDocument.Parse(rig.Pi.Written[1]).RootElement.GetProperty("id").GetString();
        Assert.Equal($"{{\"id\":\"{promptId}\",\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{{\"disposition\":\"started\"}}}}", delivered.Acknowledgement);
    }

    [Fact]
    public async Task SendPromptAsync_ToolPrompt_YieldsAFinishedToolCall()
    {
        // Arrange
        await using var rig = StartHost(new ScriptedPi("tool"));
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        var receipt = await rig.Host.SendPromptAsync("Run echo fixture-ok with bash", CancellationToken.None);
        var turnEnded = await rig.Sink.WaitForAsync<TurnEnded>();

        // Assert: the card started running and finished, succeeded, inside the one turn, which ended done.
        Assert.True(receipt.Delivered);
        var started = Assert.Single(rig.Sink.Events.OfType<ToolCallStarted>());
        var finished = Assert.Single(rig.Sink.Events.OfType<ToolCallFinished>());
        Assert.Equal("bash", started.ToolName);
        Assert.Equal(ToolCallKind.Execute, started.Kind);
        Assert.Equal(started.ToolCallId, finished.ToolCallId);
        Assert.Equal(ToolCallOutcome.Succeeded, finished.Outcome);
        Assert.Equal("fixture-ok\n", finished.Output);
        Assert.Equal(TurnOutcome.Done, turnEnded.Outcome);
        Assert.Empty(rig.Host.Defects);
    }

    [Fact]
    public async Task StopAsync_DuringATurn_PiConfirmsAndTheTurnEndsCancelled()
    {
        // Arrange: the recording's prompt answer stops mid-thought, where the recorder sent its abort.
        await using var rig = StartHost(new ScriptedPi("abort"));
        await rig.Sink.WaitForAsync<ConnectionReady>();
        await rig.Host.SendPromptAsync("Write a 2000 word essay about rivers.", CancellationToken.None);
        await rig.Sink.WaitForAsync<MessageText>(m => m.Channel == MessageTextChannel.Thought);

        // Act
        var stopped = await rig.Host.StopAsync(CancellationToken.None);

        // Assert: Stop is Pi's abort, and Stop returns only once Pi ended the turn cancelled.
        Assert.NotNull(stopped);
        Assert.Equal(TurnOutcome.Cancelled, stopped.Outcome);
        Assert.Equal("stopped by the user", stopped.Reason);
        Assert.Equal("aborted", stopped.AgentStopReason);
        Assert.Equal(["get_state", "prompt", "abort"], rig.Pi.WrittenTypes);
        Assert.Empty(rig.Host.Defects);
    }

    [Fact]
    public async Task StopAsync_NothingRunning_ReturnsNullAndEmitsNothing()
    {
        // Arrange: the refusal recording's abort while idle (its fifth command), answered success with no events.
        await using var rig = StartHost(new ScriptedPi("refusal"));
        await rig.Sink.WaitForAsync<ConnectionReady>();
        var before = rig.Sink.Events.Count;

        // Act
        var stopped = await rig.Host.StopAsync(CancellationToken.None);

        // Assert
        Assert.Null(stopped);
        Assert.Equal(before, rig.Sink.Events.Count);
    }

    [Fact]
    public async Task SendPromptAsync_PiRefuses_IsRefusedWithPisOwnReason()
    {
        // Arrange: the host's prompt gets the answer Pi gave the recording's second prompt - a refusal.
        await using var rig = StartHost(new ScriptedPi("refusal") { PromptAnswers = [2] });
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        var receipt = await rig.Host.SendPromptAsync("A second prompt", CancellationToken.None);

        // Assert
        Assert.False(receipt.Delivered);
        Assert.Equal(PromptRefusedBy.Agent, receipt.RefusedBy);
        Assert.Equal("Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.", receipt.RefusalReason);
        var refused = Assert.Single(rig.Sink.Events.OfType<PromptRefused>());
        Assert.Equal(receipt.RefusalReason, refused.Reason);
        Assert.Empty(rig.Sink.Events.OfType<PromptDelivered>());
    }

    [Fact]
    public async Task SendPromptAsync_PiExitsMidTurn_TurnEndsFailedWithTheReasonAndNeverDone()
    {
        // Arrange: the recording's Pi was killed while the reply streamed.
        await using var rig = StartHost(new ScriptedPi("exit"));
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        var receipt = await rig.Host.SendPromptAsync("Write a long answer", CancellationToken.None);
        var ended = await rig.Host.Ended;

        // Assert: Delivered stays (Pi acknowledged it), and the prompt's turn ended failed with the exit stated, so
        // the receipt never reads as a plain success (event model, section 2 ruling and rule 5).
        Assert.True(receipt.Delivered);
        var turnStarted = Assert.Single(rig.Sink.Events.OfType<TurnStarted>());
        var turnEnded = Assert.Single(rig.Sink.Events.OfType<TurnEnded>());
        Assert.Equal(["prompt-1"], turnStarted.PromptIds);
        Assert.Equal(turnStarted.TurnId, turnEnded.TurnId);
        Assert.Equal(TurnOutcome.Failed, turnEnded.Outcome);
        Assert.StartsWith("Pi exited during the turn (exit code 137)", turnEnded.Reason);
        Assert.Null(turnEnded.AgentStopReason);
        Assert.Equal(137, ended.ExitCode);
        Assert.IsType<ConnectionEnded>(rig.Sink.Events[^1]);
    }

    [Fact]
    public async Task SendPromptAsync_PiExitsBeforeAcknowledging_IsRefusedByTheDirector()
    {
        // Arrange: the recording's Pi was killed 50 milliseconds after the prompt was written and wrote nothing.
        await using var rig = StartHost(new ScriptedPi("exit-before-ack"));
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        var receipt = await rig.Host.SendPromptAsync("Reply with exactly the word PONG", CancellationToken.None);
        var ended = await rig.Host.Ended;

        // Assert
        Assert.Equal(new ApiModePromptReceipt("prompt-1", false, "Pi exited before acknowledging", PromptRefusedBy.Director), receipt);
        Assert.Empty(rig.Sink.Events.OfType<TurnStarted>());
        Assert.Empty(rig.Sink.Events.OfType<PromptDelivered>());
        Assert.Equal(137, ended.ExitCode);
    }

    [Fact]
    public async Task SendPromptAsync_AfterPiEnded_IsRefusedWithWhyPiIsNotRunning()
    {
        // Arrange
        await using var rig = StartHost(new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();
        await rig.ShutdownAsync();

        // Act
        var receipt = await rig.Host.SendPromptAsync("Too late", CancellationToken.None);

        // Assert
        Assert.False(receipt.Delivered);
        Assert.Equal(PromptRefusedBy.Director, receipt.RefusedBy);
        Assert.Equal("Pi is not running: Pi exited (exit code 0)", receipt.RefusalReason);
        Assert.Equal(["get_state"], rig.Pi.WrittenTypes);
    }

    [Fact]
    public async Task SendPromptAsync_WhileAPromptIsInFlight_IsQueuedThenWrittenAfterTheFirstSettles()
    {
        // Arrange: Pi answers only when the test says so.
        var pi = new ScriptedPi("plain") { AutoRespond = false, RepeatFirstPromptAnswer = true };
        await using var rig = StartHost(pi);
        await pi.WaitForWrittenAsync("get_state");
        pi.RespondToNext();
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act: the first prompt is written and waits for Pi; the second is sent while it is in flight.
        var first = rig.Host.SendPromptAsync("First", CancellationToken.None);
        await pi.WaitForWrittenAsync("prompt");
        var second = rig.Host.SendPromptAsync("Second", CancellationToken.None);
        var queued = await rig.Sink.WaitForAsync<PromptQueued>();

        // Assert: held by the Director, not written - Pi would refuse a prompt while busy.
        Assert.Equal(new[] { "prompt-2", "TurnRunning", "1" }, new[] { queued.PromptId, queued.Reason.ToString(), queued.Position.ToString() });
        Assert.Equal(["get_state", "prompt"], pi.WrittenTypes);

        // Act: Pi acknowledges the first and runs its turn to the end.
        pi.RespondToNext();
        Assert.True((await first).Delivered);
        await rig.Sink.WaitForAsync<TurnEnded>(t => t.TurnId == "turn-1");
        var secondLine = await pi.WaitForWrittenAsync("prompt", occurrence: 2);
        pi.RespondToNext();

        // Assert: the second was written only after the first settled, and got its own receipt and turn.
        Assert.Equal("Second", MessageOf(secondLine));
        Assert.True((await second).Delivered);
        var turnTwo = await rig.Sink.WaitForAsync<TurnStarted>(t => t.TurnId == "turn-2");
        Assert.Equal(["prompt-2"], turnTwo.PromptIds);
        var lines = rig.Sink.Lines();
        Assert.True(lines.IndexOf("TurnEnded turn-1 Done reason= stop=stop") < lines.IndexOf("PromptDelivered prompt-2 Started"));
        Assert.Empty(rig.Host.Defects);
    }

    [Fact]
    public async Task SendPromptAsync_BeforePiNamedItsConversation_IsQueuedAgentStartingThenWritten()
    {
        // Arrange
        var pi = new ScriptedPi("plain") { AutoRespond = false };
        await using var rig = StartHost(pi);
        await pi.WaitForWrittenAsync("get_state");

        // Act
        var sending = rig.Host.SendPromptAsync("Reply with exactly the word PONG", CancellationToken.None);
        var queued = await rig.Sink.WaitForAsync<PromptQueued>();
        var writtenBeforeReady = pi.WrittenTypes;
        pi.RespondToNext();
        await pi.WaitForWrittenAsync("prompt");
        pi.RespondToNext();
        var receipt = await sending;

        // Assert: no prompt before ConnectionReady.
        Assert.Equal(PromptQueuedReason.AgentStarting, queued.Reason);
        Assert.Equal(1, queued.Position);
        Assert.Equal(["get_state"], writtenBeforeReady);
        Assert.True(receipt.Delivered);
        var lines = rig.Sink.Lines();
        Assert.True(lines.IndexOf("ConnectionReady 311ffc3b-7a39-45cf-bcb5-596c494cdbbf gpt-5.6-sol") < lines.IndexOf("PromptDelivered prompt-1 Started"));
    }

    [Fact]
    public async Task Start_ResumeThatFoundNothing_StopsPiWithTheReasonAndRefusesTheWaitingPrompt()
    {
        // Arrange: a resumed Pi whose conversation is empty - get_state says messageCount 0, as a Pi that found no
        // conversation under the id does (event model, section 3.7).
        var pi = new ScriptedPi("resume") { AutoRespond = false, EditStdout = line => line.Replace("\"messageCount\":5", "\"messageCount\":0") };
        await using var rig = StartHost(pi, resuming: true);
        await pi.WaitForWrittenAsync("get_state");
        var sending = rig.Host.SendPromptAsync("Reply with exactly the word PONG", CancellationToken.None);
        await rig.Sink.WaitForAsync<PromptQueued>();

        // Act
        pi.RespondToNext();
        var ended = await rig.Host.Ended;
        var receipt = await sending;

        // Assert: the resume fails with the stated reason, no prompt was written into the empty conversation, and the
        // waiting prompt is refused with that reason.
        Assert.Equal("Resume failed: Pi's conversation '311ffc3b-7a39-45cf-bcb5-596c494cdbbf' has no messages (messageCount 0)", ended.Reason);
        Assert.Empty(rig.Sink.Events.OfType<ConnectionReady>());
        Assert.Equal(["get_state"], pi.WrittenTypes);
        Assert.False(receipt.Delivered);
        Assert.Equal(PromptRefusedBy.Director, receipt.RefusedBy);
        Assert.Equal($"{ApiModeSessionHost.ExitedBeforeWritingReason}: {ended.Reason}", receipt.RefusalReason);
    }

    [Fact]
    public async Task Start_ResumeThatFoundTheConversation_AnswersFromIt()
    {
        // Arrange: the recording's Pi resumed the plain conversation and answered from it.
        await using var rig = StartHost(new ScriptedPi("resume"), resuming: true);
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act
        var receipt = await rig.Host.SendPromptAsync("What did you reply last time?", CancellationToken.None);
        var turnEnded = await rig.Sink.WaitForAsync<TurnEnded>();

        // Assert
        Assert.True(receipt.Delivered);
        Assert.Equal(TurnOutcome.Done, turnEnded.Outcome);
        Assert.Equal("PONG", rig.Sink.Events.OfType<MessageText>().Last(m => m.Kind == MessageTextKind.Final).Text);
    }

    [Fact]
    public void Start_MissingExecutable_FailsWithAReasonNamingIt()
    {
        // Arrange: the real process, given an executable path that cannot exist - it fails before anything is spawned.
        var missing = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"), "pi");
        var stream = new SessionEventStream(SessionId, new RecordingSink(), new FakeTimeProvider(Start));
        var host = new ApiModeSessionHost(SessionId, "agent-session", resuming: false, () => new SystemApiModeProcess(), stream, new FakeTimeProvider(Start));

        // Act
        var error = Assert.Throws<ApiModeStartException>(() => host.Start(PiStart with { Executable = missing, WorkingDirectory = Path.GetTempPath() }));

        // Assert
        Assert.Equal(missing, error.Executable);
        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public async Task SessionEventStream_TwoPiProcessesOfOneSession_KeepOneGenerationAndKeepCounting()
    {
        // Arrange: one session, its stream shared by a first Pi process and then a second one on the same conversation
        // (what take the wheel and hand back do inside one Director process).
        var sink = new RecordingSink();
        var stream = new SessionEventStream(SessionId, sink, new FakeTimeProvider(Start));
        await using (var first = StartHost(new ScriptedPi("plain"), stream: stream, sink: sink))
        {
            await sink.WaitForAsync<ConnectionReady>();
            await first.Host.SendPromptAsync("Reply with exactly the word PONG", CancellationToken.None);
            await sink.WaitForAsync<TurnEnded>();
            await first.ShutdownAsync();
        }
        var countAfterFirst = sink.Events.Count;

        // Act
        await using (var second = StartHost(new ScriptedPi("resume"), resuming: true, stream: stream, sink: sink))
        {
            await sink.WaitForAsync<ConnectionReady>(c => c.Sequence > countAfterFirst);
            await second.Host.SendPromptAsync("What did you reply last time?", CancellationToken.None);
            await sink.WaitForAsync<TurnEnded>(t => t.TurnId == "turn-2");
            await second.ShutdownAsync();
        }

        // Assert: one generation, a sequence rising by one across both processes, and turn and prompt ids not reused.
        var generation = Assert.Single(sink.Batches.Select(b => b.Generation).Distinct());
        Assert.True(Guid.TryParse(generation, out _));
        Assert.Equal(Start.UtcDateTime, Assert.Single(sink.Batches.Select(b => b.GenerationStartedUtc).Distinct()));
        Assert.Equal(Enumerable.Range(1, sink.Events.Count).Select(i => (long)i), sink.Events.Select(e => e.Sequence));
        Assert.Equal(["prompt-1", "prompt-2"], sink.Events.OfType<PromptDelivered>().Select(d => d.PromptId));
        Assert.Equal(["turn-1", "turn-2"], sink.Events.OfType<TurnStarted>().Select(t => t.TurnId));
    }

    [Fact]
    public void SessionEventStream_ADirectorRestart_StartsANewGeneration()
    {
        // Arrange: two streams of one session, as two Director processes make them.
        var clock = new FakeTimeProvider(Start);
        var first = new SessionEventStream(SessionId, new RecordingSink(), clock);
        clock.Advance(TimeSpan.FromMinutes(3));

        // Act
        var second = new SessionEventStream(SessionId, new RecordingSink(), clock);

        // Assert: a new identity, ordered by its later start, and the numbering starts again.
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.True(second.GenerationStartedUtc > first.GenerationStartedUtc);
        Assert.Equal(1, second.Numbering.NextSequence());
    }

    [Fact]
    public async Task TurnStartBound_PiAcknowledgesAndNeverStarts_TurnFailsAndPiRestartsOnTheConversationWithoutResending()
    {
        // Arrange: the first Pi acknowledges the prompt and never begins its turn; the restarted one behaves.
        var stuck = new ScriptedPi("plain") { AcknowledgeOnly = true };
        var restarted = new ScriptedPi("plain");
        await using var rig = StartHost(stuck, restarts: restarted);
        await rig.Sink.WaitForAsync<ConnectionReady>();
        Assert.True((await rig.Host.SendPromptAsync("Reply with exactly the word PONG", CancellationToken.None)).Delivered);
        // A second prompt waits in the Director's queue behind the first; seeing it queued also means the event loop
        // has finished with the acknowledgement, so the bound is armed.
        var second = rig.Host.SendPromptAsync("Second", CancellationToken.None);
        await rig.Sink.WaitForAsync<PromptQueued>();

        // Act: just short of the bound nothing happens; at the bound the turn is given up and Pi restarted.
        rig.Clock.Advance(ApiModeSessionDefaults.TurnStartBound - TimeSpan.FromMilliseconds(1));
        Assert.Empty(rig.Sink.Events.OfType<TurnStarted>());
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var failed = await rig.Sink.WaitForAsync<TurnEnded>();
        await rig.Sink.WaitForAsync<ConnectionReady>(c => c.Sequence > failed.Sequence);
        var secondReceipt = await second;

        // Assert: the acknowledged prompt's turn ended failed with the restart stated (the ruling's third row)...
        var turn = Assert.Single(rig.Sink.Events.OfType<TurnStarted>(), t => t.TurnId == failed.TurnId);
        Assert.Equal(["prompt-1"], turn.PromptIds);
        Assert.Equal(TurnOutcome.Failed, failed.Outcome);
        Assert.StartsWith(PiDialect.AcknowledgedButNeverStartedReason, failed.Reason);
        Assert.Contains(PiDialect.RestartingPiReason, failed.Reason);
        Assert.Equal(failed.Reason, rig.Sink.Events.OfType<ConnectionEnded>().Single().Reason);
        // ...the stuck Pi was asked to exit, a new one started with the same arguments on the same conversation...
        Assert.True(stuck.Process.InputClosed);
        Assert.Same(stuck.Process.StartedWith, restarted.Process.StartedWith);
        Assert.Equal(1, rig.Host.Restarts);
        Assert.False(rig.Host.Ended.IsCompleted);
        // ...and the overdue prompt was NEVER sent again: the new Pi got only the queued second prompt.
        Assert.Equal(["get_state", "prompt"], stuck.WrittenTypes);
        Assert.Equal(["get_state", "prompt"], restarted.WrittenTypes);
        Assert.Equal("Second", MessageOf(restarted.Written[1]));
        Assert.True(secondReceipt.Delivered);
        Assert.Equal(Enumerable.Range(1, rig.Sink.Events.Count).Select(i => (long)i), rig.Sink.Events.Select(e => e.Sequence));
        Assert.Empty(rig.Host.Defects);
    }

    [Fact]
    public async Task TurnStartBound_TurnBeginsInTime_NeverRestarts()
    {
        // Arrange
        await using var rig = StartHost(new ScriptedPi("plain"));
        await rig.Sink.WaitForAsync<ConnectionReady>();
        await rig.Host.SendPromptAsync("Reply with exactly the word PONG", CancellationToken.None);
        await rig.Sink.WaitForAsync<TurnEnded>();

        // Act
        rig.Clock.Advance(ApiModeSessionDefaults.TurnStartBound * 2);
        var ended = await rig.ShutdownAsync();

        // Assert
        Assert.Equal(0, rig.Host.Restarts);
        Assert.Equal(TurnOutcome.Done, Assert.Single(rig.Sink.Events.OfType<TurnEnded>()).Outcome);
        Assert.Equal("Pi exited (exit code 0)", ended.Reason);
    }

    public static TheoryData<string> Scenarios => ["plain", "tool", "abort", "resume", "exit", "exit-before-ack"];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task SendPromptAsync_EveryRecording_KeepsTheEventModelsRules(string scenario)
    {
        // Arrange
        await using var rig = StartHost(new ScriptedPi(scenario), resuming: scenario == "resume");
        await rig.Sink.WaitForAsync<ConnectionReady>();

        // Act: one prompt; Stop where the recording stopped; then the end.
        var receipt = await rig.Host.SendPromptAsync("A prompt", CancellationToken.None);
        if (scenario == "abort")
            await rig.Host.StopAsync(CancellationToken.None);
        else if (receipt.Delivered && scenario != "exit")
            await rig.Sink.WaitForAsync<TurnEnded>();
        await rig.ShutdownAsync();

        // Assert: one receipt per prompt, one TurnEnded per TurnStarted, one finish per tool start, the sequence rising
        // by one, ConnectionEnded last and once.
        var events = rig.Sink.Events;
        Assert.Equal(1, events.OfType<PromptDelivered>().Count() + events.OfType<PromptRefused>().Count());
        Assert.Equal(events.OfType<TurnStarted>().Select(t => t.TurnId), events.OfType<TurnEnded>().Select(t => t.TurnId));
        Assert.Equal(events.OfType<ToolCallStarted>().Select(t => t.ToolCallId).Order(), events.OfType<ToolCallFinished>().Select(t => t.ToolCallId).Order());
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.IsType<ConnectionEnded>(events[^1]);
        Assert.Single(events.OfType<ConnectionEnded>());
        Assert.All(events, e => Assert.Equal(SessionId, e.SessionId));
        Assert.Empty(rig.Host.Defects);
        Assert.Empty(rig.Pi.Unexpected);
    }
}
