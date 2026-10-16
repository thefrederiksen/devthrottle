using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.ApiMode;
using Xunit;

namespace CcDirector.Core.Tests.ApiMode;

/// <summary>
/// The transport against Pi's real output: the recorded wire logs in TestData/pi-rpc (Pi 1.2.0, 16 October 2026),
/// replayed byte for byte through the fake process. See that folder's README for each scenario.
/// </summary>
public sealed class ApiModeTransportPiFixtureTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "pi-rpc", name);

    private static readonly ApiModeProcessStartInfo PiStart = new()
    {
        Executable = "pi",
        Arguments = ["--mode", "rpc"],
        WorkingDirectory = "/work",
    };

    private static async Task<List<ApiModeTransportEvent>> ReadAllAsync(ApiModeTransport transport)
    {
        var events = new List<ApiModeTransportEvent>();
        await foreach (var item in transport.Events.ReadAllAsync())
            events.Add(item);
        return events;
    }

    public static IEnumerable<object[]> ScenariosAndReadSizes()
    {
        foreach (var scenario in new[] { "plain", "tool", "abort", "resume", "refusal", "exit", "exit-before-ack" })
        {
            foreach (var readSize in new[] { 1, 7, 4096 })
                yield return [scenario, readSize];
        }
    }

    [Theory]
    [MemberData(nameof(ScenariosAndReadSizes))]
    public async Task Events_RealPiOutputInSmallReads_EveryLineIsOneParsedRecordInOrder(string scenario, int readSize)
    {
        // Arrange
        var bytes = await File.ReadAllBytesAsync(FixturePath($"{scenario}.stdout.jsonl"));
        var expected = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var process = new FakeApiModeProcess();
        await using var transport = new ApiModeTransport(process);
        transport.Start(PiStart);

        // Act
        for (var offset = 0; offset < bytes.Length; offset += readSize)
            process.EmitOutput(bytes[offset..Math.Min(bytes.Length, offset + readSize)]);
        process.Exit(0);
        var events = await ReadAllAsync(transport);

        // Assert
        var records = events.OfType<ApiModeRecord>().ToList();
        Assert.Equal(expected, records.Select(record => record.RawLine));
        Assert.All(records, record => Assert.Null(record.ParseError));
        Assert.All(records, record => Assert.Equal(JsonValueKind.Object, record.Json?.ValueKind));
        Assert.Equal(Enumerable.Range(1, records.Count).Select(n => (long)n), records.Select(record => record.Sequence));
        Assert.Null(Assert.IsType<ApiModeProcessEnded>(events[^1]).UnterminatedOutput);
    }

    [Theory]
    [InlineData("plain", 0, false)]
    [InlineData("tool", 0, false)]
    [InlineData("abort", 0, false)]
    [InlineData("resume", 0, false)]
    [InlineData("exit", 137, false)]
    [InlineData("exit-before-ack", 137, true)]
    public async Task SendRequestAsync_RealPiConversationReplayed_EachCommandGetsItsRecordedResponse(string scenario, int expectedExitCode, bool promptUnanswered)
    {
        // Arrange
        var replay = await PiReplay.LoadAsync(FixturePath($"{scenario}.transcript.jsonl"));
        var process = new FakeApiModeProcess();
        replay.Attach(process);
        await using var transport = new ApiModeTransport(process);
        transport.Start(PiStart);
        var pump = ReadAllAsync(transport);

        // Act: send what the recorder sent, in the same order.
        var answered = new List<string>();
        var unanswered = new List<ApiModeConnectionEndedException>();
        foreach (var line in replay.CommandLines)
        {
            var command = JsonNode.Parse(line) as JsonObject ?? throw new InvalidOperationException($"Recorded command is not an object: {line}");
            try
            {
                var response = await transport.SendRequestAsync(command, CancellationToken.None);
                answered.Add(response.Id ?? "(none)");
            }
            catch (ApiModeConnectionEndedException ex)
            {
                unanswered.Add(ex);
            }
        }
        var ended = await transport.ShutdownAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        var events = await pump;

        // Assert
        Assert.Equal(replay.CommandIds.Take(answered.Count), answered);
        Assert.Equal(replay.CommandIds.Count, answered.Count + unanswered.Count);
        Assert.Equal(replay.OutputLines, events.OfType<ApiModeRecord>().Select(record => record.RawLine));
        Assert.Equal(answered.Count, events.OfType<ApiModeRecord>().Count(record => record.AnsweredRequest));
        Assert.Equal(expectedExitCode, ended.ExitCode);
        Assert.Equal(replay.ErrorLines, ended.ErrorLines.Select(line => line.Text));
        // In 'exit' Pi acknowledged the prompt before it was killed mid-reply; in 'exit-before-ack' it never answered.
        if (promptUnanswered)
        {
            var failure = Assert.Single(unanswered);
            Assert.Contains("exit code 137", failure.Message);
            Assert.Contains("prompt", failure.Message);
        }
        else
        {
            Assert.Empty(unanswered);
        }
    }

    /// <summary>
    /// A recorded transcript turned into a script for the fake: each line written to standard input releases the
    /// standard output and error that followed it in the recording, all at once (see the fixture README for why at
    /// once). A recorded kill makes the fake exit as a .NET process reports a kill on Linux, 137; otherwise it exits
    /// when its input is closed, as Pi did.
    /// </summary>
    private sealed class PiReplay
    {
        private readonly List<List<JsonElement>> _afterCommand = [];
        private int _written;

        public List<string> CommandLines { get; } = [];
        public List<string> CommandIds { get; } = [];
        public List<string> OutputLines { get; } = [];
        public List<string> ErrorLines { get; } = [];

        public static async Task<PiReplay> LoadAsync(string path)
        {
            var replay = new PiReplay();
            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                var entry = JsonDocument.Parse(line).RootElement.Clone();
                var direction = entry.GetProperty("dir").GetString();
                switch (direction)
                {
                    case "stdin":
                        var text = entry.GetProperty("line").GetString() ?? throw new InvalidDataException("A stdin entry has no line.");
                        replay.CommandLines.Add(text);
                        replay.CommandIds.Add(JsonNode.Parse(text)?["id"]?.GetValue<string>() ?? throw new InvalidDataException($"A recorded command has no id: {text}"));
                        replay._afterCommand.Add([]);
                        break;
                    case "stdout" or "stderr" or "kill":
                        if (replay._afterCommand.Count == 0)
                            throw new InvalidDataException($"The transcript has {direction} before the first command; the replay does not script that.");
                        replay._afterCommand[^1].Add(entry);
                        if (direction == "stdout")
                            replay.OutputLines.Add(entry.GetProperty("line").GetString() ?? "");
                        if (direction == "stderr")
                            replay.ErrorLines.Add(entry.GetProperty("line").GetString() ?? "");
                        break;
                }
            }
            return replay;
        }

        public void Attach(FakeApiModeProcess process)
        {
            process.LineWritten = _ =>
            {
                var index = _written++;
                if (index >= _afterCommand.Count)
                    throw new InvalidOperationException("The test wrote more commands than the recording holds.");
                foreach (var entry in _afterCommand[index])
                {
                    switch (entry.GetProperty("dir").GetString())
                    {
                        case "stdout":
                            process.EmitOutput(entry.GetProperty("line").GetString() + "\n");
                            break;
                        case "stderr":
                            process.EmitError(entry.GetProperty("line").GetString() ?? "");
                            break;
                        case "kill":
                            process.Kill();
                            break;
                    }
                }
            };
        }
    }
}
