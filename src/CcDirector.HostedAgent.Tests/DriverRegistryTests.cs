using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using Xunit;
using CcDirector.Core.Tests.Drivers;

namespace CcDirector.HostedAgent.Tests;

/// <summary>
/// The driver registry and the per-CLI keystroke contracts the Director relies on
/// (docs/plans/director-drivers.md). Byte-level assertions: these ARE the protocol.
/// </summary>
public class DriverRegistryTests : IDisposable
{
    /// <summary>
    /// This suite asserts submit behaviour ON A MACHINE WITH MEMORY TO SPARE, which is what it has
    /// always asserted - issue #2818 left that path untouched. Pinning it is not a formality: the submit
    /// path reads the machine now, and an unpinned suite passes or fails according to how much memory
    /// the build agent happens to have free. This exact gap was found when a HostedAgent test failed on
    /// a laptop that had drifted into Tight while passing on the same code minutes earlier.
    /// </summary>
    private readonly PinnedMachineMemory _machine = PinnedMachineMemory.Healthy();

    public void Dispose() => _machine.Dispose();


    [Fact]
    public void For_ResolvesTheVerifiedDrivers()
    {
        Assert.IsType<ClaudeDriver>(AgentDrivers.For(AgentKind.ClaudeCode));
        Assert.IsType<PiDriver>(AgentDrivers.For(AgentKind.Pi));
        Assert.IsType<CursorDriver>(AgentDrivers.For(AgentKind.Cursor));
        Assert.IsType<CopilotDriver>(AgentDrivers.For(AgentKind.Copilot));
        Assert.IsType<CodexDriver>(AgentDrivers.For(AgentKind.Codex));
        Assert.IsType<GenericDriver>(AgentDrivers.For(AgentKind.Gemini));
    }

    [Fact]
    public void For_ReturnsSingletons()
    {
        Assert.Same(AgentDrivers.For(AgentKind.ClaudeCode), AgentDrivers.For(AgentKind.ClaudeCode));
        Assert.Same(AgentDrivers.For(AgentKind.Codex), AgentDrivers.For(AgentKind.Codex));
    }

    // ------------------------------------------------------------ Claude

    [Fact]
    public void ClaudeDriver_Interrupt_IsCtrlC()
    {
        var input = new ClaudeDriver().Interrupt();

        var write = Assert.Single(input.Keys).Bytes;
        Assert.Equal(new byte[] { 0x03 }, write);
    }

    [Fact]
    public void ClaudeDriver_History_IsDoubleEsc()
    {
        var input = new ClaudeDriver().ShowHistory();

        Assert.Equal(2, input.Keys.Count);
        Assert.All(input.Keys, k => Assert.Equal(new byte[] { 0x1B }, k.Bytes));
        Assert.Equal(TimeSpan.FromMilliseconds(350), input.Keys[1].PauseBefore);
    }

    [Fact]
    public void ClaudeDriver_DeclaresInterruptAndHistory()
    {
        var caps = new ClaudeDriver().Capabilities;
        Assert.True(caps.HasFlag(DriverCapabilities.Interrupt));
        Assert.True(caps.HasFlag(DriverCapabilities.History));
    }

    // ---------------------------------------------------------------- Pi

    [Fact]
    public void PiDriver_Cancel_IsEsc()
    {
        var input = new PiDriver().Cancel();

        var write = Assert.Single(input.Keys).Bytes;
        Assert.Equal(new byte[] { 0x1B }, write);
    }

