using System;
using System.Linq;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Direct tests of the ONE cookie writer (<see cref="GatewayTokenCookie"/>) shared by BOTH the hosted mint and
/// the self-host cookie-mirror. The <c>Secure</c> flag is conditional on the <c>hosted</c> argument every caller passes:
///  - HOSTED is always HTTPS behind the platform front door, so the standing credential MUST be marked Secure -
///    a browser then never sends cc-gateway-token over plain HTTP.
///  - SELF-HOST is reached over loopback/tailnet HTTP, where a Secure cookie would never be sent back and would
///    strand the browser with no credential - so Secure stays OFF and the cookie survives HTTP.
/// </summary>
public sealed class GatewayTokenCookieTests
{
    private static string SetCookieHeader(bool hosted)
    {
        var ctx = new DefaultHttpContext();
        GatewayTokenCookie.Set(ctx, "dtd_test_device_key", hosted);
        var header = ctx.Response.Headers["Set-Cookie"].FirstOrDefault(
            c => c is not null && c.StartsWith(Util.AuthMiddleware.CookieName + "=", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(header));
        return header!;
    }

    private static bool HasAttribute(string setCookie, string attribute) =>
        setCookie.Split(';').Any(a => a.Trim().Equals(attribute, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Hosted_SetsSecure()
    {
        var header = SetCookieHeader(hosted: true);
        Assert.True(HasAttribute(header, "secure"));
        // The rest of the agreed options are unchanged.
        Assert.True(HasAttribute(header, "httponly"));
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelfHost_DoesNotSetSecure_SoCookieSurvivesHttp()
    {
        var header = SetCookieHeader(hosted: false);
        Assert.False(HasAttribute(header, "secure"));   // must survive plain-HTTP loopback/tailnet
        // Everything else the self-host path relies on is still set.
        Assert.True(HasAttribute(header, "httponly"));
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
    }
}
