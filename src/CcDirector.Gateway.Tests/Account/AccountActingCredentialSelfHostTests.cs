using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Account;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Issue #984, the SELF-HOST half - the control for
/// <see cref="HostedAccountRoutesTellTheTruthTests"/>, and the proof of the two acceptance points that are
/// not about hosted mode at all.
///
/// Self-host is where one Gateway holds one account, so "does this Gateway hold a credential?" genuinely IS
/// a question about the user, and 401 "not signed in ... sign in from the Gateway tray, then retry" is the
/// right answer - for exactly ONE of the states the old code lumped together. These tests pin the
/// separation:
/// <list type="number">
/// <item>NOTHING STORED - genuinely signed out. 401, and the sign-in instruction is correct and useful. This
/// test exists so the fix cannot be mistaken for "never say not signed in": the message is not the problem,
/// saying it to the wrong person is.</item>
/// <item>SOMETHING STORED THAT WILL NOT FORWARD - an internal inconsistency, never a sign-out. Reporting it
/// as one is what sent people to redo a sign-in that was already done and could not help.</item>
/// <item>THE CREDENTIAL IS RENEWED BEFORE ACTING (acceptance 3): an expired token is renewed and the FRESH
/// one is what reaches the cloud, a healthy token costs no exchange at all, and an expired token that can
/// never be renewed is NAMED here rather than forwarded dead for the cloud to reject with a message about
/// some other cause.</item>
/// </list>
///
/// On where the refresh belongs. The issue asked for a refresh "before failing", and putting it literally
/// there would have been theatre: <see cref="DevThrottleAccountService.RefreshIfNeededAsync"/> deliberately
/// declines to spend a refresh on an access token that does not verify - it is not ours to renew - and that
/// is the ONLY state in which the forwarding read comes back empty. A refresh at that point could not change
/// the outcome in any reachable case. Moved to before the action it does real work, and these tests assert
/// the work rather than the call.
///
/// A note on the hypothesis these tests do NOT encode. The reported 401 was first assumed to be an expired
/// access token whose refresh had not run. It was not, and it could not have been:
/// <see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/> returns an expired-but-well-formed
/// token quite happily - expiry alone never empties it - so an expired token cannot produce this failure even
/// in principle. It returns null only when nothing is stored or the token does not verify. The refresh
/// attempt below is still worth having (a renewable credential should be renewed, not refused), but it is a
/// robustness measure, not the fix, and the tests are written to say which is which.
///
/// Everything here runs over a credential service built on an in-memory token store, so no Windows Data
/// Protection, no registry, no network - the states are provable cross-platform.
/// </summary>
public sealed class AccountActingCredentialSelfHostTests
{
    /// <summary>An in-memory store so the credential service can be seeded and cleared off Windows.</summary>
    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// A refresher the test drives: it records whether it was asked, and either renews with a token the
    /// caller supplies or reports the exchange unavailable.
    /// </summary>
    private sealed class FakeRefresher : ITokenRefresher
    {
        private readonly Func<string?>? _renewWith;
        private readonly bool _misconfigured;
        public int Calls;

        /// <param name="renewWith">Supplies the renewed access token, or null to renew nothing.</param>
        /// <param name="misconfigured">
        /// True to report the exchange PERSISTENTLY broken (issue #911) rather than merely unavailable. The
        /// distinction matters: unavailable keeps the cached credential usable, misconfigured condemns it.
        /// </param>
        public FakeRefresher(Func<string?>? renewWith = null, bool misconfigured = false)
        {
            _renewWith = renewWith;
            _misconfigured = misconfigured;
        }

