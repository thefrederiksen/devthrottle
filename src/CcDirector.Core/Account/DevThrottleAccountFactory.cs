using System.Runtime.Versioning;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// Builds the <see cref="DevThrottleAccountService"/> the startup gate (issue #580) consumes, wiring
/// it to the operating-system credential store (issue #583), the local token validator, the
/// authentication-event log, and the token refresher. It keeps the "where does the signing secret
/// come from" concern out of the application startup code.
///
/// A cached access token's signature is verified locally: an ES256 token against the backend's
/// published public key set (<see cref="DevThrottleSigningKeys"/>), an HS256 token against the
/// signing secret read from the <c>DEVTHROTTLE_JWT_SIGNING_SECRET</c> environment variable. Until
/// the live backend sign-in exists
/// (a dependency flagged on issue #580), the gate is exercised with a test-issued token: when
/// <c>DEVTHROTTLE_TEST_SEED_TOKEN</c> is set, this factory seeds that test token pair into the
/// credential store on construction so the "credential present" startup outcomes can be proven. Both
/// environment variables are a documented test seam, not production configuration.
/// </summary>
public static class DevThrottleAccountFactory
{
    /// <summary>The environment variable carrying the HMAC-SHA256 signing secret for token validation.</summary>
    public const string SigningSecretEnvVar = "DEVTHROTTLE_JWT_SIGNING_SECRET";

    /// <summary>The environment variable carrying a test access token to seed (the access-plus-refresh pair, split on a single newline).</summary>
    public const string TestSeedTokenEnvVar = "DEVTHROTTLE_TEST_SEED_TOKEN";

    /// <summary>
    /// Creates the credential service for the running Director on Windows, using Windows Data
    /// Protection as the credential store. Honors the test seam (see the type summary) so the gate's
    /// "credential present" outcomes can be proven before the live backend exists.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static DevThrottleAccountService CreateForWindows()
    {
        FileLog.Write("[DevThrottleAccountFactory] CreateForWindows: building credential service");

        var store = new WindowsProtectedTokenStore();
        var service = Build(store);
        SeedTestCredentialIfRequested(service);
        return service;
    }

    /// <summary>
    /// Creates the credential service over an explicit store. Used by tests and by non-Windows
    /// startup paths that supply their own <see cref="IProtectedTokenStore"/> implementation.
    /// </summary>
    public static DevThrottleAccountService Build(IProtectedTokenStore store)
    {
        if (store is null)
            throw new ArgumentNullException(nameof(store));

        var validator = new JwtAccessTokenValidator(
            ResolveSigningSecret(),
            publicKeySetJson: DevThrottleSigningKeys.ResolvePublicKeySet());
        var eventLog = new AuthEventLog();
        var refresher = new BackendUnavailableTokenRefresher();

        FileLog.Write("[DevThrottleAccountFactory] Build: credential service constructed");
        return new DevThrottleAccountService(store, validator, eventLog, refresher);
    }

    /// <summary>
    /// Resolves the signing secret from the environment. When unset, returns a non-empty placeholder
    /// so the validator can be constructed: a cached token signed by the real backend will simply
    /// fail signature verification (reported as not logged in), which is the correct, explicit
    /// behavior - not a fallback that hides a problem. The secret itself is never logged.
    /// </summary>
    private static string ResolveSigningSecret()
    {
        var secret = Environment.GetEnvironmentVariable(SigningSecretEnvVar);
        if (string.IsNullOrEmpty(secret))
        {
            FileLog.Write($"[DevThrottleAccountFactory] ResolveSigningSecret: {SigningSecretEnvVar} not set; using placeholder (a backend-signed token would not verify until the secret is configured)");
            return "devthrottle-signing-secret-not-configured";
        }

        FileLog.Write($"[DevThrottleAccountFactory] ResolveSigningSecret: signing secret resolved from {SigningSecretEnvVar}");
        return secret;
    }

    /// <summary>
    /// When the test-seed environment variable is set, stores its access-plus-refresh token pair in
    /// the credential store so the gate's "credential present" outcomes can be proven. The pair is
    /// the access token and refresh token separated by a single newline; the tokens themselves are
    /// never logged.
    /// </summary>
    private static void SeedTestCredentialIfRequested(DevThrottleAccountService service)
    {
        var seed = Environment.GetEnvironmentVariable(TestSeedTokenEnvVar);
        if (string.IsNullOrEmpty(seed))
            return;

        FileLog.Write($"[DevThrottleAccountFactory] SeedTestCredentialIfRequested: {TestSeedTokenEnvVar} is set; seeding a test credential into the store");
        var parts = seed.Split('\n', 2);
        var accessToken = parts[0].Trim();
        var refreshToken = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        service.StoreTokens(new DevThrottleTokens(accessToken, refreshToken));
        FileLog.Write("[DevThrottleAccountFactory] SeedTestCredentialIfRequested: test credential stored");
    }
}
