using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// Reads the signed-in identity (email and authentication provider) from a DevThrottle access token's
/// claims, entirely locally - no network call. DevThrottle access tokens are Supabase JSON Web Tokens
/// whose payload carries the user's <c>email</c> claim and the provider under
/// <c>app_metadata.provider</c>. This reader only decodes the payload to surface those two display
/// values for the account area (issue #582); it does NOT verify the token's signature - signature and
/// expiry validation is the job of <see cref="JwtAccessTokenValidator"/>, and the account area only
/// shows identity for a credential the gate has already accepted.
///
/// When the payload is malformed or the claims are absent, the reader returns null rather than
/// inventing a value, so the account area can show an explicit "identity unavailable" state instead of
/// a fabricated one (no fallback that hides a problem).
/// </summary>
public static class JwtIdentityReader
{
    /// <summary>
    /// Reads the email and provider from the access token's payload claims. Returns null when the
    /// token is not a three-part JSON Web Token, the payload cannot be decoded, or the email claim is
    /// absent. The provider falls back to "unknown" only as a displayed label when the claim is
    /// genuinely missing from an otherwise-valid payload - it is never substituted for a missing email.
    /// </summary>
    public static AccountIdentity? Read(string accessToken)
    {
        FileLog.Write("[JwtIdentityReader] Read: extracting identity claims from the cached access token (no network call)");

        var parts = accessToken?.Split('.') ?? Array.Empty<string>();
        if (parts.Length != 3)
        {
            FileLog.Write("[JwtIdentityReader] Read: not a three-part JSON Web Token -> no identity");
            return null;
        }

        var payloadJson = DecodeSegment(parts[1]);
        if (payloadJson is null)
        {
            FileLog.Write("[JwtIdentityReader] Read: payload could not be decoded -> no identity");
            return null;
        }

        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;

        var email = ReadEmail(root);
        if (string.IsNullOrWhiteSpace(email))
        {
            FileLog.Write("[JwtIdentityReader] Read: no email claim present -> no identity");
            return null;
        }

        var provider = ReadProvider(root);
        FileLog.Write($"[JwtIdentityReader] Read: identity resolved (provider={provider})");
        return new AccountIdentity(email, provider);
    }

    /// <summary>Reads the <c>email</c> claim from the payload, or null when it is absent or not a string.</summary>
    private static string? ReadEmail(JsonElement root)
    {
        if (root.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String)
            return email.GetString();
        return null;
    }

    /// <summary>
    /// Reads the authentication provider. Supabase records it under <c>app_metadata.provider</c>;
    /// when that is absent, returns the label "unknown" so the account area can still render the
    /// (present) email with an honest, non-fabricated provider value.
    /// </summary>
    private static string ReadProvider(JsonElement root)
    {
        if (root.TryGetProperty("app_metadata", out var appMetadata)
            && appMetadata.ValueKind == JsonValueKind.Object
            && appMetadata.TryGetProperty("provider", out var provider)
            && provider.ValueKind == JsonValueKind.String)
        {
            var value = provider.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "unknown";
    }

    /// <summary>
    /// Decodes a base64url JSON Web Token segment (no padding) to its UTF-8 string, or null when the
    /// segment is not valid base64url.
    /// </summary>
    private static string? DecodeSegment(string segment)
    {
        var normalized = segment.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
        }

        if (!Convert.TryFromBase64String(normalized, new byte[normalized.Length], out _))
            return null;

        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }
}
