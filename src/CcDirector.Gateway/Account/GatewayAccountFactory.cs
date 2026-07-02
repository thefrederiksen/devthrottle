using System.Runtime.Versioning;
using CcDirector.Core.Account;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Builds the Gateway-hosted DevThrottle credential service - the Gateway Centralization Phase 2
/// foundation (issue #636). Phase 2 moves the DevThrottle account off each Director and onto the
/// Gateway: this factory wires the reused <see cref="DevThrottleAccountService"/> (the same Core
/// account type the Director side used, reused here as-is, not duplicated) to a credential store and
/// authentication-event log rooted under the GATEWAY config directory (config/gateway), so the
/// Gateway holds the one machine-wide credential rather than each Director holding its own copy.
///
/// The service stores the access-plus-refresh token pair encrypted at rest (Windows Data Protection),
/// validates it entirely locally (signature plus expiry, no network call - ES256 against the backend's
/// published public key set, or HS256 against the configured shared secret), and reads the signed-in
/// identity (email and provider) from the cached token's claims. The HS256 signing secret is read
/// from the <c>DEVTHROTTLE_JWT_SIGNING_SECRET</c> environment variable and the
/// <c>DEVTHROTTLE_TEST_SEED_TOKEN</c> environment variable seeds a test token pair on construction so
/// the Gateway-hosted credential can be proven before the live browser sign-in (issue #637) exists.
/// Both environment variables are a documented test seam, not production configuration. The access and
/// refresh tokens are never written to the log on any path.
/// </summary>
public static class GatewayAccountFactory
{
    /// <summary>The environment variable carrying the HMAC-SHA256 signing secret used to validate a cached token.</summary>
    public const string SigningSecretEnvVar = "DEVTHROTTLE_JWT_SIGNING_SECRET";

    /// <summary>The environment variable carrying a test access-plus-refresh token pair to seed (split on a single newline).</summary>
    public const string TestSeedTokenEnvVar = "DEVTHROTTLE_TEST_SEED_TOKEN";

    /// <summary>
    /// The environment variable that configures the backend refresh-exchange endpoint the Gateway-owned
    /// token refresher (issue #640) uses. Mirrors <see cref="GatewayHttpTokenRefresher.RefreshUrlEnvVar"/>;
    /// when unset the refresher reports refresh unavailable and the cached credential is kept.
    /// </summary>
    public const string RefreshUrlEnvVar = GatewayHttpTokenRefresher.RefreshUrlEnvVar;

    /// <summary>
    /// Creates the Gateway-hosted credential service on Windows, using Windows Data Protection as the
    /// credential store under the Gateway config directory. Honors the signing-secret and test-seed
    /// environment variables (see the type summary) so the "credential present" outcomes can be proven
    /// before the live browser sign-in exists.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static DevThrottleAccountService CreateForWindows()
    {
        FileLog.Write("[GatewayAccountFactory] CreateForWindows: building the Gateway-hosted credential service");

        var store = new WindowsProtectedTokenStore(CcStorage.GatewayDevThrottleCredentialBlob());
        var service = Build(store, CcStorage.GatewayDevThrottleAuthEventsLog());
        SeedTestCredentialIfRequested(service);
        return service;
    }

    /// <summary>
    /// Creates the Gateway-hosted credential service over an explicit credential store and an explicit
    /// authentication-event log path. Used by tests (which supply an in-memory or temp-directory store)
    /// and by non-Windows hosts that supply their own <see cref="IProtectedTokenStore"/>. Does NOT seed
    /// the test credential - callers that want the seed seam exercised use
    /// <see cref="SeedTestCredentialIfRequested"/> explicitly.
    /// </summary>
    public static DevThrottleAccountService Build(IProtectedTokenStore store, string authEventsLogPath)
    {
        if (store is null)
            throw new ArgumentNullException(nameof(store));
        if (string.IsNullOrWhiteSpace(authEventsLogPath))
            throw new ArgumentException("Authentication-event log path is required", nameof(authEventsLogPath));

        var validator = new JwtAccessTokenValidator(
            ResolveSigningSecret(),
            publicKeySetJson: DevThrottleSigningKeys.ResolvePublicKeySet());
        var eventLog = new AuthEventLog(authEventsLogPath);
        // Issue #640: the real Gateway-owned token refresher replaces the no-op
        // BackendUnavailableTokenRefresher. It exchanges an expired token's refresh token for a fresh
        // pair against the backend ONLY when DEVTHROTTLE_REFRESH_URL is configured; with no endpoint
        // configured (or it unreachable) it reports refresh unavailable and the caller keeps the cached
        // credential (no fallback that hides the failure). A short timeout keeps a slow/unreachable
        // backend from holding a background refresh pass open. Tokens are never logged.
        var refresher = new GatewayHttpTokenRefresher(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });

        FileLog.Write("[GatewayAccountFactory] Build: Gateway credential service constructed");
        return new DevThrottleAccountService(store, validator, eventLog, refresher);
    }

    /// <summary>
    /// When the test-seed environment variable is set, stores its access-plus-refresh token pair in the
    /// credential store so the Gateway's "credential present" outcomes can be proven. The pair is the
    /// access token and refresh token separated by a single newline; the tokens themselves are never
    /// logged. A no-op when the variable is unset.
    /// </summary>
    public static void SeedTestCredentialIfRequested(DevThrottleAccountService service)
    {
        if (service is null)
            throw new ArgumentNullException(nameof(service));

        var seed = Environment.GetEnvironmentVariable(TestSeedTokenEnvVar);
        if (string.IsNullOrEmpty(seed))
            return;

        FileLog.Write($"[GatewayAccountFactory] SeedTestCredentialIfRequested: {TestSeedTokenEnvVar} is set; seeding a test credential into the Gateway store");
        var parts = seed.Split('\n', 2);
        var accessToken = parts[0].Trim();
        var refreshToken = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        service.StoreTokens(new DevThrottleTokens(accessToken, refreshToken));
        FileLog.Write("[GatewayAccountFactory] SeedTestCredentialIfRequested: test credential stored");
    }

    /// <summary>
    /// Resolves the signing secret from the environment. When unset, returns a non-empty placeholder so
    /// the validator can still be constructed: a cached token signed by the real backend will simply fail
    /// signature verification (reported as not logged in), which is the correct, explicit behavior - not a
    /// fallback that hides a problem. The secret itself is never logged.
    /// </summary>
    private static string ResolveSigningSecret()
    {
        var secret = Environment.GetEnvironmentVariable(SigningSecretEnvVar);
        if (string.IsNullOrEmpty(secret))
        {
            FileLog.Write($"[GatewayAccountFactory] ResolveSigningSecret: {SigningSecretEnvVar} not set; using placeholder (a backend-signed token would not verify until the secret is configured)");
            return "devthrottle-signing-secret-not-configured";
        }

        FileLog.Write($"[GatewayAccountFactory] ResolveSigningSecret: signing secret resolved from {SigningSecretEnvVar}");
        return secret;
    }
}
