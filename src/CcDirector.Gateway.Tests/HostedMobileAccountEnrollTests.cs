using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP wire tests for the HOSTED human account sign-in on the phone/browser enrollment seam (mission #474):
/// <c>POST /mobile/enroll</c> on a hosted Gateway reads the human's account access token from the Authorization Bearer
/// header (a public pre-auth route, so the middleware does not pre-validate it as a device key) and MINTS a
/// tenant-scoped device key through the ONE mint <see cref="HostedEnrollmentEndpoint.Enroll"/> - never through a
/// second token path. This is the ONLY hosted human sign-in entry point (the hosted /account/sign-in-callback was
/// descoped as unreached; all hosted clients use the /device-callback -> /mobile/enroll flow).
///
/// The hosted path is selected by the INDEPENDENT hosted-mode signal (<c>CC_GATEWAY_HOSTED</c>) read directly by
/// the endpoint, NOT by whether a dependency argument was passed - so these set that variable on (the assembly
/// runs sequentially, so toggling it here is safe; restored in <see cref="Dispose"/>). A hosted Gateway mapped
/// without its mint dependencies must refuse to start (finding 2, proven by
/// <see cref="HostedMode_WithoutDependencies_RefusesToStart"/>).
///
/// Each refusal checks EVERY half - the status, no cookie, no tenant mapping, AND (mission #474 rework) that the
/// device registry is byte-for-byte UNCHANGED (no device key minted OR PERSISTED). A 401 that still persisted a
/// key would satisfy a status/cookie/tenant assertion while leaking the credential the boundary protects, so the
/// registry-unchanged assertion is stated directly (finding 1). The account token is never echoed (DT-05).
/// </summary>
public sealed class HostedMobileAccountEnrollTests : IDisposable
{
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";

    // Account subjects are Supabase auth identifiers, which are uuids - and on the production (Postgres)
    // Gateway the gateway.entitlements.subject column is a uuid mapped through Guid.ParseExact(v, "D"), so a
    // non-uuid subject could never exist there and would crash the converter. These fixtures therefore seed
    // and read canonical "D"-form uuids, exactly the shape production carries; the readable names stand in for
    // the personas the tests exercised before (alice, the attacker, two distinct accounts a/b).
    private const string SubjectAlice = "11111111-1111-4111-8111-111111111111";
    private const string SubjectA = "22222222-2222-4222-8222-222222222222";
    private const string SubjectB = "33333333-3333-4333-8333-333333333333";
    private const string SubjectAttacker = "44444444-4444-4444-8444-444444444444";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _devPath = Path.Combine(Path.GetTempPath(), $"hma-dev-{Guid.NewGuid():N}.json");
    private readonly TestEs256Key _key = new();
    private readonly string? _priorHosted;

    public HostedMobileAccountEnrollTests()
    {
        // These tests exercise the HOSTED path, which the endpoint selects on GatewayHostedMode.IsHosted read
        // directly. The assembly runs sequentially (TestParallelization disabled), so setting it here is safe;
        // Dispose restores the prior value.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        _harness.Dispose();
        _key.Dispose();
        if (File.Exists(_devPath)) File.Delete(_devPath);
    }

    /// <summary>Runs a block with hosted mode forced OFF (the self-host control), restoring the prior value after.</summary>
    private static IDisposable SelfHostMode() => new EnvScope("CC_GATEWAY_HOSTED", null);

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

    private GatewayDatabase OpenWithEntitlements(string subject, bool entitled)
    {
        var db = _harness.Open();
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS entitlements (" +
            "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
            "livemode INTEGER NULL, tier TEXT NULL)");
        if (entitled)
        {
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO entitlements (subject, status, livemode) VALUES ({0}, {1}, {2})",
                subject, EntitlementRegistry.StatusActive, true);
        }
        return db;
    }

    private void SeedEntitled(GatewayDatabase db, string subject)
    {
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("INSERT INTO entitlements (subject, status, livemode) VALUES ({0}, {1}, {2})",
            subject, EntitlementRegistry.StatusActive, true);
    }

    private (DeviceRegistry devices, TenantRegistry tenants, HostedEnrollDependencies hosted) WireHosted(GatewayDatabase db)
    {
        var devices = new DeviceRegistry(_devPath);
        var tenants = new TenantRegistry(db);
        var validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);
        // The free-trial ledger (issue #2117) rides in the bundle. These tests seed a PAID entitlement, which
        // wins outright, so the trial is never consulted and this wiring changes none of their outcomes - it
        // just keeps the bundle complete, the way the host builds it.
        var hosted = new HostedEnrollDependencies(devices, tenants, validator,
            new EntitlementRegistry(db, requireLivemode: false), new TrialRegistry(db));
        return (devices, tenants, hosted);
    }

    /// <summary>
    /// A full snapshot of the device registry's observable state - the live device count AND the persisted store
    /// file's exact bytes. A refusal that wrongly minted or PERSISTED a key changes one or both, so asserting this
    /// unchanged across a refusal is the direct proof that nothing was given away (mission #474 rework: a status +
    /// cookie + tenant proof does NOT prove no key was persisted).
    /// </summary>
    private (int count, string file) RegistrySnapshot(DeviceRegistry devices) =>
        (devices.Count, File.Exists(_devPath) ? File.ReadAllText(_devPath) : "");

    /// <summary>A stand-in enrollment service; on the hosted branch it is never called, and on the self-host
    /// control it reports NotSignedIn (no cloud account), which is enough to prove the self-host path was taken.</summary>
    private MobileDeviceEnrollmentService UnusedService() =>
        new(account: null, new DeviceRegistryClient(new HttpClient()), new DeviceRegistry(_devPath));

    private async Task<(WebApplication app, HttpClient http)> StartHostedAsync(bool hostedMode, HostedEnrollDependencies? hosted)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        MobileEnrollmentEndpoint.Map(app, hostedMode, UnusedService(), hostedDependencies: hosted);
        await app.StartAsync();

        var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    private static string? GatewayCookieValue(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        foreach (var c in cookies)
        {
            if (c.StartsWith(Util.AuthMiddleware.CookieName + "=", StringComparison.Ordinal))
            {
                var value = c.Substring(Util.AuthMiddleware.CookieName.Length + 1).Split(';')[0];
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

    private static bool GatewayCookieIsHttpOnly(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => c.StartsWith(Util.AuthMiddleware.CookieName + "=", StringComparison.Ordinal)
                            && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the response's cc-gateway-token Set-Cookie header carries the <c>Secure</c> attribute -
    /// the flag that keeps a hosted (always-HTTPS) standing credential off any plain-HTTP request.</summary>
    private static bool GatewayCookieIsSecure(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => c.StartsWith(Util.AuthMiddleware.CookieName + "=", StringComparison.Ordinal)
                            && c.Split(';').Any(a => a.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// POSTs the enrollment seam with the account token in the Bearer header and the device id in the body.
    /// Defaults to the canonical <c>/mobile/enroll</c> (re-based from /m in Phase D); <paramref name="path"/>
    /// lets the back-compat test drive the legacy <c>/m/enroll</c> alias through the SAME mint.
    /// </summary>
    private static async Task<HttpResponseMessage> PostBearerAsync(HttpClient http, string? bearer, string deviceId, string platform = "android", string path = "/mobile/enroll")
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new Dictionary<string, string?> { ["deviceId"] = deviceId, ["platform"] = platform, ["name"] = "phone" }),
        };
        if (bearer is not null) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await http.SendAsync(msg);
    }

    private string Token(string subject) => _key.Token(subject, subject + "@example.com", Audience, Issuer);

    // -------- The positive control every refusal below is measured against. --------

    [Fact]
    public async Task ValidBearerToken_ReturnsTenantScopedKey_SetsHttpOnlyCookie_NoTokenEchoed()
    {
        const string subject = SubjectAlice;
        var db = OpenWithEntitlements(subject, entitled: true);
        var (devices, tenants, hosted) = WireHosted(db);
        var token = Token(subject);
        var (app, http) = await StartHostedAsync(hostedMode: true, hosted);
        try
        {
            var resp = await PostBearerAsync(http, token, "dev-a");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var cookieKey = GatewayCookieValue(resp);
            Assert.False(string.IsNullOrEmpty(cookieKey));
            Assert.True(GatewayCookieIsHttpOnly(resp));
            // Hosted is always HTTPS behind the platform front door, so the standing credential MUST be Secure -
            // a browser then never sends cc-gateway-token over plain HTTP.
            Assert.True(GatewayCookieIsSecure(resp));
            Assert.False(string.IsNullOrEmpty(devices.TenantForKey(cookieKey!)));
            Assert.NotNull(tenants.LookupBySubject(subject));

            var body = await resp.Content.ReadAsStringAsync();
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task LegacyMEnrollAlias_MintsThroughTheSameHostedPath()
    {
        // Phase D re-based the seam to /mobile/enroll, but the Gateway keeps POST /m/enroll mapped to the
        // SAME handler so an installed phone PWA still on the previous bundle keeps enrolling. This proves
        // the legacy alias mints a tenant-scoped key identically. Unmapping /m/enroll in
        // MobileEnrollmentEndpoint.Map reddens this (fails-on-purpose proof of the back-compat route).
        const string subject = SubjectAlice;
        var db = OpenWithEntitlements(subject, entitled: true);
        var (devices, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hostedMode: true, hosted);
        try
        {
            var resp = await PostBearerAsync(http, Token(subject), "dev-a", path: "/m/enroll");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var cookieKey = GatewayCookieValue(resp);
            Assert.False(string.IsNullOrEmpty(cookieKey));
            Assert.False(string.IsNullOrEmpty(devices.TenantForKey(cookieKey!)));
            Assert.NotNull(tenants.LookupBySubject(subject));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task HostedEnrollSuccess_SetsSecureCookie()
    {
        // The hosted standing credential (cc-gateway-token) rides HTTPS behind the platform front door, so its
        // Set-Cookie on a successful hosted /mobile/enroll MUST carry Secure - proven directly off the wire header.
        // Dropping the Secure flag in GatewayTokenCookie.Set reddens this (fails-on-purpose proof).
        const string subject = SubjectAlice;
        var db = OpenWithEntitlements(subject, entitled: true);
        var (_, _, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hostedMode: true, hosted);
        try
        {
            var resp = await PostBearerAsync(http, Token(subject), "dev-a");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.True(GatewayCookieIsSecure(resp));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task TwoAccounts_GetDistinctTenants()
    {
        var db = OpenWithEntitlements(SubjectA, entitled: true);
        SeedEntitled(db, SubjectB);
        var (devices, _, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hostedMode: true, hosted);
        try
        {
            var respA = await PostBearerAsync(http, Token(SubjectA), "dev-a");
            var respB = await PostBearerAsync(http, Token(SubjectB), "dev-b");
            Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
            Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
            var tenantA = devices.TenantForKey(GatewayCookieValue(respA)!);
            var tenantB = devices.TenantForKey(GatewayCookieValue(respB)!);
            Assert.False(string.IsNullOrEmpty(tenantA));
            Assert.NotEqual(tenantA, tenantB);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    // -------- Bad tokens: each 401, NO cookie, NOTHING minted OR PERSISTED. --------

    [Fact]
    public async Task ForgedHs256Bearer_401_NoCookie_RegistryUnchanged()
    {
        var db = OpenWithEntitlements(SubjectAttacker, entitled: true);
        var (devices, tenants, hosted) = WireHosted(db);
        var forged = TestEs256Key.Hs256Token("test-signing-secret", SubjectAttacker, Audience, Issuer);
        var (app, http) = await StartHostedAsync(hostedMode: true, hosted);
        try
        {
            var before = RegistrySnapshot(devices);
            var resp = await PostBearerAsync(http, forged, "dev-a");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject(SubjectAttacker));
            Assert.Equal(before, RegistrySnapshot(devices));   // no device key minted or persisted
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task ExpiredBearer_401_NoCookie_RegistryUnchanged()
    {
        var db = OpenWithEntitlements(SubjectAlice, entitled: true);
        var (devices, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hostedMode: true, hosted);
        try
        {
            var before = RegistrySnapshot(devices);
            var resp = await PostBearerAsync(http, _key.ExpiredToken(SubjectAlice, "a@x.com", Audience, Issuer), "dev-a");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject(SubjectAlice));
            Assert.Equal(before, RegistrySnapshot(devices));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task WrongAudienceBearer_401_NoCookie_RegistryUnchanged()
    {
        var db = OpenWithEntitlements(SubjectAlice, entitled: true);
        var (devices, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hostedMode: true, hosted);
        try
        {
            var before = RegistrySnapshot(devices);
            var resp = await PostBearerAsync(http, _key.Token(SubjectAlice, "a@x.com", audience: "some-other-audience", issuer: Issuer), "dev-a");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject(SubjectAlice));
            Assert.Equal(before, RegistrySnapshot(devices));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task MissingBearer_401_NoCookie_RegistryUnchanged()
    {
        var db = OpenWithEntitlements(SubjectAlice, entitled: true);
        var (devices, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hostedMode: true, hosted);
        try
        {
            var before = RegistrySnapshot(devices);
            var resp = await PostBearerAsync(http, bearer: null, "dev-a");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject(SubjectAlice));
            Assert.Equal(before, RegistrySnapshot(devices));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    // -------- The paid gate: 402 on NotEntitled, 503-retry on Unknown, NOTHING minted OR PERSISTED on either. --------

    [Fact]
    public async Task NotEntitled_402_NoCookie_RegistryUnchanged()
    {
        var db = OpenWithEntitlements(SubjectAlice, entitled: false);
        var (devices, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hostedMode: true, hosted);
        try
        {
            // Since issue #2117 an account with no paid entitlement that is ARRIVING FOR THE FIRST TIME is
            // granted the free Pro trial and enrolls - that is the promise on the pricing page, and it is
            // proven in HostedProTrialTests. So the account that is genuinely NOT entitled, and the one this
            // test is about, is one whose trial has already ended: granted thirty days ago, so its fourteen
            // days are long gone and the trial is never re-granted.
            hosted.Trials.GrantIfFirstArrival(SubjectAlice, alreadyKnownToGateway: false, DateTime.UtcNow.AddDays(-30));

            var before = RegistrySnapshot(devices);
            var resp = await PostBearerAsync(http, Token(SubjectAlice), "dev-a");
            Assert.Equal(HttpStatusCode.PaymentRequired, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject(SubjectAlice));
            Assert.Equal(before, RegistrySnapshot(devices));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task UnknownEntitlement_503Retry_NoCookie_RegistryUnchanged()
    {
        var db = _harness.Open();   // no entitlements table -> the read fails -> Unknown
        var (devices, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hostedMode: true, hosted);
        try
        {
            var before = RegistrySnapshot(devices);
            var resp = await PostBearerAsync(http, Token(SubjectAlice), "dev-a");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.NotEqual(HttpStatusCode.PaymentRequired, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject(SubjectAlice));
            Assert.Equal(before, RegistrySnapshot(devices));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task MissingDeviceId_400_RegistryUnchanged()
    {
        var db = OpenWithEntitlements(SubjectAlice, entitled: true);
        var (devices, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hostedMode: true, hosted);
        try
        {
            var before = RegistrySnapshot(devices);
            var resp = await PostBearerAsync(http, Token(SubjectAlice), deviceId: "   ");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject(SubjectAlice));
            Assert.Equal(before, RegistrySnapshot(devices));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    // -------- Finding 2 (fail-closed): hosted mode WITHOUT the mint dependencies must refuse to start. --------

    [Fact]
    public async Task HostedMode_WithoutDependencies_RefusesToStart()
    {
        // The map-time fail-closed guard: a Gateway in hosted mode mapped without hosted enrollment dependencies
        // must THROW rather than silently fall through to the self-host device-key-in-body path (the fail-open the
        // law forbids). CC_GATEWAY_HOSTED is "1" (class ctor), so mapping with hosted=null must throw. This is the
        // finding-2 guard; deleting the guard in MobileEnrollmentEndpoint reddens this test (proven at rework).
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        try
        {
            Assert.Throws<InvalidOperationException>(() => MobileEnrollmentEndpoint.Map(app, hosted: true, UnusedService(), hostedDependencies: null));
        }
        finally { await app.DisposeAsync(); }
    }

    // -------- The self-host control, asserted POSITIVELY: the hosted mint path is gated on the hosted signal. --------

    [Fact]
    public async Task SelfHost_IgnoresBearer_TakesDeviceKeyInBodyPath_Unchanged()
    {
        // THE CONTROL, asserted positively. With hosted mode OFF, /mobile/enroll is the self-host device-key-in-body
        // path: the Bearer account token is NOT treated as an account token, no tenant-scoped mint runs, and the
        // request flows to the enrollment service. With no cloud account wired that service answers NotSignedIn
        // (409) - which is precisely NOT the hosted mint's 200/401/402/503, so it proves the hosted mint never
        // engaged on self-host.
        using var _ = SelfHostMode();
        var (app, http) = await StartHostedAsync(hostedMode: false, hosted: null);
        try
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/mobile/enroll")
            {
                Content = JsonContent.Create(new Dictionary<string, string?> { ["deviceKey"] = "dtd_some_cloud_key", ["deviceId"] = "dev-a", ["platform"] = "android" }),
            };
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(SubjectAlice));
            var resp = await http.SendAsync(msg);

            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);   // NotSignedIn from the self-host service path
            Assert.Null(GatewayCookieValue(resp));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}
