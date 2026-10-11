using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP wire tests for the break-glass raw-token login pair (<see cref="GatewayLoginEndpoint"/>), boots ONLY
/// that endpoint on an ephemeral port. Proves production-readiness MH-2:
/// <list type="bullet">
/// <item>On HOSTED the whole surface is bind-broken - GET /login and POST /login (even with the correct
///   token) return 404 and NO cc-gateway-token cookie is ever written. The shared machine token
///   authenticates with no device (no tenant) and must not be mintable into a browser cookie on a
///   multi-tenant Gateway.</item>
/// <item>On SELF-HOST /login is the reachable break-glass: GET serves the form, a correct token 302-redirects
///   AND writes the cookie THROUGH the single GatewayTokenCookie helper (HttpOnly + SameSite=Lax, and - being
///   plain-HTTP loopback - not Secure so it survives), and a wrong token is 401 with no cookie.</item>
/// </list>
/// The assembly runs sequentially (parallelization disabled), so toggling CC_GATEWAY_HOSTED here is safe;
/// each test restores the prior value.
/// </summary>
public sealed class GatewayLoginEndpointTests
{
    private const string SharedToken = "shared-machine-token-mh2";

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

    private static async Task<(WebApplication app, HttpClient http)> StartAsync(bool hosted)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        GatewayLoginEndpoint.Map(app, hosted, SharedToken);
        await app.StartAsync();

        // Do NOT auto-follow redirects: the self-host success path is a 302 whose Set-Cookie we must observe.
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        var http = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    private static IEnumerable<string> SetCookies(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("Set-Cookie", out var values) ? values : Enumerable.Empty<string>();

    private static string? GatewayCookie(HttpResponseMessage resp) =>
        SetCookies(resp).FirstOrDefault(c => c.StartsWith(Util.AuthMiddleware.CookieName + "=", StringComparison.Ordinal));

    private static bool HasAttribute(string setCookie, string attribute) =>
        setCookie.Split(';').Any(a => a.Trim().Equals(attribute, StringComparison.OrdinalIgnoreCase));

    // ===== HOSTED: the whole /login surface is bind-broken (MH-2) =====

    [Fact]
    public async Task Hosted_GET_login_is_404()
    {
        using var _ = new EnvScope(GatewayHostedMode.HostedEnvVar, "1");
        var (app, http) = await StartAsync(hosted: true);
        try
        {
            var resp = await http.GetAsync("/login");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Hosted_POST_login_with_the_correct_token_is_404_and_writes_no_cookie()
    {
        using var _ = new EnvScope(GatewayHostedMode.HostedEnvVar, "1");
        var (app, http) = await StartAsync(hosted: true);
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = SharedToken });
            var resp = await http.PostAsync("/login", body);

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            // The break-glass mint is dead on hosted - not even the correct token writes the cookie.
            Assert.Null(GatewayCookie(resp));
        }
        finally { await app.StopAsync(); }
    }

    // ===== SELF-HOST: /login stays the reachable break-glass, cookie via the single helper =====

    [Fact]
    public async Task SelfHost_GET_login_serves_the_form()
    {
        using var _ = new EnvScope(GatewayHostedMode.HostedEnvVar, null);
        var (app, http) = await StartAsync(hosted: false);
        try
        {
            var resp = await http.GetAsync("/login");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task SelfHost_POST_login_with_the_correct_token_redirects_and_writes_the_cookie_through_the_helper()
    {
        using var _ = new EnvScope(GatewayHostedMode.HostedEnvVar, null);
        var (app, http) = await StartAsync(hosted: false);
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = SharedToken,
                ["next"] = "/fleet",
            });
            var resp = await http.PostAsync("/login", body);

            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);   // 302
            Assert.Equal("/fleet", resp.Headers.Location?.ToString());

            var cookie = GatewayCookie(resp);
            Assert.NotNull(cookie);
            // Routed through GatewayTokenCookie.Set: HttpOnly + SameSite=Lax on every write...
            Assert.True(HasAttribute(cookie!, "httponly"));
            Assert.Contains("samesite=lax", cookie!, StringComparison.OrdinalIgnoreCase);
            // ...and NOT Secure on self-host, so the cookie survives plain-HTTP loopback/tailnet.
            Assert.False(HasAttribute(cookie!, "secure"));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task SelfHost_POST_login_with_a_wrong_token_is_401_and_writes_no_cookie()
    {
        using var _ = new EnvScope(GatewayHostedMode.HostedEnvVar, null);
        var (app, http) = await StartAsync(hosted: false);
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "not-the-token" });
            var resp = await http.PostAsync("/login", body);

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookie(resp));
        }
        finally { await app.StopAsync(); }
    }

    // ===== /logout clears the cookie THROUGH the single helper, so the expiring header carries Secure=IsHosted =====

    [Fact]
    public async Task Hosted_GET_logout_clears_the_cookie_with_a_Secure_header()
    {
        using var _ = new EnvScope(GatewayHostedMode.HostedEnvVar, "1");
        var (app, http) = await StartAsync(hosted: true);
        try
        {
            var resp = await http.GetAsync("/logout");

            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);   // 302 back to /login
            Assert.Equal(GatewayLoginEndpoint.Path, resp.Headers.Location?.ToString());

            var cookie = GatewayCookie(resp);
            Assert.NotNull(cookie);
            // Routed through GatewayTokenCookie.Delete: the expiring Set-Cookie is Secure on hosted, so a browser
            // over HTTPS accepts the deletion and never emits the cleared cookie over plain HTTP.
            Assert.True(HasAttribute(cookie!, "secure"));
            Assert.True(HasAttribute(cookie!, "httponly"));
            Assert.Contains("samesite=lax", cookie!, StringComparison.OrdinalIgnoreCase);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task SelfHost_GET_logout_clears_the_cookie_with_an_HTTP_usable_header()
    {
        using var _ = new EnvScope(GatewayHostedMode.HostedEnvVar, null);
        var (app, http) = await StartAsync(hosted: false);
        try
        {
            var resp = await http.GetAsync("/logout");

            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);   // 302 back to /login
            Assert.Equal(GatewayLoginEndpoint.Path, resp.Headers.Location?.ToString());

            var cookie = GatewayCookie(resp);
            Assert.NotNull(cookie);
            // NOT Secure on self-host, so the expiring cookie is delivered - and the deletion takes - over the
            // plain-HTTP loopback/tailnet the self-host owner logs out on. Same HttpOnly/SameSite as the write.
            Assert.False(HasAttribute(cookie!, "secure"));
            Assert.True(HasAttribute(cookie!, "httponly"));
            Assert.Contains("samesite=lax", cookie!, StringComparison.OrdinalIgnoreCase);
        }
        finally { await app.StopAsync(); }
    }
}
