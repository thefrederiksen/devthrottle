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
/// HTTP wire tests for <c>GET /account/status</c> (issue #638, Gateway Centralization Phase 2). Boots
/// only <see cref="AccountStatusEndpoint"/> on an ephemeral port over a Gateway credential service built
/// on an in-memory token store (no Windows Data Protection, no registry, no Tailscale), so the four
/// states are provable cross-platform. Proves the issue's five acceptance criteria:
/// <list type="number">
/// <item>200 with <c>signedIn:true</c> and the decoded <c>email</c>/<c>provider</c> when the Gateway
/// holds a valid (seeded) credential;</item>
/// <item>200 with <c>signedIn:false</c> and NO identity fields when the Gateway has no credential;</item>
/// <item>when Gateway auth is enabled the endpoint requires the Gateway token (401 without it) and
/// passes (200) with it - using the SAME host-wide <see cref="AuthMiddleware"/> the real host applies;</item>
/// <item>the response NEVER contains the access or refresh token (only the boolean + identity);</item>
/// <item>(this whole class) the signed-in, not-signed-in, and unauthorized paths are covered.</item>
/// </list>
/// The status read is entirely local (no network call) - the credential service's only network seam is
/// the refresher, which the status path never invokes.
/// </summary>
public sealed class AccountStatusEndpointTests
{
    private const string GatewayToken = "test-gateway-token-for-issue-638";

    /// <summary>
    /// An in-memory <see cref="IProtectedTokenStore"/> so the Gateway credential service can be built and
    /// seeded without touching the Windows-only Data Protection store. The Gateway.Tests project does not
    /// reference the Core.Tests in-memory store, so this mirrors it locally (the documented test pattern).
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
    /// <see cref="GatewayTestJwt"/>-issued token validates. The env var is set and restored around
    /// construction so the test is self-contained. Optionally seeds a stored credential.
    /// </summary>
    private static DevThrottleAccountService MakeAccount(DevThrottleTokens? seed)
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var authEventsLog = Path.Combine(Path.GetTempPath(), "cc-gw-acct-status-" + Guid.NewGuid().ToString("N") + ".jsonl");
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
    /// Boots a minimal Kestrel host on an ephemeral port mapping ONLY the account-status endpoint over
    /// <paramref name="account"/>. When <paramref name="authEnabled"/> is true the SAME host-wide
    /// <see cref="AuthMiddleware"/> the real Gateway uses is applied, so the 401 path is exercised
    /// against the production gate, not a stand-in.
    /// </summary>
    private static async Task<(WebApplication app, HttpClient http)> StartAsync(DevThrottleAccountService? account, bool authEnabled)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        if (authEnabled)
        {
            var requireToken = new AuthMiddleware.RequireToken { Token = GatewayToken, Devices = new DeviceRegistry(Path.Combine(Path.GetTempPath(), "cc-gw-acct-status-dev-" + Guid.NewGuid().ToString("N") + ".json")) };
            app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        }

        AccountStatusEndpoint.Map(app, account);
        await app.StartAsync();

        var baseUrl = app.Urls.First();
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (app, http);
    }

    // Acceptance criterion 1 + 4: a valid stored credential -> 200, signedIn:true, decoded email/provider,
    // and the response body carries NO token (neither the access JWT nor the refresh token).
    [Fact]
    public async Task SignedIn_Returns200_WithIdentity_AndNoToken()
    {
        var jwt = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "gateway-user@example.com", "github");
        const string refreshToken = "REFRESH-TOKEN-PLAINTEXT-MARKER-638";
        var account = MakeAccount(new DevThrottleTokens(jwt, refreshToken));

        var (app, http) = await StartAsync(account, authEnabled: false);
        try
        {
            var resp = await http.GetAsync("/account/status");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            Assert.True(root.GetProperty("signedIn").GetBoolean());
            Assert.Equal("gateway-user@example.com", root.GetProperty("email").GetString());
            Assert.Equal("github", root.GetProperty("provider").GetString());

            // Criterion 4: the response must never include the access or refresh token.
            Assert.DoesNotContain(jwt, body, StringComparison.Ordinal);
            Assert.DoesNotContain(refreshToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Acceptance criterion 2: no stored credential -> 200, signedIn:false, and NO identity fields present.
    [Fact]
    public async Task NotSignedIn_Returns200_SignedInFalse_NoIdentityFields()
    {
        var account = MakeAccount(seed: null);

        var (app, http) = await StartAsync(account, authEnabled: false);
        try
        {
            var resp = await http.GetAsync("/account/status");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("signedIn").GetBoolean());
            Assert.False(root.TryGetProperty("email", out _), "email must be omitted when not signed in");
            Assert.False(root.TryGetProperty("provider", out _), "provider must be omitted when not signed in");
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Acceptance criterion 2 (no credential service on this host): the endpoint still answers 200
    // signedIn:false with no identity fields when the Gateway holds no credential service at all.
    [Fact]
    public async Task NoCredentialService_Returns200_SignedInFalse()
    {
        var (app, http) = await StartAsync(account: null, authEnabled: false);
        try
        {
            var resp = await http.GetAsync("/account/status");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("signedIn").GetBoolean());
            Assert.False(doc.RootElement.TryGetProperty("email", out _));
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Acceptance criterion 3: when Gateway auth is enabled the endpoint requires the Gateway token -
    // a call with NO Authorization header is answered 401 by the host-wide middleware.
    [Fact]
    public async Task AuthEnabled_NoToken_Returns401()
    {
        var account = MakeAccount(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-1"));

        var (app, http) = await StartAsync(account, authEnabled: true);
        try
        {
            var resp = await http.GetAsync("/account/status");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Acceptance criterion 3 (the authorized path): with the Gateway token present the same auth-enabled
    // endpoint passes (200) and returns the signed-in status.
    [Fact]
    public async Task AuthEnabled_WithToken_Returns200()
    {
        var jwt = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "authed@example.com", "google");
        var account = MakeAccount(new DevThrottleTokens(jwt, "refresh-1"));

        var (app, http) = await StartAsync(account, authEnabled: true);
        try
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
            var resp = await http.GetAsync("/account/status");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("signedIn").GetBoolean());
            Assert.Equal("authed@example.com", doc.RootElement.GetProperty("email").GetString());
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }
}
