using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Issue #1856: on the HOSTED Gateway, <c>GET /account/status</c> must answer about the CALLER, not about
/// the Gateway.
///
/// The defect it closes: the endpoint asked <c>account.IsLoggedIn()</c>, which means "does THIS GATEWAY hold
/// a signed-in credential". Hosted holds none by design - it is one shared multi-tenant Gateway and identity
/// arrives per device - so a machine that had enrolled CORRECTLY (device key issued, tunnel up, roster served
/// back tenant-scoped) was told it was signed OUT. That is a product-facing lie about the one thing the user
/// had just done, and it reads as a failed setup, so people start undoing work that was correct.
///
/// THE RULE THESE TESTS EXIST TO HOLD: on hosted, <c>signedIn=false</c> is never the answer to an
/// authenticated tenant-bound caller. When the identity cannot be resolved the answer is
/// <c>signedIn=true</c> with the identity ABSENT - never a confident false. An unresolvable identity and a
/// signed-out user are different answers and must not share a code path. That branch has its own test below
/// (<see cref="Enrolled_without_a_recorded_email_is_signed_in_with_the_identity_absent"/>) because it is the
/// one most easily written as a quiet false, and it is not a rare corner: the tenant row records an email on
/// a FRESH MINT ONLY, so any older or null-email tenant reaches it.
///
/// Revert-prove, two of them, each reddening a DIFFERENT thing:
///  - Delete the hosted early return in <c>AccountStatusEndpoint.Map</c> so hosted falls through to the
///    Gateway-credential path, and both signed-in tests go RED with <c>signedIn:false</c> - the reported bug.
///  - Change the selector from <c>GatewayHostedMode.IsHosted</c> back to <c>tenantBoundary is { IsHosted: true }</c>
///    and ONLY <see cref="A_hosted_gateway_with_no_tenant_boundary_refuses_rather_than_reporting_signed_out"/>
///    goes RED, with 200 in place of 503 - the fail-open under miswiring.
/// Both were run and watched; a revert that reddens is not the same as a revert that reddens the assertion
/// under proof.
///
/// This drives a REAL GatewayHost through REAL HTTP and the REAL auth middleware, with the REAL tenant
/// registry, so the binding under test is the one enrollment actually creates. Self-host behaviour is
/// unchanged and is covered by <see cref="AccountStatusEndpointTests"/>, which maps the endpoint with no
/// boundary at all - those are the control for this change. The assembly runs sequentially, so toggling
/// CC_GATEWAY_HOSTED here is safe; it is restored in DisposeAsync.
/// </summary>
public sealed class HostedAccountStatusTests : IAsyncLifetime
{
    private const string Token = "test-token";

    // Account subjects UNIQUE to this class. They used to be the shared literals "sub-alice" and
    // "sub-bob", which several other Gateway test classes also mint - and at least one of them
    // (PromptLogTenantIsolationTests) mints "sub-bob" WITH an email. MintOrLookupBySubject records
    // an email on a FRESH MINT ONLY and returns the existing tenant otherwise, so whichever class
    // ran first decided whether this class's no-email caller actually had an email. That made
    // Enrolled_without_a_recorded_email... pass or fail purely on test ORDER (issue #1911): green
    // alone, red in a full suite run, and red or green at random in continuous integration.
    //
    // A per-instance subject cannot collide with any other class, present or future. The point of
    // this fixture is a tenant with NO email, so it must own the identity that carries that fact.
    private readonly string _subjectWithEmail = "sub-alice-" + Guid.NewGuid().ToString("N");
    private readonly string _subjectNoEmail = "sub-bob-" + Guid.NewGuid().ToString("N");

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    /// <summary>Enrolled, with an email recorded on its tenant row.</summary>
    private string _keyWithEmail = "";

    /// <summary>Enrolled and perfectly valid, but its tenant row carries no email.</summary>
    private string _keyNoEmail = "";

    /// <summary>Authenticated, but bound to no tenant at all.</summary>
    private string _keyUnbound = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-has-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private string? _priorRoot;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        // ISOLATE THE STORAGE ROOT. Without this the tenant registry mints into the RUNNING USER'S REAL
        // storage root, which is shared with every other test class in the assembly - so a subject minted
        // here with no email and the SAME subject minted elsewhere WITH one become one row, and whichever
        // class runs first decides the other's answer. That made this class fail in a full-suite run while
        // passing in isolation, which is the worst shape a test can have: it reads as a product defect.
        // It is also the same defect the mission is fixing in production - a shared root with no partition.
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Minted through the REAL registry, exactly as POST /devices/enroll-hosted does it, so the tenant
        // these keys are bound to is a genuine tenant row rather than a string invented by the test.
        var withEmail = _gateway.TenantRegistry.MintOrLookupBySubject(_subjectWithEmail, "alice@example.com");
        var noEmail = _gateway.TenantRegistry.MintOrLookupBySubject(_subjectNoEmail, null);

