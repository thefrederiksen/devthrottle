using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Gateway.Tests.ApiModeEvents;

/// <summary>
/// Session events and batches for the API-mode ingest tests (mission #4070, phase 2). The event sequences are the ones
/// the event model's Pi mapping (<c>docs/missions/api-mode-pi-2026-10-16/event-model.md</c>, section 3) makes of the
/// recorded Pi wire logs in <c>src/CcDirector.Core.Tests/TestData/pi-rpc/</c>; the acknowledgement and refusal strings
/// are those files' own lines, copied verbatim, so a receipt is checked against what Pi really wrote.
/// </summary>
internal static class ApiModeEventTestData
{
    public const string Sid = "7c1e2d3f-4a5b-4c6d-8e9f-0a1b2c3d4e5f";
    public const string Director = "dir-devlinux";

    public static readonly DateTime GenAStarted = new(2026, 10, 16, 11, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime GenBStarted = new(2026, 10, 16, 12, 30, 0, DateTimeKind.Utc);
    public static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary><c>plain.stdout.jsonl</c>: the prompt's acknowledgement.</summary>
    public const string PlainAck = """{"id":"req-dc0e2601","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""";

    /// <summary><c>refusal.stdout.jsonl</c>: the first prompt's acknowledgement, and Pi's refusal of the second, sent while it was busy.</summary>
    public const string RefusalFirstAck = """{"id":"req-e780f8e4","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""";
    public const string RefusalBusy = "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.";

    /// <summary><c>exit.stdout.jsonl</c>: acknowledged, then Pi killed while the reply streamed.</summary>
    public const string ExitAck = """{"id":"req-11f17cb7","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""";

    /// <summary><c>abort.stdout.jsonl</c>: acknowledged, then stopped.</summary>
    public const string AbortAck = """{"id":"req-7eaf9639","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""";

    public static readonly TurnUsage NoUsage = new() { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 };

    public static ConnectionReady Ready(long seq, string sid = Sid) => new() { SessionId = sid, Sequence = seq, At = T0, AgentSessionId = "e173b676-b5d6-4605-bc6c-cdbc82fd42a7", Model = "gpt-5.6-sol" };
    public static PromptQueued Queued(long seq, string prompt, PromptQueuedReason reason = PromptQueuedReason.TurnRunning, string sid = Sid) => new() { SessionId = sid, Sequence = seq, At = T0, PromptId = prompt, Reason = reason, Position = 1 };
    public static PromptDelivered Delivered(long seq, string prompt, string ack, string sid = Sid) => new() { SessionId = sid, Sequence = seq, At = T0, PromptId = prompt, Disposition = PromptDisposition.Started, Acknowledgement = ack };
    public static PromptRefused Refused(long seq, string prompt, string reason, PromptRefusedBy by, string sid = Sid) => new() { SessionId = sid, Sequence = seq, At = T0, PromptId = prompt, Reason = reason, RefusedBy = by };
    public static TurnStarted Started(long seq, string turn, params string[] prompts) => new() { SessionId = Sid, Sequence = seq, At = T0, TurnId = turn, PromptIds = prompts };
    public static MessageText Text(long seq, string turn, string text, MessageTextKind kind = MessageTextKind.Delta, MessageTextChannel channel = MessageTextChannel.Reply) => new() { SessionId = Sid, Sequence = seq, At = T0, TurnId = turn, MessageId = "m1", Channel = channel, Kind = kind, Text = text };
    public static TurnEnded Ended(long seq, string turn, TurnOutcome outcome, string? reason, string? stop) => new() { SessionId = Sid, Sequence = seq, At = T0, TurnId = turn, Outcome = outcome, Reason = reason, AgentStopReason = stop, Usage = NoUsage };
    public static ConnectionEnded Gone(long seq, int exit, string reason) => new() { SessionId = Sid, Sequence = seq, At = T0, ExitCode = exit, Reason = reason, StderrTail = Array.Empty<string>() };

    public static string Json(SessionEvent e) => JsonSerializer.Serialize(e, SessionEventJson.Options);

    public static ApiModeEventBatch Batch(string generation, DateTime started, params SessionEvent[] events) => new()
    {
        SessionId = events.Length > 0 ? events[0].SessionId : Sid,
        Generation = generation,
        GenerationStartedUtc = started,
        Events = events.Select(Json).ToList(),
    };

    public static ApiModeEventBatch A(params SessionEvent[] events) => Batch("gen-a", GenAStarted, events);
    public static ApiModeEventBatch B(params SessionEvent[] events) => Batch("gen-b", GenBStarted, events);

    // ----- the five outcomes, as the event model maps the recorded fixtures -----

    /// <summary><c>plain</c>: delivered, the reply PONG streamed, the turn done.</summary>
    public static SessionEvent[] DeliveredThenDone() => new SessionEvent[]
    {
        Ready(1),
        Delivered(2, "p1", PlainAck),
        Started(3, "t1", "p1"),
        Text(4, "t1", "PO"),
        Text(5, "t1", "NG"),
        Text(6, "t1", "PONG", MessageTextKind.Final),
        Ended(7, "t1", TurnOutcome.Done, null, "stop"),
    };

    /// <summary><c>refusal</c>: the first prompt delivered and running; a second written during it, refused by Pi in its
    /// own words; the first turn done.</summary>
    public static SessionEvent[] RefusedByTheAgent() => new SessionEvent[]
    {
        Ready(1),
        Delivered(2, "p1", RefusalFirstAck),
        Started(3, "t1", "p1"),
        Refused(4, "p2", RefusalBusy, PromptRefusedBy.Agent),
        Text(5, "t1", "OK", MessageTextKind.Final),
        Ended(6, "t1", TurnOutcome.Done, null, "stop"),
    };

    /// <summary><c>exit-before-ack</c>: Pi killed 50 milliseconds after the prompt was written; it wrote nothing.</summary>
    public static SessionEvent[] RefusedByTheDirectorBeforeAcknowledgement() => new SessionEvent[]
    {
        Ready(1),
        Refused(2, "p1", "Pi exited before acknowledging", PromptRefusedBy.Director),
        Gone(3, 137, "Pi exited (exit code 137)"),
    };

    /// <summary><c>exit</c>: acknowledged, then Pi killed while the reply streamed.</summary>
    public static SessionEvent[] DeliveredThenFailedMidTurn() => new SessionEvent[]
    {
        Ready(1),
        Delivered(2, "p1", ExitAck),
        Started(3, "t1", "p1"),
        Text(4, "t1", "Once upon"),
        Ended(5, "t1", TurnOutcome.Failed, "Pi exited during the turn (exit code 137)", null),
        Gone(6, 137, "Pi exited during the turn (exit code 137)"),
    };

    /// <summary><c>abort</c>: acknowledged, thinking streamed, then stopped.</summary>
    public static SessionEvent[] DeliveredThenCancelled() => new SessionEvent[]
    {
        Ready(1),
        Delivered(2, "p1", AbortAck),
        Started(3, "t1", "p1"),
        Text(4, "t1", "Considering rivers", channel: MessageTextChannel.Thought),
        Ended(5, "t1", TurnOutcome.Cancelled, "stopped by the user", "aborted"),
    };
}
