using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Core.ApiMode.Pi;

/// <summary>
/// Pi's API dialect (<c>pi --mode rpc</c>): a pure translator between Pi's records and the session event
/// model (issue #4070; the contract is docs/missions/api-mode-pi-2026-10-16/event-model.md, sections 2 and 3).
///
/// One instance per Pi process. It starts nothing, reads and writes nothing, and keeps no clock of its own:
/// the transport feeds it four things, in the order they happened, from one place at a time -
/// <list type="bullet">
/// <item><see cref="OnCommandWritten"/> after a command line has been written to Pi's standard input;</item>
/// <item><see cref="OnStdoutRecord"/> for each record Pi wrote on standard output, parsed;</item>
/// <item><see cref="OnStderrLine"/> for each standard error line;</item>
/// <item><see cref="OnProcessExited"/> once, after standard output has reached its end.</item>
/// </list>
/// Each returns a <see cref="PiDialectStep"/>: the events to report, any line to write back, and any defect.
/// The commands themselves are built by the <c>Build*</c> methods. The Director's prompt queue lives outside;
/// <see cref="CanWritePrompt"/> says when the next prompt may go.
/// </summary>
public sealed class PiDialect
{
    /// <summary>The standard error warning Pi writes when it has no conversation under the id it was given.</summary>
    public const string NoProjectSessionWarning = "No project session found";

    /// <summary>The reason a prompt is refused when Pi exits before acknowledging it (event model, rule 1).</summary>
    public const string ExitedBeforeAcknowledgingReason = "Pi exited before acknowledging";

    /// <summary>The reason a turn ends when Pi reports it aborted (event model, section 3.4).</summary>
    public const string StoppedByUserReason = "stopped by the user";

    /// <summary>The reason a done turn carries when the reply hit the model's length limit.</summary>
    public const string LengthLimitReason = "the reply hit the model's length limit";

    private static readonly HashSet<string> DialogMethods = ["select", "confirm", "input", "editor"];
    private static readonly HashSet<string> FireAndForgetMethods = ["notify", "setStatus", "setWidget", "setTitle", "set_editor_text"];

    // Pi records that carry nothing for the session event model (section 3.3 says why for each).
    private static readonly HashSet<string> IgnoredRecordTypes =
    [
        "turn_start", "turn_end", "agent_end", "queue_update", "tool_execution_update", "auto_retry_start",
        "compaction_start", "compaction_end", "session_info_changed", "thinking_level_changed", "entry_appended",
    ];

    private readonly string _sessionId;
    private readonly string _agentSessionId;
    private readonly bool _resuming;
    private readonly TimeProvider _clock;
    private readonly SessionEventNumbering _numbering;

    // Commands written and not yet answered, by id, in the order written.
    private readonly Dictionary<string, PiCommand> _awaitingResponse = new(StringComparer.Ordinal);
    private readonly List<string> _awaitingOrder = [];

    // Prompts in flight: from the write until their turn settled, their refusal, or a 'handled' acknowledgement.
    private readonly List<string> _inFlight = [];

    // Prompts Pi acknowledged as 'started' that no turn holds yet, and prompts Pi queued itself.
    private readonly List<string> _deliveredAwaitingTurn = [];
    private readonly List<string> _queuedByAgent = [];

    // Standard error lines since the current turn started, or since the prompt was written when no turn had.
    private readonly List<string> _stderrSinceMark = [];

    private Turn? _turn;
    private bool _connectionReadySent;
    private bool _exited;
    private string? _stopReason;

