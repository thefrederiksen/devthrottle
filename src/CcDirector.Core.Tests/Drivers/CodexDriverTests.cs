using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

public sealed class CodexDriverTests : IDisposable
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
    public void AgentDrivers_For_Codex_ReturnsCodexDriver()
    {
        Assert.IsType<CodexDriver>(AgentDrivers.For(AgentKind.Codex));
    }

    [Fact]
    public void Capabilities_DeclareVerifiedCodexControls()
    {
        var caps = new CodexDriver().Capabilities;

        Assert.True(caps.HasFlag(DriverCapabilities.Cancel));
        Assert.True(caps.HasFlag(DriverCapabilities.Interrupt));
        Assert.True(caps.HasFlag(DriverCapabilities.ClearContext));
        Assert.False(caps.HasFlag(DriverCapabilities.History));
        Assert.False(caps.HasFlag(DriverCapabilities.PreassignedSessionId));
    }

    [Fact]
    public async Task CancelAsync_WritesEscapeByte()
    {
        var backend = new RecordingSessionBackend();

        await new CodexDriver().CancelAsync(backend);

        var bytes = Assert.Single(backend.WrittenBytes);
        Assert.Equal([0x1B], bytes);
    }

    [Fact]
    public async Task InterruptAsync_WritesControlCByte()
    {
        var backend = new RecordingSessionBackend();

        await new CodexDriver().InterruptAsync(backend);

        var bytes = Assert.Single(backend.WrittenBytes);
        Assert.Equal([0x03], bytes);
    }

    [Fact]
    public async Task SubmitAsync_EchoVerified_TypesTextThenPressesEnterSeparately()
    {
        // A buffering backend that echoes typed bytes back, like the repainting Codex composer.
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };

        await new CodexDriver().SubmitAsync(backend, "hello codex");

        // The fix: type the text, wait for the echo, THEN a SEPARATE Enter (0x0D). A blind
        // text+Enter (the old behavior) is dropped by the repainting composer.
        Assert.Equal(2, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("hello codex"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[1]);
        Assert.Empty(backend.SentTexts); // did not take the blind SendTextAsync path
    }

    [Fact]
    public async Task SubmitAsync_NoBufferBackend_WritesTextAndEnter()
    {
        // No buffer: nothing to echo-verify against, so the shared submit writes raw text then Enter.
        var backend = new RecordingSessionBackend();

        await new CodexDriver().SubmitAsync(backend, "hello");

        Assert.Equal(2, backend.WrittenBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes("hello"), backend.WrittenBytes[0]);
        Assert.Equal(new byte[] { 0x0D }, backend.WrittenBytes[1]);
        Assert.Empty(backend.SentTexts);
    }

    [Fact]
    public async Task ClearContextAsync_SubmitsSlashClear()
    {
        var backend = new RecordingSessionBackend();

        await new CodexDriver().ClearContextAsync(backend);

        Assert.Equal("/clear", Assert.Single(backend.SentTexts));
    }

    [Fact]
    public void BuildLaunchSpec_UsesBaseArgsWithoutPreassignedSession()
    {
        var spec = new CodexDriver().BuildLaunchSpec(" --sandbox danger-full-access ", "old-session");

        Assert.Equal("--sandbox danger-full-access", spec.Arguments);
        Assert.Null(spec.PreassignedSessionId);
    }

    [Fact]
    public async Task ShowHistoryAsync_ThrowsBecauseTerminalPickerIsNotVerified()
    {
        var backend = new RecordingSessionBackend();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => new CodexDriver().ShowHistoryAsync(backend));

        Assert.Contains("history tab", ex.Message);
    }
}
