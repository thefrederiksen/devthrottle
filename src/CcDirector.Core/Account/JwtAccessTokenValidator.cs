using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The outcome of validating a cached access token locally. <see cref="IsValid"/> is true only
/// for a correctly-signed, unexpired token; <see cref="IsExpiredButWellFormed"/> distinguishes a
/// token that is genuinely ours but past its expiry (renewable with the refresh token) from one
/// that is malformed or carries a wrong signature (never logged in).
/// </summary>
/// <param name="IsValid">True when the signature verifies and the token has not expired.</param>
/// <param name="IsExpiredButWellFormed">True when the signature verifies but the token is past its expiry.</param>
/// <param name="ExpiresAtUtc">The token's expiry instant, when present.</param>
public sealed record AccessTokenValidation(bool IsValid, bool IsExpiredButWellFormed, DateTime? ExpiresAtUtc);

/// <summary>
/// Validates a cached access token entirely locally - signature and expiry only - with no network
/// call. DevThrottle access tokens are Supabase JSON Web Tokens, and two signing schemes are
/// supported: ES256 (the current Supabase signing keys - an elliptic-curve P-256 signature verified
/// against the backend's published PUBLIC key set, see <see cref="DevThrottleSigningKeys"/>) and
/// HS256 (the legacy shared-secret scheme, kept for installs configured with the signing secret and
/// for the test seam). A token with a wrong or tampered signature, an unsupported algorithm, or a
/// malformed structure is reported as not valid (and not well-formed), so the logged-in check
/// treats it as not logged in.
/// </summary>
public sealed class JwtAccessTokenValidator
{
    private readonly byte[] _signingSecret;
    private readonly IReadOnlyList<VerificationKey> _publicKeys;
    private readonly TimeProvider _timeProvider;

    /// <summary>One elliptic-curve P-256 public verification key from the configured key set.</summary>
    private sealed record VerificationKey(string? KeyId, ECParameters PublicKey);

    /// <summary>
    /// Creates the validator with the verification material for both supported signing schemes.
    /// </summary>
    /// <param name="signingSecret">The shared signing secret used to verify an HS256 token's signature.</param>
    /// <param name="timeProvider">Time source for expiry checks; defaults to the system clock. Injected so tests control "now".</param>
    /// <param name="publicKeySetJson">
    /// The JSON Web Key Set document holding the elliptic-curve P-256 PUBLIC keys used to verify an
    /// ES256 token's signature (see <see cref="DevThrottleSigningKeys.ResolvePublicKeySet"/>). Null
    /// or empty means no ES256 keys are configured, so every ES256 token is reported not valid. A
    /// malformed document throws - a broken key configuration must fail loud, not validate nothing.
    /// </param>
    public JwtAccessTokenValidator(string signingSecret, TimeProvider? timeProvider = null, string? publicKeySetJson = null)
    {
        if (string.IsNullOrEmpty(signingSecret))
            throw new ArgumentException("Signing secret is required", nameof(signingSecret));

        _signingSecret = Encoding.UTF8.GetBytes(signingSecret);
        _publicKeys = ParsePublicKeySet(publicKeySetJson);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Validates the access token's signature and expiry locally. Makes no network call. Returns a
    /// not-valid, not-well-formed result for a malformed token, an unsupported algorithm, or a wrong
    /// signature; a valid signature past expiry is reported well-formed-but-expired so the caller can
    /// renew it with the refresh token.
    /// </summary>
    public AccessTokenValidation Validate(string accessToken)
    {
        var parts = accessToken?.Split('.') ?? Array.Empty<string>();
        if (parts.Length != 3)
        {
            FileLog.Write("[JwtAccessTokenValidator] Validate: not a three-part JSON Web Token");
            return new AccessTokenValidation(IsValid: false, IsExpiredButWellFormed: false, ExpiresAtUtc: null);
        }

        var header = ReadHeader(parts[0]);
        if (header is null)
        {
            FileLog.Write("[JwtAccessTokenValidator] Validate: token header is malformed");
            return new AccessTokenValidation(IsValid: false, IsExpiredButWellFormed: false, ExpiresAtUtc: null);
        }

        var signatureVerifies = header.Value.Algorithm switch
        {
            "HS256" => HmacSignatureVerifies(parts[0], parts[1], parts[2]),
            "ES256" => EcdsaSignatureVerifies(header.Value.KeyId, parts[0], parts[1], parts[2]),
            _ => UnsupportedAlgorithm(header.Value.Algorithm),
        };

        if (!signatureVerifies)
            return new AccessTokenValidation(IsValid: false, IsExpiredButWellFormed: false, ExpiresAtUtc: null);

        var expiresAtUtc = ReadExpiry(parts[1]);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var expired = expiresAtUtc is not null && expiresAtUtc.Value <= nowUtc;

        FileLog.Write($"[JwtAccessTokenValidator] Validate: signature OK ({header.Value.Algorithm}), expiresAtUtc={expiresAtUtc:o}, expired={expired}");
        return new AccessTokenValidation(IsValid: !expired, IsExpiredButWellFormed: expired, ExpiresAtUtc: expiresAtUtc);
    }

    /// <summary>
    /// Reads the token header's signing algorithm and optional key id. Returns null when the header
    /// segment is not valid base64url or not a JSON object - a malformed token, not an error.
    /// </summary>
    private static (string? Algorithm, string? KeyId)? ReadHeader(string encodedHeader)
    {
        var headerJson = DecodeSegment(encodedHeader);
        if (headerJson is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(headerJson);
            var algorithm = doc.RootElement.TryGetProperty("alg", out var alg) ? alg.GetString() : null;
            var keyId = doc.RootElement.TryGetProperty("kid", out var kid) ? kid.GetString() : null;
            return (algorithm, keyId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool UnsupportedAlgorithm(string? algorithm)
    {
        FileLog.Write($"[JwtAccessTokenValidator] Validate: unsupported signing algorithm '{algorithm}' (only ES256 and HS256 are supported)");
        return false;
    }

    private bool HmacSignatureVerifies(string encodedHeader, string encodedPayload, string encodedSignature)
    {
        var signingInput = Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedPayload}");
        using var hmac = new HMACSHA256(_signingSecret);
        var expected = hmac.ComputeHash(signingInput);

        var actual = DecodeSegmentBytes(encodedSignature);
        if (actual is null)
            return false;

        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            FileLog.Write("[JwtAccessTokenValidator] Validate: HS256 signature does not verify (tampered or wrong secret)");
            return false;
        }

        return true;
    }

    private bool EcdsaSignatureVerifies(string? keyId, string encodedHeader, string encodedPayload, string encodedSignature)
    {
        if (_publicKeys.Count == 0)
        {
            FileLog.Write("[JwtAccessTokenValidator] Validate: ES256 token but no public key set is configured -> cannot verify");
            return false;
        }

        // An ES256 signature is the two 32-byte P-256 curve values concatenated (the JSON Web
        // Signature "R || S" form) - anything else cannot be a P-256 signature.
        var signature = DecodeSegmentBytes(encodedSignature);
        if (signature is null || signature.Length != 64)
        {
            FileLog.Write("[JwtAccessTokenValidator] Validate: ES256 signature is not a 64-byte P-256 signature");
            return false;
        }

        // A token that names its signing key is verified against that key only; a token without a
        // key id is verified against every configured key (verification either passes or it does not).
        var candidates = keyId is null
            ? _publicKeys
            : _publicKeys.Where(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal)).ToArray();
        if (candidates.Count == 0)
        {
            FileLog.Write($"[JwtAccessTokenValidator] Validate: the token's key id matches no configured public key (keyId={keyId})");
            return false;
        }

        var signingInput = Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedPayload}");
        foreach (var candidate in candidates)
        {
            using var ecdsa = ECDsa.Create(candidate.PublicKey);
            if (ecdsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256))
                return true;
        }

