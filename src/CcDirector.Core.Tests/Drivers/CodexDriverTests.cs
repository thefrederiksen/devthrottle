using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

public sealed class CodexDriverTests
{
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
