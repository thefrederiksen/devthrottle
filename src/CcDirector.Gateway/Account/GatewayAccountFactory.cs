using System.Runtime.Versioning;
using CcDirector.Core.Account;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Builds the Gateway-hosted DevThrottle credential service - the Gateway Centralization Phase 2
/// foundation (issue #636). Phase 2 moves the DevThrottle account off each Director and onto the
/// Gateway: this factory wires the reused <see cref="DevThrottleAccountService"/> (the same Core
/// account type the Director side used, reused here as-is, not duplicated) to a credential store rooted
/// under the GATEWAY config directory (config/gateway), so the
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
    /// The environment variable that OVERRIDES the backend refresh-exchange endpoint the Gateway-owned
    /// token refresher (issue #640) uses. Since issue #876 the endpoint defaults to the embedded
    /// production backend (<see cref="CcDirector.Core.Account.DevThrottleAuthBackend"/>); the override
    /// exists for tests and for pointing an install at a different backend without a rebuild.
    /// </summary>
    public const string RefreshUrlEnvVar = GatewayHttpTokenRefresher.RefreshUrlEnvVar;

    /// <summary>The environment variable overriding the expected account-token audience (Hosted Multi-Tenancy
    /// increment 1). Unset in production; tests point it at their test audience.</summary>
    public const string SupabaseAudienceEnvVar = "CC_GATEWAY_SUPABASE_AUDIENCE";

    /// <summary>The environment variable overriding the expected account-token issuer. Unset in production;
    /// tests point it at their test issuer.</summary>
    public const string SupabaseIssuerEnvVar = "CC_GATEWAY_SUPABASE_ISSUER";

    /// <summary>The Supabase audience (<c>aud</c>) a DevThrottle account token must carry to authorize hosted
    /// enrollment. Supabase mints authenticated-user tokens with this audience.</summary>
    public const string DefaultSupabaseAudience = "authenticated";

    /// <summary>The Supabase issuer (<c>iss</c>) a DevThrottle account token must carry - the project's auth
    /// endpoint (project ompujpfrglgqvqprilxa), the same project whose public key set verifies the signature.</summary>
    public const string DefaultSupabaseIssuer = "https://ompujpfrglgqvqprilxa.supabase.co/auth/v1";

    /// <summary>
    /// Build the AUTHORIZATION-mode account-token validator used by the hosted enrollment boundary (Hosted
    /// Multi-Tenancy increment 1). Unlike <see cref="Build"/>'s membership validator (which does not check
    /// audience/issuer), this one is configured with the Supabase audience and issuer so
    /// <see cref="JwtAccessTokenValidator.ValidateForAuthorization"/> enforces them and exposes the subject -
    /// the verified account id the tenant maps from. The public key set and signing secret resolve exactly as
    /// the membership validator's do; the audience and issuer are the Supabase defaults unless overridden for
    /// tests. Cross-platform (no operating-system credential store involved).
    /// </summary>
    public static JwtAccessTokenValidator BuildAuthorizationValidator()
    {
        var audience = Environment.GetEnvironmentVariable(SupabaseAudienceEnvVar);
        if (string.IsNullOrWhiteSpace(audience))
            audience = DefaultSupabaseAudience;

        var issuer = Environment.GetEnvironmentVariable(SupabaseIssuerEnvVar);
        if (string.IsNullOrWhiteSpace(issuer))
            issuer = DefaultSupabaseIssuer;

        return new JwtAccessTokenValidator(
            ResolveSigningSecret(),
            publicKeySetJson: DevThrottleSigningKeys.ResolvePublicKeySet(),
            expectedAudience: audience,
            expectedIssuer: issuer,
            // ES256-ONLY: Supabase account tokens are asymmetric ES256, verified against the project's public
            // key set. Refuse symmetric HS256, so the shared signing secret - which can be an unconfigured
            // public placeholder - can never be used to forge an arbitrary-subject enrollment token.
            allowSymmetricHs256: false);
    }

    /// <summary>
    /// Creates the Gateway-hosted credential service on Windows, using Windows Data Protection as the
    /// credential store under the Gateway config directory. Honors the signing-secret and test-seed
    /// environment variables (see the type summary) so the "credential present" outcomes can be proven
    /// before the live browser sign-in exists.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static DevThrottleAccountService CreateForWindows(bool hosted)
    {
        FileLog.Write("[GatewayAccountFactory] CreateForWindows: building the Gateway-hosted credential service");

        var store = new WindowsProtectedTokenStore(CcStorage.GatewayDevThrottleCredentialBlob());
        var service = Build(store, hosted);
        SeedTestCredentialIfRequested(service);
        return service;
    }

    /// <summary>
    /// Creates the Gateway-hosted credential service on macOS, using the login Keychain as the
    /// credential store (through the <c>security</c> command-line tool). Same contract and same
    /// environment seams as <see cref="CreateForWindows"/> - only the store binding differs, which is
    /// the whole point of the shared <see cref="IProtectedTokenStore"/> interface.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static DevThrottleAccountService CreateForMac(bool hosted)
    {
        FileLog.Write("[GatewayAccountFactory] CreateForMac: building the Gateway-hosted credential service");

        var store = new MacKeychainProtectedTokenStore();
        var service = Build(store, hosted);
        SeedTestCredentialIfRequested(service);
        return service;
    }

    /// <summary>
    /// Creates the Gateway-hosted credential service over an explicit credential store.
    /// Used by tests (which supply an in-memory or temp-directory store)
    /// and by non-Windows hosts that supply their own <see cref="IProtectedTokenStore"/>. Does NOT seed
    /// the test credential - callers that want the seed seam exercised use
    /// <see cref="SeedTestCredentialIfRequested"/> explicitly.
    /// </summary>
    /// <param name="store">The credential store.</param>
    /// <param name="hosted">The owning host's deployment signal (<see cref="GatewayHost.Hosted"/>): on hosted the
    /// validator refuses symmetric HS256 tokens. REQUIRED - see <see cref="GatewayHostOptions"/>.</param>
    public static DevThrottleAccountService Build(IProtectedTokenStore store, bool hosted)
    {
        if (store is null)
            throw new ArgumentNullException(nameof(store));

        var validator = new JwtAccessTokenValidator(
            ResolveSigningSecret(),
            publicKeySetJson: DevThrottleSigningKeys.ResolvePublicKeySet(),
            // Defense in depth (Hosted Multi-Tenancy increment 2a): in HOSTED mode this account/membership
            // validator is ES256-only too, so ANY hosted account-token authorization path - not just
            // enrollment - refuses a symmetric HS256 token forged against the (possibly public placeholder)
            // signing secret. Self-host is unaffected (HS256 stays allowed) so legacy non-hosted behavior does
            // not change. The audience/issuer are unset here (this is the membership validator, not the
            // authorization-mode enrollment validator), so this only hardens the signature-algorithm surface.
            allowSymmetricHs256: !hosted);
        // Issue #640 / #876: the real Gateway-owned token refresher. It exchanges the cached refresh
        // token for a fresh pair against the embedded production backend (environment override for
        // tests, see DevThrottleAuthBackend). An unreachable backend keeps the cached credential; a
        // definitive rejection (revoked session) clears it. A short timeout keeps a slow/unreachable
        // backend from holding a background refresh pass open. Tokens are never logged.
        var refresher = new GatewayHttpTokenRefresher(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });

        FileLog.Write("[GatewayAccountFactory] Build: Gateway credential service constructed");
        return new DevThrottleAccountService(store, validator, refresher);
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