        FileLog.Write("[JwtAccessTokenValidator] Validate: ES256 signature does not verify (tampered or wrong key)");
        return false;
    }

    /// <summary>
    /// Parses the JSON Web Key Set document into the elliptic-curve P-256 verification keys. Keys of
    /// any other type or curve are skipped (a published key set may carry keys for other purposes);
    /// a document that is not a key set, or an elliptic-curve key missing its coordinates, throws -
    /// a broken key configuration must fail loud at construction, not silently verify nothing.
    /// </summary>
    private static IReadOnlyList<VerificationKey> ParsePublicKeySet(string? publicKeySetJson)
    {
        if (string.IsNullOrWhiteSpace(publicKeySetJson))
            return Array.Empty<VerificationKey>();

        using var doc = JsonDocument.Parse(publicKeySetJson);
        if (!doc.RootElement.TryGetProperty("keys", out var keysElement) || keysElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("The public key set is not a JSON Web Key Set document (no \"keys\" array)", nameof(publicKeySetJson));

        var keys = new List<VerificationKey>();
        foreach (var key in keysElement.EnumerateArray())
        {
            var keyType = key.TryGetProperty("kty", out var kty) ? kty.GetString() : null;
            var curve = key.TryGetProperty("crv", out var crv) ? crv.GetString() : null;
            if (!string.Equals(keyType, "EC", StringComparison.Ordinal) || !string.Equals(curve, "P-256", StringComparison.Ordinal))
            {
                FileLog.Write($"[JwtAccessTokenValidator] ParsePublicKeySet: skipping a non-P-256 key (kty={keyType}, crv={curve})");
                continue;
            }

            var x = key.TryGetProperty("x", out var xElement) ? DecodeSegmentBytes(xElement.GetString() ?? string.Empty) : null;
            var y = key.TryGetProperty("y", out var yElement) ? DecodeSegmentBytes(yElement.GetString() ?? string.Empty) : null;
            if (x is null || y is null)
                throw new ArgumentException("An elliptic-curve key in the public key set is missing its x or y coordinate", nameof(publicKeySetJson));

            var keyId = key.TryGetProperty("kid", out var kid) ? kid.GetString() : null;
            keys.Add(new VerificationKey(keyId, new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y },
            }));
        }

        FileLog.Write($"[JwtAccessTokenValidator] ParsePublicKeySet: {keys.Count} P-256 verification key(s) configured");
        return keys;
    }

    private static DateTime? ReadExpiry(string encodedPayload)
    {
        var payloadJson = DecodeSegment(encodedPayload);
        if (payloadJson is null)
            return null;

        using var doc = JsonDocument.Parse(payloadJson);
        if (!doc.RootElement.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number)
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()).UtcDateTime;
    }

    private static string? DecodeSegment(string segment)
    {
        var bytes = DecodeSegmentBytes(segment);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static byte[]? DecodeSegmentBytes(string segment)
    {
        // JSON Web Tokens use base64url without padding; restore standard base64 before decoding.
        var normalized = segment.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
        }

        return Convert.TryFromBase64String(normalized, new byte[normalized.Length], out var written)
            ? Convert.FromBase64String(normalized)
            : null;
    }
}
