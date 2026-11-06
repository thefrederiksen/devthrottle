using CcDirector.Gateway.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests.Tenancy;

/// <summary>
/// The first-Director trial setting (Website Black mission): "1" is on, unset, blank or "0" is off, and anything else
/// stops the Gateway. Pure, so no host, database or clock.
/// </summary>
public sealed class FirstDirectorTrialSettingTests
{
    [Fact]
    public void Parse_One_IsOn()
    {
        Assert.True(FirstDirectorTrial.Parse("1").Enabled);
        Assert.True(FirstDirectorTrial.Parse(" 1 ").Enabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    public void Parse_UnsetBlankOrZero_IsOff(string? value)
    {
        Assert.False(FirstDirectorTrial.Parse(value).Enabled);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("2")]
    [InlineData("01")]
    public void Parse_AnythingElse_RefusesToStart(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FirstDirectorTrial.Parse(value));
        Assert.Contains(FirstDirectorTrial.EnvVar, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Off_IsTheDefault_AndTheLengthIsSevenDays()
    {
        Assert.False(FirstDirectorTrial.Off.Enabled);
        Assert.False(new GatewayHostOptions().FirstDirectorTrial.Enabled);
        Assert.Equal(TimeSpan.FromDays(7), FirstDirectorTrial.Length);
    }
}
