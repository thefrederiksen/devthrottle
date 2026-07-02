using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Tests the Gateway-owned token refresh (issue #640, Gateway Centralization Phase 2). Verifies the
/// acceptance criteria: with an expired-but-well-formed access token and a refresh endpoint configured
/// (a local stub), the Gateway exchanges the refresh token and stores a fresh valid pair so the install
/// flips back to a non-expired signed-in token (criterion 1); with NO refresh endpoint configured the
/// Gateway keeps the cached credential, no exchange happens, and it does not crash (criterion 2); a
/// still-valid access token triggers NO refresh call at all (criterion 3). The refresh-on-expiry and
/// no-op paths are driven through <see cref="DevThrottleAccountService.RefreshIfNeededAsync"/> (the exact
/// path the background sweep runs), so the decision logic is exercised end-to-end against the real
/// <see cref="GatewayHttpTokenRefresher"/>.
///
/// The credential store under test is the real Windows Data Protection store, so the store/load round-trip
/// of the renewed pair is exercised on disk (the facts no-op on a non-Windows host - the operating-system
/// credential store is Windows-only for now, the #636 assumption). The class is annotated
/// [SupportedOSPlatform("windows")] so the platform-compatibility analyzer is satisfied.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GatewayTokenRefreshTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _blobPath;
    private readonly string _authEventsPath;

    public GatewayTokenRefreshTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-gw-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _blobPath = Path.Combine(_tempDir, "devthrottle-credential.bin");
        _authEventsPath = Path.Combine(_tempDir, "devthrottle-auth-events.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static bool OnWindows => OperatingSystem.IsWindows();

    /// <summary>
    /// Builds a Gateway credential service over the real Windows Data Protection store at the temp blob
    /// path, wired with the supplied refresher, so the refresh decision runs end-to-end. The signing
    /// secret env var is set and restored around construction so test-issued tokens validate.
    /// </summary>
    private DevThrottleAccountService MakeService(ITokenRefresher refresher)
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var store = new WindowsProtectedTokenStore(_blobPath);
            var validator = new JwtAccessTokenValidator(GatewayTestJwt.SigningSecret);
            var eventLog = new AuthEventLog(_authEventsPath);
            return new DevThrottleAccountService(store, validator, eventLog, refresher);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    // Criterion 1: with an expired-but-well-formed access token and a refresh endpoint configured (a local
    // stub), the Gateway exchanges the refresh token and stores a fresh valid pair - the install flips from
    // expired back to a non-expired, signed-in token.
    [Fact]
    public async Task RefreshIfNeeded_ExpiredTokenWithStubEndpoint_StoresFreshValidPair()
    {
        if (!OnWindows) return;

        await using var stub = await RefreshStub.StartAsync(requestRefreshToken =>
        {
            // The backend issues a brand-new, unexpired access token plus a rotated refresh token.
            var freshAccess = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
            return (freshAccess, "rotated-refresh-token");
        });

        var refresher = new GatewayHttpTokenRefresher(new HttpClient(), () => stub.Url);
        var service = MakeService(refresher);

        // Seed an EXPIRED but correctly-signed access token (well-formed -> renewable).
        var expiredAccess = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(-1));
        service.StoreTokens(new DevThrottleTokens(expiredAccess, "seed-refresh-token"));
        Assert.True(service.IsLoggedIn());                 // expired-but-well-formed still reports signed-in
        Assert.True(IsExpired(expiredAccess));             // the seeded token is genuinely past its expiry

        var refreshed = await service.RefreshIfNeededAsync();

        Assert.True(refreshed);
        Assert.Equal("seed-refresh-token", stub.LastRefreshToken);   // the backend received the cached refresh token
        // The stored access token is now non-expired: signed-in AND forwardable as a current token.
        var forwardable = service.GetAccessTokenForForwarding();
        Assert.NotNull(forwardable);
        Assert.False(IsExpired(forwardable));
    }

    // Criterion 2: with NO refresh endpoint configured, the Gateway keeps the cached credential (the expired
    // token is untouched), no exchange happens, and nothing crashes.
    [Fact]
    public async Task RefreshIfNeeded_ExpiredTokenNoEndpoint_KeepsCachedCredential()
    {
        if (!OnWindows) return;

        // The refresher resolves NO endpoint (the DEVTHROTTLE_REFRESH_URL-unset path), so it reports refresh
        // unavailable without any network call.
        var refresher = new GatewayHttpTokenRefresher(new HttpClient(), () => null);
        var service = MakeService(refresher);

        var expiredAccess = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(-1));
        service.StoreTokens(new DevThrottleTokens(expiredAccess, "seed-refresh-token"));

        var refreshed = await service.RefreshIfNeededAsync();

        Assert.False(refreshed);                            // no renewal performed
        // The cached credential is kept unchanged - still signed-in (well-formed) on the SAME expired token.
        var store = new WindowsProtectedTokenStore(_blobPath);
        var kept = store.Load();
        Assert.NotNull(kept);
        Assert.Equal(expiredAccess, kept.AccessToken);
        Assert.Equal("seed-refresh-token", kept.RefreshToken);
    }

    // Criterion 2 (unreachable endpoint variant): a configured-but-unreachable endpoint also keeps the
    // cached credential and does not crash - the same null "refresh unavailable" signal, no hiding fallback.
    [Fact]
    public async Task RefreshIfNeeded_ExpiredTokenUnreachableEndpoint_KeepsCachedCredential()
    {
        if (!OnWindows) return;

        // A configured URL that nothing is listening on. A short timeout keeps the test fast.
        var unreachableUrl = "http://127.0.0.1:0/refresh";
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var refresher = new GatewayHttpTokenRefresher(http, () => unreachableUrl);
        var service = MakeService(refresher);

        var expiredAccess = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(-1));
        service.StoreTokens(new DevThrottleTokens(expiredAccess, "seed-refresh-token"));

        var refreshed = await service.RefreshIfNeededAsync();

        Assert.False(refreshed);
        var store = new WindowsProtectedTokenStore(_blobPath);
        var kept = store.Load();
        Assert.NotNull(kept);
        Assert.Equal(expiredAccess, kept.AccessToken);
    }

    // Criterion 3: a still-valid (unexpired) access token triggers NO refresh call - the stub never receives
    // a request.
    [Fact]
    public async Task RefreshIfNeeded_ValidToken_TriggersNoExchange()
    {
        if (!OnWindows) return;

        await using var stub = await RefreshStub.StartAsync(_ =>
            (GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "rotated-refresh-token"));

        var refresher = new GatewayHttpTokenRefresher(new HttpClient(), () => stub.Url);
        var service = MakeService(refresher);

        var validAccess = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        service.StoreTokens(new DevThrottleTokens(validAccess, "seed-refresh-token"));

        var refreshed = await service.RefreshIfNeededAsync();

        Assert.False(refreshed);                            // no renewal
        Assert.Equal(0, stub.RequestCount);                 // the backend was never called
    }

    // The refresher itself, endpoint resolver returning null: reports unavailable (the unit beneath
    // the service). Since #876 production always resolves the embedded endpoint, but a null resolver
    // remains the explicit "exchange disabled" state for tests.
    [Fact]
    public async Task GatewayHttpTokenRefresher_NoEndpoint_ReportsUnavailable()
    {
        var refresher = new GatewayHttpTokenRefresher(new HttpClient(), () => null);

        var result = await refresher.RefreshAsync("any-refresh-token");

        Assert.Null(result.Renewed);
        Assert.False(result.RefreshTokenRejected);
    }

    // Issue #876: with the override environment variables unset, the refresher resolves the EMBEDDED
    // production backend - no machine configuration required. This is the defect the issue fixes (the
    // pre-#876 refresher was gated on an environment variable no install ever set).
    [Fact]
    public void DefaultResolution_NoEnvironmentOverrides_UsesEmbeddedProductionBackend()
    {
        var previousUrl = Environment.GetEnvironmentVariable(DevThrottleAuthBackend.RefreshUrlEnvVar);
        var previousKey = Environment.GetEnvironmentVariable(DevThrottleAuthBackend.AnonymousKeyEnvVar);
        Environment.SetEnvironmentVariable(DevThrottleAuthBackend.RefreshUrlEnvVar, null);
        Environment.SetEnvironmentVariable(DevThrottleAuthBackend.AnonymousKeyEnvVar, null);
        try
        {
            Assert.Equal(DevThrottleAuthBackend.ProductionRefreshUrl, DevThrottleAuthBackend.ResolveRefreshUrl());
            Assert.Equal(DevThrottleAuthBackend.ProductionAnonymousKey, DevThrottleAuthBackend.ResolveAnonymousKey());
            Assert.Contains("grant_type=refresh_token", DevThrottleAuthBackend.ProductionRefreshUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DevThrottleAuthBackend.RefreshUrlEnvVar, previousUrl);
            Environment.SetEnvironmentVariable(DevThrottleAuthBackend.AnonymousKeyEnvVar, previousKey);
        }
    }

    // Issue #876: the exchange carries the public anonymous key in the apikey header (the backend
    // answers 401 "No API key found" without it - verified against the live endpoint).
    [Fact]
    public async Task Exchange_SendsTheAnonymousKeyAsTheApiKeyHeader()
    {
        await using var stub = await RefreshStub.StartAsync(_ =>
            (GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "rotated-refresh-token"));

        var refresher = new GatewayHttpTokenRefresher(
            new HttpClient(), () => stub.Url, () => "test-anonymous-key");

        var result = await refresher.RefreshAsync("seed-refresh-token");

        Assert.NotNull(result.Renewed);
        Assert.Equal("test-anonymous-key", stub.LastApiKeyHeader);
    }

    // Issue #876: HTTP 400 is the backend DEFINITIVELY refusing the refresh token (revoked session or
    // rotated-away token). Driven end-to-end through the service: the dead credential is CLEARED, so
    // the install flips to signed-out instead of faking "Signed in" forever.
    [Fact]
    public async Task RefreshIfNeeded_BackendRejectsWith400_ClearsTheDeadCredential()
    {
        if (!OnWindows) return;

        await using var stub = await RefreshStub.StartRejectingAsync(
            """{"code":400,"error_code":"validation_failed","msg":"Refresh token is not valid"}""");

        var refresher = new GatewayHttpTokenRefresher(new HttpClient(), () => stub.Url);
        var service = MakeService(refresher);

        var expiredAccess = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(-1));
        service.StoreTokens(new DevThrottleTokens(expiredAccess, "revoked-refresh-token"));
        Assert.True(service.IsLoggedIn());

        var refreshed = await service.RefreshIfNeededAsync();

        Assert.False(refreshed);
        Assert.Equal(1, stub.RequestCount);
        Assert.False(service.IsLoggedIn());                 // the dead credential is gone
        var store = new WindowsProtectedTokenStore(_blobPath);
        Assert.Null(store.Load());
    }

    // Issue #876 contrast case: a server-side error (500) is NOT a rejection - the cached credential
    // is kept and the next sweep retries. Only a definitive 400 may clear a credential.
    [Fact]
    public async Task RefreshIfNeeded_BackendServerError_KeepsCachedCredential()
    {
        if (!OnWindows) return;

        await using var stub = await RefreshStub.StartRejectingAsync(
            """{"message":"internal error"}""", statusCode: 500);

        var refresher = new GatewayHttpTokenRefresher(new HttpClient(), () => stub.Url);
        var service = MakeService(refresher);

        var expiredAccess = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(-1));
        service.StoreTokens(new DevThrottleTokens(expiredAccess, "seed-refresh-token"));

        var refreshed = await service.RefreshIfNeededAsync();

        Assert.False(refreshed);
        Assert.True(service.IsLoggedIn());                  // still signed in on the cached credential
        var store = new WindowsProtectedTokenStore(_blobPath);
        Assert.NotNull(store.Load());
    }

    /// <summary>True when the access token's exp claim is in the past (used to assert expiry flips).</summary>
    private static bool IsExpired(string accessToken)
    {
        var parts = accessToken.Split('.');
        var payloadJson = DecodeSegment(parts[1]);
        using var doc = JsonDocument.Parse(payloadJson);
        var exp = doc.RootElement.GetProperty("exp").GetInt64();
        return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime <= DateTime.UtcNow;
    }

    private static string DecodeSegment(string segment)
    {
        var normalized = segment.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
        }
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }

    /// <summary>
    /// A local in-process stub of the backend refresh-exchange endpoint. Stands up a Kestrel server on a
    /// loopback port that answers the refresh POST with a caller-supplied { access_token, refresh_token }
    /// pair, recording how many times it was called and the last refresh token it received - so the tests
    /// can assert the exchange happened (or did not) without the live backend.
    /// </summary>
    private sealed class RefreshStub : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public string Url { get; }
        public int RequestCount { get; private set; }
        public string? LastRefreshToken { get; private set; }
        public string? LastApiKeyHeader { get; private set; }

        private RefreshStub(WebApplication app, string url)
        {
            _app = app;
            Url = url;
        }

        public static Task<RefreshStub> StartAsync(Func<string, (string AccessToken, string RefreshToken)> issue) =>
            StartCoreAsync(issue, errorBody: null, statusCode: 200);

        /// <summary>A stub that answers every exchange with the given error body and status (default 400, the definitive-rejection shape).</summary>
        public static Task<RefreshStub> StartRejectingAsync(string errorBody, int statusCode = 400) =>
            StartCoreAsync(issue: null, errorBody, statusCode);

        private static async Task<RefreshStub> StartCoreAsync(
            Func<string, (string AccessToken, string RefreshToken)>? issue, string? errorBody, int statusCode)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));   // OS-assigned free port
            var app = builder.Build();

            RefreshStub? self = null;
            app.MapPost("/refresh", async (HttpContext ctx) =>
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var receivedRefresh = doc.RootElement.GetProperty("refresh_token").GetString() ?? "";
                if (self is not null)
                {
                    self.RequestCount++;
                    self.LastRefreshToken = receivedRefresh;
                    self.LastApiKeyHeader = ctx.Request.Headers.TryGetValue("apikey", out var key) ? key.ToString() : null;
                }

                if (issue is null)
                    return Results.Content(errorBody ?? "{}", "application/json", statusCode: statusCode);

                var (access, refresh) = issue(receivedRefresh);
                return Results.Json(new Dictionary<string, string>
                {
                    ["access_token"] = access,
                    ["refresh_token"] = refresh,
                });
            });

            await app.StartAsync();
            var address = app.Urls.First();
            self = new RefreshStub(app, address.TrimEnd('/') + "/refresh");
            return self;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
