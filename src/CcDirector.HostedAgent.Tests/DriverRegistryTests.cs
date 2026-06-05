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
}