    /// <param name="sessionId">The DevThrottle session the events belong to.</param>
    /// <param name="agentSessionId">The id Pi was started with (<c>--session-id</c>).</param>
    /// <param name="resuming">True when this process resumes an existing conversation, so a Pi that found
    /// none must fail rather than quietly start a new one (event model, section 3.7).</param>
    /// <param name="clock">Where <c>at</c> comes from.</param>
    /// <param name="numbering">The session's numbers, shared by every Pi process of the session.</param>
    public PiDialect(string sessionId, string agentSessionId, bool resuming, TimeProvider clock, SessionEventNumbering numbering)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentSessionId);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(numbering);
        _sessionId = sessionId;
        _agentSessionId = agentSessionId;
        _resuming = resuming;
        _clock = clock;
        _numbering = numbering;
        FileLog.Write($"[PiDialect] Created: session={sessionId}, agentSession={agentSessionId}, resuming={resuming}");
    }

    /// <summary>
    /// Whether the next prompt may be written now: the process is up, nothing says it must stop, and no
    /// prompt is in flight. A busy Pi refuses a second prompt rather than queueing it, and the in-flight
    /// window starts at the write, not at <c>agent_start</c> (event model, section 3.1).
    /// </summary>
    public bool CanWritePrompt => !_exited && _stopReason is null && _inFlight.Count == 0;

    /// <summary>True while a turn is running (from <c>agent_start</c> to <c>agent_settled</c>).</summary>
    public bool TurnRunning => _turn is not null;

    /// <summary>Set once this process must be stopped, with the reason (a resume that found no conversation).</summary>
    public string? StopReason => _stopReason;

    // ---------------------------------------------------------------- commands

    /// <summary>A <c>prompt</c> command. Never with <c>streamingBehavior</c>: the Director owns the queue.</summary>
    public PiCommand BuildPrompt(string promptId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);
        ArgumentNullException.ThrowIfNull(message);
        var id = _numbering.NextCommandId();
        var line = new JsonObject { ["id"] = id, ["type"] = "prompt", ["message"] = message }.ToJsonString();
        FileLog.Write($"[PiDialect] BuildPrompt: session={_sessionId}, prompt={promptId}, command={id}, length={message.Length}");
        return new PiCommand(id, "prompt", line, promptId);
    }

    /// <summary>An <c>abort</c> command (Stop). Pi answers it only once the session is idle.</summary>
    public PiCommand BuildAbort() => BuildSimple("abort");

    /// <summary>A <c>get_state</c> command: Pi's conversation id and model, for <see cref="ConnectionReady"/>.</summary>
    public PiCommand BuildGetState() => BuildSimple("get_state");

    /// <summary>A <c>get_session_stats</c> command: tokens for the spend record.</summary>
    public PiCommand BuildGetSessionStats() => BuildSimple("get_session_stats");

    /// <summary>A <c>get_last_assistant_text</c> command.</summary>
    public PiCommand BuildGetLastAssistantText() => BuildSimple("get_last_assistant_text");

    /// <summary>The one cancellation Pi accepts for any extension dialog (event model, section 3.6).</summary>
    public PiCommand BuildExtensionUiCancellation(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var line = new JsonObject { ["type"] = "extension_ui_response", ["id"] = requestId, ["cancelled"] = true }.ToJsonString();
        FileLog.Write($"[PiDialect] BuildExtensionUiCancellation: session={_sessionId}, request={requestId}");
        return new PiCommand(requestId, "extension_ui_response", line);
    }

    private PiCommand BuildSimple(string type)
    {
        var id = _numbering.NextCommandId();
        var line = new JsonObject { ["id"] = id, ["type"] = type }.ToJsonString();
        FileLog.Write($"[PiDialect] Build: session={_sessionId}, type={type}, command={id}");
        return new PiCommand(id, type, line);
    }

    // ---------------------------------------------------------------- inputs

    /// <summary>The transport wrote <paramref name="command"/> to Pi's standard input.</summary>
    public PiDialectStep OnCommandWritten(PiCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        FileLog.Write($"[PiDialect] OnCommandWritten: session={_sessionId}, type={command.Type}, command={command.Id}, prompt={command.PromptId}");
        var step = new StepBuilder();
        if (_exited)
            throw new InvalidOperationException($"Command {command.Id} ({command.Type}) was written after Pi exited.");

        // An extension dialog's answer has no response; there is nothing to wait for.
        if (command.Type == "extension_ui_response")
            return step.Build();

        if (_awaitingResponse.ContainsKey(command.Id))
            throw new InvalidOperationException($"Command id {command.Id} is already waiting for Pi's response; ids must be unique.");

        if (command.Type == "prompt")
        {
            if (command.PromptId is null)
                throw new ArgumentException("A prompt command must carry the Director's prompt id.", nameof(command));
            if (_inFlight.Count > 0)
                Defect(step, $"prompt {command.PromptId} was written while prompt {_inFlight[0]} is in flight; Pi refuses a prompt while it is busy");
            if (_turn is null)
                _stderrSinceMark.Clear();
            _inFlight.Add(command.PromptId);
        }

        _awaitingResponse[command.Id] = command;
        _awaitingOrder.Add(command.Id);
        return step.Build();
    }

    /// <summary>One standard error line. Diagnostics, never protocol; kept for a failure reason.</summary>
    public PiDialectStep OnStderrLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        FileLog.Write($"[PiDialect] OnStderrLine: session={_sessionId}, line={line}");
        var step = new StepBuilder();
        _stderrSinceMark.Add(line);
        if (_resuming && line.Contains(NoProjectSessionWarning, StringComparison.Ordinal))
            RequireStop(step, $"Resume failed: Pi found no conversation with id '{_agentSessionId}' in this folder ({line})");
        return step.Build();
    }

    /// <summary>One record Pi wrote on standard output.</summary>
    public PiDialectStep OnStdoutRecord(JsonElement record)
    {
        if (_exited)
            throw new InvalidOperationException("A record arrived after Pi exited; the transport reports the exit only after standard output ended.");
        var step = new StepBuilder();
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            Defect(step, $"Pi wrote a record with no type: {Truncate(record.GetRawText())}");
            return step.Build();
        }

        var type = typeElement.GetString()!;
        if (type != "message_update")
            FileLog.Write($"[PiDialect] OnStdoutRecord: session={_sessionId}, type={type}");

        switch (type)
        {
            case "response": OnResponse(record, step); break;
            case "agent_start": OnAgentStart(step); break;
            case "agent_settled": OnAgentSettled(record, step); break;
            case "message_start": OnMessageStart(record, step); break;
            case "message_update": OnMessageUpdate(record, step); break;
            case "message_end": OnMessageEnd(record, step); break;
            case "tool_execution_start": OnToolStart(record, step); break;
            case "tool_execution_end": OnToolEnd(record, step); break;
            case "auto_retry_end": OnAutoRetryEnd(record); break;
            case "extension_ui_request": OnExtensionUiRequest(record, step); break;
            case "extension_error":
                FileLog.Write($"[PiDialect] Extension error: session={_sessionId}, record={Truncate(record.GetRawText())}");
                break;
            default:
                if (!IgnoredRecordTypes.Contains(type) && !type.StartsWith("summarization_retry_", StringComparison.Ordinal))
                    Defect(step, $"Pi wrote a record of a type the dialect does not know: {type}");
                break;
        }
        return step.Build();
    }

    /// <summary>
    /// Pi's process ended. Call once, after standard output reached its end. A prompt still waiting for its
    /// acknowledgement is refused, a running turn ends <c>failed</c>, and <see cref="ConnectionEnded"/> follows.
    /// </summary>
    /// <param name="exitCode">As .NET reports it: a process killed by a signal on Linux is 128 plus the signal (137 for a kill).</param>
    public PiDialectStep OnProcessExited(int exitCode)
    {
        FileLog.Write($"[PiDialect] OnProcessExited: session={_sessionId}, exitCode={exitCode}, turn={_turn?.Id}, inFlight={_inFlight.Count}");
        if (_exited)
            throw new InvalidOperationException("OnProcessExited was called twice.");
        _exited = true;
        var step = new StepBuilder();
        var stderr = _stderrSinceMark.ToList();

        var unacknowledged = _awaitingOrder.Select(id => _awaitingResponse[id]).Where(c => c.Type == "prompt").ToList();
        foreach (var prompt in unacknowledged)
            Refuse(step, prompt.PromptId!, ExitedBeforeAcknowledgingReason, PromptRefusedBy.Director);

        string? turnReason = null;
        if (_turn is not null)
        {
            turnReason = $"Pi exited during the turn (exit code {exitCode})";
            if (stderr.Count > 0)
                turnReason += ". Standard error: " + string.Join(" | ", stderr);
            EndTurn(step, TurnOutcome.Failed, turnReason, _turn.LastStopReason);
        }

        var reason = _stopReason
            ?? turnReason
            ?? (unacknowledged.Count > 0 ? ExitedBeforeAcknowledgingReason : $"Pi exited (exit code {exitCode})");
        step.Events.Add(new ConnectionEnded
        {
            SessionId = _sessionId, Sequence = _numbering.NextSequence(), At = _clock.GetUtcNow(),
            ExitCode = exitCode, Reason = reason, StderrTail = stderr,
        });

        _awaitingResponse.Clear();
        _awaitingOrder.Clear();
        _inFlight.Clear();
        _deliveredAwaitingTurn.Clear();
        _queuedByAgent.Clear();
        FileLog.Write($"[PiDialect] OnProcessExited: session={_sessionId}, reason={reason}");
        return step.Build();
    }

    // ---------------------------------------------------------------- responses

    private void OnResponse(JsonElement record, StepBuilder step)
    {
        var command = record.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
        var success = record.GetProperty("success").GetBoolean();
        var error = record.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

        if (!record.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            // Pi could not read a line we wrote, so it could not find the id in it either.
            Defect(step, $"Pi answered '{command}' with no id: {error}");
            var waitingPrompt = _awaitingOrder.Select(id => _awaitingResponse[id]).FirstOrDefault(p => p.Type == "prompt");
            if (command == "parse" && waitingPrompt is not null)
            {
                Forget(waitingPrompt.Id);
                Refuse(step, waitingPrompt.PromptId!, $"Pi could not read the prompt: {error}", PromptRefusedBy.Director);
            }
            return;
        }

        var id = idElement.GetString()!;
        if (!_awaitingResponse.TryGetValue(id, out var written))
        {
            Defect(step, $"Pi answered command '{command}' with id {id}, which the Director did not write or already had answered: {error}");
            return;
        }
        Forget(id);

        if (written.Type == "prompt")
        {
            OnPromptResponse(record, written, success, error, step);
            return;
        }

        string? data = record.TryGetProperty("data", out var d) ? d.GetRawText() : null;
        step.Result = new PiCommandResult(id, written.Type, success, error, data);
        FileLog.Write($"[PiDialect] Response: session={_sessionId}, command={id}, type={written.Type}, success={success}");
        if (!success)
        {
            Defect(step, $"Pi refused command '{written.Type}' ({id}): {error}");
            return;
        }
        if (written.Type == "get_state")
            OnState(record.GetProperty("data"), step);
    }

    private void OnPromptResponse(JsonElement record, PiCommand written, bool success, string? error, StepBuilder step)
    {
        var promptId = written.PromptId!;
        if (!success)
        {
            _inFlight.Remove(promptId);
            Refuse(step, promptId, error ?? "", PromptRefusedBy.Agent);
            return;
        }

        var disposition = record.GetProperty("data").GetProperty("disposition").GetString();
        PromptDisposition mapped;
        switch (disposition)
        {
            case "started":
                mapped = PromptDisposition.Started;
                _deliveredAwaitingTurn.Add(promptId);
                break;
            case "queued":
                mapped = PromptDisposition.QueuedByAgent;
                _queuedByAgent.Add(promptId);
                break;
            case "handled":
                // An extension command consumed it: no turn follows, so it is no longer in flight.
                mapped = PromptDisposition.Handled;
                _inFlight.Remove(promptId);
                break;
            default:
                // Pi accepted the prompt in a way this dialect does not understand; a receipt cannot claim it.
                Defect(step, $"Pi acknowledged prompt {promptId} with a disposition the dialect does not know: {disposition}");
                _inFlight.Remove(promptId);
                Refuse(step, promptId, $"Pi acknowledged the prompt with a disposition the Director does not know: {disposition}", PromptRefusedBy.Director);
                return;
        }

        FileLog.Write($"[PiDialect] Delivered: session={_sessionId}, prompt={promptId}, disposition={disposition}");
        step.Events.Add(new PromptDelivered
        {
            SessionId = _sessionId, Sequence = _numbering.NextSequence(), At = _clock.GetUtcNow(),
            PromptId = promptId, Disposition = mapped, Acknowledgement = record.GetRawText(),
        });
    }

    private void OnState(JsonElement data, StepBuilder step)
    {
        var agentSessionId = data.GetProperty("sessionId").GetString()!;
        var model = data.GetProperty("model").GetProperty("id").GetString()!;
        var messageCount = data.GetProperty("messageCount").GetInt32();
        FileLog.Write($"[PiDialect] State: session={_sessionId}, agentSession={agentSessionId}, model={model}, messages={messageCount}");

        if (_resuming && messageCount == 0)
            RequireStop(step, $"Resume failed: Pi's conversation '{agentSessionId}' has no messages (messageCount 0)");
        if (_stopReason is not null || _connectionReadySent)
            return;

        _connectionReadySent = true;
        step.Events.Add(new ConnectionReady
        {
            SessionId = _sessionId, Sequence = _numbering.NextSequence(), At = _clock.GetUtcNow(),
            AgentSessionId = agentSessionId, Model = model,
        });
    }

    // ---------------------------------------------------------------- the turn

    private void OnAgentStart(StepBuilder step)
    {
        // Pi's agent run is our turn; a second agent_start inside one run starts nothing new.
        if (_turn is not null)
            return;

        _turn = new Turn(_numbering.NextTurnId(), [.. _deliveredAwaitingTurn]);
        _deliveredAwaitingTurn.Clear();
        _stderrSinceMark.Clear();
        FileLog.Write($"[PiDialect] TurnStarted: session={_sessionId}, turn={_turn.Id}, prompts={string.Join(",", _turn.PromptIds)}");
        step.Events.Add(new TurnStarted
        {
            SessionId = _sessionId, Sequence = _numbering.NextSequence(), At = _clock.GetUtcNow(),
            TurnId = _turn.Id, PromptIds = [.. _turn.PromptIds],
        });
    }

    private void OnMessageStart(JsonElement record, StepBuilder step)
    {
        var role = record.GetProperty("message").GetProperty("role").GetString();
        if (role == "user" && _turn is not null && _queuedByAgent.Count > 0)
        {
            // A prompt Pi queued starts now, inside the current run. The turn's promptIds went out with
            // TurnStarted, so this is recorded here for its in-flight rule only.
            var promptId = _queuedByAgent[0];
            _queuedByAgent.RemoveAt(0);
            _turn.PromptIds.Add(promptId);
            FileLog.Write($"[PiDialect] Queued prompt started: session={_sessionId}, turn={_turn.Id}, prompt={promptId}");
            return;
        }
        if (role != "assistant")
            return;
        if (_turn is null)
        {
            Defect(step, "Pi started an assistant message outside a turn");
            return;
        }
        _turn.OpenMessage();
    }

    private void OnMessageUpdate(JsonElement record, StepBuilder step)
    {
        var update = record.GetProperty("assistantMessageEvent");
        var kind = update.GetProperty("type").GetString();
        switch (kind)
        {
            case "text_delta":
                EmitText(step, MessageTextChannel.Reply, MessageTextKind.Delta, update.GetProperty("delta").GetString()!, null);
                break;
            case "text_end":
                EmitText(step, MessageTextChannel.Reply, MessageTextKind.Final, update.GetProperty("content").GetString()!, update.GetProperty("contentIndex").GetInt32());
                break;
            case "thinking_delta":
                EmitText(step, MessageTextChannel.Thought, MessageTextKind.Delta, update.GetProperty("delta").GetString()!, null);
                break;
            case "thinking_end":
                EmitText(step, MessageTextChannel.Thought, MessageTextKind.Final, update.GetProperty("content").GetString()!, update.GetProperty("contentIndex").GetInt32());
                break;
            case "start":
            case "text_start":
            case "thinking_start":
            case "toolcall_start":
            case "toolcall_delta":
            case "toolcall_end":
            case "done":
            case "error":
                // The first delta opens a block; a tool's arguments are not a card until the tool runs; the
                // message's end comes with message_end.
                break;
            default:
                Defect(step, $"Pi wrote a message update the dialect does not know: {kind}");
                break;
        }
    }

    private void EmitText(StepBuilder step, MessageTextChannel channel, MessageTextKind kind, string text, int? contentIndex)
    {
        if (_turn?.MessageId is null)
        {
            Defect(step, $"Pi streamed {channel} text outside an assistant message");
            return;
        }
        if (contentIndex is { } index)
            _turn.FinalisedBlocks.Add(index);
        step.Events.Add(new MessageText
        {
            SessionId = _sessionId, Sequence = _numbering.NextSequence(), At = _clock.GetUtcNow(),
            TurnId = _turn.Id, MessageId = _turn.MessageId, Channel = channel, Kind = kind, Text = text,
        });
    }

    private void OnMessageEnd(JsonElement record, StepBuilder step)
    {
        var message = record.GetProperty("message");
        if (message.GetProperty("role").GetString() != "assistant")
            return;
        if (_turn?.MessageId is null)
        {
            Defect(step, "Pi ended an assistant message that never started inside a turn");
            return;
        }

        // The message's content is the authoritative final text: finalise any block no *_end finalised.
        var index = 0;
        foreach (var block in message.GetProperty("content").EnumerateArray())
        {
            if (!_turn.FinalisedBlocks.Contains(index))
            {
                switch (block.GetProperty("type").GetString())
                {
                    case "text":
                        EmitText(step, MessageTextChannel.Reply, MessageTextKind.Final, block.GetProperty("text").GetString()!, index);
                        break;
                    case "thinking":
                        EmitText(step, MessageTextChannel.Thought, MessageTextKind.Final, block.GetProperty("thinking").GetString()!, index);
                        break;
                }
            }
            index++;
        }

        if (message.TryGetProperty("usage", out var usage))
        {
            _turn.Input += usage.GetProperty("input").GetInt64();
            _turn.Output += usage.GetProperty("output").GetInt64();
            _turn.CacheRead += usage.GetProperty("cacheRead").GetInt64();
            _turn.CacheWrite += usage.GetProperty("cacheWrite").GetInt64();
        }
        _turn.LastStopReason = message.TryGetProperty("stopReason", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        _turn.LastErrorMessage = message.TryGetProperty("errorMessage", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        FileLog.Write($"[PiDialect] Message ended: session={_sessionId}, turn={_turn.Id}, message={_turn.MessageId}, stopReason={_turn.LastStopReason}");
        _turn.CloseMessage();
    }

    private void OnToolStart(JsonElement record, StepBuilder step)
    {
        var toolCallId = record.GetProperty("toolCallId").GetString()!;
        var toolName = record.GetProperty("toolName").GetString()!;
        var input = record.GetProperty("args").GetRawText();
        if (_turn is null)
        {
            Defect(step, $"Pi started tool {toolName} ({toolCallId}) outside a turn");
            return;
        }
        _turn.OpenTools.Add(toolCallId);
        FileLog.Write($"[PiDialect] ToolCallStarted: session={_sessionId}, turn={_turn.Id}, tool={toolName}, call={toolCallId}");
        step.Events.Add(new ToolCallStarted
        {
            SessionId = _sessionId, Sequence = _numbering.NextSequence(), At = _clock.GetUtcNow(),
            TurnId = _turn.Id, ToolCallId = toolCallId, ToolName = toolName, Kind = ToolKind(toolName), Input = input,
        });
    }

    /// <summary>Pi's tool name to the event model's kind (event model, section 3.3).</summary>
    public static ToolCallKind ToolKind(string toolName) => toolName switch
    {
        "bash" => ToolCallKind.Execute,
        "read" => ToolCallKind.Read,
        "edit" or "write" => ToolCallKind.Edit,
        _ => ToolCallKind.Other,
    };

    private void OnToolEnd(JsonElement record, StepBuilder step)
    {
        var toolCallId = record.GetProperty("toolCallId").GetString()!;
        var isError = record.GetProperty("isError").GetBoolean();
        var output = string.Concat(record.GetProperty("result").GetProperty("content").EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "text")
            .Select(b => b.GetProperty("text").GetString()));
        long? duration = record.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : null;
        if (_turn is null || !_turn.OpenTools.Remove(toolCallId))
        {
            Defect(step, $"Pi finished tool call {toolCallId}, which is not running in a turn");
            return;
        }
        FileLog.Write($"[PiDialect] ToolCallFinished: session={_sessionId}, turn={_turn.Id}, call={toolCallId}, isError={isError}");
        step.Events.Add(new ToolCallFinished
        {
            SessionId = _sessionId, Sequence = _numbering.NextSequence(), At = _clock.GetUtcNow(),
            TurnId = _turn.Id, ToolCallId = toolCallId, Outcome = isError ? ToolCallOutcome.Failed : ToolCallOutcome.Succeeded,
            Output = output, DurationMilliseconds = duration,
        });
    }

    private void OnAutoRetryEnd(JsonElement record)
    {
        if (_turn is null || record.GetProperty("success").GetBoolean())
            return;
        _turn.RetryFinalError = record.TryGetProperty("finalError", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        FileLog.Write($"[PiDialect] Retries exhausted: session={_sessionId}, turn={_turn.Id}, finalError={_turn.RetryFinalError}");
    }

    private void OnAgentSettled(JsonElement record, StepBuilder step)
    {
        if (_turn is null)
        {
            Defect(step, "Pi settled with no turn running");
            return;
        }

        var aborted = record.GetProperty("aborted").GetBoolean();
        var stopReason = _turn.LastStopReason;
        TurnOutcome outcome;
        string? reason;
        if (aborted)
        {
            outcome = TurnOutcome.Cancelled;
            reason = StoppedByUserReason;
        }
        else
        {
            switch (stopReason)
            {
                case "stop":
                    outcome = TurnOutcome.Done;
                    reason = null;
                    break;
                case "length":
                    outcome = TurnOutcome.Done;
                    reason = LengthLimitReason;
                    break;
                case "error":
                    outcome = TurnOutcome.Failed;
                    reason = _turn.LastErrorMessage ?? _turn.RetryFinalError ?? "Pi ended the turn with an error and gave no message";
                    break;
                case null:
                    outcome = TurnOutcome.Failed;
                    reason = _turn.RetryFinalError ?? "Pi settled the turn without a reply";
                    break;
                default:
                    Defect(step, $"Pi ended turn {_turn.Id} with a stop reason the dialect does not know: {stopReason}");
                    outcome = TurnOutcome.Failed;
                    reason = $"Pi ended the turn with a stop reason the Director does not know: {stopReason}";
                    break;
            }
        }

        foreach (var promptId in _turn.PromptIds)
            _inFlight.Remove(promptId);
        EndTurn(step, outcome, reason, stopReason);
    }

    /// <summary>Closes every tool still running (rule 3), then emits the one TurnEnded (rule 2).</summary>
    private void EndTurn(StepBuilder step, TurnOutcome outcome, string? reason, string? agentStopReason)
    {
        var turn = _turn!;
        foreach (var toolCallId in turn.OpenTools)
        {
            if (outcome == TurnOutcome.Done)
                Defect(step, $"turn {turn.Id} ended done with tool call {toolCallId} still running");
            step.Events.Add(new ToolCallFinished
            {
                SessionId = _sessionId, Sequence = _numbering.NextSequence(), At = _clock.GetUtcNow(),
                TurnId = turn.Id, ToolCallId = toolCallId,
                Outcome = outcome == TurnOutcome.Cancelled ? ToolCallOutcome.Cancelled : ToolCallOutcome.Failed,
                Output = "", DurationMilliseconds = null,
            });
        }
        turn.OpenTools.Clear();

        FileLog.Write($"[PiDialect] TurnEnded: session={_sessionId}, turn={turn.Id}, outcome={outcome}, reason={reason}, stopReason={agentStopReason}");
        step.Events.Add(new TurnEnded
        {
            SessionId = _sessionId, Sequence = _numbering.NextSequence(), At = _clock.GetUtcNow(),
            TurnId = turn.Id, Outcome = outcome, Reason = reason, AgentStopReason = agentStopReason,
            Usage = new TurnUsage { Input = turn.Input, Output = turn.Output, CacheRead = turn.CacheRead, CacheWrite = turn.CacheWrite },
        });
        _turn = null;
    }

    // ---------------------------------------------------------------- extension dialogs

    private void OnExtensionUiRequest(JsonElement record, StepBuilder step)
    {
        var requestId = record.GetProperty("id").GetString()!;
        var method = record.GetProperty("method").GetString()!;
        if (DialogMethods.Contains(method))
        {
            // No dialog may hang an API-mode turn: cancel it at once (event model, section 3.6).
            FileLog.Write($"[PiDialect] Cancelling extension dialog: session={_sessionId}, request={requestId}, method={method}");
            step.Commands.Add(BuildExtensionUiCancellation(requestId));
            return;
        }
        if (FireAndForgetMethods.Contains(method))
        {
            FileLog.Write($"[PiDialect] Extension request ignored: session={_sessionId}, request={requestId}, method={method}");
            return;
        }
        Defect(step, $"Pi sent an extension request with a method the dialect does not know: {method}");
    }

    // ---------------------------------------------------------------- helpers

    private void Refuse(StepBuilder step, string promptId, string reason, PromptRefusedBy by)
    {
        FileLog.Write($"[PiDialect] Refused: session={_sessionId}, prompt={promptId}, by={by}, reason={reason}");
        _inFlight.Remove(promptId);
        step.Events.Add(new PromptRefused
        {
            SessionId = _sessionId, Sequence = _numbering.NextSequence(), At = _clock.GetUtcNow(),
            PromptId = promptId, Reason = reason, RefusedBy = by,
        });
    }

    private void Forget(string commandId)
    {
        _awaitingResponse.Remove(commandId);
        _awaitingOrder.Remove(commandId);
    }

    private void RequireStop(StepBuilder step, string reason)
    {
        if (_stopReason is not null)
            return;
        _stopReason = reason;
        step.StopReason = reason;
        FileLog.Write($"[PiDialect] Stop required: session={_sessionId}, reason={reason}");
    }

    private void Defect(StepBuilder step, string defect)
    {
        FileLog.Write($"[PiDialect] DEFECT: session={_sessionId}, {defect}");
        step.Defects.Add(defect);
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "...";

    private sealed class StepBuilder
    {
        public List<SessionEvent> Events { get; } = [];
        public List<PiCommand> Commands { get; } = [];
        public List<string> Defects { get; } = [];
        public PiCommandResult? Result { get; set; }
        public string? StopReason { get; set; }

        public PiDialectStep Build() => new(Events, Commands, Defects, Result, StopReason);
    }

    private sealed class Turn(string id, List<string> promptIds)
    {
        private int _messages;

        public string Id { get; } = id;
        public List<string> PromptIds { get; } = promptIds;
        public List<string> OpenTools { get; } = [];
        public HashSet<int> FinalisedBlocks { get; } = [];
        public string? MessageId { get; private set; }
        public string? LastStopReason { get; set; }
        public string? LastErrorMessage { get; set; }
        public string? RetryFinalError { get; set; }
        public long Input { get; set; }
        public long Output { get; set; }
        public long CacheRead { get; set; }
        public long CacheWrite { get; set; }

        public void OpenMessage()
        {
            MessageId = $"{Id}-message-{++_messages}";
            FinalisedBlocks.Clear();
        }

        public void CloseMessage() => MessageId = null;
    }
}
