using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The published verification keys for DevThrottle access tokens. The DevThrottle backend (the
/// Supabase project behind devthrottle.com) signs access tokens with ES256 - an elliptic-curve
/// P-256 signature made with the project's PRIVATE key. Verifying such a token needs only the
/// matching PUBLIC key, which the project publishes as its JSON Web Key Set at
/// <c>https://ompujpfrglgqvqprilxa.supabase.co/auth/v1/.well-known/jwks.json</c>. That public key
/// set is embedded below so a cached token can be verified entirely locally - no network call at
/// validation time, and no secret material shipped with the app (a public key cannot mint tokens).
///
/// When the Supabase signing key is rotated, the embedded set must be updated and a new build
/// released. The environment-variable override exists so a rotated key set can be supplied to an
/// existing install without a rebuild, and so tests can supply their own key set.
/// </summary>
public static class DevThrottleSigningKeys
{
    /// <summary>
    /// The environment variable that overrides the embedded public key set. Its value is a JSON Web
    /// Key Set document (the same shape the backend publishes). Unset in normal production use.
    /// </summary>
    public const string PublicKeySetEnvVar = "DEVTHROTTLE_JWT_PUBLIC_KEY_SET";

    /// <summary>
    /// The DevThrottle backend's published public key set, embedded at build time. Public material
    /// only - it can verify a token's signature but can never create one.
    /// </summary>
    public const string ProductionPublicKeySetJson =
        """{"keys":[{"alg":"ES256","crv":"P-256","ext":true,"key_ops":["verify"],"kid":"78abda78-683e-480c-9111-a8f320011550","kty":"EC","use":"sig","x":"F2hR3ftzfzyscTqcZr1u7OyTKATZRehFNCT033Reng8","y":"p2o6FP4Km3WGl39BUB7IAdeBK8jFK602lVq6cDL556E"}]}""";

    /// <summary>
    /// Resolves the public key set the token validator verifies ES256 signatures against: the
    /// environment-variable override when set, otherwise the embedded production key set. Public
    /// keys are not secret, so which source was used is logged.
    /// </summary>
    public static string ResolvePublicKeySet()
    {
        var overrideValue = Environment.GetEnvironmentVariable(PublicKeySetEnvVar);
        if (!string.IsNullOrEmpty(overrideValue))
        {
            FileLog.Write($"[DevThrottleSigningKeys] ResolvePublicKeySet: public key set resolved from {PublicKeySetEnvVar}");
            return overrideValue;
        }

        FileLog.Write("[DevThrottleSigningKeys] ResolvePublicKeySet: using the embedded production public key set");
        return ProductionPublicKeySetJson;
    }
}
