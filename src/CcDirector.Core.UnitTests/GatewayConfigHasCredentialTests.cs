using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.UnitTests;

/// <summary>
/// "Has this machine signed in" is one rule, read by the error reporter and by the launcher deciding
/// whether to open the Director at sign-in to Windows (issue #3503). Both halves are needed.
/// </summary>
public sealed class GatewayConfigHasCredentialTests
{
    [Fact]
    public void HasCredential_GatewayAndToken_IsTrue()
        => Assert.True(new GatewayConfig { Url = "https://gateway.example", Token = "device-key" }.HasCredential);

    [Fact]
    public void HasCredential_NeverSignedIn_IsFalse()
        => Assert.False(new GatewayConfig().HasCredential);

    [Fact]
    public void HasCredential_GatewayWithoutToken_IsFalse()
        => Assert.False(new GatewayConfig { Url = "https://gateway.example", Token = " " }.HasCredential);

    [Fact]
    public void HasCredential_TokenWithoutGateway_IsFalse()
        => Assert.False(new GatewayConfig { Url = "", Token = "device-key" }.HasCredential);
}
