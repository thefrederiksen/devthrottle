using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.HostedAgent.Tests;

/// <summary>
/// The driver registry and the per-CLI keystroke contracts the Director relies on
/// (docs/plans/director-drivers.md). Byte-level assertions: these ARE the protocol.
/// </summary>
public class DriverRegistryTests
{
    [Fact]
    public void For_ResolvesTheVerifiedDrivers()
    {
        Assert.IsType<ClaudeDriver>(AgentDrivers.For(AgentKind.ClaudeCode));
        Assert.IsType<PiDriver>(AgentDrivers.For(AgentKind.Pi));
        Assert.IsType<CursorDriver>(AgentDrivers.For(AgentKind.Cursor));
        Assert.IsType<GenericDriver>(AgentDrivers.For(AgentKind.Codex));
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
    public async Task ClaudeDriver_Interrupt_IsCtrlC()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);

        await new ClaudeDriver().InterruptAsync(backend);

        var write = Assert.Single(backend.RawWrites);
        Assert.Equal(new byte[] { 0x03 }, write);
    }

    [Fact]
    public async Task ClaudeDriver_History_IsDoubleEsc()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);

        await new ClaudeDriver().ShowHistoryAsync(backend);

        Assert.Equal(2, backend.RawWrites.Count);
        Assert.All(backend.RawWrites, w => Assert.Equal(new byte[] { 0x1B }, w));
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
    public async Task PiDriver_Cancel_IsEsc()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);

        await new PiDriver().CancelAsync(backend);

        var write = Assert.Single(backend.RawWrites);
        Assert.Equal(new byte[] { 0x1B }, write);
    }

    [Fact]
    public async Task PiDriver_Interrupt_RefusesBecauseCtrlCQuitsPi()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => new PiDriver().InterruptAsync(backend));
        Assert.Contains("QUITS pi", ex.Message);
        Assert.Empty(backend.RawWrites);   // nothing must reach the terminal
    }

    [Fact]
    public async Task PiDriver_ClearContext_SubmitsSlashNew()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);

        await new PiDriver().ClearContextAsync(backend);

        Assert.Contains("/new", backend.SentTexts);
    }

    [Fact]
    public void PiDriver_DeclaresOnlyCancelAndClearContext()
    {
        var caps = new PiDriver().Capabilities;
        Assert.Equal(DriverCapabilities.Cancel | DriverCapabilities.ClearContext, caps);
    }

    // ------------------------------------------------------------ Generic

    [Fact]
    public async Task GenericDriver_ReproducesPreDriverBytes()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);
        var driver = new GenericDriver(AgentKind.Codex);

        await driver.CancelAsync(backend);
        await driver.InterruptAsync(backend);
        await driver.SubmitAsync(backend, "hello");

        Assert.Equal(new byte[] { 0x1B }, backend.RawWrites[0]);
        Assert.Equal(new byte[] { 0x03 }, backend.RawWrites[1]);
        Assert.Contains("hello", backend.SentTexts);   // blind submit, no echo gate
    }

    [Fact]
    public async Task GenericDriver_UndeclaredVerbs_FailLoud()
    {
        var driver = new GenericDriver(AgentKind.Gemini);
        var backend = new FakeBackend();

        Assert.Equal(DriverCapabilities.Cancel | DriverCapabilities.Interrupt, driver.Capabilities);
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.ShowHistoryAsync(backend));
        Assert.Throws<NotSupportedException>(() => driver.ReadWidgets("x", "y"));
        Assert.Throws<NotSupportedException>(() => driver.BuildLaunchSpec(null, null));
    }

    // ------------------------------------------------------------ Cursor (issue #517)

    [Fact]
    public async Task CursorDriver_Interrupt_IsCtrlC()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);

        await new CursorDriver().InterruptAsync(backend);

        var write = Assert.Single(backend.RawWrites);
        Assert.Equal(new byte[] { 0x03 }, write);
    }

    [Fact]
    public async Task CursorDriver_Submit_IsBlind()
    {
        var backend = new FakeBackend();
        backend.Start("x", "", ".", 80, 24);

        await new CursorDriver().SubmitAsync(backend, "do the thing");

        Assert.Contains("do the thing", backend.SentTexts);
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
    public async Task CursorDriver_UndeclaredVerbs_ThrowNotSupported()
    {
        // AC11: calling an unsupported verb throws rather than fabricating behavior/data.
        var driver = new CursorDriver();
        var backend = new FakeBackend();

        await Assert.ThrowsAsync<NotSupportedException>(() => driver.CancelAsync(backend));
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.ShowHistoryAsync(backend));
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.ClearContextAsync(backend));
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
