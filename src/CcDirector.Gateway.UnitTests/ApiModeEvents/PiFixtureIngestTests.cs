using System.Text.Json;
using CcDirector.Core.ApiMode;
using CcDirector.Core.ApiMode.Pi;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static CcDirector.Gateway.Tests.ApiModeEvents.ApiModeEventTestData;

namespace CcDirector.Gateway.Tests.ApiModeEvents;

/// <summary>
/// THE RECORDED PI WIRE LOGS, END TO END THROUGH THE GATEWAY (mission #4070, phase 2). Each real recording in
/// <c>src/CcDirector.Core.Tests/TestData/pi-rpc/</c> is fed record by record through the REAL Pi dialect
/// (<see cref="PiDialect"/>, the Director's translator, with a fake clock and no process), and the session events it
/// makes are pushed to the Gateway's REAL ingest in batches, exactly as a Director would push them. What is asserted is
/// what a reader of the Gateway sees: each prompt's receipt, and every event held in order. So the Gateway is proven
/// against what Pi actually wrote, through the code that will actually translate it - not against a hand-made
/// sequence that could agree with the Gateway's own assumptions.
/// </summary>
public sealed class PiFixtureIngestTests : IDisposable
{
    private static readonly TenantId Account = new("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly DateTimeOffset RecordingStart = new(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly AsyncLocalTenantContext _ambient = new();
    private readonly HostedTenantBoundary _boundary;
    private readonly ApiModeReceiptStore _receipts;
    private readonly ApiModeEventBuffer _buffer = new();
    private readonly ApiModeEventIngest _ingest;

    public PiFixtureIngestTests()
    {
        var path = _harness.LegacyPath("devices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _boundary = new HostedTenantBoundary(_ambient, new DeviceRegistry(_harness.Open(), path), hosted: false);
        _receipts = new ApiModeReceiptStore(_harness.Open(_ambient), () => RecordingStart.UtcDateTime);
        _ingest = new ApiModeEventIngest(_buffer, _receipts, _boundary);
    }

    public void Dispose() => _harness.Dispose();

    /// <summary>The session events the real Pi dialect makes of one recording, in order - the replay the Director's own
    /// suite uses (<c>PiWireLogReplay</c>): <c>stdin</c> is a command written (prompts named prompt-1, prompt-2, ...),
    /// <c>stdout</c> a Pi record, <c>stderr</c> a diagnostic line, <c>exit</c> the process ending (a recorded -9 is the
    /// 137 .NET reports on Linux). A stdin line that is not JSON is one the dialect could never write, so it is skipped.</summary>
    private static List<SessionEvent> DialectEventsOf(string scenario, bool resuming = false)
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "pi-rpc", $"{scenario}.transcript.jsonl"));
        var argv = JsonDocument.Parse(lines[0]).RootElement.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToList();
        var clock = new FakeTimeProvider(RecordingStart);
        var dialect = new PiDialect(Sid, argv[argv.IndexOf("--session-id") + 1], resuming, clock, new SessionEventNumbering());
        var events = new List<SessionEvent>();
        var prompts = 0;
        foreach (var raw in lines.Skip(1))
        {
            var record = JsonDocument.Parse(raw).RootElement;
            clock.SetUtcNow(RecordingStart.AddMilliseconds(record.GetProperty("ms").GetInt64()));
            PiDialectStep? step = null;
            switch (record.GetProperty("dir").GetString())
            {
                case "stdin":
                    var line = record.GetProperty("line").GetString()!;
                    JsonElement command;
                    try { command = JsonDocument.Parse(line).RootElement; }
                    catch (JsonException) { continue; }
                    var type = command.GetProperty("type").GetString()!;
                    step = dialect.OnCommandWritten(new PiCommand(command.GetProperty("id").GetString()!, type, line,
                        type == "prompt" ? $"prompt-{++prompts}" : null, type == "prompt" ? command.GetProperty("message").GetString()! : null));
                    break;
                case "stdout":
                    step = dialect.OnStdoutRecord(JsonDocument.Parse(record.GetProperty("line").GetString()!).RootElement);
                    break;
                case "stderr":
                    step = dialect.OnStderrLine(record.GetProperty("line").GetString()!);
                    break;
                case "exit":
                    var code = record.GetProperty("code").GetInt32();
                    step = dialect.OnProcessExited(code == -9 ? 137 : code);
                    break;
            }
            if (step is not null) events.AddRange(step.Events);
        }
        return events;
    }

    /// <summary>Push the events as a Director would: batches of a few, in order, all in one generation.</summary>
    private void PushInBatches(IReadOnlyList<SessionEvent> events, int size = 5)
    {
        for (var i = 0; i < events.Count; i += size)
            Assert.NotNull(_ingest.Accept(Account, Director, A(events.Skip(i).Take(size).ToArray())));
    }

    private ApiModeReceiptDto Receipt(string promptId)
    {
        using (_boundary.EnterScope(Account))
            return Assert.Single(_receipts.Read(Sid), r => r.PromptId == promptId);
    }

    private static string AckLine(string scenario, int nth)
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "pi-rpc", $"{scenario}.stdout.jsonl"))
            .Where(l => l.Contains("\"command\":\"prompt\"", StringComparison.Ordinal) && l.Contains("\"success\":true", StringComparison.Ordinal))
            .ElementAt(nth);

    [Theory]
    [InlineData("plain", false)]
    [InlineData("tool", false)]
    [InlineData("resume", true)]
    public void Accept_ARecordingThatEndsDone_TheReceiptIsDeliveredWithPisOwnLineAndDone(string scenario, bool resuming)
    {
        var events = DialectEventsOf(scenario, resuming);

        PushInBatches(events);

        var r = Receipt("prompt-1");
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal("started", r.Disposition);
        Assert.Equal(AckLine(scenario, 0), r.Acknowledgement);
        Assert.Equal("done", r.TurnOutcome);
        var held = _buffer.Read(Account, Sid);
        Assert.Equal(events.Select(e => e.Sequence), held.Events.Select(e => e.Event.Sequence));
        // The real dialect numbers a generation from 1, so the Gateway holds it from the start and can say so.
        Assert.Equal(1, events[0].Sequence);
        Assert.True(held.Complete);
    }

    [Fact]
    public void Accept_TheRefusalRecording_TheSecondPromptIsRefusedByPiInItsOwnWords()
    {
        PushInBatches(DialectEventsOf("refusal"));

        var first = Receipt("prompt-1");
        Assert.Equal(ApiModeReceiptState.Delivered, first.State);
        Assert.Equal("done", first.TurnOutcome);
        var refused = Receipt("prompt-2");
        Assert.Equal(ApiModeReceiptState.Refused, refused.State);
        Assert.Equal("agent", refused.RefusedBy);
        Assert.Equal(RefusalBusy, refused.RefusalReason);
    }

    [Fact]
    public void Accept_TheExitBeforeAcknowledgementRecording_TheReceiptIsRefusedByTheDirector()
    {
        PushInBatches(DialectEventsOf("exit-before-ack"));

        var r = Receipt("prompt-1");
        Assert.Equal(ApiModeReceiptState.Refused, r.State);
        Assert.Equal("director", r.RefusedBy);
        Assert.Equal(PiDialect.ExitedBeforeAcknowledgingReason, r.RefusalReason);
        Assert.Null(r.TurnOutcome);
    }

    [Fact]
    public void Accept_TheExitMidTurnRecording_TheReceiptShowsFailedBesideDelivered()
    {
        PushInBatches(DialectEventsOf("exit"));

        var r = Receipt("prompt-1");
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal(AckLine("exit", 0), r.Acknowledgement);
        Assert.Equal("failed", r.TurnOutcome);
        Assert.Contains("137", r.TurnOutcomeReason);
        Assert.IsType<ConnectionEnded>(_buffer.Read(Account, Sid).Events[^1].Event);
    }

    [Fact]
    public void Accept_TheAbortRecording_TheReceiptShowsCancelledBesideDelivered()
    {
        PushInBatches(DialectEventsOf("abort"));

        var r = Receipt("prompt-1");
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal("cancelled", r.TurnOutcome);
        Assert.Equal(PiDialect.StoppedByUserReason, r.TurnOutcomeReason);
    }

    [Fact]
    public void Accept_EveryBatchOfARecordingSentTwice_StoresNothingTheSecondTime()
    {
        var events = DialectEventsOf("tool");
        var seen = 0;
        using var sub = _buffer.Subscribe(Account, Sid, null, _ => seen++);
        PushInBatches(events);
        var before = Receipt("prompt-1");

        PushInBatches(events, size: 3);

        Assert.Equal(events.Count, seen);
        Assert.Equal(events.Count, _buffer.Read(Account, Sid).Events.Count);
        Assert.Equal(before.UpdatedAtUtc, Receipt("prompt-1").UpdatedAtUtc);
        Assert.Equal("done", Receipt("prompt-1").TurnOutcome);
    }
}
