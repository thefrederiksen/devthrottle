using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// What a session key may call of the Licenses routes (License Switching): exactly the three reads, by verb. A route
/// that is mapped but missing from <see cref="SessionKeyGuard"/> answers 403 to every agent while every other test
/// stays green, so each route is named here. Switch now is the owner's own action and stays refused to a session key.
/// </summary>
public sealed class LicenseGuardTests
{
    [Theory]
    [InlineData("GET", "/gateway/licenses", true)]
    [InlineData("GET", "/gateway/licenses/", true)]
    [InlineData("GET", "/Gateway/Licenses", true)]
    [InlineData("HEAD", "/gateway/licenses", true)]
    [InlineData("GET", "/gateway/licenses/forecast", true)]
    [InlineData("GET", "/gateway/licenses/log", true)]
    [InlineData("POST", "/gateway/licenses", false)]
    [InlineData("PUT", "/gateway/licenses", false)]
    [InlineData("DELETE", "/gateway/licenses", false)]
    [InlineData("POST", "/gateway/licenses/forecast", false)]
    [InlineData("POST", "/gateway/licenses/log", false)]
    [InlineData("DELETE", "/gateway/licenses/log", false)]
    [InlineData("GET", "/gateway/licenses/log/x", false)]
    [InlineData("GET", "/gateway/licenses/accounts", false)]
    [InlineData("POST", "/gateway/licenses/switch-now", false)]
    [InlineData("GET", "/gateway/licenses/switch-now", false)]
    public void TheGuard_AllowsASessionKey_ExactlyTheLicenseReads(string method, string path, bool allowed)
    {
        Assert.Equal(allowed, SessionKeyGuard.Check(method, path).Allowed);
    }

    [Fact]
    public void TheGuard_KnowsEveryRouteTheGatewayMaps()
    {
        // The routes the endpoints map come from these same constants, so a route added there and not here fails.
        Assert.All(new[] { LicenseRoutes.Overview, LicenseRoutes.Forecast, LicenseRoutes.Log },
            route => Assert.True(SessionKeyGuard.Check("GET", route).Allowed, route));
    }

    [Fact]
    public void TheGuard_RefusesSwitchNow_EvenToARaisedSession()
    {
        Assert.False(SessionKeyGuard.Check("POST", "/gateway/licenses/switch-now", raised: true).Allowed);
    }
}
