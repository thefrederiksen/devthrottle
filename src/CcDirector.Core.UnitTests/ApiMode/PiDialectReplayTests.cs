using CcDirector.Gateway.Contracts.ApiMode;
using Xunit;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>
/// Pi's dialect against every real wire log in TestData/pi-rpc (Pi 1.2.0, recorded 16 October 2026). Each
/// test replays one recording through the translator (<see cref="PiWireLogReplay"/>) and asserts the exact
/// session events it must produce, per docs/missions/api-mode-pi-2026-10-16/event-model.md. No Pi, no
/// process, no real clock.
/// </summary>
public sealed class PiDialectReplayTests
{
    public static TheoryData<string> AllScenarios => ["plain", "tool", "abort", "resume", "refusal", "exit", "exit-before-ack"];

    [Fact]
    public void Replay_Plain_DeliversStreamsTheReplyAndEndsDone()
    {
        // Act
        var replay = PiWireLogReplay.Run("plain");

        // Assert
        Assert.Equal(
        [
            "PromptDelivered prompt-1 Started",
            "TurnStarted turn-1 [prompt-1]",
            "MessageText turn-1-message-1 Reply Delta \"P\"",
            "MessageText turn-1-message-1 Reply Delta \"ONG\"",
            "MessageText turn-1-message-1 Reply Final \"PONG\"",
            "TurnEnded turn-1 Done reason= stop=stop",
            "ConnectionReady 311ffc3b-7a39-45cf-bcb5-596c494cdbbf gpt-5.6-sol",
            "ConnectionEnded 0: Pi exited (exit code 0) []",
        ], replay.Lines());
        Assert.Empty(replay.Defects);
        Assert.Empty(replay.StopReasons);
        Assert.Equal(["get_last_assistant_text", "get_state", "get_commands", "get_session_stats"], replay.Results.Select(r => r.Command));
        Assert.Equal("""{"text":"PONG"}""", replay.Results[0].DataJson);
        var ended = replay.Events.OfType<TurnEnded>().Single();
        Assert.Equal(new TurnUsage { Input = 3, Output = 6, CacheRead = 0, CacheWrite = 3743 }, ended.Usage);
    }

    [Fact]
    public void Replay_Plain_AcknowledgementIsPisResponseLineVerbatim()
    {
        // Act
        var replay = PiWireLogReplay.Run("plain");

        // Assert: rule 4, the receipt can be checked against the wire log byte for byte.
        var delivered = replay.Events.OfType<PromptDelivered>().Single();
        Assert.Equal("""{"id":"req-dc0e2601","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""", delivered.Acknowledgement);
    }

    [Fact]
    public void Replay_Plain_AtIsTheInjectedClocksTime()
    {
        // Act
        var replay = PiWireLogReplay.Run("plain");

        // Assert: Pi's acknowledgement was recorded at 632 milliseconds, its settle at 1822.
        Assert.Equal(PiWireLogReplay.RecordingStart.AddMilliseconds(632), replay.Events.OfType<PromptDelivered>().Single().At);
        Assert.Equal(PiWireLogReplay.RecordingStart.AddMilliseconds(1822), replay.Events.OfType<TurnEnded>().Single().At);
    }

    [Fact]
    public void Replay_Tool_ToolCardRunsAndFinishesInsideOneTurn()
    {
        // Act
        var replay = PiWireLogReplay.Run("tool");

        // Assert: two Pi turns (one model call each) are one turn of ours; the tool-call message has no text.
        Assert.Equal(
        [
            "PromptDelivered prompt-1 Started",
            "TurnStarted turn-1 [prompt-1]",
            "ToolCallStarted turn-1 bash Execute {\"command\":\"echo fixture-ok\",\"timeout\":10}",
            "ToolCallFinished turn-1 Succeeded \"fixture-ok\\n\" 16",
            "MessageText turn-1-message-2 Reply Delta \"fixture\"",
            "MessageText turn-1-message-2 Reply Delta \"-ok\"",
            "MessageText turn-1-message-2 Reply Final \"fixture-ok\"",
            "TurnEnded turn-1 Done reason= stop=stop",
            "ConnectionEnded 0: Pi exited (exit code 0) []",
        ], replay.Lines());
        Assert.Empty(replay.Defects);
        var started = replay.Events.OfType<ToolCallStarted>().Single();
        var finished = replay.Events.OfType<ToolCallFinished>().Single();
        Assert.Equal(started.ToolCallId, finished.ToolCallId);
        Assert.Equal(new TurnUsage { Input = 6, Output = 30, CacheRead = 3754, CacheWrite = 3792 }, replay.Events.OfType<TurnEnded>().Single().Usage);
    }

