using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

/// <summary>
/// Unit tests for the GitHub Copilot driver (issue #625 phase 2): the driver registry (AC4),
/// capability honesty (AC14), the launch-spec session-id preassignment (AC2/AC11), session-id
/// capture and the --output-format json JSONL transcript parse against a committed fixture (AC12).
/// </summary>
public sealed class CopilotDriverTests
{
    // ---------------------------------------------------------------- AC4 (registry)

    [Fact]
    public void AgentDrivers_For_Copilot_ReturnsCopilotDriver()
    {
        Assert.IsType<CopilotDriver>(AgentDrivers.For(AgentKind.Copilot));
    }

    // ---------------------------------------------------------------- AC14 (capability honesty)

    [Fact]
    public void Capabilities_DeclareOnlyInterruptAndPreassignedSessionId()
    {
        var caps = new CopilotDriver().Capabilities;

        Assert.True(caps.HasFlag(DriverCapabilities.Interrupt));
        Assert.True(caps.HasFlag(DriverCapabilities.PreassignedSessionId));
        Assert.False(caps.HasFlag(DriverCapabilities.Cancel));
        Assert.False(caps.HasFlag(DriverCapabilities.ClearContext));
        Assert.False(caps.HasFlag(DriverCapabilities.History));
        Assert.False(caps.HasFlag(DriverCapabilities.TranscriptRead));
    }

    [Fact]
    public async Task Interrupt_IsCtrlC()
    {
        var driver = new CopilotDriver();
        var backend = new RecordingSessionBackend();

        await driver.InterruptAsync(backend);

        var bytes = Assert.Single(backend.WrittenBytes);
        Assert.Equal(new byte[] { 0x03 }, bytes);
    }

    [Fact]
    public async Task Submit_IsBlind()
    {
        var driver = new CopilotDriver();
        var backend = new RecordingSessionBackend();

        await driver.SubmitAsync(backend, "do the thing");

        Assert.Contains("do the thing", backend.SentTexts);
    }