        _keyWithEmail = _gateway.Devices.Register("dev-alice", "MA").DeviceKey;
        _keyNoEmail = _gateway.Devices.Register("dev-bob", "MB").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-alice", _subjectWithEmail, withEmail.Value);
        _gateway.Devices.SetAccountBinding("dev-bob", _subjectNoEmail, noEmail.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task An_enrolled_hosted_caller_is_signed_in_as_itself()
    {
        var root = await StatusFor(_keyWithEmail);

        Assert.True(root.GetProperty("signedIn").GetBoolean());
        Assert.Equal("alice@example.com", root.GetProperty("email").GetString());
    }

    [Fact]
    public async Task A_hosted_gateway_says_it_holds_no_sign_in_of_its_own()
    {
        // devthrottle#3681: the Cockpit offers the Gateway's own Log out only where the Gateway says it holds a
        // sign-in. Hosted holds none (it refuses that logout, issue #984), so it must say false - present, not
        // absent, so the answer cannot be mistaken for an older Gateway that never said.
        var root = await StatusFor(_keyWithEmail);

        Assert.False(root.GetProperty("gatewaySignIn").GetBoolean());
    }

    [Fact]
    public async Task Enrolled_without_a_recorded_email_is_signed_in_with_the_identity_absent()
    {
        // The branch most likely to be quietly written as a false. This caller is fully enrolled - its key is
        // bound to a real tenant - but its tenant row has no email, which is ORDINARY: the registry records an
        // email on a fresh mint only. The honest answer is "signed in, I cannot tell you as whom", never
        // "signed out". An absent field is honest where a false one is not - the same lesson /healthz taught
        // when a zeroed fleet count read as a permanently dead fleet.
        var root = await StatusFor(_keyNoEmail);

        Assert.True(root.GetProperty("signedIn").GetBoolean());

        // The identity must be ABSENT here. This assertion reports the value it rejects, deliberately and
        // permanently - an assertion that rejects a value should say what the value was.
        //
        // It is written this way because of issue #1894. This test failed intermittently in CI while passing
        // in isolation, and the rejected value had never been printed, so its source was only ever reasoned
        // about and was never named. The value cannot come from this caller's own tenant row: the subjects
        // are GUID-unique per class instance, TenantRegistry looks up strictly on account_subject under a
        // unique index, and this subject is minted fresh with a null email. So when an email is present it
        // came from SOMEWHERE ELSE.
        //
        // If this fails again, read the message rather than re-running:
        //   "alice@example.com"  -> the leak is inside this class, and is almost certainly benign
        //   anything else        -> it crossed a class boundary, in a suite whose job is to prove that
        //                           identity never crosses a TENANT boundary
        //
        // #1894 stays open until that source is named and fixed. A subsequent green run does not discharge
        // it: a passing run with no named cause is the instrument certifying itself.
        var hasEmail = root.TryGetProperty("email", out var emailProperty);
        var hasProvider = root.TryGetProperty("provider", out var providerProperty);
        Assert.False(
            hasEmail || hasProvider,
            $"#1894 DIAGNOSTIC: expected the identity to be ABSENT for a tenant with no recorded email. " +
            $"email={(hasEmail ? $"'{emailProperty.GetString()}'" : "<absent>")}, " +
            $"provider={(hasProvider ? $"'{providerProperty.GetString()}'" : "<absent>")}. " +
            $"This subject ({_subjectNoEmail}) is unique to this test instance, so any value here came from " +
            $"outside this caller's own tenant row. Full response body: {root}");
    }

    [Fact]
    public async Task A_caller_with_no_bound_tenant_is_denied_and_never_told_it_is_signed_out()
    {
        // Deny-by-default, as on the other tenant-bearing reads. A tenant-unbound row is not a valid hosted
        // credential; answering signedIn=false would recreate the reported bug through another code path.
        var resp = await Get(_keyUnbound);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.DoesNotContain("signedIn", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        // Control: the hosted fold must not have opened the route up. Without a key the host-wide auth
        // middleware still refuses, so the branch under test is only ever reached by a proven caller.
        var resp = await _http.GetAsync("account/status");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task The_response_carries_no_credential_material()
    {
        // Control carried over from the self-host contract: the status body is a verdict and an identity, and
        // never a token. Folding it from the device key must not have put the key itself into the answer.
        var body = await (await Get(_keyWithEmail)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(_keyWithEmail, body);
        Assert.DoesNotContain(Token, body);
    }

    [Fact]
    public async Task A_hosted_gateway_with_no_tenant_boundary_refuses_rather_than_reporting_signed_out()
    {
        // The adversarial case: a hosted Gateway MISWIRED so the endpoint has no tenant boundary. The boundary
        // argument is optional, so omitting it is a one-word mistake, and selecting the hosted path on the
        // boundary itself would fail OPEN - falling through to the self-host path and answering signedIn=false
        // again. That is the very lie this endpoint exists to stop, restored by configuration rather than by
        // design, and with no test it would be invisible.
        //
        // So hosted mode is asked directly, and a hosted host with no usable boundary FAILS CLOSED. The answer
        // is "I cannot tell you", never "you are signed out". Note the account service passed here holds no
        // credential, which is exactly the condition that used to produce a confident 200 signedIn:false.
        //
        // Revert-prove: change the selector in Map back to `tenantBoundary is { IsHosted: true }` and this goes
        // RED with a 200 carrying signedIn:false - the reported bug, reached by a different route.
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        // Hosted mode is on; no boundary, no registry. The boundary parameter is required and non-nullable
        // now (finding I1-01), so the accidental version of this miswire no longer compiles - the forced
        // null below is the deliberate simulation of it, and the runtime gate must still fail closed.
        AccountStatusEndpoint.Map(app, hosted: true, account: null, tenantBoundary: null!);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var resp = await http.GetAsync("/account/status");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.DoesNotContain("signedIn", await resp.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private Task<HttpResponseMessage> Get(string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "account/status");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private async Task<JsonElement> StatusFor(string deviceKey)
    {
        var resp = await Get(deviceKey);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

}
