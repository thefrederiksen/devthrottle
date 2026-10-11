using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE FORMS OF THE HOSTED SWITCH, proved once and without the environment.
///
/// Hosted mode is a value handed to a <see cref="GatewayHost"/> (<see cref="GatewayHostOptions.Hosted"/>). A host's
/// value is read out of <c>CC_GATEWAY_HOSTED</c> in <see cref="GatewayHostOptions.FromEnvironment"/>, through
/// <see cref="GatewayHostedMode.IsHosted"/> and so through <see cref="GatewayHostedMode.IsHostedValue"/>; the only
/// other readers are the entry point, for the listen port, and the startup contract, which checks the variable is
/// set on the hosted image. The self-host controls in Gateway.Tests used to run every
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

    /// <summary>
    /// The variable's NAME is the one a hosted deployment sets. A typo in the constant would make every hosted
    /// Gateway boot self-host and nothing above would notice, because every other test spells the name through
    /// the constant.
    /// </summary>
    [Fact]
    public void The_variable_is_named_CC_GATEWAY_HOSTED()
    {
        Assert.Equal("CC_GATEWAY_HOSTED", GatewayHostedMode.HostedEnvVar);
    }

    /// <summary>
    /// The chain from the variable to a host's value, pinned without writing the variable: the options built
    /// from the environment carry exactly what the mode property reads from it, whatever this process's
    /// environment happens to hold. Before hosted mode became a value, hundreds of fixtures exercised this
    /// chain by setting the variable and building a host; none does now, so it is stated here.
    /// </summary>
    [Fact]
    public void Options_built_from_the_environment_carry_the_mode_the_property_reads()
    {
        Assert.Equal(GatewayHostedMode.IsHosted, GatewayHostOptions.FromEnvironment().Hosted);
        Assert.Equal(GatewayHostedMode.IsHostedValue(Environment.GetEnvironmentVariable(GatewayHostedMode.HostedEnvVar)),
            GatewayHostedMode.IsHosted);
    }
}
