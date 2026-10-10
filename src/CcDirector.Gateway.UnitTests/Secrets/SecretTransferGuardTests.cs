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
    public void TheGuard_AllowsASessionKey_ExactlyTheSecretRoutes(string method, string path, bool allowed)
    {
        Assert.Equal(allowed, SessionKeyGuard.Check(method, path).Allowed);
    }
}
