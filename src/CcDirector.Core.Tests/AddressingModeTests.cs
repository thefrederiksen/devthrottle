using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Issue #457: the fleet network addressing mode parse/format helpers. The default is
/// Tailscale; a typo never silently picks a mode (no-fallback rule).
/// </summary>
public sealed class AddressingModeTests
{
    [Theory]
    [InlineData("tailscale", AddressingMode.Tailscale)]
    [InlineData("Tailscale", AddressingMode.Tailscale)]
    [InlineData("  LAN  ", AddressingMode.Lan)]
    [InlineData("lan", AddressingMode.Lan)]
    public void Parse_RecognizedValues(string value, AddressingMode expected)
        => Assert.Equal(expected, AddressingModeExtensions.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_MissingValue_DefaultsToTailscale(string? value)
        => Assert.Equal(AddressingMode.Tailscale, AddressingModeExtensions.Parse(value));

    [Theory]
    [InlineData("wifi")]
    [InlineData("tailnet")]
    [InlineData("ipv6")]
    public void Parse_UnknownValue_Throws(string value)
        => Assert.Throws<ArgumentException>(() => AddressingModeExtensions.Parse(value));

    [Theory]
    [InlineData(AddressingMode.Tailscale, "tailscale")]
    [InlineData(AddressingMode.Lan, "lan")]
    public void ToConfigString_RoundTrips(AddressingMode mode, string expected)
    {
        Assert.Equal(expected, mode.ToConfigString());
        Assert.Equal(mode, AddressingModeExtensions.Parse(expected));
    }

    [Theory]
    [InlineData("tailscale", true)]
    [InlineData("lan", true)]
    [InlineData("", true)]      // empty is valid (means default)
    [InlineData("nope", false)]
    public void IsValid_ClassifiesInput(string value, bool expected)
        => Assert.Equal(expected, AddressingModeExtensions.IsValid(value));
}
