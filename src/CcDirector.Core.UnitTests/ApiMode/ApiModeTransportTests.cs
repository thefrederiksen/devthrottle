using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.ApiMode;
using CcDirector.TestInfrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.Tests.ApiMode;

public sealed class ApiModeTransportTests
{
    private static ApiModeProcessStartInfo PiStart() => new()
    {
        Executable = "pi",
        Arguments = ["--mode", "rpc", "--session-id", "311ffc3b-7a39-45cf-bcb5-596c494cdbbf"],
        WorkingDirectory = "/work",
        RemoveVariablePrefixes = ["CC_"],
    };

    private static (ApiModeTransport Transport, FakeApiModeProcess Process) Started(FakeApiModeProcess? process = null, TimeProvider? time = null)
    {
        process ??= new FakeApiModeProcess();
        var transport = new ApiModeTransport(process, time);
        transport.Start(PiStart());
        return (transport, process);
    }

    private static async Task<List<ApiModeTransportEvent>> ReadAllAsync(ApiModeTransport transport)
    {
        var events = new List<ApiModeTransportEvent>();
        await foreach (var item in transport.Events.ReadAllAsync())
            events.Add(item);
        return events;
    }

    private static JsonObject Command(string type, string? id = null)
    {
        var command = new JsonObject { ["type"] = type };
        if (id is not null)
            command["id"] = id;
        return command;
    }

