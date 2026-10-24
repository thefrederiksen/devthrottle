using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

public sealed class PiDriverTests
{
    [Fact]
    public void Capabilities_Default_DeclaresPiTerminalAndTranscriptFeatures()
    {
        var driver = new PiDriver();

        var capabilities = driver.Capabilities;

        Assert.True(capabilities.HasFlag(DriverCapabilities.Cancel));
        Assert.True(capabilities.HasFlag(DriverCapabilities.ClearContext));
        Assert.False(capabilities.HasFlag(DriverCapabilities.Interrupt));
        Assert.False(capabilities.HasFlag(DriverCapabilities.History));
        Assert.True(capabilities.HasFlag(DriverCapabilities.TranscriptRead));
        Assert.True(capabilities.HasFlag(DriverCapabilities.TokenUsage));
    }

    [Fact]
    public void Cancel_Default_AsksForEscapeByte()
    {
        var driver = new PiDriver();

        var input = driver.Cancel();

        var bytes = Assert.Single(input.Keys).Bytes;
        Assert.Equal([0x1B], bytes);
    }

    [Fact]
    public void ClearContext_Default_AsksForNewCommand()
    {
        var driver = new PiDriver();

        var input = driver.ClearContext();

        Assert.Equal("/new", input.Command);
        Assert.Empty(input.Keys);
    }

    [Fact]
    public void Interrupt_Default_ThrowsNotSupported()
    {
        var driver = new PiDriver();

        var error = Assert.Throws<NotSupportedException>(() => driver.Interrupt());

        Assert.Contains("no safe hard interrupt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShowHistory_Default_ThrowsNotSupported()
    {
        var driver = new PiDriver();

        var error = Assert.Throws<NotSupportedException>(() => driver.ShowHistory());

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
