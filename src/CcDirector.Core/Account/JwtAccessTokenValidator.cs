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
/// call. DevThrottle access tokens are Supabase JSON Web Tokens signed with HMAC-SHA256 (the
/// "HS256" algorithm), so the signature is verified against the configured signing secret without
/// contacting any server. A token with a wrong or tampered signature, an unsupported algorithm, or
/// a malformed structure is reported as not valid (and not well-formed), so the logged-in check
/// treats it as not logged in.
/// </summary>
public sealed class JwtAccessTokenValidator
{
    private readonly byte[] _signingSecret;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the validator with the HMAC-SHA256 signing secret the backend issues tokens with.
    /// </summary>
    /// <param name="signingSecret">The shared signing secret used to verify the token's HMAC-SHA256 signature.</param>
    /// <param name="timeProvider">Time source for expiry checks; defaults to the system clock. Injected so tests control "now".</param>
    public JwtAccessTokenValidator(string signingSecret, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrEmpty(signingSecret))
            throw new ArgumentException("Signing secret is required", nameof(signingSecret));

        _signingSecret = Encoding.UTF8.GetBytes(signingSecret);
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

        if (!HeaderDeclaresHs256(parts[0]))
        {
            FileLog.Write("[JwtAccessTokenValidator] Validate: header does not declare the HMAC-SHA256 algorithm");
            return new AccessTokenValidation(IsValid: false, IsExpiredButWellFormed: false, ExpiresAtUtc: null);
        }

        if (!SignatureVerifies(parts[0], parts[1], parts[2]))
        {
            FileLog.Write("[JwtAccessTokenValidator] Validate: signature does not verify (tampered or wrong key)");
            return new AccessTokenValidation(IsValid: false, IsExpiredButWellFormed: false, ExpiresAtUtc: null);
        }

        var expiresAtUtc = ReadExpiry(parts[1]);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var expired = expiresAtUtc is not null && expiresAtUtc.Value <= nowUtc;

        FileLog.Write($"[JwtAccessTokenValidator] Validate: signature OK, expiresAtUtc={expiresAtUtc:o}, expired={expired}");
        return new AccessTokenValidation(IsValid: !expired, IsExpiredButWellFormed: expired, ExpiresAtUtc: expiresAtUtc);
    }

    private static bool HeaderDeclaresHs256(string encodedHeader)
    {
        var headerJson = DecodeSegment(encodedHeader);
        if (headerJson is null)
            return false;

        using var doc = JsonDocument.Parse(headerJson);
        return doc.RootElement.TryGetProperty("alg", out var alg)
            && string.Equals(alg.GetString(), "HS256", StringComparison.Ordinal);
    }

    private bool SignatureVerifies(string encodedHeader, string encodedPayload, string encodedSignature)
    {
        var signingInput = Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedPayload}");
        using var hmac = new HMACSHA256(_signingSecret);
        var expected = hmac.ComputeHash(signingInput);

        var actual = DecodeSegmentBytes(encodedSignature);
        if (actual is null)
            return false;

        return CryptographicOperations.FixedTimeEquals(expected, actual);
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
