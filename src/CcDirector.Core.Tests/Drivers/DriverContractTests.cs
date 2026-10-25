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
    public void GenericDriver_ClearContext_ThrowsBecauseCapabilityIsAbsent()
    {
        var driver = new GenericDriver(AgentKind.Codex);

        Assert.Throws<NotSupportedException>(() => driver.ClearContext());
    }

    [Fact]
    public void GenericDriver_Cancel_AsksForEscapeByte()
    {
        var driver = new GenericDriver(AgentKind.Codex);

        var input = driver.Cancel();

        var bytes = Assert.Single(input.Keys).Bytes;
        Assert.Equal([0x1B], bytes);
    }

    [Fact]
    public void GenericDriver_Interrupt_AsksForControlCByte()
    {
        var driver = new GenericDriver(AgentKind.Codex);

        var input = driver.Interrupt();

        var bytes = Assert.Single(input.Keys).Bytes;
        Assert.Equal([0x03], bytes);
    }
}
