using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Teams;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Tenancy;

/// <summary>
/// WHO GETS WHAT ON THE HOSTED GATEWAY AFTER THE PRICING CHANGE (mission section 7, "Gateway"; B2, B3 and B4). Free
/// keeps the Director and loses the Cockpit and the phone app; Remote and Pro have them; nobody new is given a trial;
/// an account created before launch keeps what it had until a cut-off.
///
/// NOTHING HERE HAND-BUILDS THE DECISION IT ASSERTS. Every cell is produced by the product's own path:
///  - a key is minted by POSTing to the two REAL routes - <c>/devices/enroll-hosted</c> (the Director door) and
///    <c>/mobile/enroll</c> (the Cockpit and phone door) - on a real host, with a signed account token;
///  - a request is judged by the REAL <see cref="AuthMiddleware.Run"/>, with the real device registry, tenant boundary
///    and access lease, carrying a key the mint issued (or, for an account the mint now refuses, a browser key the
///    account already held - which is exactly the key such an account still has);
///  - dictation is asked of the real <see cref="TenantAiGate"/> and answered by the real HTTP answer for it.
/// The registries read a real database: the Gateway's own migrated schema, and the payment side's table created here
/// as the seam states its columns. No Postgres, no Docker.
///
/// HOW THESE FAIL ON PURPOSE: take the remote access question out of the mint and the phone and Cockpit sign-in cells
/// of the Free rows go from 402 to 200; take it out of the lease's answer and the Cockpit request cells do; put the
/// trial grant back and the new-account row finds a ledger row; give free an artificial-intelligence scope and the
/// dictation cells go to 200.
/// </summary>
public sealed class RemoteAccessMatrixTests : IAsyncLifetime
{
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";
    private const string Subject = "sub-the-account-under-test";

    // Test instants only: the product holds no date. An account is "created before launch" by having its tenant row
    // dated before Launch, and "created after launch" by being minted now.
    private static readonly DateTime Launch = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeLaunch = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CutOffPassed = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    // Every date a test writes is fixed. A paid period and a running trial end here, far past any day this runs on.
    private static readonly DateTime FarFuture = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly PreLaunchGrace LaunchedNoCutOff = new(Launch, CutOffUtc: null);
    private static readonly PreLaunchGrace LaunchedCutOffPassed = new(Launch, CutOffPassed);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TestEs256Key _key = new();
    private readonly RecordingRevoker _revoker = new();
    private GatewayDatabase _db = null!;
    private TenantRegistry _tenants = null!;
    private TrialRegistry _trials = null!;
    private EntitlementRegistry _entitlements = null!;
    private DeviceRegistry _devices = null!;
    private HostedAccessLeaseService _leases = null!;
    private TenantAiGate _aiGate = null!;
    private AuthMiddleware.RequireToken _auth = null!;
    private TeamRegistry _teamRegistry = null!;
    private WebApplication? _app;
    private HttpClient? _http;