    [Fact]
    public async Task UndeclaredVerbs_ThrowNotSupported()
    {
        // AC14: an unsupported verb throws rather than fabricating behavior/data.
        var driver = new CopilotDriver();
        var backend = new RecordingSessionBackend();

        await Assert.ThrowsAsync<NotSupportedException>(() => driver.CancelAsync(backend));
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.ShowHistoryAsync(backend));
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.ClearContextAsync(backend));
        Assert.Throws<NotSupportedException>(() => driver.ReadWidgets("sid", "wd"));
        Assert.Throws<NotSupportedException>(() => driver.ReadUsage("sid", "wd"));
        Assert.Throws<NotSupportedException>(() => driver.ListTranscripts("wd"));
    }

    // ---------------------------------------------------------------- AC2 / AC11 (launch + id)

    [Fact]
    public void BuildLaunchSpec_NewSession_MintsSessionId()
    {
        var spec = new CopilotDriver().BuildLaunchSpec(baseArgs: null, resumeSessionId: null);

        Assert.Contains("--session-id", spec.Arguments);
        Assert.NotNull(spec.PreassignedSessionId);
        Assert.True(Guid.TryParse(spec.PreassignedSessionId, out _));
        Assert.Contains(spec.PreassignedSessionId, spec.Arguments);
    }

    [Fact]
    public void BuildLaunchSpec_Resume_EmitsResumeAndNoMintedId()
    {
        var spec = new CopilotDriver().BuildLaunchSpec(baseArgs: null, resumeSessionId: "sess-9");

        Assert.Contains("--resume sess-9", spec.Arguments);
        Assert.DoesNotContain("--session-id", spec.Arguments);
        Assert.Null(spec.PreassignedSessionId);
    }

    [Fact]
    public void TryCaptureSessionId_FromFlatSessionIdField()
    {
        var line = "{\"type\":\"session.mcp_servers_loaded\",\"sessionId\":\"abc-uuid\"}";

        Assert.Equal("abc-uuid", CopilotDriver.TryCaptureSessionId(line));
    }

    [Fact]
    public void TryCaptureSessionId_FromNestedSessionObject()
    {
        var line = "{\"type\":\"x\",\"session\":{\"id\":\"nested-uuid\"}}";

        Assert.Equal("nested-uuid", CopilotDriver.TryCaptureSessionId(line));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"type\":\"assistant.message\",\"text\":\"hi\"}")]
    [InlineData("")]
    public void TryCaptureSessionId_ReturnsNull_WhenNoIdPresent(string line)
    {
        Assert.Null(CopilotDriver.TryCaptureSessionId(line));
    }

    // ---------------------------------------------------------------- AC12 (JSONL parse)

    [Fact]
    public void ParseStreamLine_AssistantMessage_ProducesTextWidget()
    {
        var line = "{\"type\":\"assistant.message\",\"text\":\"2 plus 2 equals 4.\"}";

        var widget = CopilotDriver.ParseStreamLine(line);

        Assert.NotNull(widget);
        Assert.Equal("Text", widget.Kind);
        Assert.Equal("GitHub Copilot", widget.Header);
        Assert.Equal("2 plus 2 equals 4.", widget.Content);
    }

    [Fact]
    public void ParseStreamLine_AssistantMessageDelta_ProducesTextWidget()
    {
        var line = "{\"type\":\"assistant.message_delta\",\"delta\":\"partial \"}";

        var widget = CopilotDriver.ParseStreamLine(line);

        Assert.NotNull(widget);
        Assert.Equal("Text", widget.Kind);
        Assert.Equal("partial ", widget.Content);
    }

    [Fact]
    public void ParseStreamLine_UserMessage_ProducesUserWidget()
    {
        var line = "{\"type\":\"user.message\",\"text\":\"hello\"}";

        var widget = CopilotDriver.ParseStreamLine(line);

        Assert.NotNull(widget);
        Assert.Equal("UserMessage", widget.Kind);
        Assert.Equal("hello", widget.Content);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"type\":\"assistant.turn_start\"}")]
    [InlineData("{\"type\":\"session.tools_updated\"}")]
    [InlineData("{\"type\":\"result\",\"status\":\"success\"}")]
    [InlineData("")]
    public void ParseStreamLine_ReturnsNull_ForNonWidgetLines(string line)
    {
        Assert.Null(CopilotDriver.ParseStreamLine(line));
    }

    [Fact]
    public void ParseStreamLine_CommittedFixture_ParsesTurnIntoWidgets()
    {
        // AC12: parse a captured --output-format json event stream into TurnWidgetDto.
        // The committed fixture is one full turn: session load events, the user prompt,
        // reasoning, assistant deltas + the terminal assistant message, turn boundaries, result.
        var lines = File.ReadAllLines(FixturePath("copilot-session.jsonl"));

        var widgets = lines
            .Select(CopilotDriver.ParseStreamLine)
            .Where(w => w is not null)
            .Select(w => w!)
            .ToList();

        // The user prompt, two assistant deltas, and the terminal assistant message produce
        // widgets; the session.*, turn boundaries, reasoning, and the empty result do not.
        var user = Assert.Single(widgets, w => w.Kind == "UserMessage");
        Assert.Equal("What is 2 plus 2?", user.Content);

        var assistantWidgets = widgets.Where(w => w.Kind == "Text").ToList();
        Assert.Equal(3, assistantWidgets.Count);   // 2 deltas + 1 terminal message
        Assert.All(assistantWidgets, w => Assert.Equal("GitHub Copilot", w.Header));

        // The terminal assistant.message carries the fully assembled text.
        Assert.Contains(assistantWidgets, w => w.Content == "2 plus 2 equals 4.");

        // Session-id preassignment is echoed back in the stream (AC11).
        var capturedIds = lines
            .Select(CopilotDriver.TryCaptureSessionId)
            .Where(id => id is not null)
            .ToList();
        Assert.Contains("11111111-2222-3333-4444-555555555555", capturedIds);
    }

    private static string FixturePath(string fileName)
    {
        // TestData\** is copied to the output directory (csproj content glob), preserving the
        // TestData subfolder; fall back to the project-dir location the existing parser tests use.
        var fromOutput = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        if (File.Exists(fromOutput))
            return fromOutput;
        return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", fileName);
    }
}
