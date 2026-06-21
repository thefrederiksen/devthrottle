using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Builds test-issued HMAC-SHA256 ("HS256") JSON Web Tokens so the credential service can be proven
/// against a test token pair while the real backend sign-in does not yet exist (issue #583
/// authorizes this). Mirrors the shape of a Supabase access token: a header declaring HS256, a
/// payload with an <c>exp</c> claim, and an HMAC-SHA256 signature.
/// </summary>
internal static class TestJwt
{
    public const string SigningSecret = "test-signing-secret-for-issue-583-credential-store";

    /// <summary>Creates a valid HS256 token signed with <paramref name="secret"/>, expiring at <paramref name="expiresAtUtc"/>.</summary>
    public static string Create(DateTime expiresAtUtc, string secret = SigningSecret)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT",
        }));

        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = "test-user",
            ["exp"] = new DateTimeOffset(expiresAtUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
        }));

        var signingInput = $"{header}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
        return $"{signingInput}.{signature}";
    }

    /// <summary>Returns the token with its final signature byte flipped, so the signature no longer verifies.</summary>
    public static string Tamper(string token)
    {
        var lastChar = token[^1];
        var replacement = lastChar == 'A' ? 'B' : 'A';
        return token[..^1] + replacement;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