    public Task InitializeAsync()
    {
        _db = _harness.Open();
        using (var ctx = _db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS entitlements (" +
                "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
                "livemode INTEGER NULL, tier TEXT NULL)");
        }
        _tenants = new TenantRegistry(_db);
        _trials = new TrialRegistry(_db);
        // The production shape: live money required and the trial ledger wired, so an account with no paid row and no
        // running trial reads as the FREE tier, exactly as the hosted Gateway reads it.
        _entitlements = new EntitlementRegistry(_db, requireLivemode: true, trials: _trials);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true);
        _aiGate = new TenantAiGate(_entitlements, _tenants);
        _teamRegistry = new TeamRegistry(_db, _tenants);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        _devices.Dispose();
        _harness.Dispose();
        _key.Dispose();
    }

    /// <summary>
    /// Build the hosted Gateway's own wiring over the registries, for one statement of the pre-launch grace: ONE rule
    /// instance asked by the lease and by the Cockpit and phone door, the two real routes on a host, and the real
    /// middleware configuration.
    /// </summary>
    /// <param name="teamsReleased">Whether Teams is released on this Gateway. Released, the rule has its team half and
    /// the lease its team read, as the host wires them; dark, neither exists.</param>
    private async Task StartAsync(PreLaunchGrace grace, bool teamsReleased = false)
    {
        var rule = new RemoteAccessRule(grace, teamsReleased ? new TeamRemoteAccess(_teamRegistry, _entitlements) : null);
        _leases = new HostedAccessLeaseService(_entitlements, _tenants, _revoker,
            teams: teamsReleased ? new TeamMemberEntitlement(_teamRegistry, _entitlements) : null, remoteAccess: rule);
        _auth = new AuthMiddleware.RequireToken
        {
            Hosted = true,
            Token = "the-shared-token-a-hosted-gateway-never-accepts",
            Devices = _devices,
            Leases = _leases,
            Boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices, hosted: true),
        };

        var validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        HostedEnrollmentEndpoint.Map(_app, _devices, _tenants, validator, _entitlements);
        MobileEnrollmentEndpoint.Map(_app, hosted: true,
            new MobileDeviceEnrollmentService(account: null, new DeviceRegistryClient(new HttpClient()), _devices),
            new HostedEnrollDependencies(_devices, _tenants, validator, _entitlements, rule));
        await _app.StartAsync();
        _http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(_app.Urls.First()) };
    }

    // ================================================================= what an account is

    private void PaidRow(string tier)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
            Subject, EntitlementRegistry.StatusActive, FarFuture, true, tier);
    }

    /// <summary>An account that reached this Gateway at <paramref name="createdAtUtc"/>: its tenant row, dated then.</summary>
    private TenantId TenantCreatedAt(DateTime createdAtUtc)
    {
        var tenant = _tenants.MintOrLookupBySubject(Subject, "person@example.com");
        using var ctx = _db.CreateUnscopedContext();
        ctx.Tenants.Single(t => t.Id == tenant.Value).CreatedAtUtc = createdAtUtc;
        ctx.SaveChanges();
        return tenant;
    }

    private TenantId Tenant() => _tenants.LookupBySubject(Subject)
        ?? throw new InvalidOperationException("the account has no tenant yet");

    private int TrialRows()
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.AccountTrials.Count();
    }

    /// <summary>A browser key the account ALREADY HOLDS, issued before the door would refuse it. This is the key a Free
    /// account's Cockpit still carries, and what the request path has to refuse without help from the mint.</summary>
    private string BrowserKeyTheAccountAlreadyHolds()
        => _devices.RegisterForTenant(Tenant(), Subject, "held-before|cockpit-from-before", "Cockpit", "browser", "browser").DeviceKey;

    // ================================================================= the product's own paths

    private sealed record Answer(int Status, string Body, string? RetryAfter, string? DeviceKey = null);

    private string Token() => _key.Token(Subject, "person@example.com", Audience, Issuer);

    private async Task<Answer> PostAsync(string path, object body)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        using var resp = await _http!.SendAsync(msg);
        var text = await resp.Content.ReadAsStringAsync();
        string? key = null;
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            using var doc = JsonDocument.Parse(text);
            key = doc.RootElement.GetProperty("deviceKey").GetString();
        }
        var retryAfter = resp.Headers.TryGetValues("Retry-After", out var values) ? values.Single() : null;
        return new Answer((int)resp.StatusCode, text, retryAfter, key);
    }

    /// <summary>The Director door: <c>POST /devices/enroll-hosted</c>.</summary>
    private Task<Answer> EnrolDirectorAsync(string deviceType = "workstation", string deviceId = "director-1")
        => PostAsync(HostedEnrollmentEndpoint.Path, new { deviceId, machineName = "DESK", platform = "windows", deviceType });

    /// <summary>The Cockpit and phone door: <c>POST /mobile/enroll</c>. The platform decides the key's type.</summary>
    private Task<Answer> EnrolPhoneAsync() => PostAsync("/mobile/enroll", new { deviceId = "phone-1", platform = "android", name = "Phone" });

    private Task<Answer> SignInToTheCockpitAsync(string deviceId = "cockpit-1")
        => PostAsync("/mobile/enroll", new { deviceId, platform = "browser", name = "Cockpit" });

    /// <summary>One request through the REAL auth middleware with a device key as its Bearer.</summary>
    private async Task<Answer> RequestAsync(string deviceKey)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Get;
        ctx.Request.Path = "/sessions";
        ctx.Request.Headers.Authorization = "Bearer " + deviceKey;
        ctx.Response.Body = new MemoryStream();

        var served = false;
        await AuthMiddleware.Run(ctx, _auth, () => { served = true; return Task.CompletedTask; });

        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var retryAfter = ctx.Response.Headers.RetryAfter.ToString();
        return new Answer(served ? StatusCodes.Status200OK : ctx.Response.StatusCode, body,
            string.IsNullOrEmpty(retryAfter) ? null : retryAfter);
    }

    /// <summary>Dictation for the account's tenant: the real gate's verdict, as the status the real answer carries.</summary>
    private async Task<int> DictationStatusAsync()
    {
        var decision = _aiGate.ForTenant(Tenant(), EntitlementScopes.Dictation);
        if (decision.IsAllowed)
            return StatusCodes.Status200OK;
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await HostedAiHttp.TenantAiRefusal(decision).ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    private static void AssertRemotePlanRequired(Answer answer)
    {
        Assert.Equal(402, answer.Status);
        using var doc = JsonDocument.Parse(answer.Body);
        var root = doc.RootElement;
        // The shape the phone's screen and the Cockpit read: these four fields, by these names.
        Assert.Equal(new[] { "code", "error", "message", "subscribeUrl" }, root.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("remote_plan_required", root.GetProperty("code").GetString());
        Assert.Equal(EntitlementRegistry.RemotePlanRequiredMessage, root.GetProperty("message").GetString());
        Assert.Equal(EntitlementRegistry.SubscribeUrl, root.GetProperty("subscribeUrl").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("error").GetString()));
    }

    private sealed class RecordingRevoker : ITenantAccessRevoker
    {
        public List<string> Revoked { get; } = new();

        public Task RevokeAsync(TenantId tenant, string reason, CancellationToken ct = default)
        {
            Revoked.Add(tenant.Value);
            return Task.CompletedTask;
        }
    }

    // ================================================================= THE MATRIX

    public enum Account
    {
        FreeCreatedAfterLaunch,
        Remote,
        Pro,
        New,
        RunningTrial,
        FreeCreatedBeforeLaunchNoCutOff,
        FreeCreatedBeforeLaunchCutOffPassed,
    }

    [Theory]
    //          the account                                  Director  phone  Cockpit sign-in and request  dictation
    [InlineData(Account.FreeCreatedAfterLaunch,              200,      402,   402,                         402)]
    [InlineData(Account.Remote,                              200,      200,   200,                         402)]
    [InlineData(Account.Pro,                                 200,      200,   200,                         200)]
    [InlineData(Account.New,                                 200,      402,   402,                         402)]
    [InlineData(Account.RunningTrial,                        200,      200,   200,                         200)]
    [InlineData(Account.FreeCreatedBeforeLaunchNoCutOff,     200,      200,   200,                         402)]
    [InlineData(Account.FreeCreatedBeforeLaunchCutOffPassed, 200,      402,   402,                         402)]
    public async Task TheMatrix_EveryAccount_ThroughTheRealRoutesAndTheRealMiddleware(
        Account account, int director, int phone, int cockpit, int dictation)
    {
        await StartAsync(account == Account.FreeCreatedBeforeLaunchCutOffPassed ? LaunchedCutOffPassed : LaunchedNoCutOff);
        switch (account)
        {
            case Account.FreeCreatedAfterLaunch:
                // Reached this Gateway after launch: a tenant row, minted now, and no paid row.
                _tenants.MintOrLookupBySubject(Subject, "person@example.com");
                break;
            case Account.Remote:
                PaidRow(EntitlementRegistry.TierRemote);
                break;
            case Account.Pro:
                PaidRow(EntitlementRegistry.TierPro);
                break;
            case Account.New:
                // Never seen by this Gateway: no tenant, no paid row, no trial.
                Assert.Null(_tenants.LookupBySubject(Subject));
                break;
            case Account.RunningTrial:
                // Granted before the change, by the ledger's own writer; enrolment itself grants none.
                Assert.Equal(TrialOutcome.Active, _trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, FarFuture).Outcome);
                break;
            case Account.FreeCreatedBeforeLaunchNoCutOff:
            case Account.FreeCreatedBeforeLaunchCutOffPassed:
                TenantCreatedAt(BeforeLaunch);
                break;
        }
        var trialRowsBefore = TrialRows();

        // The Director door. Every account enrols its Director, and enrolling grants nobody a trial.
        var directorAnswer = await EnrolDirectorAsync();
        Assert.Equal(director, directorAnswer.Status);
        Assert.Equal(trialRowsBefore, TrialRows());
        if (account == Account.New)
            Assert.Equal(0, TrialRows());

        // The Cockpit and phone door, once for each kind of key.
        var phoneAnswer = await EnrolPhoneAsync();
        Assert.Equal(phone, phoneAnswer.Status);
        var cockpitAnswer = await SignInToTheCockpitAsync();
        Assert.Equal(cockpit, cockpitAnswer.Status);
        if (phone == 402) AssertRemotePlanRequired(phoneAnswer);
        if (cockpit == 402) AssertRemotePlanRequired(cockpitAnswer);

        // A Cockpit request, through the real middleware: with the key the sign-in just issued, or - where the sign-in
        // is refused - with a browser key the account already held.
        var cockpitRequest = await RequestAsync(cockpitAnswer.DeviceKey ?? BrowserKeyTheAccountAlreadyHolds());
        Assert.Equal(cockpit, cockpitRequest.Status);
        if (cockpit == 402) AssertRemotePlanRequired(cockpitRequest);

        // The Director's own request is served on every row: no row of this matrix takes the Director away.
        Assert.Equal(200, (await RequestAsync(directorAnswer.DeviceKey!)).Status);

        Assert.Equal(dictation, await DictationStatusAsync());
        Assert.Empty(_revoker.Revoked);
    }

    [Fact]
    public async Task TheMatrix_SelfHost_NoEntitlementRegistry_IsUnchanged_NothingIsGated()
    {
        // Decision D5. A self-hosted Gateway has no entitlement registry: no gate where a key is minted (through either
        // door), no lease on the request path, and an AI gate that allows everything. An account with no paid row at
        // all is served everywhere, exactly as before the Pricing Change.
        var validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);

        var director = HostedEnrollmentEndpoint.Enroll(Token(),
            new EnrollSignedInRequest { DeviceId = "director-1", MachineName = "DESK", Platform = "windows", DeviceType = "workstation" },
            _devices, _tenants, validator, entitlements: null);
        var phone = HostedEnrollmentEndpoint.Enroll(Token(),
            new EnrollSignedInRequest { DeviceId = "phone-1", MachineName = "Phone", Platform = "android", DeviceType = "phone" },
            _devices, _tenants, validator, entitlements: null, door: HostedEnrollmentEndpoint.EnrollDoor.CockpitOrPhone);
        var cockpit = HostedEnrollmentEndpoint.Enroll(Token(),
            new EnrollSignedInRequest { DeviceId = "cockpit-1", MachineName = "Cockpit", Platform = "browser", DeviceType = "browser" },
            _devices, _tenants, validator, entitlements: null, door: HostedEnrollmentEndpoint.EnrollDoor.CockpitOrPhone);

        Assert.Equal(200, director.Status);
        Assert.Equal(200, phone.Status);
        Assert.Equal(200, cockpit.Status);

        // The request path with no lease service wired - what a self-hosted Gateway builds - asks nothing of a plan.
        _auth = new AuthMiddleware.RequireToken
        {
            Hosted = true, Token = "unused", Devices = _devices, Leases = null,
            Boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices, hosted: true),
        };
        Assert.Equal(200, (await RequestAsync(cockpit.Response!.DeviceKey)).Status);
        Assert.Equal(200, (await RequestAsync(phone.Response!.DeviceKey)).Status);

        Assert.Equal(TenantAiVerdict.Allowed, TenantAiGate.SelfHost.ForTenant(Tenant(), EntitlementScopes.Dictation).Verdict);
        Assert.Equal(0, TrialRows());
    }

    // ================================================================= a refusal refuses the request and nothing else

    [Fact]
    public async Task AfterEveryFreeRefusal_NoDeviceKeyIsRevoked_TheLeaseStillAllowsTheDirector_AndTheRevokerWasNeverCalled()
    {
        await StartAsync(LaunchedNoCutOff);
        _tenants.MintOrLookupBySubject(Subject, "person@example.com");
        var directorKey = (await EnrolDirectorAsync()).DeviceKey!;
        var browserKey = BrowserKeyTheAccountAlreadyHolds();

        // Every refusal a Free account can meet: both kinds of mint, and requests - several, so the first (which reads
        // the plan) and the later ones (which are answered from the lease) are both covered.
        AssertRemotePlanRequired(await EnrolPhoneAsync());
        AssertRemotePlanRequired(await SignInToTheCockpitAsync());
        for (var i = 0; i < 3; i++)
            AssertRemotePlanRequired(await RequestAsync(browserKey));

        // None of the tenant's device keys is revoked - the Director's, and the refused browser key itself.
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(directorKey).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(browserKey).Kind);
        Assert.All(_devices.List(), d => Assert.Equal(DeviceRegistry.StatusActive, d.Status));

        // The access lease still allows the tenant's Director, and it is a LIVE POSITIVE lease, not a negative one.
        Assert.Equal(HostedAccessDecision.Allow, await _leases.AuthorizeAsync(Tenant()));
        Assert.Equal(new[] { new LiveLease(Tenant(), null) }, _leases.LiveLeases().ToArray());

        Assert.Empty(_revoker.Revoked);
    }

    [Fact]
    public async Task AFreeAccountsDirectorRequest_IsServed_OnTheSameTenantWhoseBrowserKeyWasJustRefused()
    {
        await StartAsync(LaunchedNoCutOff);
        _tenants.MintOrLookupBySubject(Subject, "person@example.com");
        var directorKey = (await EnrolDirectorAsync()).DeviceKey!;
        var browserKey = BrowserKeyTheAccountAlreadyHolds();
        Assert.Equal(_devices.TenantForKey(directorKey), _devices.TenantForKey(browserKey));

        // The browser first, so the lease the Director then meets is the one the refusal left behind.
        AssertRemotePlanRequired(await RequestAsync(browserKey));
        Assert.Equal(200, (await RequestAsync(directorKey)).Status);
        // And in the other order, on the same lease.
        AssertRemotePlanRequired(await RequestAsync(browserKey));
        Assert.Equal(200, (await RequestAsync(directorKey)).Status);
    }

    [Fact]
    public async Task ARefusedBrowserKey_IsServedAgain_WithNoNewSignIn_OnceTheAccountHasARemoteRow_AndTheLeaseIsReRead()
    {
        await StartAsync(LaunchedNoCutOff);
        _tenants.MintOrLookupBySubject(Subject, "person@example.com");
        var browserKey = BrowserKeyTheAccountAlreadyHolds();
        AssertRemotePlanRequired(await RequestAsync(browserKey));

        // The member subscribes: the payment side writes a Remote row. Nothing else is done - no sign-in, no new key.
        PaidRow(EntitlementRegistry.TierRemote);
        // Until the lease is read again the old answer stands (it is the lease's lifetime, as for a cancellation).
        AssertRemotePlanRequired(await RequestAsync(browserKey));

        // The 60 second sweep re-reads every live lease.
        await new EntitlementLeaseMonitor(_leases).SweepOnceAsync();

        Assert.Equal(200, (await RequestAsync(browserKey)).Status);
        Assert.Empty(_revoker.Revoked);
    }

    [Fact]
    public async Task ARemoteAccountThatStopsPaying_LosesTheCockpitOnTheNextLeaseRead_AndKeepsItsDirector()
    {
        // The other direction: the plan goes, the Cockpit goes with it, and nothing is revoked - the account is on
        // free, which keeps the Director.
        await StartAsync(LaunchedNoCutOff);
        PaidRow(EntitlementRegistry.TierRemote);
        var directorKey = (await EnrolDirectorAsync()).DeviceKey!;
        var cockpitKey = (await SignInToTheCockpitAsync()).DeviceKey!;
        Assert.Equal(200, (await RequestAsync(cockpitKey)).Status);

        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DELETE FROM entitlements WHERE subject = {0}", Subject);
        await new EntitlementLeaseMonitor(_leases).SweepOnceAsync();

        AssertRemotePlanRequired(await RequestAsync(cockpitKey));
        Assert.Equal(200, (await RequestAsync(directorKey)).Status);
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(cockpitKey).Kind);
        Assert.Empty(_revoker.Revoked);
    }

    // ================================================================= an unreadable plan is never a refusal

    [Fact]
    public async Task AnUnreadablePlan_Is503WithRetryAfter_Never402_AtTheMint_AndOnARequest()
    {
        await StartAsync(LaunchedNoCutOff);
        PaidRow(EntitlementRegistry.TierPro);
        var directorKey = (await EnrolDirectorAsync()).DeviceKey!;
        var cockpitKey = (await SignInToTheCockpitAsync()).DeviceKey!;
        var keysBefore = _devices.List().Count;

        // The payment side's table cannot be read. No request was made yet, so no lease covers either key.
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE entitlements");

        foreach (var mint in new[] { await EnrolDirectorAsync(deviceId: "director-2"), await EnrolPhoneAsync(), await SignInToTheCockpitAsync() })
        {
            Assert.Equal(503, mint.Status);
            Assert.Equal("10", mint.RetryAfter);
            Assert.Contains("entitlement_unknown", mint.Body);
            Assert.DoesNotContain("remote_plan_required", mint.Body);
        }
        Assert.Equal(keysBefore, _devices.List().Count);

        foreach (var request in new[] { await RequestAsync(cockpitKey), await RequestAsync(directorKey) })
        {
            Assert.Equal(503, request.Status);
            Assert.Equal("10", request.RetryAfter);
            Assert.Contains("entitlement_unknown", request.Body);
        }
        Assert.Empty(_revoker.Revoked);
    }

    // ================================================================= the Director door is not a way round

    [Theory]
    [InlineData("phone")]
    [InlineData("browser")]
    [InlineData("Browser")]
    public async Task TheDirectorDoor_RefusesARequestThatAsksForTheDeviceTypePhoneOrBrowser(string deviceType)
    {
        // Asked by a PRO account, so the refusal is the door's and not the plan's: nobody is issued a Cockpit's or a
        // phone's key through the Director route.
        await StartAsync(LaunchedNoCutOff);
        PaidRow(EntitlementRegistry.TierPro);

        var answer = await EnrolDirectorAsync(deviceType);

        Assert.Equal(400, answer.Status);
        Assert.Contains(HostedEnrollmentEndpoint.DirectorDoorDeviceTypeCode, answer.Body);
        Assert.Null(_tenants.LookupBySubject(Subject));
        Assert.Empty(_devices.List());
    }

    [Fact]
    public async Task TheDirectorDoor_AFreeAccountAskingForABrowserKey_GetsNoKeyToTakeToTheCockpit()
    {
        // The way round, tried: a Free account that cannot sign in to the Cockpit asks the Director route for a
        // browser's key instead.
        await StartAsync(LaunchedNoCutOff);
        _tenants.MintOrLookupBySubject(Subject, "person@example.com");

        AssertRemotePlanRequired(await SignInToTheCockpitAsync());
        var answer = await EnrolDirectorAsync(deviceType: "browser", deviceId: "cockpit-1");

        Assert.Equal(400, answer.Status);
        Assert.Null(answer.DeviceKey);
        Assert.Empty(_devices.List());
    }

    [Fact]
    public async Task TheCockpitAndPhoneDoor_ANewAccountThatArrivesThereFirst_Is402_AndNothingIsMinted()
    {
        // An account this Gateway has never seen, whose first arrival is the phone app. It has no tenant row, so with
        // a launch instant set it is not in grace; it is refused before any tenant is minted or key issued, and it is
        // granted no trial.
        await StartAsync(LaunchedNoCutOff);

        AssertRemotePlanRequired(await EnrolPhoneAsync());
        AssertRemotePlanRequired(await SignInToTheCockpitAsync());

        Assert.Null(_tenants.LookupBySubject(Subject));
        Assert.Empty(_devices.List());
        Assert.Equal(0, TrialRows());
    }

    [Fact]
    public async Task WithNeitherGraceSettingSet_AFreeAccount_KeepsTheCockpitAndThePhoneApp_AndStillHasNoDictation()
    {
        // The default deploy: nothing set takes nothing from anyone - a brand-new Free account included - and gives
        // nothing either: the grace grants remote access only, never a model call.
        await StartAsync(PreLaunchGrace.NotSet);

        Assert.Equal(200, (await EnrolDirectorAsync()).Status);
        Assert.Equal(200, (await EnrolPhoneAsync()).Status);
        var cockpit = await SignInToTheCockpitAsync();
        Assert.Equal(200, cockpit.Status);
        Assert.Equal(200, (await RequestAsync(cockpit.DeviceKey!)).Status);

        Assert.Equal(EntitlementRegistry.TierFree, _entitlements.Evaluate(Subject, CutOffPassed).Tier);
        Assert.Equal(402, await DictationStatusAsync());
        Assert.Equal(0, TrialRows());
    }

    // ================================================================= one device id, two doors

    private static void AssertDeviceIdOfAnotherClass(Answer answer)
    {
        Assert.Equal(409, answer.Status);
        using var doc = JsonDocument.Parse(answer.Body);
        Assert.Equal(HostedEnrollmentEndpoint.DeviceIdOfAnotherClassCode, doc.RootElement.GetProperty("code").GetString());
        Assert.Null(answer.DeviceKey);
    }

    [Fact]
    public async Task TheCockpitDoor_SentADirectorsDeviceId_Is409_AndNoBrowserIsHandedAKeyThatReadsAsADirectors()
    {
        // The device id is the client's to choose. A registration issues a new key against an active row under that
        // id and keeps the row's type, so without the refusal this browser would hold a workstation-typed key: one
        // the request path never asks remote access of, which would keep working after the plan ended.
        await StartAsync(LaunchedNoCutOff);
        PaidRow(EntitlementRegistry.TierRemote);
        var directorKey = (await EnrolDirectorAsync(deviceId: "one-id")).DeviceKey!;
        var keysBefore = _devices.List().Count;

        AssertDeviceIdOfAnotherClass(await SignInToTheCockpitAsync(deviceId: "one-id"));

        // Refused before any write: no key was issued, and the Director's own key is the one it was.
        Assert.Equal(keysBefore, _devices.List().Count);
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(directorKey).Kind);
        Assert.Equal("workstation", _devices.DeviceTypeForKey(directorKey));
        Assert.Equal(200, (await RequestAsync(directorKey)).Status);

        // The same browser under an id of its own is served: the refusal is of the id, not of the account.
        Assert.Equal(200, (await SignInToTheCockpitAsync()).Status);
    }

    [Fact]
    public async Task TheDirectorDoor_SentABrowsersDeviceId_Is409_AndNoDirectorIsHandedAKeyThatNeedsRemoteAccess()
    {
        // The reverse: without the refusal this Director would hold a browser-typed key, and on the day its account
        // has no remote access the Director would meet 402 on every request - which a Director never may.
        await StartAsync(LaunchedNoCutOff);
        PaidRow(EntitlementRegistry.TierRemote);
        var cockpitKey = (await SignInToTheCockpitAsync(deviceId: "one-id")).DeviceKey!;
        var keysBefore = _devices.List().Count;

        AssertDeviceIdOfAnotherClass(await EnrolDirectorAsync(deviceId: "one-id"));

        Assert.Equal(keysBefore, _devices.List().Count);
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(cockpitKey).Kind);
        Assert.Equal("browser", _devices.DeviceTypeForKey(cockpitKey));
        Assert.Equal(200, (await RequestAsync(cockpitKey)).Status);
        Assert.Equal(200, (await EnrolDirectorAsync()).Status);
    }

    [Fact]
    public async Task TheSameDoorSentTheSameDeviceIdAgain_IsServed_AndKeepsItsType()
    {
        // The control: signing the same browser in again, and setting the same Director up again, is the ordinary
        // case and must not meet the refusal above.
        await StartAsync(LaunchedNoCutOff);
        PaidRow(EntitlementRegistry.TierRemote);

        Assert.Equal(200, (await EnrolDirectorAsync()).Status);
        var directorAgain = await EnrolDirectorAsync();
        Assert.Equal(200, directorAgain.Status);
        Assert.Equal("workstation", _devices.DeviceTypeForKey(directorAgain.DeviceKey));

        Assert.Equal(200, (await SignInToTheCockpitAsync()).Status);
        var cockpitAgain = await SignInToTheCockpitAsync();
        Assert.Equal(200, cockpitAgain.Status);
        Assert.Equal("browser", _devices.DeviceTypeForKey(cockpitAgain.DeviceKey));
    }

    [Fact]
    public async Task ATenantRowThatCannotBeRead_AtTheCockpitAndPhoneDoor_Is503WithRetryAfter_NeverA502()
    {
        // The plan was read (this is a free account, created after launch), so the door goes on to ask the grace of
        // the account's tenant row - and that read fails. Ignorance, not "this account has no row".
        await StartAsync(LaunchedNoCutOff);
        _tenants.MintOrLookupBySubject(Subject, "person@example.com");
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("ALTER TABLE tenants RENAME TO tenants_unreadable");

        foreach (var answer in new[] { await EnrolPhoneAsync(), await SignInToTheCockpitAsync() })
        {
            Assert.Equal(503, answer.Status);
            Assert.Equal("10", answer.RetryAfter);
            Assert.Contains(HostedEnrollmentEndpoint.EntitlementUnknownCode, answer.Body);
        }
        Assert.Empty(_revoker.Revoked);
    }

    // ================================================================= a team with a running bill

    private const string TeamOwner = "sub-the-owner-of-the-team";

    /// <summary>A team owned by someone else, with <see cref="Subject"/> in it at <paramref name="role"/>, and a bill
    /// in <paramref name="billStatus"/> (null for a team that never started its plan).</summary>
    private string TeamWithTheAccountIn(TeamRole role, string? billStatus)
    {
        var team = _teamRegistry.CreateTeam(TeamOwner, "Acme").Team!.TeamId;
        Assert.True(_teamRegistry.AddMember(team, Subject, role).IsDone);
        if (billStatus is not null)
            TeamBillSeed.Put(_db, team, billStatus, seats: 2, autoRenew: billStatus == EntitlementRegistry.StatusActive);
        return team;
    }

    [Theory]
    [InlineData(TeamRole.Collaborator, EntitlementRegistry.StatusActive)]
    [InlineData(TeamRole.Developer, EntitlementRegistry.StatusActive)]
    [InlineData(TeamRole.Manager, EntitlementRegistry.StatusActive)]
    [InlineData(TeamRole.Collaborator, EntitlementRegistry.StatusPastDue)]
    public async Task AFreeAccount_ThatIsAMemberOfATeamWithARunningBill_HasTheCockpitAndThePhoneApp_AndStillNoDictation(TeamRole role, string billStatus)
    {
        // A person's Cockpit and phone key is their OWN account's, so a team seat reaches the Cockpit only through
        // the personal read. Any role counts, a Collaborator included - the Cockpit is a Collaborator's only way into
        // the team. It grants remote access and nothing else: the account is still on free, with no model call.
        await StartAsync(LaunchedNoCutOff, teamsReleased: true);
        _tenants.MintOrLookupBySubject(Subject, "person@example.com");
        TeamWithTheAccountIn(role, billStatus);

        Assert.Equal(200, (await EnrolPhoneAsync()).Status);
        var cockpit = await SignInToTheCockpitAsync();
        Assert.Equal(200, cockpit.Status);
        Assert.Equal(200, (await RequestAsync(cockpit.DeviceKey!)).Status);

        Assert.Equal(EntitlementRegistry.TierFree, _entitlements.Evaluate(Subject, CutOffPassed).Tier);
        Assert.Equal(402, await DictationStatusAsync());
        Assert.Empty(_revoker.Revoked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(EntitlementRegistry.StatusCanceled)]
    public async Task AFreeAccount_WhoseOnlyTeamHasNoRunningBill_Is402_EvenAsThatTeamsOwner(string? billStatus)
    {
        // A team that was created and never paid for, or whose bill has ended, gives its members nothing here -
        // otherwise creating a team would be a free way into the Cockpit. The account under test OWNS this team.
        await StartAsync(LaunchedNoCutOff, teamsReleased: true);
        _tenants.MintOrLookupBySubject(Subject, "person@example.com");
        var team = _teamRegistry.CreateTeam(Subject, "Mine").Team!.TeamId;
        if (billStatus is not null)
            TeamBillSeed.Put(_db, team, billStatus, seats: 1, autoRenew: false);
        var directorKey = (await EnrolDirectorAsync()).DeviceKey!;

        AssertRemotePlanRequired(await EnrolPhoneAsync());
        AssertRemotePlanRequired(await SignInToTheCockpitAsync());
        AssertRemotePlanRequired(await RequestAsync(BrowserKeyTheAccountAlreadyHolds()));

        Assert.Equal(200, (await RequestAsync(directorKey)).Status);
        Assert.Empty(_revoker.Revoked);
    }

    [Fact]
    public async Task WhereTeamsIsNotReleased_ATeamSeatGivesNothing_BecauseTheRuleHasNoTeamHalf()
    {
        // The control for the team tests above: the same account and the same billed team, on a Gateway whose rule
        // was built with no team half. If this passed with a 200, the tests above would prove nothing about the team
        // read.
        await StartAsync(LaunchedNoCutOff, teamsReleased: false);
        _tenants.MintOrLookupBySubject(Subject, "person@example.com");
        TeamWithTheAccountIn(TeamRole.Collaborator, EntitlementRegistry.StatusActive);

        AssertRemotePlanRequired(await EnrolPhoneAsync());
        AssertRemotePlanRequired(await RequestAsync(BrowserKeyTheAccountAlreadyHolds()));
    }

    [Fact]
    public async Task ATeamMembershipThatCannotBeRead_Is503WithRetryAfter_AtTheMintAndOnARequest_AndTheDirectorIsStillServed()
    {
        await StartAsync(LaunchedNoCutOff, teamsReleased: true);
        _tenants.MintOrLookupBySubject(Subject, "person@example.com");
        TeamWithTheAccountIn(TeamRole.Collaborator, EntitlementRegistry.StatusActive);
        var directorKey = (await EnrolDirectorAsync()).DeviceKey!;
        var browserKey = BrowserKeyTheAccountAlreadyHolds();
        var keysBefore = _devices.List().Count;

        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE team_members");

        // Not a refusal (it would tell a team member to buy a plan) and not a grant: ask again.
        foreach (var answer in new[] { await EnrolPhoneAsync(), await SignInToTheCockpitAsync(), await RequestAsync(browserKey), await RequestAsync(browserKey) })
        {
            Assert.Equal(503, answer.Status);
            Assert.Equal("10", answer.RetryAfter);
            Assert.Contains("entitlement_unknown", answer.Body);
            Assert.DoesNotContain("remote_plan_required", answer.Body);
        }
        Assert.Equal(keysBefore, _devices.List().Count);

        // The plan itself was read, so the Director - which needs no remote access - is served on the same lease.
        Assert.Equal(200, (await RequestAsync(directorKey)).Status);
        Assert.Empty(_revoker.Revoked);
    }

    [Fact]
    public async Task ATeamsBillThatCannotBeRead_Is503_NeverARefusal()
    {
        await StartAsync(LaunchedNoCutOff, teamsReleased: true);
        _tenants.MintOrLookupBySubject(Subject, "person@example.com");
        TeamWithTheAccountIn(TeamRole.Developer, EntitlementRegistry.StatusActive);
        var browserKey = BrowserKeyTheAccountAlreadyHolds();

        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE team_bills");

        Assert.Equal(503, (await EnrolPhoneAsync()).Status);
        var request = await RequestAsync(browserKey);
        Assert.Equal(503, request.Status);
        Assert.Equal("10", request.RetryAfter);
        Assert.Empty(_revoker.Revoked);
    }

    [Fact]
    public async Task APersonRemovedFromTheirTeam_IsRefusedOnTheNextLeaseRead_AndNothingIsRevoked()
    {
        await StartAsync(LaunchedNoCutOff, teamsReleased: true);
        _tenants.MintOrLookupBySubject(Subject, "person@example.com");
        var team = TeamWithTheAccountIn(TeamRole.Collaborator, EntitlementRegistry.StatusActive);
        var directorKey = (await EnrolDirectorAsync()).DeviceKey!;
        var cockpitKey = (await SignInToTheCockpitAsync()).DeviceKey!;
        Assert.Equal(200, (await RequestAsync(cockpitKey)).Status);

        Assert.True(_teamRegistry.RemoveMember(team, Subject).IsDone);
        // The 60 second sweep re-reads every live lease.
        await new EntitlementLeaseMonitor(_leases).SweepOnceAsync();

        AssertRemotePlanRequired(await RequestAsync(cockpitKey));
        AssertRemotePlanRequired(await EnrolPhoneAsync());
        Assert.Equal(200, (await RequestAsync(directorKey)).Status);
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(cockpitKey).Kind);
        Assert.Empty(_revoker.Revoked);
    }

    // ================================================================= the door is told, never guessed

    [Fact]
    public void Enroll_TheCockpitAndPhoneDoor_GivenADirectorsDeviceType_Throws_ItNeverTakesATypeFromACaller()
    {
        var validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);

        Assert.Throws<InvalidOperationException>(() => HostedEnrollmentEndpoint.Enroll(Token(),
            new EnrollSignedInRequest { DeviceId = "d", MachineName = "M", Platform = "windows", DeviceType = "workstation" },
            _devices, _tenants, validator, _entitlements, door: HostedEnrollmentEndpoint.EnrollDoor.CockpitOrPhone,
            remoteAccess: new RemoteAccessRule(PreLaunchGrace.NotSet)));
    }

    [Fact]
    public void Enroll_TheCockpitAndPhoneDoor_OnAGatedGatewayWithNoRemoteAccessRule_Throws_RatherThanIssueAKeyUnasked()
    {
        var validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);

        Assert.Throws<InvalidOperationException>(() => HostedEnrollmentEndpoint.Enroll(Token(),
            new EnrollSignedInRequest { DeviceId = "d", MachineName = "M", Platform = "android", DeviceType = "phone" },
            _devices, _tenants, validator, _entitlements, door: HostedEnrollmentEndpoint.EnrollDoor.CockpitOrPhone,
            remoteAccess: null));
        Assert.Empty(_devices.List());
    }
}
