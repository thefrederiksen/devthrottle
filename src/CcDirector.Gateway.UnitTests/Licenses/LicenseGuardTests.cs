using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// What a session key may call of the Licenses routes (License Switching): exactly the three reads, by verb. A route
/// that is mapped but missing from <see cref="SessionKeyGuard"/> answers 403 to every agent while every other test
/// stays green, so each route is named here by hand - a new route needs a new line. Switch now is the owner's own action and stays refused to a session key.
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
    [InlineData("PUT", "/gateway/licenses/accounts/renewal", false)]
    [InlineData("GET", "/gateway/licenses/accounts/renewal", false)]
    [InlineData("POST", "/gateway/licenses/accounts/renewal", false)]
    [InlineData("POST", "/gateway/licenses/switch-now", false)]
    [InlineData("GET", "/gateway/licenses/switch-now", false)]
    public void TheGuard_AllowsASessionKey_ExactlyTheLicenseReads(string method, string path, bool allowed)
    {
        Assert.Equal(allowed, SessionKeyGuard.Check(method, path).Allowed);
    }

    [Fact]
    public void TheGuard_AllowsTheThreeReads_ByTheSameConstantsTheEndpointsMapThemWith()
    {
        // The endpoints map these three constants, so a route whose text is CHANGED cannot drift from the guard
        // unseen. This lists the three by hand: a FOURTH route mapped under a new constant fails nothing here. What
        // catches a mapped route the guard forgot is a request to it over real HTTP (LicenseHostTests), and that
        // too only for the routes it names - a new route needs its own line in both.
        Assert.All(new[] { LicenseRoutes.Overview, LicenseRoutes.Forecast, LicenseRoutes.Log },
            route => Assert.True(SessionKeyGuard.Check("GET", route).Allowed, route));
    }

    [Fact]
    public void TheGuard_RefusesTheRenewalDate_EvenToARaisedSession_ByTheConstantTheEndpointMapsItWith()
    {
        Assert.False(SessionKeyGuard.Check("PUT", LicenseRoutes.AccountRenewal).Allowed);
        Assert.False(SessionKeyGuard.Check("PUT", LicenseRoutes.AccountRenewal, raised: true).Allowed);
    }

    [Fact]
    public void TheGuard_RefusesSwitchNow_EvenToARaisedSession()
    {
        Assert.False(SessionKeyGuard.Check("POST", "/gateway/licenses/switch-now", raised: true).Allowed);
    }
}
