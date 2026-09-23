using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Issue #984, the P0: on the HOSTED Gateway, no <c>/account/*</c> route may tell a signed-in caller that
/// they are not signed in.
///
/// THE DEFECT THESE TESTS CLOSE. Every account route opened with the same three lines - read
/// <c>GetAccessTokenForForwarding()</c>, and if it is empty answer "not signed in to DevThrottle ... Sign in
/// from the Gateway tray, then retry." That conditional asks whether THIS GATEWAY holds a credential and
/// reports the answer as a statement about THE CALLER. The hosted Gateway holds none by design - it is one
/// shared multi-tenant Gateway, identity arrives per device and is bound to a tenant at enrollment, and the
/// hosted mint stores neither the access nor the refresh token - so on hosted that token is empty for EVERY
/// caller, ALWAYS. Two routes were fixed for this before (<c>/account/status</c> in #1856,
/// <c>/account/devices</c> in #2076) and three were not, which produced this, live, in one minute against
/// one Gateway with one token:
/// <code>
///   GET  /account/status   -> 200 {"signedIn":true,"email":"..."}
///   GET  /account/devices  -> 200 {"signedIn":true, ...the caller's own devices...}
///   GET  /account/credits  -> 200 {"signedIn":false,"balanceMicros":null}
///   POST /account/email    -> 401 "not signed in to DevThrottle ... Sign in from the Gateway tray"
/// </code>
///
/// The missing email was never the damage. The damage is that the product misreports its own state and then
/// instructs the user to perform the one action they have already performed and that cannot help - it cost
/// real time twice in a single day, because an agent read the message, believed it, and told the owner to
/// sign in while his sessions and schedules were running through that same Gateway. Anyone hitting it
/// concludes either that sign-in is broken or that the software does not know what it is doing, and both are
/// worse than the feature being honestly unavailable. On <c>/account/credits</c> - a BILLING surface served
/// to a paying subscriber - "not signed in, no balance" reads as "my account is gone" or "my money is gone".
///
/// THE INVARIANT UNDER PROOF, and the reason this is one class and not three:
/// <b>when <c>/account/status</c> answers <c>signedIn:true</c>, no sibling <c>/account/*</c> route may answer
/// that the caller is not signed in.</b> It is asserted end to end, in one test per route, by asking BOTH
/// endpoints with the SAME device key in the same fixture - a per-route test could pass while the pair still
/// contradicted each other, and it is the contradiction that does the harm.
///
/// Note what is NOT claimed: hosted <c>/account/email</c> still does not SEND. The cloud primitive resolves
/// the recipient from an account access token and hosted holds none, so telling the truth is the whole of
/// this change; the cloud-side sender is tracked separately in devthrottle_internal. A route that cannot do
/// the thing must say so accurately - that is the fix - and these tests assert the accuracy, not a send.
///
/// Revert-prove: delete the <c>AccountActingCredential</c> call in any one of the three endpoints and put
/// back <c>if (string.IsNullOrEmpty(account?.GetAccessTokenForForwarding()))</c> with the old message. That
/// route's test goes RED on the exact string the user was shown, and the other two stay green - so each test
/// is pinned to its own route rather than to a shared happy accident.
///
/// This drives a REAL GatewayHost over REAL HTTP through the REAL auth middleware with the REAL tenant
/// registry, so the binding under test is the one enrollment actually creates. Self-host behaviour is
/// unchanged and is covered by <see cref="AccountActingCredentialSelfHostTests"/>, the control for this
/// change. The assembly runs sequentially, so toggling CC_GATEWAY_HOSTED here is safe; it is restored in
/// DisposeAsync.
/// </summary>
public sealed class HostedAccountRoutesTellTheTruthTests : IAsyncLifetime
{
    private const string Token = "test-token";

    /// <summary>The message the user was shown, and the one no hosted route may ever produce again.</summary>
    private const string TheLie = "not signed in";

