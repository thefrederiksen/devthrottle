using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// <c>GET /account/trial</c> (issue #1243) driven END TO END through a REAL <see cref="GatewayHost"/> over
/// REAL HTTP, with the REAL auth middleware, the REAL tenant registry and the REAL trial ledger.
///
/// WHY END TO END AND NOT ONLY THE FOLD. <see cref="AccountTrialReadTests"/> proves the states in isolation,
/// and it would keep passing while the route was never mapped, or was mapped where the auth middleware does
/// not cover it, or resolved the caller from something other than their own device key. The defect being
/// closed is precisely that NOTHING COULD ASK - so at least one test has to actually ask, over the wire, the
/// way the website and the app will.
///
/// The trial is granted here through <see cref="Tenancy.TrialRegistry.GrantIfFirstArrival"/> - the same call
/// hosted enrolment makes, and the only place a trial is ever created - so the row these tests read is a
/// genuine trial row and not a fixture that merely resembles one.
///
/// Revert-prove: delete the <c>AccountTrialEndpoint.Map(...)</c> line in <c>GatewayHost</c> and every test
/// here goes RED with 404 - which is the defect itself, exactly as it stands on main today.
///
/// This class sets the process-wide CC_GATEWAY_HOSTED, so it belongs to the hosted-mode collection; the
/// storage root is isolated per instance for the same reason the sibling hosted classes isolate theirs.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedAccountTrialEndpointTests : IAsyncLifetime
{
    private const string Token = "test-token";

    // Unique per instance: a subject shared with another class would let whichever ran first decide whether
    // this one's account had a trial, which is the shape that makes a suite fail only when it runs whole.
    private readonly string _subjectOnTrial = "sub-on-trial-" + Guid.NewGuid().ToString("N");
    private readonly string _subjectExpired = "sub-trial-over-" + Guid.NewGuid().ToString("N");
    private readonly string _subjectNoTrial = "sub-no-trial-" + Guid.NewGuid().ToString("N");

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-trial-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _keyOnTrial = "";
    private string _keyExpired = "";
    private string _keyNoTrial = "";

    /// <summary>Authenticated, but bound to no tenant at all - the caller we cannot identify.</summary>
    private string _keyUnbound = "";

    private string? _priorHosted;
    private string? _priorRoot;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Minted through the REAL registry exactly as hosted enrolment does it, so the tenant behind each key
        // is a genuine row and the subject the trial ledger is keyed by is the one the route will resolve.
        var onTrial = _gateway.TenantRegistry.MintOrLookupBySubject(_subjectOnTrial, "new@example.com");
        var expired = _gateway.TenantRegistry.MintOrLookupBySubject(_subjectExpired, "old@example.com");
        var noTrial = _gateway.TenantRegistry.MintOrLookupBySubject(_subjectNoTrial, "paid@example.com");

        _keyOnTrial = _gateway.Devices.Register("dev-trial", "M1").DeviceKey;
        _keyExpired = _gateway.Devices.Register("dev-expired", "M2").DeviceKey;
        _keyNoTrial = _gateway.Devices.Register("dev-none", "M3").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-unbound", "M4").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-trial", _subjectOnTrial, onTrial.Value);
        _gateway.Devices.SetAccountBinding("dev-expired", _subjectExpired, expired.Value);
        _gateway.Devices.SetAccountBinding("dev-none", _subjectNoTrial, noTrial.Value);

        // The route is mapped with the REAL clock, so the fixtures are placed relative to real now. One trial
        // starting now has its full fourteen days; one granted five weeks ago is long over. The third account
        // is granted nothing at all, which is how a paying member and a pre-rollout account look.
        var now = DateTime.UtcNow;
        _gateway.TrialRegistry.GrantIfFirstArrival(_subjectOnTrial, alreadyKnownToGateway: false, now);
        _gateway.TrialRegistry.GrantIfFirstArrival(_subjectExpired, alreadyKnownToGateway: false, now.AddDays(-35));
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

    private async Task<HttpResponseMessage> Get(string deviceKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "account/trial");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return await _http.SendAsync(req);
    }

    private async Task<JsonElement> TrialFor(string deviceKey)
    {
        var resp = await Get(deviceKey);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task An_enrolled_member_on_a_running_trial_is_TOLD_SO_with_the_days_left_and_the_end_date()
    {
        // The whole issue in one assertion. Before this route existed the product granted this member fourteen
        // days of Pro and had no way to mention it: a search for a trial end, a day count, or an active flag
        // across the entire product found nothing. This is the read that was missing.
        var body = await TrialFor(_keyOnTrial);

        Assert.Equal("active", body.GetProperty("state").GetString());
        Assert.Equal(14, body.GetProperty("daysRemaining").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("endsAtUtc").ValueKind);

        var message = body.GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("14 days left", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_whose_trial_is_over_is_told_it_ENDED_and_not_that_they_never_had_one()
    {
        var body = await TrialFor(_keyExpired);

        Assert.Equal("expired", body.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("endsAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("daysRemaining").ValueKind);
    }

    [Fact]
    public async Task An_account_with_no_trial_gets_NONE_and_is_never_told_a_trial_expired()
    {
        // A paying member, or one that predates the trial, is in this state. It is a real answer - the read
        // succeeded and found nothing - and it must not borrow the expired sentence.
        var body = await TrialFor(_keyNoTrial);

        Assert.Equal("none", body.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("endsAtUtc").ValueKind);
        Assert.DoesNotContain("ended", body.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_tenant_unbound_key_is_refused_by_the_middleware_and_is_never_told_it_has_no_trial()
    {
        // WRITTEN AGAINST WHAT ACTUALLY HAPPENS, after the first version of this test asserted 403 and was
        // wrong. A key that is authenticated but bound to no tenant never reaches this endpoint at all: the
        // host-wide auth middleware refuses it with 401 first, the same way it does on /account/status. So the
        // endpoint's own deny branch is unreachable this way and is proved directly instead, below.
        //
        // What still matters here, and is the whole point of asserting a refusal rather than skipping it: a
        // consumer must not be able to read this refusal as an answer about a trial. It carries no state, no
        // dates, and nothing resembling "none" - so a page that treats any non-ok response as UNKNOWN (which
        // is what the website client does) cannot be led into printing "no trial" by this path.
        var resp = await Get(_keyUnbound);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"none\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("daysRemaining", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_endpoints_own_deny_branch_answers_UNKNOWN_and_never_no_trial()
    {
        // The branch the middleware hides from the wire, proved where it lives. A hosted request that reaches
        // the route with nothing binding it to a tenant is IGNORANCE about who is asking - so it keeps its 403
        // AND still carries a three-way state, rather than a bare error envelope a consumer would have to
        // guess about. Answering "none" here would tell somebody with twelve days left that they have nothing.
        //
        // Driven through the real fold with a real boundary over the real device registry; only the request is
        // synthetic, because a request with no authenticated device is exactly the condition under test.
        var boundary = new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
            new CcDirector.Core.Tenancy.AsyncLocalTenantContext(), _gateway.Devices, hosted: true);
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();

        var (dto, status) = await CcDirector.Gateway.Api.AccountTrialEndpoint.ResolveAsync(
            ctx, true, _gateway.TrialRegistry, boundary, _gateway.TenantRegistry, DateTime.UtcNow);

        Assert.Equal(403, status);
        Assert.Equal(CcDirector.Gateway.Contracts.AccountTrialDto.StateUnknown, dto.State);
        Assert.NotEqual(CcDirector.Gateway.Contracts.AccountTrialDto.StateNone, dto.State);
        Assert.False(string.IsNullOrWhiteSpace(dto.Message));
        Assert.Null(dto.DaysRemaining);
        Assert.Null(dto.EndsAtUtc);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused()
    {
        // Control: adding this route must not have opened a hole. The host-wide auth middleware still refuses,
        // so every branch above is only ever reached by a proven caller.
        var resp = await _http.GetAsync("account/trial");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task The_response_carries_no_credential_material()
    {
        // Control (DT-05): the caller's identity is resolved FROM the device key, so the risk is echoing it
        // back. The body is a state, a sentence and two dates - never a key and never the Gateway token.
        var body = await (await Get(_keyOnTrial)).Content.ReadAsStringAsync();

        Assert.DoesNotContain(_keyOnTrial, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, body, StringComparison.Ordinal);
        Assert.DoesNotContain(_subjectOnTrial, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_member_can_never_read_another_members_trial()
    {
        // The tenant boundary, asserted on a NEW route rather than assumed from the ones beside it. Each key
        // resolves its OWN account: the member on a live trial and the member whose trial is over must not be
        // able to see each other's state, and the route takes nothing from the request but the proven key.
        var onTrial = await TrialFor(_keyOnTrial);
        var expired = await TrialFor(_keyExpired);

        Assert.Equal("active", onTrial.GetProperty("state").GetString());
        Assert.Equal("expired", expired.GetProperty("state").GetString());
    }
}
