using System.Text.Json;
using CcDirector.Core.ApiMode;
using CcDirector.Core.ApiMode.Pi;
using CcDirector.Gateway.Contracts.ApiMode;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>
/// Pi's dialect on the cases no recording provoked: hand-built records in Pi's documented shapes
/// (event-model.md marks each <b>documented only</b>), the in-flight rule step by step, the closing of a tool
/// left running, the standard error rule for a failure reason, extension dialogs, and the commands it builds.
/// The real recordings are replayed in <see cref="PiDialectReplayTests"/>.
/// </summary>
public sealed class PiDialectTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionEventNumbering _numbering = new();
    private readonly List<SessionEvent> _events = [];
    private readonly List<string> _defects = [];
    private readonly List<PiCommand> _toWrite = [];
    private readonly PiDialect _dialect;

    public PiDialectTests() => _dialect = new PiDialect("s1", "agent-1", resuming: false, _clock, _numbering);

    private void Take(PiDialectStep step)
    {
        _events.AddRange(step.Events);
        _defects.AddRange(step.Defects);
        _toWrite.AddRange(step.CommandsToWrite);
    }

    private void Pi(string json) => Take(_dialect.OnStdoutRecord(JsonDocument.Parse(json).RootElement));

    private static string StateResponse(string commandId, int messageCount) =>
        $$$"""{"id":"{{{commandId}}}","type":"response","command":"get_state","success":true,"data":{"sessionId":"agent-1","model":{"id":"model-1"},"messageCount":{{{messageCount}}}}}""";

    /// <summary>Pi has answered get_state, so the conversation is named; the ConnectionReady is set aside.</summary>
    private void Ready(PiDialect? dialect = null, int messageCount = 3)
    {
        dialect ??= _dialect;
        var getState = dialect.BuildGetState();
        Take(dialect.OnCommandWritten(getState));
        Take(dialect.OnStdoutRecord(JsonDocument.Parse(StateResponse(getState.Id, messageCount)).RootElement));
        _events.Clear();
    }

    private PiCommand WritePrompt(string promptId)
    {
        var command = _dialect.BuildPrompt(promptId, "hello");
        Take(_dialect.OnCommandWritten(command));
        return command;
    }

    /// <summary>A prompt written, acknowledged as started, and its turn begun.</summary>
    private void StartTurn(string promptId = "p1")
    {
        var command = WritePrompt(promptId);
        Pi($$$"""{"id":"{{{command.Id}}}","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""");
        Pi("""{"type":"agent_start"}""");
    }

    private static string AssistantEnd(string stopReason, string content = "[]", string extra = "") =>
        $$$"""{"type":"message_end","message":{"role":"assistant","content":{{{content}}},"usage":{"input":1,"output":2,"cacheRead":3,"cacheWrite":4},"stopReason":"{{{stopReason}}}"{{{extra}}}}}""";

    private const string ToolStart = """{"type":"tool_execution_start","toolCallId":"c1","toolName":"read","args":{"path":"a.txt"}}""";

    [Fact]
    public void CanWritePrompt_FromTheWriteUntilTheTurnSettles_IsFalse()
    {
        // Arrange + Act + Assert, step by step: the in-flight window starts at the write, not at agent_start.
        Ready();
        Assert.True(_dialect.CanWritePrompt);
        var command = WritePrompt("p1");
        Assert.False(_dialect.CanWritePrompt);
        Pi($$$"""{"id":"{{{command.Id}}}","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""");
        Assert.False(_dialect.CanWritePrompt);
        Pi("""{"type":"agent_start"}""");
        Pi("""{"type":"message_start","message":{"role":"assistant","content":[]}}""");
        Pi(AssistantEnd("stop"));
        Pi("""{"type":"agent_end","messages":[],"willRetry":false}""");
        Assert.False(_dialect.CanWritePrompt);
        Pi("""{"type":"agent_settled","aborted":false}""");
        Assert.True(_dialect.CanWritePrompt);
        Assert.Empty(_defects);
    }

    [Fact]
    public void CanWritePrompt_AfterPiRefusedThePrompt_IsTrue()
    {
        // Arrange
        Ready();
        var command = WritePrompt("p1");

        // Act
        Pi($$$"""{"id":"{{{command.Id}}}","type":"response","command":"prompt","success":false,"error":"Agent is already processing."}""");

        // Assert
        Assert.True(_dialect.CanWritePrompt);
        var refused = Assert.IsType<PromptRefused>(Assert.Single(_events));
        Assert.Equal(PromptRefusedBy.Agent, refused.RefusedBy);
        Assert.Equal("Agent is already processing.", refused.Reason);
    }

    [Fact]
    public void OnStdoutRecord_HandledDisposition_DeliveredWithNoTurnAndNoLongerInFlight()
    {
        // Arrange
        Ready();
        var command = WritePrompt("p1");

        // Act: documented only - an extension command consumed the prompt.
        Pi($$$"""{"id":"{{{command.Id}}}","type":"response","command":"prompt","success":true,"data":{"disposition":"handled"}}""");

        // Assert
        var delivered = Assert.IsType<PromptDelivered>(Assert.Single(_events));
        Assert.Equal(PromptDisposition.Handled, delivered.Disposition);
        Assert.True(_dialect.CanWritePrompt);
        Assert.False(_dialect.TurnRunning);
    }

    [Fact]
    public void OnStdoutRecord_UnknownDisposition_RefusedByTheDirectorAndReportedAsADefect()
    {
        // Arrange
        var command = WritePrompt("p1");

        // Act
        Pi($$$"""{"id":"{{{command.Id}}}","type":"response","command":"prompt","success":true,"data":{"disposition":"deferred"}}""");

        // Assert: a receipt never claims a delivery the dialect cannot name.
        var refused = Assert.IsType<PromptRefused>(Assert.Single(_events));
        Assert.Equal(PromptRefusedBy.Director, refused.RefusedBy);
        Assert.Contains("deferred", refused.Reason);
        Assert.Single(_defects);
    }

    [Fact]
    public void OnStdoutRecord_ParseErrorWhileAPromptAwaitsItsAcknowledgement_RefusesItAsTheDirector()
    {
        // Arrange
        Ready();
        WritePrompt("p1");

        // Act
        Pi("""{"type":"response","command":"parse","success":false,"error":"Failed to parse command: bad"}""");

        // Assert
        var refused = Assert.IsType<PromptRefused>(Assert.Single(_events));
        Assert.Equal(PromptRefusedBy.Director, refused.RefusedBy);
        Assert.Equal("Pi could not read the prompt: Failed to parse command: bad", refused.Reason);
        Assert.Single(_defects);
        Assert.True(_dialect.CanWritePrompt);
    }

    [Fact]
    public void OnStdoutRecord_LengthStopReason_EndsDoneWithTheLengthReason()
    {
        // Arrange
        StartTurn();
        Pi("""{"type":"message_start","message":{"role":"assistant","content":[]}}""");

        // Act
        Pi(AssistantEnd("length", """[{"type":"text","text":"cut"}]"""));
        Pi("""{"type":"agent_settled","aborted":false}""");

        // Assert
        var ended = _events.OfType<TurnEnded>().Single();
        Assert.Equal(TurnOutcome.Done, ended.Outcome);
        Assert.Equal(PiDialect.LengthLimitReason, ended.Reason);
        Assert.Equal("length", ended.AgentStopReason);
        Assert.Equal("cut", _events.OfType<MessageText>().Single(m => m.Kind == MessageTextKind.Final).Text);
    }

    [Fact]
    public void OnStdoutRecord_ErrorStopReason_EndsFailedWithPisErrorMessage()
    {
        // Arrange
        StartTurn();
        Pi("""{"type":"message_start","message":{"role":"assistant","content":[]}}""");

        // Act: documented only.
        Pi(AssistantEnd("error", extra: ""","errorMessage":"rate limited" """));
        Pi("""{"type":"agent_settled","aborted":false}""");

        // Assert
        var ended = _events.OfType<TurnEnded>().Single();
        Assert.Equal(TurnOutcome.Failed, ended.Outcome);
        Assert.Equal("rate limited", ended.Reason);
        Assert.Equal(new TurnUsage { Input = 1, Output = 2, CacheRead = 3, CacheWrite = 4 }, ended.Usage);
    }

    [Fact]
    public void OnStdoutRecord_RetriesExhaustedAndNoReply_EndsFailedWithTheFinalError()
    {
        // Arrange
        StartTurn();

        // Act: documented only.
        Pi("""{"type":"auto_retry_end","success":false,"attempt":3,"finalError":"overloaded"}""");
        Pi("""{"type":"agent_settled","aborted":false}""");

        // Assert
        var ended = _events.OfType<TurnEnded>().Single();
        Assert.Equal(TurnOutcome.Failed, ended.Outcome);
        Assert.Equal("overloaded", ended.Reason);
    }

    [Fact]
    public void OnStdoutRecord_AbortedWhileAToolRuns_ClosesTheToolCancelledBeforeTheTurnEnds()
    {
        // Arrange
        StartTurn();
        Pi(ToolStart);

        // Act
        Pi("""{"type":"agent_settled","aborted":true}""");

        // Assert: rule 3, no card left spinning.
        Assert.Equal(
        [
            "ToolCallStarted turn-1 read Read {\"path\":\"a.txt\"}",
            "ToolCallFinished turn-1 Cancelled \"\" ",
            "TurnEnded turn-1 Cancelled reason=stopped by the user stop=",
        ], _events.Skip(2).Select(PiWireLogReplay.Describe));
    }

    [Fact]
    public void OnProcessExited_WhileAToolRuns_ClosesTheToolFailed()
    {
        // Arrange
        StartTurn();
        Pi(ToolStart);

        // Act
        Take(_dialect.OnProcessExited(137));

        // Assert
        Assert.Equal(ToolCallOutcome.Failed, _events.OfType<ToolCallFinished>().Single().Outcome);
        Assert.Equal(TurnOutcome.Failed, _events.OfType<TurnEnded>().Single().Outcome);
    }

    [Fact]
    public void OnProcessExited_MidTurn_ReasonCarriesOnlyTheStandardErrorWrittenSinceTheTurnStarted()
    {
        // Arrange
        var command = WritePrompt("p1");
        Take(_dialect.OnStderrLine("Warning: No project session found with id 'agent-1'; creating a new session with that id."));
        Pi($$$"""{"id":"{{{command.Id}}}","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""");
        Pi("""{"type":"agent_start"}""");
        Take(_dialect.OnStderrLine("Error: out of memory"));

        // Act
        Take(_dialect.OnProcessExited(134));

        // Assert
        Assert.Equal("Pi exited during the turn (exit code 134). Standard error: Error: out of memory", _events.OfType<TurnEnded>().Single().Reason);
        var ended = _events.OfType<ConnectionEnded>().Single();
        Assert.Equal(["Error: out of memory"], ended.StderrTail);
        Assert.Equal(134, ended.ExitCode);
    }

    [Fact]
    public void OnProcessExited_BeforeAcknowledgement_StandardErrorSinceThePromptWasWrittenIsKept()
    {
        // Arrange
        Take(_dialect.OnStderrLine("an earlier line"));
        WritePrompt("p1");
        Take(_dialect.OnStderrLine("Error: cannot reach the model"));

        // Act
        Take(_dialect.OnProcessExited(1));

        // Assert
        Assert.Equal("PromptRefused p1 Director: Pi exited before acknowledging", PiWireLogReplay.Describe(_events[0]));
        Assert.Equal(["Error: cannot reach the model"], _events.OfType<ConnectionEnded>().Single().StderrTail);
    }

    [Fact]
    public void OnProcessExited_CalledTwice_Throws()
    {
        // Arrange
        Take(_dialect.OnProcessExited(0));

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => _dialect.OnProcessExited(0));
    }

    [Theory]
    [InlineData("select")]
    [InlineData("confirm")]
    [InlineData("input")]
    [InlineData("editor")]
    public void OnStdoutRecord_ExtensionDialog_AnswersAtOnceWithTheCancellation(string method)
    {
        // Act: documented only.
        Pi($$$"""{"type":"extension_ui_request","id":"ui-7","method":"{{{method}}}","title":"Pick one"}""");

        // Assert
        var answer = Assert.Single(_toWrite);
        Assert.Equal("""{"type":"extension_ui_response","id":"ui-7","cancelled":true}""", answer.Line);
        Assert.Empty(_events);
        Assert.Empty(_defects);
    }

    [Fact]
    public void OnStdoutRecord_ExtensionNotification_IsIgnored()
    {
        // Act
        Pi("""{"type":"extension_ui_request","id":"ui-8","method":"notify","message":"hi"}""");

        // Assert
        Assert.Empty(_toWrite);
        Assert.Empty(_events);
        Assert.Empty(_defects);
    }

    [Fact]
    public void OnStdoutRecord_UnknownRecordType_ReportsADefect()
    {
        // Act
        Pi("""{"type":"brand_new_event"}""");

        // Assert
        Assert.Equal(["Pi wrote a record of a type the dialect does not know: brand_new_event"], _defects);
        Assert.Empty(_events);
    }

    [Fact]
    public void OnStdoutRecord_ResponseToACommandNeverWritten_ReportsADefect()
    {
        // Act
        Pi("""{"id":"dt-99","type":"response","command":"get_state","success":true,"data":{}}""");

        // Assert
        Assert.Single(_defects);
        Assert.Empty(_events);
    }

    [Fact]
    public void Build_EveryCommand_HasAUniqueIdAndPisShape()
    {
        // Act
        var commands = new[]
        {
            _dialect.BuildPrompt("p1", "Say \"hi\"\nplease"),
            _dialect.BuildAbort(),
            _dialect.BuildGetState(),
            _dialect.BuildGetSessionStats(),
            _dialect.BuildGetLastAssistantText(),
        };

        // Assert
        Assert.Equal(["prompt", "abort", "get_state", "get_session_stats", "get_last_assistant_text"], commands.Select(c => c.Type));
        Assert.Equal(commands.Length, commands.Select(c => c.Id).Distinct().Count());
        foreach (var c in commands)
        {
            var json = JsonDocument.Parse(c.Line).RootElement;
            Assert.Equal(c.Id, json.GetProperty("id").GetString());
            Assert.Equal(c.Type, json.GetProperty("type").GetString());
            Assert.DoesNotContain('\n', c.Line);
            Assert.False(json.TryGetProperty("streamingBehavior", out _));
        }
        Assert.Equal("Say \"hi\"\nplease", JsonDocument.Parse(commands[0].Line).RootElement.GetProperty("message").GetString());
        Assert.Equal("p1", commands[0].PromptId);
    }

    [Fact]
    public void Build_AcrossTwoPiProcessesOfOneSession_IdsAndSequenceKeepRising()
    {
        // Arrange: take the wheel and hand back start a second Pi process on the same numbering.
        var first = _dialect.BuildGetState();
        Take(_dialect.OnProcessExited(0));
        var second = new PiDialect("s1", "agent-1", resuming: true, _clock, _numbering);

        // Act
        var next = second.BuildGetState();
        var ended = second.OnProcessExited(0).Events.Single();

        // Assert
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(2, ended.Sequence);
    }

    [Theory]
    [InlineData("bash", ToolCallKind.Execute)]
    [InlineData("read", ToolCallKind.Read)]
    [InlineData("edit", ToolCallKind.Edit)]
    [InlineData("write", ToolCallKind.Edit)]
    [InlineData("grep", ToolCallKind.Other)]
    public void ToolKind_PiToolName_MapsToTheEventModelKind(string toolName, ToolCallKind expected)
    {
        // Act + Assert
        Assert.Equal(expected, PiDialect.ToolKind(toolName));
    }

    [Fact]
    public void CanWritePrompt_BeforePiHasNamedItsConversation_IsFalse()
    {
        // Act + Assert: get_state is written right after start, and no prompt goes before its answer.
        Assert.False(_dialect.CanWritePrompt);
        Ready();
        Assert.True(_dialect.CanWritePrompt);
    }

    [Fact]
    public void CanWritePrompt_ResumeThatFoundNoConversation_NeverOpens()
    {
        // Arrange: review finding 3 - a prompt held for hand back must not run in the empty conversation Pi made.
        var resuming = new PiDialect("s1", "agent-1", resuming: true, _clock, _numbering);
        Assert.False(resuming.CanWritePrompt);

        // Act
        Ready(resuming, messageCount: 0);

        // Assert
        Assert.False(resuming.CanWritePrompt);
        Assert.NotNull(resuming.StopReason);
    }

    [Fact]
    public void CanWritePrompt_ResumeThatFoundTheConversation_Opens()
    {
        // Arrange
        var resuming = new PiDialect("s1", "agent-1", resuming: true, _clock, _numbering);

        // Act
        Ready(resuming, messageCount: 5);

        // Assert
        Assert.True(resuming.CanWritePrompt);
    }

    /// <summary>A prompt written and acknowledged as started, with Pi's run not yet begun.</summary>
    private void AcknowledgeWithoutTurn(string promptId = "p1")
    {
        Ready();
        var command = WritePrompt(promptId);
        Pi($$$"""{"id":"{{{command.Id}}}","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""");
        Assert.Equal([promptId], _dialect.PromptsAwaitingTurn);
    }

    [Fact]
    public void OnProcessExited_AfterAcknowledgementBeforeTheTurnStarted_EndsTheirTurnFailed()
    {
        // Arrange: the ruling's third row (review finding 1 of #4076) - Pi promises no turn after 'started'.
        Take(_dialect.OnStderrLine("a line from before the prompt"));
        AcknowledgeWithoutTurn();
        Take(_dialect.OnStderrLine("Error: the run could not start"));

        // Act
        Take(_dialect.OnProcessExited(137));

        // Assert: rule 5 has a turn to read, failed, with the exit and only the standard error since the write.
        const string reason = "Pi acknowledged the prompt but never started it. Pi exited (exit code 137). Standard error: Error: the run could not start";
        Assert.Equal(
        [
            "PromptDelivered p1 Started",
            "TurnStarted turn-1 [p1]",
            $"TurnEnded turn-1 Failed reason={reason} stop=",
            $"ConnectionEnded 137: {reason} [Error: the run could not start]",
        ], _events.Select(PiWireLogReplay.Describe));
        Assert.Empty(_dialect.PromptsAwaitingTurn);
    }

    [Fact]
    public void OnTurnStartOverdue_PromptWaiting_EndsItsTurnFailedAndCondemnsTheProcess()
    {
        // Arrange
        AcknowledgeWithoutTurn();

        // Act: the Director's bound ran out.
        var step = _dialect.OnTurnStartOverdue();
        Take(step);

        // Assert: the restart is in the reason, the process must stop, and nothing reopens the gate.
        const string reason = "Pi acknowledged the prompt but never started it. The Director is restarting Pi; the prompt is not sent again";
        Assert.Equal(
        [
            "PromptDelivered p1 Started",
            "TurnStarted turn-1 [p1]",
            $"TurnEnded turn-1 Failed reason={reason} stop=",
        ], _events.Select(PiWireLogReplay.Describe));
        Assert.Equal(reason, step.StopReason);
        Assert.False(_dialect.CanWritePrompt);
        Assert.Empty(_dialect.PromptsAwaitingTurn);
        Assert.Empty(_defects);
    }

    [Fact]
    public void OnTurnStartOverdue_ThenPiStopped_EndsTheConnectionWithTheRestartReasonAndNoSecondTurn()
    {
        // Arrange
        AcknowledgeWithoutTurn();
        Take(_dialect.OnTurnStartOverdue());

        // Act: the Director stops the process it condemned.
        Take(_dialect.OnProcessExited(137));

        // Assert: rule 2 - one TurnStarted, one TurnEnded; the prompt keeps its one receipt.
        Assert.Single(_events.OfType<TurnStarted>());
        Assert.Single(_events.OfType<TurnEnded>());
        Assert.Single(_events.OfType<PromptDelivered>());
        Assert.DoesNotContain(_events, e => e is PromptRefused);
        Assert.Equal("Pi acknowledged the prompt but never started it. The Director is restarting Pi; the prompt is not sent again",
            _events.OfType<ConnectionEnded>().Single().Reason);
    }

    [Fact]
    public void OnTurnStartOverdue_TurnBeganInTime_DoesNothing()
    {
        // Arrange
        AcknowledgeWithoutTurn();
        Pi("""{"type":"agent_start"}""");
        _events.Clear();

        // Act
        var step = _dialect.OnTurnStartOverdue();

        // Assert
        Assert.Empty(step.Events);
        Assert.Null(step.StopReason);
        Assert.True(_dialect.TurnRunning);
    }

    [Fact]
    public void OnTurnStartOverdue_AfterPiExited_Throws()
    {
        // Arrange
        Take(_dialect.OnProcessExited(0));

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => _dialect.OnTurnStartOverdue());
    }

    [Fact]
    public void OnStdoutRecord_PromptAcknowledgementWithNoDisposition_RefusedByTheDirectorAndOutOfFlight()
    {
        // Arrange: review finding 2 - this shape used to throw after the command was forgotten.
        Ready();
        var command = WritePrompt("p1");

        // Act
        Pi($$$"""{"id":"{{{command.Id}}}","type":"response","command":"prompt","success":true}""");
        Take(_dialect.OnProcessExited(0));

        // Assert: rule 1, exactly one receipt.
        Assert.Equal(
        [
            "PromptRefused p1 Director: Pi acknowledged the prompt with a disposition the Director does not know: (none)",
            "ConnectionEnded 0: Pi exited (exit code 0) []",
        ], _events.Select(PiWireLogReplay.Describe));
        Assert.Single(_defects);
    }

    [Fact]
    public void OnStdoutRecord_RefusalWithNoErrorText_SaysSoAndReportsADefect()
    {
        // Arrange: review finding 4.
        Ready();
        var command = WritePrompt("p1");

        // Act
        Pi($$$"""{"id":"{{{command.Id}}}","type":"response","command":"prompt","success":false}""");

        // Assert
        Assert.Equal("Pi refused the prompt and gave no reason", Assert.IsType<PromptRefused>(Assert.Single(_events)).Reason);
        Assert.Equal(["Pi refused prompt p1 and gave no error text"], _defects);
        Assert.True(_dialect.CanWritePrompt);
    }
}