    [Fact]
    public void PiDriver_Interrupt_RefusesBecauseCtrlCQuitsPi()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);

        var ex = Assert.Throws<NotSupportedException>(
            () => new PiDriver().Interrupt());
        Assert.Contains("QUITS pi", ex.Message);
        Assert.Empty(backend.RawWrites);   // nothing must reach the terminal
    }

    [Fact]
    public void PiDriver_ClearContext_AsksForSlashNew()
    {
        var input = new PiDriver().ClearContext();

        Assert.Equal("/new", input.Command);
    }

    [Fact]
    public void PiDriver_DeclaresCancelClearContextTranscriptReadContextUsageModelReportTokenUsageAndCompactContext()
    {
        var caps = new PiDriver().Capabilities;
        Assert.Equal(
            DriverCapabilities.Cancel | DriverCapabilities.ClearContext | DriverCapabilities.TranscriptRead
            | DriverCapabilities.ContextUsage | DriverCapabilities.ModelReport | DriverCapabilities.TokenUsage
            | DriverCapabilities.CompactContext,
            caps);
    }

    // ------------------------------------------------------------ Codex

    [Fact]
    public async Task CodexDriver_ReproducesVerifiedControlBytes()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);
        // Codex's submit is echo-verified (TerminalSubmit): type the text, wait for the composer to
        // echo it back, then a SEPARATE Enter. Simulate the TUI echoing typed characters so the
        // verified submit completes instead of timing out.
        backend.OnRawWrite = bytes => backend.EmitOutput(System.Text.Encoding.UTF8.GetString(bytes));
        var driver = new CodexDriver();

        await driver.SubmitAsync(backend, "hello");

        Assert.Equal(new byte[] { 0x1B }, Assert.Single(driver.Cancel().Keys).Bytes);      // Cancel = Esc
        Assert.Equal(new byte[] { 0x03 }, Assert.Single(driver.Interrupt().Keys).Bytes);   // Interrupt = Ctrl+C
        Assert.Equal("/clear", driver.ClearContext().Command);                             // ClearContext = the /clear command
        // SubmitAsync types the text then Enter as separate raw writes (not a blind SendText).
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(backend.RawWrites[0]));
        Assert.Equal(new byte[] { 0x0D }, backend.RawWrites[1]);
    }

    [Fact]
    public void CodexDriver_DeclaresCancelInterruptClearContextUsageModelReportAndCompactContext()
    {
        var caps = new CodexDriver().Capabilities;

        // CompactContext but NOT CompactCompletionReport - codex's records are not readable by the
        // Director, so it can start a compaction but cannot observe one finishing (see CodexDriver).
        Assert.Equal(
            DriverCapabilities.Cancel | DriverCapabilities.Interrupt | DriverCapabilities.ClearContext
            | DriverCapabilities.ContextUsage | DriverCapabilities.ModelReport
            | DriverCapabilities.CompactContext,
            caps);
    }

    // ------------------------------------------------------------ Generic

    [Fact]
    public async Task GenericDriver_ReproducesPreDriverBytes()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);
        backend.OnRawWrite = bytes =>
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (text == "hello")
                backend.EmitOutput(text);
        };
        var driver = new GenericDriver(AgentKind.Gemini);

        await driver.SubmitAsync(backend, "hello");

        Assert.Equal(new byte[] { 0x1B }, Assert.Single(driver.Cancel().Keys).Bytes);
        Assert.Equal(new byte[] { 0x03 }, Assert.Single(driver.Interrupt().Keys).Bytes);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(backend.RawWrites[0]));
        Assert.Equal(new byte[] { 0x0D }, backend.RawWrites[1]);
        Assert.Empty(backend.SentTexts);
    }

    [Fact]
    public void GenericDriver_UndeclaredVerbs_FailLoud()
    {
        var driver = new GenericDriver(AgentKind.Gemini);

        Assert.Equal(DriverCapabilities.Cancel | DriverCapabilities.Interrupt, driver.Capabilities);
        Assert.Throws<NotSupportedException>(() => driver.ShowHistory());
        Assert.Throws<NotSupportedException>(() => driver.ReadWidgets("x", "y"));
        Assert.Throws<NotSupportedException>(() => driver.BuildLaunchSpec(null, null));
    }

    // ------------------------------------------------------------ Cursor (issue #517)

    [Fact]
    public void CursorDriver_Interrupt_IsCtrlC()
    {
        var input = new CursorDriver().Interrupt();

        var write = Assert.Single(input.Keys).Bytes;
        Assert.Equal(new byte[] { 0x03 }, write);
    }

    [Fact]
    public async Task CursorDriver_Submit_IsBlind()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);
        backend.OnRawWrite = bytes =>
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (text == "do the thing")
                backend.EmitOutput(text);
        };

        await new CursorDriver().SubmitAsync(backend, "do the thing");

        Assert.Equal(2, backend.RawWrites.Count);
        Assert.Equal("do the thing", System.Text.Encoding.UTF8.GetString(backend.RawWrites[0]));
        Assert.Equal(new byte[] { 0x0D }, backend.RawWrites[1]);
        Assert.Empty(backend.SentTexts);
    }

    [Fact]
    public void CursorDriver_DeclaresOnlyInterrupt()
    {
        // AC11 capability honesty: only Ctrl+C (Interrupt) is verified; Cursor's
        // soft-cancel/clear/history/transcript verbs are NOT advertised.
        var caps = new CursorDriver().Capabilities;

        Assert.Equal(DriverCapabilities.Interrupt, caps);
        Assert.False(caps.HasFlag(DriverCapabilities.Cancel));
        Assert.False(caps.HasFlag(DriverCapabilities.ClearContext));
        Assert.False(caps.HasFlag(DriverCapabilities.History));
        Assert.False(caps.HasFlag(DriverCapabilities.TranscriptRead));
        Assert.False(caps.HasFlag(DriverCapabilities.PreassignedSessionId));
    }

    [Fact]
    public void CursorDriver_UndeclaredVerbs_ThrowNotSupported()
    {
        // AC11: calling an unsupported verb throws rather than fabricating behavior/data.
        var driver = new CursorDriver();

        Assert.Throws<NotSupportedException>(() => driver.Cancel());
        Assert.Throws<NotSupportedException>(() => driver.ShowHistory());
        Assert.Throws<NotSupportedException>(() => driver.ClearContext());
        Assert.Throws<NotSupportedException>(() => driver.ReadWidgets("sid", "wd"));
        Assert.Throws<NotSupportedException>(() => driver.ReadUsage("sid", "wd"));
        Assert.Throws<NotSupportedException>(() => driver.ListTranscripts("wd"));
        Assert.Throws<NotSupportedException>(() => driver.BuildLaunchSpec(null, null));
        Assert.Throws<NotSupportedException>(() => driver.ResolveExecutable("cursor-agent"));
    }

    [Fact]
    public void CursorDriver_CaptureSessionId_FromSystemInitEvent()
    {
        // AC10: Cursor's session id is captured from the stream-json system/init event.
        var line = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"chat_abc123\"}";

        var id = CursorDriver.TryCaptureSessionId(line);

        Assert.Equal("chat_abc123", id);
    }

    [Fact]
    public void CursorDriver_CaptureSessionId_FromBareInitType()
    {
        var line = "{\"type\":\"init\",\"session_id\":\"chat_xyz\"}";

        Assert.Equal("chat_xyz", CursorDriver.TryCaptureSessionId(line));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"type\":\"assistant\"}")]                       // no init, no id
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\"}")]     // init but no session_id
    [InlineData("")]
    public void CursorDriver_CaptureSessionId_ReturnsNull_WhenNoIdPresent(string line)
    {
        Assert.Null(CursorDriver.TryCaptureSessionId(line));
    }

    [Fact]
    public void CursorDriver_ParseStreamLine_AssistantText_ProducesTextWidget()
    {
        var line = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hello from Cursor\"}]}}";

        var widget = CursorDriver.ParseStreamLine(line);

        Assert.NotNull(widget);
        Assert.Equal("Text", widget.Kind);
        Assert.Equal("Cursor", widget.Header);
        Assert.Equal("Hello from Cursor", widget.Content);
    }

    [Fact]
    public void CursorDriver_ParseStreamLine_ToolCallStarted_IsPending()
    {
        var line = "{\"type\":\"tool_call\",\"subtype\":\"started\",\"tool\":\"shell\",\"tool_call_id\":\"tc1\",\"command\":\"ls\"}";

        var widget = CursorDriver.ParseStreamLine(line);

        Assert.NotNull(widget);
        Assert.Equal("GenericTool", widget.Kind);
        Assert.Equal("shell", widget.Header);
        Assert.True(widget.IsPending);
        Assert.Equal("tc1", widget.ToolUseId);
    }

    [Fact]
    public void CursorDriver_ParseStreamLine_Result_ProducesTextWidget()
    {
        var line = "{\"type\":\"result\",\"result\":\"All done.\",\"is_error\":false}";

        var widget = CursorDriver.ParseStreamLine(line);

        Assert.NotNull(widget);
        Assert.Equal("Text", widget.Kind);
        Assert.Equal("All done.", widget.Content);
        Assert.False(widget.IsError);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"x\"}")]  // envelope, no widget
    [InlineData("")]
    public void CursorDriver_ParseStreamLine_ReturnsNull_ForNonWidgetLines(string line)
    {
        Assert.Null(CursorDriver.ParseStreamLine(line));
    }
}
