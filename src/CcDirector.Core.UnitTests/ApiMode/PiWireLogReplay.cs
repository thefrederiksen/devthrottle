using System.Text.Json;
using CcDirector.Core.ApiMode;
using CcDirector.Core.ApiMode.Pi;
using CcDirector.Gateway.Contracts.ApiMode;
using Microsoft.Extensions.Time.Testing;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>
/// Replays one recorded Pi wire log (TestData/pi-rpc/&lt;scenario&gt;.transcript.jsonl, real Pi 1.2.0) through a
/// <see cref="PiDialect"/>, record by record in recorded order, exactly as the transport will feed it: a
/// <c>stdin</c> record is "we wrote this command", <c>stdout</c> is a Pi record, <c>stderr</c> a diagnostic
/// line and <c>exit</c> the end of the process. No Pi, no process, no clock: the fake clock is set to the
/// recording's own milliseconds before each record, so <c>at</c> is the recorded time.
///
/// Prompts get the Director ids <c>prompt-1</c>, <c>prompt-2</c>, ... in the order they were written. A
/// stdin line that is not JSON (the <c>refusal</c> scenario's deliberate malformed line) is one the dialect
/// could never have written, so it is not reported as written; Pi's answer to it still is replayed.
/// The recorder's exit code for a kill is Python's -9; .NET on Linux reports 137, so the replay passes 137.
/// </summary>
internal sealed class PiWireLogReplay
{
    public static readonly DateTimeOffset RecordingStart = new(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);

    public List<SessionEvent> Events { get; } = [];
    public List<string> Defects { get; } = [];
    public List<PiCommandResult> Results { get; } = [];
    public List<string> StopReasons { get; } = [];
    public List<PiCommand> CommandsToWrite { get; } = [];
    public PiDialect Dialect { get; }
    public FakeTimeProvider Clock { get; }

    private PiWireLogReplay(PiDialect dialect, FakeTimeProvider clock)
    {
        Dialect = dialect;
        Clock = clock;
    }

    public static string FixturePath(string scenario) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "pi-rpc", $"{scenario}.transcript.jsonl");

    public static PiWireLogReplay Run(string scenario, bool resuming = false, Func<string, string>? editStdout = null)
    {
        var lines = File.ReadAllLines(FixturePath(scenario));
        var meta = JsonDocument.Parse(lines[0]).RootElement;
        var argv = meta.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToList();
        var agentSessionId = argv[argv.IndexOf("--session-id") + 1];

        var clock = new FakeTimeProvider(RecordingStart);
        var dialect = new PiDialect("session-under-test", agentSessionId, resuming, clock, new SessionEventNumbering());
        var replay = new PiWireLogReplay(dialect, clock);
        var prompts = 0;

        foreach (var raw in lines.Skip(1))
        {
            var record = JsonDocument.Parse(raw).RootElement;
            replay.Clock.SetUtcNow(RecordingStart.AddMilliseconds(record.GetProperty("ms").GetInt64()));
            switch (record.GetProperty("dir").GetString())
            {
                case "stdin":
                {
                    var line = record.GetProperty("line").GetString()!;
                    JsonElement command;
                    try { command = JsonDocument.Parse(line).RootElement; }
                    catch (JsonException) { continue; }
                    var type = command.GetProperty("type").GetString()!;
                    var promptId = type == "prompt" ? $"prompt-{++prompts}" : null;
                    replay.Take(dialect.OnCommandWritten(new PiCommand(command.GetProperty("id").GetString()!, type, line, promptId)));
                    break;
                }
                case "stdout":
                {
                    var line = record.GetProperty("line").GetString()!;
                    if (editStdout is not null)
                        line = editStdout(line);
                    replay.Take(dialect.OnStdoutRecord(JsonDocument.Parse(line).RootElement));
                    break;
                }
                case "stderr":
                    replay.Take(dialect.OnStderrLine(record.GetProperty("line").GetString()!));
                    break;
                case "exit":
                {
                    var code = record.GetProperty("code").GetInt32();
                    replay.Take(dialect.OnProcessExited(code == -9 ? 137 : code));
                    break;
                }
                case "kill":
                    break;
                default:
                    throw new InvalidOperationException($"Unknown transcript direction in {scenario}: {raw}");
            }
        }
        return replay;
    }

    public void Take(PiDialectStep step)
    {
        Events.AddRange(step.Events);
        Defects.AddRange(step.Defects);
        CommandsToWrite.AddRange(step.CommandsToWrite);
        if (step.Result is not null) Results.Add(step.Result);
        if (step.StopReason is not null) StopReasons.Add(step.StopReason);
    }

    /// <summary>The events as one line each, for asserting an exact sequence.</summary>
    public List<string> Lines() => Events.Select(Describe).ToList();

    public static string Describe(SessionEvent e) => e switch
    {
        PromptDelivered d => $"PromptDelivered {d.PromptId} {d.Disposition}",
        PromptRefused r => $"PromptRefused {r.PromptId} {r.RefusedBy}: {r.Reason}",
        PromptQueued q => $"PromptQueued {q.PromptId} {q.Reason} {q.Position}",
        TurnStarted t => $"TurnStarted {t.TurnId} [{string.Join(",", t.PromptIds)}]",
        MessageText m => $"MessageText {m.MessageId} {m.Channel} {m.Kind} {JsonSerializer.Serialize(m.Text)}",
        ToolCallStarted s => $"ToolCallStarted {s.TurnId} {s.ToolName} {s.Kind} {s.Input}",
        ToolCallFinished f => $"ToolCallFinished {f.TurnId} {f.Outcome} {JsonSerializer.Serialize(f.Output)} {f.DurationMilliseconds}",
        TurnEnded t => $"TurnEnded {t.TurnId} {t.Outcome} reason={t.Reason} stop={t.AgentStopReason}",
        PermissionRequested p => $"PermissionRequested {p.RequestId}",
        ConnectionReady c => $"ConnectionReady {c.AgentSessionId} {c.Model}",
        ConnectionEnded c => $"ConnectionEnded {c.ExitCode}: {c.Reason} [{string.Join(" | ", c.StderrTail)}]",
        _ => throw new InvalidOperationException($"Unknown event {e.GetType().Name}"),
    };
}
