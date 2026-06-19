using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

public sealed class DriverContractTests
{
    public static IEnumerable<object[]> DriverData()
    {
        yield return [new ClaudeDriver(new EmptyTranscriptReader(), TimeSpan.Zero, TimeSpan.Zero)];
        yield return [new PiDriver()];
        yield return [new GenericDriver(AgentKind.Codex)];
    }

    [Theory]
    [MemberData(nameof(DriverData))]
    public void SlashCommands_AllDrivers_NormalizeNamesWithoutLeadingSlash(IAgentDriver driver)
    {
        foreach (var command in driver.SlashCommands)
        {
            Assert.False(string.IsNullOrWhiteSpace(command.NormalizedName));
            Assert.False(command.NormalizedName.StartsWith('/'));
        }
    }

    [Fact]
    public async Task GenericDriver_ClearContextAsync_ThrowsBecauseCapabilityIsAbsent()
    {
        var driver = new GenericDriver(AgentKind.Codex);
        var backend = new RecordingSessionBackend();

        await Assert.ThrowsAsync<NotSupportedException>(() => driver.ClearContextAsync(backend));
    }

    [Fact]
    public async Task GenericDriver_CancelAsync_WritesEscapeByte()
    {
        var driver = new GenericDriver(AgentKind.Codex);
        var backend = new RecordingSessionBackend();

        await driver.CancelAsync(backend);

        var bytes = Assert.Single(backend.WrittenBytes);
        Assert.Equal([0x1B], bytes);
    }

    [Fact]
    public async Task GenericDriver_InterruptAsync_WritesControlCByte()
    {
        var driver = new GenericDriver(AgentKind.Codex);
        var backend = new RecordingSessionBackend();

        await driver.InterruptAsync(backend);

        var bytes = Assert.Single(backend.WrittenBytes);
        Assert.Equal([0x03], bytes);
    }
}