    [Fact]
    public void Replay_Abort_EndsCancelledAndTheAbortsOwnResponseComesLast()
    {
        // Act
        var replay = PiWireLogReplay.Run("abort");

        // Assert: the thinking never got a thinking_end, so message_end's content finalises it.
        Assert.Equal(
        [
            "PromptDelivered prompt-1 Started",
            "TurnStarted turn-1 [prompt-1]",
            "MessageText turn-1-message-1 Thought Delta \"**Planning an essay structure**\\n\\nI\"",
            "MessageText turn-1-message-1 Thought Final \"**Planning an essay structure**\\n\\nI\"",
            "TurnEnded turn-1 Cancelled reason=stopped by the user stop=aborted",
            "ConnectionEnded 0: Pi exited (exit code 0) []",
        ], replay.Lines());
        Assert.Empty(replay.Defects);
        Assert.Equal(["abort", "get_last_assistant_text", "get_session_stats"], replay.Results.Select(r => r.Command));
        Assert.True(replay.Results[0].Success);
        Assert.Equal("{}", replay.Results[1].DataJson);
    }

    [Fact]
    public void Replay_Resume_AnswersFromTheEarlierConversationWithNoStop()
    {
        // Act
        var replay = PiWireLogReplay.Run("resume", resuming: true);

        // Assert
        Assert.Equal(
        [
            "PromptDelivered prompt-1 Started",
            "TurnStarted turn-1 [prompt-1]",
            "MessageText turn-1-message-1 Reply Delta \"P\"",
            "MessageText turn-1-message-1 Reply Delta \"ONG\"",
            "MessageText turn-1-message-1 Reply Final \"PONG\"",
            "TurnEnded turn-1 Done reason= stop=stop",
            "ConnectionReady 311ffc3b-7a39-45cf-bcb5-596c494cdbbf gpt-5.6-sol",
            "ConnectionEnded 0: Pi exited (exit code 0) []",
        ], replay.Lines());
        Assert.Empty(replay.StopReasons);
        Assert.Empty(replay.Defects);
    }

    [Fact]
    public void Replay_NewSessionTreatedAsResume_StopsOnPisNoProjectSessionWarning()
    {
        // Arrange: the plain recording is a NEW session, so Pi wrote its "No project session found" warning.
        // Resuming it must fail with that reason, never quietly carry on in a new, empty conversation.

        // Act
        var replay = PiWireLogReplay.Run("plain", resuming: true);

        // Assert
        var stop = Assert.Single(replay.StopReasons);
        Assert.StartsWith("Resume failed: Pi found no conversation with id '311ffc3b-7a39-45cf-bcb5-596c494cdbbf' in this folder (Warning: No project session found", stop);
        Assert.DoesNotContain(replay.Events, e => e is ConnectionReady);
        Assert.Equal(stop, replay.Events.OfType<ConnectionEnded>().Single().Reason);
        Assert.False(replay.Dialect.CanWritePrompt);
    }

    [Fact]
    public void Replay_ResumeWithNoMessages_StopsWithThatReason()
    {
        // Arrange: the resume recording with get_state's messageCount changed from 5 to 0 - what Pi answers
        // when it started an empty conversation under the id.

        // Act
        var replay = PiWireLogReplay.Run("resume", resuming: true, editStdout: line => line.Replace("\"messageCount\":5", "\"messageCount\":0"));

        // Assert
        Assert.Equal(["Resume failed: Pi's conversation '311ffc3b-7a39-45cf-bcb5-596c494cdbbf' has no messages (messageCount 0)"], replay.StopReasons);
        Assert.DoesNotContain(replay.Events, e => e is ConnectionReady);
    }

