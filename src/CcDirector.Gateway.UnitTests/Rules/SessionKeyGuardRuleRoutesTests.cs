using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// A SESSION KEY MAY MANAGE RULES (Session Rules part 2, phase 2, owner decision D7). Every route
/// <c>SessionRuleEndpoints</c> maps is reachable with a session key, and nothing else under /gateway/rules is -
/// the guard stays an allow list, so a word hung off the path later is refused until somebody classifies it.
/// </summary>
public sealed class SessionKeyGuardRuleRoutesTests
{
    private const string Id = "6f1c2a7e-0d3b-4e55-9a51-3c2b8d4e7f10";

    [Theory]
    [InlineData("GET", "/gateway/rules")]
    [InlineData("POST", "/gateway/rules")]
    [InlineData("GET", "/gateway/rules/history")]
    [InlineData("GET", "/gateway/rules/" + Id)]
    [InlineData("PATCH", "/gateway/rules/" + Id)]
    [InlineData("DELETE", "/gateway/rules/" + Id)]
    [InlineData("GET", "/gateway/rules/" + Id + "/firings")]
    [InlineData("POST", "/gateway/rules/" + Id + "/promote")]
    [InlineData("GET", "/Gateway/Rules/" + Id + "/")]
    public void Check_EveryRouteTheRulesSurfaceMaps_IsAllowedToASessionKey(string method, string path)
    {
        // Act
        var verdict = SessionKeyGuard.Check(method, path);

        // Assert
        Assert.True(verdict.Allowed, $"{method} {path}: {verdict.Reason}");
        Assert.Equal(RaisedGrant.None, verdict.RaisedGrant);
    }

    [Theory]
    [InlineData("PUT", "/gateway/rules/" + Id)]
    [InlineData("DELETE", "/gateway/rules")]
    [InlineData("POST", "/gateway/rules/history")]
    [InlineData("DELETE", "/gateway/rules/history")]
    [InlineData("POST", "/gateway/rules/" + Id + "/firings")]
    [InlineData("GET", "/gateway/rules/" + Id + "/promote")]
    [InlineData("POST", "/gateway/rules/" + Id + "/demote")]
    [InlineData("GET", "/gateway/rules/" + Id + "/firings/extra")]
    [InlineData("GET", "/gateway/rulesets")]
    public void Check_AShapeTheRulesSurfaceDoesNotMap_IsRefused(string method, string path)
    {
        // Act
        var verdict = SessionKeyGuard.Check(method, path);

        // Assert
        Assert.False(verdict.Allowed, $"{method} {path} was allowed");
        Assert.NotEqual("", verdict.Reason);
    }
}