        public Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            var renewed = _renewWith?.Invoke();
            if (renewed is not null)
                return Task.FromResult(TokenRefreshResult.Success(new DevThrottleTokens(renewed, refreshToken)));
            return Task.FromResult(_misconfigured ? TokenRefreshResult.Misconfigured : TokenRefreshResult.Unavailable);
        }
    }

    /// <summary>
    /// A cloud stub for the owner-email egress, so a successful send never leaves the process. It records
    /// the bearer token it was handed, which is how the "renew before acting" tests prove the RENEWED token
    /// reached the cloud rather than the stale one.
    /// </summary>
    private sealed class CloudStub : HttpMessageHandler
    {
        public int Calls;
        public string? SeenBearer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            SeenBearer = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"sent":true,"id":"stub-1"}}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static DevThrottleAccountService MakeAccount(DevThrottleTokens? seed, ITokenRefresher refresher)
    {
        var service = new DevThrottleAccountService(
            new InMemoryTokenStore(),
            new JwtAccessTokenValidator(GatewayTestJwt.SigningSecret),
            refresher);
        if (seed is not null)
            service.StoreTokens(seed);
        return service;
    }

    /// <summary>Boots the email route (and the status route, to prove the pair agrees) on an ephemeral port.</summary>
    private static async Task<(WebApplication app, HttpClient http, CloudStub cloud)> StartAsync(DevThrottleAccountService? account)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var cloud = new CloudStub();
        // The boundary is required and non-nullable now (finding I1-01). This is a self-host harness, so it
        // gets the REAL self-host boundary: built over the SingleTenantContext, it always resolves Local.
        var boundary = new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
            new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false);
        AccountStatusEndpoint.Map(app, false, account, boundary);
        AccountEmailEndpoint.Map(app, false, account, new AccountNotifyClient(new HttpClient(cloud)), boundary);
        await app.StartAsync();

        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, cloud);
    }

    private static Task<HttpResponseMessage> PostEmail(HttpClient http) =>
        http.PostAsync("/account/email",
            new StringContent("""{"subject":"issue 984","bodyText":"body"}""", Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Nothing_stored_is_the_one_state_that_answers_401_and_tells_the_user_to_sign_in()
    {
        // The message is not the bug. Here it is TRUE: this Gateway holds no credential, /account/status
        // agrees, and signing in from the tray is exactly what fixes it. Removing this answer entirely would
        // trade one wrong statement for another.
        var refresher = new FakeRefresher();
        var account = MakeAccount(seed: null, refresher);

        var (app, http, cloud) = await StartAsync(account);
        try
        {
            Assert.False((await Json(await http.GetAsync("/account/status"))).GetProperty("signedIn").GetBoolean());

            var resp = await PostEmail(http);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

            var error = (await Json(resp)).GetProperty("error").GetString() ?? "";
            Assert.Contains("not signed in", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Sign in from the Gateway tray", error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, cloud.Calls);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_stored_credential_that_will_not_forward_is_reported_as_that_and_never_as_a_sign_out()
    {
        // The state the old single conditional could not see. Something IS stored - the user did sign in -
        // and it cannot be forwarded. Calling that "not signed in" is what sent people to redo a completed
        // action; naming it lets them do the thing that actually helps.
        var refresher = new FakeRefresher();
        var account = MakeAccount(new DevThrottleTokens("not-a-verifiable-token", "refresh-1"), refresher);

        var (app, http, cloud) = await StartAsync(account);
        try
        {
            var resp = await PostEmail(http);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            var error = (await Json(resp)).GetProperty("error").GetString() ?? "";
            Assert.False(error.Contains("not signed in", StringComparison.OrdinalIgnoreCase),
                $"a stored-but-unusable credential was reported as a sign-out. Body: {error}");
            Assert.Contains("holds a DevThrottle credential", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not in a usable state", error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, cloud.Calls);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_expired_credential_is_renewed_before_acting_and_the_fresh_token_is_what_reaches_the_cloud()
    {
        // Acceptance 3, in the place it can actually do work: BEFORE acting, not after failing. Refreshing
        // only on the way out of a failure would be theatre - the credential service declines to renew a
        // token that does not verify, which is the only state that empties the forwarding read, so a refresh
        // at that point could never change anything. Renewing first is what stops the Gateway attaching a
        // token it could have renewed. The assertion is on WHICH token the cloud saw, because "a refresh
        // happened" and "the fresh token was used" are different claims and only the second one helps.
        var expired = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(-2), "owner@example.com", "github");
        var renewed = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "owner@example.com", "github");
        var refresher = new FakeRefresher(() => renewed);
        var account = MakeAccount(new DevThrottleTokens(expired, "refresh-1"), refresher);

        var (app, http, cloud) = await StartAsync(account);
        try
        {
            var resp = await PostEmail(http);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.True((await Json(resp)).GetProperty("sent").GetBoolean());
            Assert.Equal(1, refresher.Calls);
            Assert.Equal(renewed, cloud.SeenBearer);
            Assert.NotEqual(expired, cloud.SeenBearer);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_healthy_credential_is_not_refreshed_on_the_request_path()
    {
        // The cost control for the test above. Renewing before acting must stay a no-op with no network call
        // when the token has comfortable life left, or every account request would carry an exchange.
        var jwt = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "owner@example.com", "github");
        var refresher = new FakeRefresher();
        var account = MakeAccount(new DevThrottleTokens(jwt, "refresh-1"), refresher);

        var (app, http, cloud) = await StartAsync(account);
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await PostEmail(http)).StatusCode);
            Assert.Equal(0, refresher.Calls);
            Assert.Equal(jwt, cloud.SeenBearer);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_expired_credential_that_cannot_be_renewed_is_named_rather_than_forwarded_dead()
    {
        // Issue #911's state, reached through this route: the access token is expired and renewal is
        // persistently broken by a client-side misconfiguration, so it can never be renewed. The forwarding
        // read still hands that token over quite happily, so before this change the Gateway would attach a
        // certainly-dead credential and the user would receive the CLOUD's rejection - a message about some
        // other cause, which is the same failure mode issue #984 is about, one layer down.
        var expired = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(-2), "owner@example.com", "github");
        var refresher = new FakeRefresher(renewWith: null, misconfigured: true);
        var account = MakeAccount(new DevThrottleTokens(expired, "refresh-1"), refresher);

        var (app, http, cloud) = await StartAsync(account);
        try
        {
            var resp = await PostEmail(http);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.Equal(0, cloud.Calls);   // nothing dead was sent upstream

            var error = (await Json(resp)).GetProperty("error").GetString() ?? "";
            Assert.False(error.Contains("not signed in", StringComparison.OrdinalIgnoreCase),
                $"an unrenewable credential was reported as a sign-out. Body: {error}");
            Assert.Contains("failing persistently", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_expired_but_renewable_token_still_sends_which_is_why_expiry_was_never_the_cause()
    {
        // Pinning the disproof of the original hypothesis so the next person does not re-derive it from
        // scratch. The reported 401 was first assumed to be an expired access token whose refresh had not
        // run. Here the refresh cannot run either - the exchange is merely unavailable, as it would be
        // offline - and the expired token is STILL forwarded and STILL sends, because
        // GetAccessTokenForForwarding treats expired-but-well-formed as forwardable. Expiry alone therefore
        // cannot produce a "not signed in" answer, in principle, and never could. If this goes red, the
        // reasoning recorded in issue #984 needs revisiting - which is exactly why it is asserted.
        var expired = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(-2), "owner@example.com", "github");
        var refresher = new FakeRefresher();   // exchange unavailable: nothing renewed, nothing condemned
        var account = MakeAccount(new DevThrottleTokens(expired, "refresh-1"), refresher);

        var (app, http, cloud) = await StartAsync(account);
        try
        {
            var resp = await PostEmail(http);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, cloud.Calls);
            Assert.Equal(expired, cloud.SeenBearer);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Status_and_email_agree_on_self_host_when_signed_in()
    {
        // The self-host statement of the same invariant the hosted class proves: the two routes must not
        // contradict each other about whether the caller is signed in.
        var jwt = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "owner@example.com", "github");
        var account = MakeAccount(new DevThrottleTokens(jwt, "refresh-1"), new FakeRefresher());

        var (app, http, _) = await StartAsync(account);
        try
        {
            Assert.True((await Json(await http.GetAsync("/account/status"))).GetProperty("signedIn").GetBoolean());

            var resp = await PostEmail(http);
            Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.DoesNotContain("not signed in", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_host_with_no_credential_store_says_so_instead_of_blaming_the_user()
    {
        // A non-Windows host has no operating system credential store, so no account can live here at all.
        // That is a property of the HOST, and telling the user to sign in cannot change it.
        var (app, http, cloud) = await StartAsync(account: null);
        try
        {
            var resp = await PostEmail(http);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            var error = (await Json(resp)).GetProperty("error").GetString() ?? "";
            Assert.Contains("no operating system credential store", error, StringComparison.OrdinalIgnoreCase);
            Assert.False(error.Contains("Sign in from the Gateway tray", StringComparison.OrdinalIgnoreCase),
                $"a host that cannot hold a credential told the user to sign in. Body: {error}");
            Assert.Equal(0, cloud.Calls);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }
}