    // Unique per instance: MintOrLookupBySubject records an email on a fresh mint only and returns the
    // existing tenant otherwise, so a shared literal subject would let whichever test class ran first decide
    // this class's answers (issue #1911).
    private readonly string _subject = "sub-984-" + Guid.NewGuid().ToString("N");

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    /// <summary>An enrolled hosted caller, tenant-bound, with an email on its tenant row.</summary>
    private string _key = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-984-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private string? _priorRoot;
    private string? _priorServiceToken;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        // The assembly default equips hosted test gateways for account-attributed AI calls. This fixture proves
        // the truthful refusal when owner email has no service credential, so keep that absence local to the fixture.
        _priorServiceToken = Environment.GetEnvironmentVariable(
            CcDirector.Core.Account.AccountNotifyByTenantClient.ServiceTokenEnvVar);
        Environment.SetEnvironmentVariable(
            CcDirector.Core.Account.AccountNotifyByTenantClient.ServiceTokenEnvVar, null);

        // Isolate the storage root so the tenant registry does not mint into the running user's real root,
        // which is shared with every other class in the assembly (issue #1911's failure shape).
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Minted through the REAL registry exactly as hosted enrollment does it, so this is a genuine tenant
        // row rather than a string invented by the test.
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(_subject, "owner@example.com");
        _key = _gateway.Devices.Register("dev-984", "M984").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-984", _subject, tenant.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        Environment.SetEnvironmentVariable(
            CcDirector.Core.Account.AccountNotifyByTenantClient.ServiceTokenEnvVar, _priorServiceToken);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Status_says_signed_in_so_email_must_never_say_not_signed_in()
    {
        // THE HEADLINE INVARIANT, asserted across the pair rather than inside one route: the same caller,
        // the same key, the same Gateway, in the same test.
        var status = await Json(await Get("account/status"));
        Assert.True(status.GetProperty("signedIn").GetBoolean(),
            "precondition: this fixture's caller must be signed in, or the invariant is untested");

        var resp = await PostEmail();
        var body = await resp.Content.ReadAsStringAsync();

        Assert.False(resp.StatusCode == HttpStatusCode.Unauthorized,
            $"POST /account/email answered 401 to a caller /account/status reports as signed in. Body: {body}");
        AssertDoesNotClaimSignedOut("POST /account/email", body);
    }

    [Fact]
    public async Task The_email_refusal_names_the_signed_in_identity_and_the_real_cause()
    {
        // A message that merely stops saying the wrong thing is not enough - a user who cannot tell WHY will
        // still go looking for a sign-in to redo. So the refusal states the identity it can see, and names
        // the Gateway as the limit. Naming the identity is what makes the sentence impossible to misread.
        var error = (await Json(await PostEmail())).GetProperty("error").GetString() ?? "";

        Assert.Contains("owner@example.com", error, StringComparison.Ordinal);
        Assert.Contains("signed in", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hosted", error, StringComparison.OrdinalIgnoreCase);
        // No email went out, and the message must say so rather than leave the user guessing.
        Assert.Contains("No email was sent", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Credits_reports_the_caller_signed_in_and_says_the_money_is_untouched()
    {
        // The billing surface. This used to answer {"signedIn":false,"balanceMicros":null} to a paying
        // subscriber, which reads as "my account is gone" or "my money is gone". The honest answer separates
        // the two facts that were conflated: the caller IS signed in, and no balance is readable HERE.
        var resp = await Get("account/credits");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var root = await Json(resp);
        Assert.True(root.GetProperty("signedIn").GetBoolean(),
            "a hosted enrolled tenant is signed in; a billing surface must never be the place that denies it");
        Assert.False(root.GetProperty("balanceAvailable").GetBoolean());

        // An unavailable balance must be ABSENT, never a fabricated zero - a zero on a billing page is a
        // different false statement, not a safer one.
        var hasBalance = root.TryGetProperty("balanceMicros", out var balance)
                         && balance.ValueKind != JsonValueKind.Null;
        Assert.False(hasBalance, $"balanceMicros must be absent when no balance was read, was: {balance}");

        var message = root.GetProperty("message").GetString() ?? "";
        AssertDoesNotClaimSignedOut("GET /account/credits", message);
        Assert.Contains("unaffected", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logout_refuses_instead_of_claiming_a_sign_out_it_did_not_perform()
    {
        // On hosted there is no Gateway credential to clear, so the old 200 {"signedIn":false} was a button
        // reporting success for an action it never performed - next to a /account/status that kept saying
        // signed in. Refusing is the truthful answer, and it names the mechanism that does work.
        var resp = await _http.SendAsync(Authed(new HttpRequestMessage(HttpMethod.Post, "account/logout")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"signedIn\"", body, StringComparison.Ordinal);
        Assert.Contains("Nothing was signed out", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remove this device", body, StringComparison.OrdinalIgnoreCase);

        // And it really did not sign anything out: status is unchanged.
        Assert.True((await Json(await Get("account/status"))).GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task No_account_route_ever_tells_this_caller_to_sign_in_again()
    {
        // The sweep, in one assertion over every route in the class. "Sign in from the Gateway tray, then
        // retry" is the specific instruction that sent people to redo a completed action; on hosted there is
        // no tray to sign in from at all, so it can never be right here.
        AssertDoesNotClaimSignedOut("GET /account/status", await (await Get("account/status")).Content.ReadAsStringAsync());
        AssertDoesNotClaimSignedOut("GET /account/devices", await (await Get("account/devices")).Content.ReadAsStringAsync());
        AssertDoesNotClaimSignedOut("GET /account/credits", await (await Get("account/credits")).Content.ReadAsStringAsync());
        AssertDoesNotClaimSignedOut("POST /account/email", await (await PostEmail()).Content.ReadAsStringAsync());
        AssertDoesNotClaimSignedOut("POST /account/logout",
            await (await _http.SendAsync(Authed(new HttpRequestMessage(HttpMethod.Post, "account/logout")))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task No_refusal_carries_credential_material()
    {
        // Control (DT-05): the new messages name an identity, and an identity is not a credential. Neither
        // the caller's device key nor the Gateway token may appear in any of them.
        foreach (var body in new[]
                 {
                     await (await PostEmail()).Content.ReadAsStringAsync(),
                     await (await Get("account/credits")).Content.ReadAsStringAsync(),
                     await (await _http.SendAsync(Authed(new HttpRequestMessage(HttpMethod.Post, "account/logout")))).Content.ReadAsStringAsync(),
                 })
        {
            Assert.DoesNotContain(_key, body, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The one assertion this class exists for. It rejects both halves of the reported message: the claim
    /// itself, and the instruction that followed it. Reporting the offending body is deliberate - an
    /// assertion that rejects a value should say what the value was.
    /// </summary>
    private static void AssertDoesNotClaimSignedOut(string route, string body)
    {
        Assert.False(body.Contains(TheLie, StringComparison.OrdinalIgnoreCase),
            $"{route} told a signed-in hosted caller '{TheLie}'. That is the issue #984 defect. Body: {body}");
        Assert.False(body.Contains("Sign in from the Gateway tray", StringComparison.OrdinalIgnoreCase),
            $"{route} told a signed-in hosted caller to sign in from the Gateway tray - the action they have " +
            $"already performed, and one a hosted Gateway has no tray for. Body: {body}");
    }

    private HttpRequestMessage Authed(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return request;
    }

    private Task<HttpResponseMessage> Get(string path) =>
        _http.SendAsync(Authed(new HttpRequestMessage(HttpMethod.Get, path)));

    private Task<HttpResponseMessage> PostEmail()
    {
        var request = Authed(new HttpRequestMessage(HttpMethod.Post, "account/email"))
            ;
        request.Content = new StringContent(
            """{"subject":"issue 984 invariant","bodyText":"body"}""", Encoding.UTF8, "application/json");
        return _http.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}
