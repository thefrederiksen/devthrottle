using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE FORMS OF THE HOSTED SWITCH, proved once and without the environment.
///
/// Hosted mode is a value handed to a <see cref="GatewayHost"/> (<see cref="GatewayHostOptions.Hosted"/>). The only
/// place that value is read out of <c>CC_GATEWAY_HOSTED</c> is <see cref="GatewayHostOptions.FromEnvironment"/>,
/// through <see cref="GatewayHostedMode.IsHostedValue"/>. The self-host controls in Gateway.Tests used to run every
/// route twice - once with the variable absent and once with it present but not "1" - to prove both non-hosted
/// forms were treated as self-host. Those forms, and the others a deployment can produce, are decided here instead,
/// against the one pure function, so no test anywhere needs to touch the process environment to prove them.
/// </summary>
public sealed class GatewayHostedModeTests
{
    [Fact]
    public void Exactly_the_string_1_means_hosted()
    {
        Assert.True(GatewayHostedMode.IsHostedValue("1"));
    }

    [Theory]
    [InlineData(null)]     // absent
    [InlineData("")]       // present, empty
    [InlineData("0")]      // present, explicitly not hosted
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData(" 1")]     // whitespace is not trimmed: the deployment sets exactly "1" or it is not hosted
    [InlineData("1 ")]
    [InlineData("01")]
    public void Every_other_value_means_self_host(string? raw)
    {
        Assert.False(GatewayHostedMode.IsHostedValue(raw));
    }
}
