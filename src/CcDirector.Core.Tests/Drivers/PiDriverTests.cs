using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

public sealed class PiDriverTests
{
    [Fact]
    public void Capabilities_Default_DeclaresOnlyCancelAndClearContext()
    {
        var driver = new PiDriver();

        var capabilities = driver.Capabilities;

        Assert.True(capabilities.HasFlag(DriverCapabilities.Cancel));
        Assert.True(capabilities.HasFlag(DriverCapabilities.ClearContext));
        Assert.False(capabilities.HasFlag(DriverCapabilities.Interrupt));
        Assert.False(capabilities.HasFlag(DriverCapabilities.History));
        Assert.False(capabilities.HasFlag(DriverCapabilities.TranscriptRead));
    }

    [Fact]
    public async Task CancelAsync_Default_WritesEscapeByte()
    {
        var driver = new PiDriver();
        var backend = new RecordingSessionBackend();

        await driver.CancelAsync(backend);

        var bytes = Assert.Single(backend.WrittenBytes);
        Assert.Equal([0x1B], bytes);
    }

    [Fact]
    public async Task ClearContextAsync_Default_SubmitsNewCommand()
    {
        var driver = new PiDriver();
        var backend = new RecordingSessionBackend();

        await driver.ClearContextAsync(backend);

        Assert.Equal(["/new"], backend.SentTexts);
        Assert.Empty(backend.WrittenBytes);
    }

    [Fact]
    public async Task InterruptAsync_Default_ThrowsNotSupported()
    {
        var driver = new PiDriver();
        var backend = new RecordingSessionBackend();

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => driver.InterruptAsync(backend));

        Assert.Contains("no safe hard interrupt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShowHistoryAsync_Default_ThrowsNotSupported()
    {
        var driver = new PiDriver();
        var backend = new RecordingSessionBackend();

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => driver.ShowHistoryAsync(backend));

        Assert.Contains("tree", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SlashCommands_Default_ExposePiCommandsOnly()
    {
        var driver = new PiDriver();

        var names = driver.SlashCommands.Select(command => command.NormalizedName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("new", names);
        Assert.Contains("settings", names);
        Assert.Contains("model", names);
        Assert.Contains("scoped-models", names);
        Assert.Contains("export", names);
        Assert.DoesNotContain("permissions", names);
        Assert.DoesNotContain("output-style", names);
    }
}
