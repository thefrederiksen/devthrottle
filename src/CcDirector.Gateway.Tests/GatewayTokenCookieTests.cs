using System;
using System.Linq;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Direct tests of the ONE cookie writer (<see cref="GatewayTokenCookie"/>) shared by BOTH the hosted mint and
/// the self-host cookie-mirror. The <c>Secure</c> flag is conditional on <see cref="GatewayHostedMode.IsHosted"/>:
///  - HOSTED is always HTTPS behind the platform front door, so the standing credential MUST be marked Secure -
///    a browser then never sends cc-gateway-token over plain HTTP.
///  - SELF-HOST is reached over loopback/tailnet HTTP, where a Secure cookie would never be sent back and would
///    strand the browser with no credential - so Secure stays OFF and the cookie survives HTTP.
///
/// The assembly runs sequentially (TestParallelization disabled), so toggling CC_GATEWAY_HOSTED here is safe; each
/// test restores the prior value.
/// </summary>
public sealed class GatewayTokenCookieTests
{
    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _prior;
        public EnvScope(string name, string? value)
        {
            _name = name;
            _prior = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _prior);
    }

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
        using var _ = new EnvScope("CC_GATEWAY_HOSTED", "1");
        var header = SetCookieHeader(hosted: true);
        Assert.True(HasAttribute(header, "secure"));
        // The rest of the agreed options are unchanged.
        Assert.True(HasAttribute(header, "httponly"));
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelfHost_DoesNotSetSecure_SoCookieSurvivesHttp()
    {
        using var _ = new EnvScope("CC_GATEWAY_HOSTED", null);
        var header = SetCookieHeader(hosted: false);
        Assert.False(HasAttribute(header, "secure"));   // must survive plain-HTTP loopback/tailnet
        // Everything else the self-host path relies on is still set.
        Assert.True(HasAttribute(header, "httponly"));
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
    }
}