    [Fact]
    public void Replay_Refusal_RefusesWithPisReasonVerbatimAndReportsEveryDefect()
    {
        // Act
        var replay = PiWireLogReplay.Run("refusal");

        // Assert: prompt-2 refused in Pi's words; prompt-3 (sent with streamingBehavior by the recorder) queued
        // by Pi and run in the same agent run, so one turn of ours.
        var lines = replay.Lines();
        Assert.Equal(
        [
            "PromptDelivered prompt-1 Started",
            "TurnStarted turn-1 [prompt-1]",
            "PromptRefused prompt-2 Agent: Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.",
            "PromptDelivered prompt-3 QueuedByAgent",
        ], lines.Take(4));
        Assert.Equal(
        [
            "MessageText turn-1-message-1 Reply Final \"1\\n2\\n3\\n4\\n5\\n6\\n7\\n8\\n9\\n10\\n11\\n12\\n13\\n14\\n15\\n16\\n17\\n18\\n19\\n20\\n21\\n22\\n23\\n24\\n25\\n26\\n27\\n28\\n29\\n30\"",
            "MessageText turn-1-message-2 Reply Delta \"DONE\"",
            "MessageText turn-1-message-2 Reply Final \"DONE\"",
            "TurnEnded turn-1 Done reason= stop=stop",
            "ConnectionReady ebec3577-8928-4307-9a9a-79841e5150a7 gpt-5.6-sol",
            "ConnectionEnded 0: Pi exited (exit code 0) []",
        ], lines.TakeLast(6));
        var deltas = replay.Events.OfType<MessageText>().Where(m => m.MessageId == "turn-1-message-1" && m.Kind == MessageTextKind.Delta);
        Assert.Equal("1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n13\n14\n15\n16\n17\n18\n19\n20\n21\n22\n23\n24\n25\n26\n27\n28\n29\n30", string.Concat(deltas.Select(d => d.Text)));
        Assert.Equal(lines.Count, 4 + deltas.Count() + 6);

        Assert.Equal(
        [
            "prompt prompt-2 was written while prompt prompt-1 is in flight; Pi refuses a prompt while it is busy",
            "Pi refused command 'no_such_command' (req-403d7cdb): Unknown command: no_such_command",
            "Pi answered 'parse' with no id: Failed to parse command: Expected property name or '}' in JSON at position 1 (line 1 column 2)",
            "prompt prompt-3 was written while prompt prompt-1 is in flight; Pi refuses a prompt while it is busy",
        ], replay.Defects);
        Assert.Contains(replay.Results, r => r.Command == "abort" && r.Success);
    }

    [Fact]
    public void Replay_Exit_TurnFailsWithTheExitCodeAndThePromptStaysDelivered()
    {
        // Act
        var replay = PiWireLogReplay.Run("exit");

        // Assert: the start-up warning came before the turn started, so it is not blamed (event model, section 1).
        Assert.Equal(
        [
            "PromptDelivered prompt-1 Started",
            "TurnStarted turn-1 [prompt-1]",
            "MessageText turn-1-message-1 Thought Delta \"**Planning a 2000-word essay**\\n\\nI\"",
            "TurnEnded turn-1 Failed reason=Pi exited during the turn (exit code 137) stop=",
            "ConnectionEnded 137: Pi exited during the turn (exit code 137) []",
        ], replay.Lines());
        Assert.DoesNotContain(replay.Events, e => e is PromptRefused);
        Assert.False(replay.Dialect.CanWritePrompt);
    }

    [Fact]
    public void Replay_ExitBeforeAcknowledgement_RefusesThePromptAsTheDirector()
    {
        // Act
        var replay = PiWireLogReplay.Run("exit-before-ack");

        // Assert
        Assert.Equal(
        [
            "PromptRefused prompt-1 Director: Pi exited before acknowledging",
            "ConnectionEnded 137: Pi exited before acknowledging []",
        ], replay.Lines());
    }

    [Theory]
    [MemberData(nameof(AllScenarios))]
    public void Replay_EveryScenario_KeepsTheEventModelsRules(string scenario)
    {
        // Act
        var replay = PiWireLogReplay.Run(scenario, resuming: scenario == "resume");
        var events = replay.Events;

        // Assert rule 1: one PromptDelivered or one PromptRefused per prompt written, never both, never neither.
        var written = PiWireLogReplay.WrittenPromptCount(scenario);
        var receipts = events.Where(e => e is PromptDelivered or PromptRefused)
            .Select(e => e is PromptDelivered d ? d.PromptId : ((PromptRefused)e).PromptId).ToList();
        Assert.Equal(written, receipts.Count);
        Assert.Equal(receipts.Count, receipts.Distinct().Count());

        // Rule 2: every TurnStarted gets exactly one TurnEnded.
        Assert.Equal(events.OfType<TurnStarted>().Select(t => t.TurnId), events.OfType<TurnEnded>().Select(t => t.TurnId));

        // Rule 3: every ToolCallStarted gets exactly one ToolCallFinished.
        Assert.Equal(events.OfType<ToolCallStarted>().Select(t => t.ToolCallId).Order(), events.OfType<ToolCallFinished>().Select(t => t.ToolCallId).Order());

        // Sequence rises by one from 1, time never goes back, and every event names the session.
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.True(events.Zip(events.Skip(1)).All(p => p.First.At <= p.Second.At));
        Assert.All(events, e => Assert.Equal("session-under-test", e.SessionId));

        // The process always ends with exactly one ConnectionEnded, last.
        Assert.IsType<ConnectionEnded>(events[^1]);
        Assert.Single(events.OfType<ConnectionEnded>());

        // A TurnEnded carries a reason unless it is done.
        Assert.All(events.OfType<TurnEnded>().Where(t => t.Outcome != TurnOutcome.Done), t => Assert.False(string.IsNullOrWhiteSpace(t.Reason)));
    }
}