    [Fact]
    public async Task SendRequestAsync_ResponseWithMatchingId_CompletesWithThatRecord()
    {
        // Arrange
        var (transport, process) = Started();
        process.LineWritten = _ => process.EmitOutput(
            "{\"id\":\"req-a\",\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{\"disposition\":\"started\"}}\n");

        // Act
        var response = await transport.SendRequestAsync(Command("prompt", "req-a"), CancellationToken.None);

        // Assert
        Assert.Equal("req-a", response.Id);
        Assert.True(response.AnsweredRequest);
        Assert.NotNull(response.Json);
        Assert.Equal("started", response.Json.Value.GetProperty("data").GetProperty("disposition").GetString());
        Assert.Equal("{\"type\":\"prompt\",\"id\":\"req-a\"}", Assert.Single(process.WrittenLines));
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task SendRequestAsync_ResponsesArriveInReverseOrder_EachRequestGetsItsOwn()
    {
        // Arrange
        var (transport, process) = Started();

        // Act
        var first = transport.SendRequestAsync(Command("get_state", "one"), CancellationToken.None);
        var second = transport.SendRequestAsync(Command("get_session_stats", "two"), CancellationToken.None);
        process.EmitOutput("{\"id\":\"two\",\"type\":\"response\",\"command\":\"get_session_stats\"}\n{\"id\":\"one\",\"type\":\"response\",\"command\":\"get_state\"}\n");

        // Assert
        Assert.Equal("get_state", (await first).Json?.GetProperty("command").GetString());
        Assert.Equal("get_session_stats", (await second).Json?.GetProperty("command").GetString());
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task SendRequestAsync_CommandWithoutId_AssignsAnIdOnACopy()
    {
        // Arrange
        var (transport, process) = Started();
        process.LineWritten = line =>
        {
            var id = JsonNode.Parse(line)?["id"]?.GetValue<string>();
            process.EmitOutput($"{{\"id\":\"{id}\",\"type\":\"response\"}}\n");
        };
        var command = Command("get_state");

        // Act
        var response = await transport.SendRequestAsync(command, CancellationToken.None);

        // Assert
        Assert.StartsWith("req-", response.Id);
        Assert.False(command.ContainsKey("id"));
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task SendRequestAsync_NumericId_MatchesTheNumericResponse()
    {
        // Arrange
        var (transport, process) = Started();
        process.LineWritten = _ => process.EmitOutput("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{}}\n");
        var command = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "initialize" };

        // Act
        var response = await transport.SendRequestAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal("7", response.Id);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task SendRequestAsync_Cancelled_ThrowsAndTheLateResponseStillReachesThePump()
    {
        // Arrange
        var (transport, process) = Started();
        using var cancellation = new CancellationTokenSource();
        var waiting = transport.SendRequestAsync(Command("get_state", "late"), cancellation.Token);

        // Act
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        process.EmitOutput("{\"id\":\"late\",\"type\":\"response\"}\n");
        process.Exit(0);
        var events = await ReadAllAsync(transport);

        // Assert
        var record = Assert.IsType<ApiModeRecord>(events[0]);
        Assert.Equal("late", record.Id);
        Assert.False(record.AnsweredRequest);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Events_RecordsWithoutIdOrWithAnIdNobodyAwaits_GoToThePump()
    {
        // Arrange
        var (transport, process) = Started();

        // Act
        process.EmitOutput("{\"type\":\"agent_start\"}\n{\"id\":\"nobody\",\"type\":\"response\"}\n{\"type\":\"response\",\"command\":\"parse\",\"success\":false}\n");
        process.Exit(0);
        var events = await ReadAllAsync(transport);

        // Assert
        var records = events.OfType<ApiModeRecord>().ToList();
        Assert.Equal(3, records.Count);
        Assert.Null(records[0].Id);
        Assert.Equal("nobody", records[1].Id);
        Assert.Null(records[2].Id);
        Assert.All(records, record => Assert.False(record.AnsweredRequest));
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Events_MatchedResponsesAndEvents_AreDeliveredInWireOrderThenTheEnd()
    {
        // Arrange: Pi's abort answers only after the turn settles, so the response comes after the events.
        var (transport, process) = Started();
        process.LineWritten = _ => process.EmitOutput(
            "{\"type\":\"message_end\"}\n{\"type\":\"agent_settled\",\"aborted\":true}\n{\"id\":\"stop\",\"type\":\"response\",\"command\":\"abort\",\"success\":true}\n");

        // Act
        await transport.SendRequestAsync(Command("abort", "stop"), CancellationToken.None);
        process.Exit(0);
        var events = await ReadAllAsync(transport);

        // Assert
        Assert.Equal(4, events.Count);
        var records = events.Take(3).Cast<ApiModeRecord>().ToList();
        Assert.Equal([1L, 2L, 3L], records.Select(record => record.Sequence));
        Assert.Equal(["message_end", "agent_settled", "response"], records.Select(record => record.Json?.GetProperty("type").GetString()));
        Assert.True(records[2].AnsweredRequest);
        Assert.IsType<ApiModeProcessEnded>(events[3]);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task SendRequestAsync_ProcessExitsWhileWaiting_FailsWithTheExitCodeAndTheRequest()
    {
        // Arrange
        var (transport, process) = Started();
        var waiting = transport.SendRequestAsync(Command("prompt", "req-11f17cb7"), CancellationToken.None);

        // Act
        process.Kill();
        var ex = await Assert.ThrowsAsync<ApiModeConnectionEndedException>(() => waiting);

        // Assert
        Assert.Contains("exit code 137", ex.Message);
        Assert.Contains("req-11f17cb7", ex.Message);
        Assert.Contains("prompt", ex.Message);
        Assert.Equal(137, ex.Ended.ExitCode);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task SendRequestAsync_AfterTheProcessEnded_ThrowsConnectionEnded()
    {
        // Arrange
        var (transport, process) = Started();
        process.Exit(1);
        await transport.Ended;

        // Act
        var ex = await Assert.ThrowsAsync<ApiModeConnectionEndedException>(
            () => transport.SendRequestAsync(Command("prompt"), CancellationToken.None));

        // Assert
        Assert.Contains("already ended", ex.Message);
        Assert.Contains("exit code 1", ex.Message);
        Assert.Empty(process.WrittenLines);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task WriteRecordAsync_ManyConcurrentWriters_LinesNeverInterleave()
    {
        // Arrange: the fake writes each line in two halves with a yield between.
        var (transport, process) = Started(new FakeApiModeProcess { WriteInHalves = true });

        // Act
        await Task.WhenAll(Enumerable.Range(0, 50).Select(n =>
            Task.Run(() => transport.WriteRecordAsync(new JsonObject { ["type"] = "extension_ui_response", ["id"] = $"ui-{n}", ["cancelled"] = true }, CancellationToken.None))));

        // Assert
        var lines = process.WrittenLines;
        Assert.Equal(50, lines.Count);
        Assert.All(lines, line => Assert.Equal("extension_ui_response", JsonNode.Parse(line)?["type"]?.GetValue<string>()));
        Assert.Equal(50, lines.Select(line => JsonNode.Parse(line)?["id"]?.GetValue<string>()).Distinct().Count());
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task WriteRecordAsync_TextWithLineBreaks_IsWrittenAsOneLine()
    {
        // Arrange
        var (transport, process) = Started();

        // Act
        await transport.WriteRecordAsync(new JsonObject { ["type"] = "prompt", ["message"] = "one\ntwo\r\nthree\u2028four" }, CancellationToken.None);

        // Assert
        var line = Assert.Single(process.WrittenLines);
        Assert.Equal("one\ntwo\r\nthree\u2028four", JsonNode.Parse(line)?["message"]?.GetValue<string>());
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Events_MultiByteCharacterSplitAcrossReads_DecodesTheRecordCorrectly()
    {
        // Arrange
        var (transport, process) = Started();
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"message_update\",\"delta\":\"café \U0001F9A6\"}\n");
        var split = Array.IndexOf(bytes, (byte)0xF0) + 1;

        // Act
        process.EmitOutput(bytes[..split], bytes[split..]);
        process.Exit(0);
        var events = await ReadAllAsync(transport);

        // Assert
        var record = Assert.IsType<ApiModeRecord>(events[0]);
        Assert.Equal("café \U0001F9A6", record.Json?.GetProperty("delta").GetString());
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Events_LineThatIsNotJson_IsDeliveredWithAParseError()
    {
        // Arrange
        var (transport, process) = Started();

        // Act
        process.EmitOutput("Debugger attached.\n{\"type\":\"agent_start\"}\n");
        process.Exit(0);
        var events = await ReadAllAsync(transport);

        // Assert
        var notJson = Assert.IsType<ApiModeRecord>(events[0]);
        Assert.Equal("Debugger attached.", notJson.RawLine);
        Assert.Null(notJson.Json);
        Assert.NotNull(notJson.ParseError);
        Assert.NotNull(Assert.IsType<ApiModeRecord>(events[1]).Json);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Events_EmptyLines_AreNotRecords()
    {
        // Arrange
        var (transport, process) = Started();

        // Act
        process.EmitOutput("\n\r\n{\"type\":\"agent_start\"}\n\n");
        process.Exit(0);
        var events = await ReadAllAsync(transport);

        // Assert
        Assert.Equal(2, events.Count);
        Assert.Equal(1, Assert.IsType<ApiModeRecord>(events[0]).Sequence);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Events_LineOverTheLimit_KillsTheProcessAndReportsTheFault()
    {
        // Arrange
        var process = new FakeApiModeProcess();
        var transport = new ApiModeTransport(process, options: new ApiModeTransportOptions { MaximumLineBytes = 32 });
        transport.Start(PiStart());

        // Act
        process.EmitOutput("{\"type\":\"" + new string('x', 64) + "\"}\n");
        var ended = await transport.Ended;

        // Assert
        Assert.Equal(1, process.KillCount);
        Assert.Equal(137, ended.ExitCode);
        Assert.NotNull(ended.TransportFault);
        Assert.Contains("32 bytes", ended.TransportFault);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task ErrorLinesSince_Marker_ReturnsOnlyTheLinesWrittenAfterIt()
    {
        // Arrange: the start-up warning comes before the turn, and must never be blamed for a later failure.
        var (transport, process) = Started();
        process.EmitError("Warning: No project session found with id '311ffc3b'; creating a new session with that id.");
        process.Exit(0);
        await transport.Ended;
        var marker = transport.MarkError();

        // Act
        var since = transport.ErrorLinesSince(marker);
        var all = transport.ErrorLinesSince(1);

        // Assert
        Assert.Empty(since.Lines);
        Assert.Equal(0, since.LinesNoLongerHeld);
        Assert.Contains("No project session found", Assert.Single(all.Lines).Text);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task ErrorLinesSince_MarkerAtTheSecondLine_ReturnsTheLaterLinesInOrder()
    {
        // Arrange
        var process = new FakeApiModeProcess();
        await using var transport = new ApiModeTransport(process);
        transport.Start(PiStart());
        process.EmitError("start-up line");
        process.EmitError("turn line one");
        process.EmitError("turn line two");
        process.Exit(9);
        var ended = await transport.Ended;

        // Act: a marker of 2 means "from the second line on", as MarkError returns after one line.
        var since = transport.ErrorLinesSince(2);

        // Assert
        Assert.Equal(["turn line one", "turn line two"], since.Lines.Select(line => line.Text));
        Assert.Equal(0, since.LinesNoLongerHeld);
        Assert.Equal([1L, 2L, 3L], ended.ErrorLines.Select(line => line.Sequence));
    }

    [Fact]
    public async Task ErrorLinesSince_MoreLinesThanHeld_SaysHowManyAreNoLongerHeld()
    {
        // Arrange
        var process = new FakeApiModeProcess();
        await using var transport = new ApiModeTransport(process, options: new ApiModeTransportOptions { ErrorLinesHeld = 3 });
        transport.Start(PiStart());
        var marker = transport.MarkError();
        for (var n = 1; n <= 5; n++)
            process.EmitError($"line {n}");
        process.Exit(1);
        await transport.Ended;

        // Act
        var since = transport.ErrorLinesSince(marker);

        // Assert
        Assert.Equal(["line 3", "line 4", "line 5"], since.Lines.Select(line => line.Text));
        Assert.Equal(2, since.LinesNoLongerHeld);
    }

    [Fact]
    public async Task Ended_OutputEndsMidRecord_ReportsTheUnterminatedOutput()
    {
        // Arrange
        var (transport, process) = Started();

        // Act
        process.EmitOutput("{\"type\":\"agent_start\"}\n{\"type\":\"message_up");
        process.Kill();
        var events = await ReadAllAsync(transport);

        // Assert
        Assert.Single(events.OfType<ApiModeRecord>());
        var ended = Assert.IsType<ApiModeProcessEnded>(events[^1]);
        Assert.Equal("{\"type\":\"message_up", ended.UnterminatedOutput);
        Assert.Contains("ended mid-record", ended.Describe());
        await transport.DisposeAsync();
    }

    [Fact]
    public void Start_ExecutableMissing_ThrowsNamingTheExecutable()
    {
        // Arrange
        var process = new FakeApiModeProcess { StartFailure = new ApiModeStartException("pi", "Cannot start 'pi': it was not found on the search path (PATH).") };
        var transport = new ApiModeTransport(process);

        // Act
        var ex = Assert.Throws<ApiModeStartException>(() => transport.Start(PiStart()));

        // Assert
        Assert.Equal("pi", ex.Executable);
        Assert.Contains("'pi'", ex.Message);
        Assert.False(transport.Ended.IsCompleted);
    }

    [Fact]
    public async Task Start_Twice_Throws()
    {
        // Arrange
        var (transport, _) = Started();

        // Act and assert
        Assert.Throws<InvalidOperationException>(() => transport.Start(PiStart()));
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task SendRequestAsync_BeforeStart_Throws()
    {
        // Arrange
        var transport = new ApiModeTransport(new FakeApiModeProcess());

        // Act and assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendRequestAsync(Command("prompt"), CancellationToken.None));
    }

    [Fact]
    public async Task ShutdownAsync_ProcessExitsWhenItsInputCloses_ReportsItsExitCodeWithoutKilling()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var (transport, process) = Started(time: time);

        // Act
        var ended = await transport.ShutdownAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        // Assert
        Assert.True(process.InputClosed);
        Assert.Equal(0, process.KillCount);
        Assert.Equal(0, ended.ExitCode);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task ShutdownAsync_ProcessIgnoresItsClosedInput_KillsAfterTheGraceAndReports137()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var (transport, process) = Started(new FakeApiModeProcess { ExitsWhenInputCloses = false }, time);

        // Act: the grace timer is armed before ShutdownAsync first yields.
        var shutdown = transport.ShutdownAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(shutdown.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(10));
        var ended = await shutdown;

        // Assert
        Assert.True(process.InputClosed);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(137, ended.ExitCode);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Ended_ProcessExitsButSomethingHoldsItsOutputOpen_EndsAfterTheDrainGrace()
    {
        // Arrange: a grandchild that inherited the pipes keeps standard output open after the agent exits.
        var clock = new VirtualClock(DateTimeOffset.UnixEpoch);
        var (transport, process) = Started(new FakeApiModeProcess { ExitEndsStreams = false }, clock);
        process.EmitOutput("{\"type\":\"agent_start\"}\n");

        // Act: once the watcher has armed its grace timer, move the clock past it.
        var graceArmed = clock.NextWaitStarted;
        process.Exit(0);
        await ApiModeTransportEndingTests.RunPastTheDrainGraceAsync(clock, graceArmed);
        var events = await ReadAllAsync(transport);

        // Assert
        Assert.True(transport.Ended.IsCompleted);
        Assert.IsType<ApiModeRecord>(events[0]);
        Assert.Equal(0, Assert.IsType<ApiModeProcessEnded>(events[^1]).ExitCode);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_ProcessStillRunning_KillsItAndReportsTheEnd()
    {
        // Arrange
        var (transport, process) = Started();

        // Act
        await transport.DisposeAsync();

        // Assert
        Assert.Equal(1, process.KillCount);
        Assert.Equal(137, (await transport.Ended).ExitCode);
    }
}
