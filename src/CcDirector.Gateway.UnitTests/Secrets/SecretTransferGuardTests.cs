using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests.Secrets;

/// <summary>What a session key may call of the Secret Handoff routes (issue #2943): exactly the routes added, by verb.</summary>
public sealed class SecretTransferGuardTests
{
    [Theory]
    [InlineData("GET", "/gateway/secrets/machines", true)]
    [InlineData("POST", "/gateway/secrets/machines", false)]
    [InlineData("PUT", "/gateway/secrets/machines", false)]
    [InlineData("DELETE", "/gateway/secrets/machines", false)]
    [InlineData("GET", "/gateway/secrets/machines/x", false)]
    [InlineData("GET", "/gateway/secrets", false)]
    [InlineData("GET", "/gateway/secrets/transfers", true)]
    [InlineData("POST", "/gateway/secrets/transfers", true)]
    [InlineData("GET", "/gateway/secrets/transfers/0123456789abcdef0123456789abcdef", true)]
    [InlineData("POST", "/gateway/secrets/transfers/0123456789abcdef0123456789abcdef/answer", true)]
    [InlineData("DELETE", "/gateway/secrets/transfers/0123456789abcdef0123456789abcdef", false)]
    [InlineData("PUT", "/gateway/secrets/transfers/0123456789abcdef0123456789abcdef", false)]
    [InlineData("POST", "/gateway/secrets/transfers/0123456789abcdef0123456789abcdef", false)]
    [InlineData("GET", "/gateway/secrets/transfers/0123456789abcdef0123456789abcdef/answer", false)]
    [InlineData("POST", "/gateway/secrets/transfers/0123456789abcdef0123456789abcdef/deliver", false)]
    public void TheGuard_AllowsASessionKey_ExactlyTheSecretRoutes(string method, string path, bool allowed)
    {
        Assert.Equal(allowed, SessionKeyGuard.Check(method, path).Allowed);
    }
}
