using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using CcDirector.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tenant-boundary hardening (release 2026-07-31, the brief's item 5, second half): the shell surfaces
/// (/mobile, the legacy /m mount, the Cockpit's /assets) used to pass the auth gate BY PREFIX in every
/// HTTP method, so any future route mapped under those prefixes was silently public. The gate now passes
/// them through the explicit allowlist in <c>AuthMiddleware.IsPublicShellSurfaceRequest</c>: the GET/HEAD
/// shell and asset surfaces, and the two exact enrollment POSTs. Everything else under the prefixes is
/// credential-gated by default.
///
/// This class proves the GATED half - the requests that used to sail through by prefix and now meet the
/// credential gate - and that the gate is what changed (the same request WITH a credential reaches
/// routing and gets routing's own answer). The PUBLIC half - the shell loading, the 301 mount, both
/// enrollment POSTs reaching their endpoint - is already pinned by <see cref="MobileAuthServingTests"/>
/// and <see cref="CockpitAuthServingTests"/>, which are the other-direction controls for the revert
/// proof: they must stay green under BOTH gate shapes.
///
/// REVERT-PROVE: restore the pre-fix prefix opens in AuthMiddleware.Run (everything under /mobile, /m,
/// /assets passing in every method) and every gated-half test here goes RED - the anonymous request gets
/// routing's 404 instead of the gate's 401 - while the public-half suites stay green (the prefix opens
/// served a strict superset of the allowlist).
/// </summary>
public sealed class ShellPrefixAllowlistTests : IAsyncLifetime
{
    private const string Token = "test-token-allowlist";
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-allowlist-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Theory]
    // A non-GET request to a route nobody has written under each shell prefix: under the old prefix
    // opens these passed the gate anonymously and got routing's 404; now the gate itself answers 401.
    [InlineData("POST", "/mobile/api/future-route")]
    [InlineData("PUT", "/mobile/settings")]
    [InlineData("DELETE", "/m/anything")]
    [InlineData("POST", "/m/api/future-route")]
    [InlineData("PUT", "/assets/index-abc123.js")]
    [InlineData("POST", "/assets/upload")]
    // The enrollment allowlist is EXACT paths: a sibling path next to the real mint seam is not public.
    [InlineData("POST", "/mobile/enroll/extra")]
    [InlineData("POST", "/m/enroll/extra")]
    public async Task A_non_allowlisted_request_under_a_shell_prefix_requires_a_credential(string method, string path)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        // The GATE's own refusal: 401 with the JSON body for a non-browser caller - never routing's 404
        // (which is what the prefix opens produced) and never a serve.
        Assert.True(HttpStatusCode.Unauthorized == res.StatusCode,
            $"{method} {path}: expected the credential gate's 401 but got {(int)res.StatusCode}; body was: {body}");
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_same_non_allowlisted_request_with_a_credential_reaches_routing()
    {
        // The discriminator that makes the 401s above the GATE refusing, not a route that never existed:
        // the identical request WITH the machine token passes the gate and gets routing's own answer for
        // a route nobody has written - 404/405, and specifically NOT the gate's 401.
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mobile/api/future-route");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req);
        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.True(res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"expected routing's own 404/405 for an unmapped route, got {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Head_requests_on_the_shell_surfaces_stay_public()
    {
        // HEAD rides the GET allowlist arm (a browser or service worker may probe the shell). Not gated:
        // whatever the shell handler answers, it is never the gate's 401 and never a sign-in redirect.
        foreach (var path in new[] { "/mobile", "/mobile/signin", "/m", "/assets/index-abc123.js" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, path);
            using var res = await _http.SendAsync(req);
            Assert.True(res.StatusCode != HttpStatusCode.Unauthorized
                        && res.StatusCode != HttpStatusCode.Redirect
                        && res.StatusCode != HttpStatusCode.Found,
                $"HEAD {path} must not be auth-gated; got {(int)res.StatusCode}");
        }
    }

    [Fact]
    public async Task A_get_client_route_under_mobile_stays_public()
    {
        // The GET surface under /mobile IS the phone app (client routes are arbitrary and a signed-in
        // phone's navigations carry no credential either), so an anonymous deep-link GET is never gated.
        // 200 (shell served) or 404 (mobile app not staged into this build) both prove the gate passed it;
        // 401 or a redirect to sign-in would be the break the allowlist must not introduce.
        using var res = await _http.GetAsync("/mobile/sessions/some-session");
        Assert.True(res.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"an anonymous GET client route under /mobile must be served or absent, never gated; got {(int)res.StatusCode}");
    }
}

/// <summary>
/// The completeness guard for the shell-prefix allowlist: the allowlist in
/// <c>AuthMiddleware.IsPublicShellSurfaceRequest</c> is complete ONLY while the endpoints mapped under
/// /mobile, /m and /assets are exactly the set it was written against. This pins that set from the real
/// host's finalised route table (<see cref="GatewayHost.MappedEndpoints"/>), so mapping a NEW route under
/// a shell prefix fails this test until the route is consciously ruled - added to the allowlist if it
/// must be public, or left credential-gated and admitted here.
/// </summary>
public sealed class ShellPrefixRouteSurfaceGuardTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-routeguard-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "guard-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void The_shell_prefixes_carry_exactly_the_endpoints_the_allowlist_was_written_against()
    {
        var underShellPrefixes = _gateway.MappedEndpoints
            .OfType<RouteEndpoint>()
            .Select(e => new
            {
                Pattern = e.RoutePattern.RawText ?? "",
                Methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>(),
            })
            .Where(e => IsUnderShellPrefix(e.Pattern))
            .SelectMany(e => e.Methods.DefaultIfEmpty("ANY"), (e, m) => $"{m} {Normalize(e.Pattern)}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        // THE RULED SET. Every row here has a written verdict in the allowlist's doc comment:
        //   - the four GET shell/redirect routes are public via the GET/HEAD arm;
        //   - the two enrollment POSTs are public via the exact-path arm;
        // and NOTHING serves under /assets as an endpoint (the Cockpit assets are static-file middleware,
        // which is why the /assets arm of the allowlist has no endpoint rows to pin).
        //
        // If this assertion fails, a route was added under a shell prefix. That is not automatically a
        // defect - but it IS a ruling nobody has made yet. Decide whether the new route is public
        // (add it to AuthMiddleware.IsPublicShellSurfaceRequest with a written reason) or credential-gated
        // (the default), and only then admit it to this list.
        Assert.Equal(new[]
        {
            "GET /m",
            "GET /m/{*path}",
            "GET /mobile",
            "GET /mobile/{*path}",
            "POST /m/enroll",
            "POST /mobile/enroll",
        }, underShellPrefixes);
    }

    private static bool IsUnderShellPrefix(string pattern)
    {
        var p = "/" + pattern.TrimStart('/');
        return p.Equals("/mobile", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("/mobile/", StringComparison.OrdinalIgnoreCase)
               || p.Equals("/m", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("/m/", StringComparison.OrdinalIgnoreCase)
               || p.Equals("/assets", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
               // The chrome-less dev reports page (issue #3019) joined the public shell surfaces, so it
               // joins this guard too: nothing is mapped under /embed today - the page is served by the
               // single-page-app fallback - and a route added there later must be ruled, not inherited.
               || p.Equals("/embed", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string pattern) => "/" + pattern.TrimStart('/');
}
