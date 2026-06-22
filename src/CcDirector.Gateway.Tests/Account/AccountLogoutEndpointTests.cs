using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// HTTP wire tests for <c>POST /account/logout</c> (issue #648, Gateway Centralization Phase 3). Boots
/// the logout endpoint - and, where it proves the before/after, the status endpoint - on an ephemeral
/// port over a Gateway credential service built on an in-memory token store (no Windows Data Protection,
/// no registry, no Tailscale), so the states are provable cross-platform. Proves the issue's acceptance
/// criteria:
/// <list type="number">
/// <item>logout CLEARS the Gateway credential: a seeded (signed-in) Gateway returns <c>signedIn:false</c>
/// after the POST, and <c>GET /account/status</c> then also reports <c>signedIn:false</c> with no
/// identity (the true before -> after);</item>
/// <item>the identity is read locally on the Gateway and the logout response NEVER contains the access or
/// refresh token;</item>
/// <item>logout on an already-not-signed-in Gateway is a harmless no-op that still reports
/// <c>signedIn:false</c>;</item>
/// <item>when Gateway auth is enabled the endpoint requires the Gateway token (401 without it) using the
/// SAME host-wide <see cref="AuthMiddleware"/> the real host applies.</item>
/// </list>
/// </summary>
public sealed class AccountLogoutEndpointTests
{
    private const string GatewayToken = "test-gateway-token-for-issue-648";

    /// <summary>
    /// An in-memory <see cref="IProtectedTokenStore"/> so the Gateway credential service can be built,
    /// seeded, and cleared without touching the Windows-only Data Protection store (mirrors the status
    /// endpoint test's pattern - the Gateway.Tests project does not reference the Core.Tests store).
    /// </summary>
    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// Builds a Gateway credential service over an in-memory store, with the signing secret set so a
    /// <see cref="GatewayTestJwt"/>-issued token validates. Optionally seeds a stored credential.
    /// </summary>
    private static DevThrottleAccountService MakeAccount(DevThrottleTokens? seed)
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var authEventsLog = Path.Combine(Path.GetTempPath(), "cc-gw-acct-logout-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var service = GatewayAccountFactory.Build(new InMemoryTokenStore(), authEventsLog);
            if (seed is not null)
                service.StoreTokens(seed);
            return service;
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    /// <summary>
    /// Boots a minimal Kestrel host on an ephemeral port mapping BOTH account endpoints over
    /// <paramref name="account"/> (the logout test often needs the status read to prove the after-state).
    /// When <paramref name="authEnabled"/> is true the SAME host-wide <see cref="AuthMiddleware"/> the real
    /// Gateway uses is applied, so the 401 path is exercised against the production gate.
    /// </summary>
    private static async Task<(WebApplication app, HttpClient http)> StartAsync(DevThrottleAccountService? account, bool authEnabled)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        if (authEnabled)
        {
            var requireToken = new AuthMiddleware.RequireToken { Token = GatewayToken, Devices = new DeviceRegistry(Path.Combine(Path.GetTempPath(), "cc-gw-acct-logout-dev-" + Guid.NewGuid().ToString("N") + ".json")) };
            app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        }

        AccountStatusEndpoint.Map(app, account);
        AccountLogoutEndpoint.Map(app, account);
        await app.StartAsync();

        var baseUrl = app.Urls.First();
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (app, http);
    }

    // Acceptance criterion 1 + 2 (the core of the issue): a signed-in Gateway -> POST /account/logout
    // returns signedIn:false, the credential is cleared (no token in the body), and GET /account/status
    // then reports signedIn:false with no identity. This is the true before -> after.
    [Fact]
    public async Task Logout_ClearsCredential_SignedInFlipsToFalse_AndNoToken()
    {
        var jwt = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "gateway-user@example.com", "github");
        const string refreshToken = "REFRESH-TOKEN-PLAINTEXT-MARKER-648";
        var account = MakeAccount(new DevThrottleTokens(jwt, refreshToken));

        var (app, http) = await StartAsync(account, authEnabled: false);
        try
        {
            // BEFORE: the Gateway holds a credential and reports signed-in with the identity.
            using (var before = await http.GetAsync("/account/status"))
            {
                using var beforeDoc = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
                Assert.True(beforeDoc.RootElement.GetProperty("signedIn").GetBoolean());
                Assert.Equal("gateway-user@example.com", beforeDoc.RootElement.GetProperty("email").GetString());
            }

            // ACT: log out.
            var resp = await http.PostAsync("/account/logout", content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("signedIn").GetBoolean());

            // The logout response must never include the access or refresh token (security rule DT-05).
            Assert.DoesNotContain(jwt, body, StringComparison.Ordinal);
            Assert.DoesNotContain(refreshToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);

            // The local credential service now reports not-signed-in directly.
            Assert.False(account.IsLoggedIn());

            // AFTER: GET /account/status confirms signedIn:false with NO identity fields.
            using (var after = await http.GetAsync("/account/status"))
            {
                using var afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
                Assert.False(afterDoc.RootElement.GetProperty("signedIn").GetBoolean());
                Assert.False(afterDoc.RootElement.TryGetProperty("email", out _), "email must be omitted after logout");
                Assert.False(afterDoc.RootElement.TryGetProperty("provider", out _), "provider must be omitted after logout");
            }
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Logout is idempotent: logging out an already-not-signed-in Gateway is a harmless no-op that still
    // reports signedIn:false.
    [Fact]
    public async Task Logout_WhenNotSignedIn_IsNoOp_ReportsSignedInFalse()
    {
        var account = MakeAccount(seed: null);

        var (app, http) = await StartAsync(account, authEnabled: false);
        try
        {
            var resp = await http.PostAsync("/account/logout", content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("signedIn").GetBoolean());
            Assert.False(account.IsLoggedIn());
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // No credential service on this host: logout still answers 200 signedIn:false (nothing to clear).
    [Fact]
    public async Task Logout_NoCredentialService_Returns200_SignedInFalse()
    {
        var (app, http) = await StartAsync(account: null, authEnabled: false);
        try
        {
            var resp = await http.PostAsync("/account/logout", content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("signedIn").GetBoolean());
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Acceptance criterion (auth): when Gateway auth is enabled the logout endpoint requires the Gateway
    // token - a call with NO Authorization header is answered 401 by the host-wide middleware before the
    // delegate runs, so an unauthenticated caller can never clear the credential.
    [Fact]
    public async Task Logout_AuthEnabled_NoToken_Returns401()
    {
        var account = MakeAccount(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-1"));

        var (app, http) = await StartAsync(account, authEnabled: true);
        try
        {
            var resp = await http.PostAsync("/account/logout", content: null);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

            // The credential must NOT have been cleared by the rejected call.
            Assert.True(account.IsLoggedIn());
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // The authorized path: with the Gateway token present the same auth-enabled endpoint passes (200) and
    // clears the credential.
    [Fact]
    public async Task Logout_AuthEnabled_WithToken_Returns200_AndClears()
    {
        var jwt = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "authed@example.com", "google");
        var account = MakeAccount(new DevThrottleTokens(jwt, "refresh-1"));

        var (app, http) = await StartAsync(account, authEnabled: true);
        try
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
            var resp = await http.PostAsync("/account/logout", content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("signedIn").GetBoolean());
            Assert.False(account.IsLoggedIn());
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }
}
