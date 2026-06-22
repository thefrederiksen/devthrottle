using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Builds test-issued HMAC-SHA256 ("HS256") JSON Web Tokens for the Gateway-hosted credential service
/// tests (issue #636), so the service can be proven against a test token pair while the live browser
/// sign-in (issue #637) does not yet exist. Mirrors the shape of a Supabase access token: a header
/// declaring HS256, a payload with an <c>exp</c> claim (and optionally the identity claims), and an
/// HMAC-SHA256 signature.
/// </summary>
internal static class GatewayTestJwt
{
    public const string SigningSecret = "gateway-test-signing-secret-for-issue-636-credential-service";

    /// <summary>Creates a valid HS256 token signed with <paramref name="secret"/>, expiring at <paramref name="expiresAtUtc"/>.</summary>
    public static string Create(DateTime expiresAtUtc, string secret = SigningSecret) =>
        Build(expiresAtUtc, email: null, provider: null, secret);

    /// <summary>
    /// Creates a valid HS256 token whose payload also carries the Supabase-shaped identity claims: an
    /// <c>email</c> claim and a provider under <c>app_metadata.provider</c>. When <paramref name="provider"/>
    /// is null the provider claim is omitted so the "provider absent" path can be exercised.
    /// </summary>
    public static string CreateWithIdentity(DateTime expiresAtUtc, string email, string? provider, string secret = SigningSecret) =>
        Build(expiresAtUtc, email, provider, secret);

    private static string Build(DateTime expiresAtUtc, string? email, string? provider, string secret)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT",
        }));

        var payloadClaims = new Dictionary<string, object>
        {
            ["sub"] = "gateway-test-user",
            ["exp"] = new DateTimeOffset(expiresAtUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
        };
        if (email is not null)
            payloadClaims["email"] = email;
        if (provider is not null)
            payloadClaims["app_metadata"] = new Dictionary<string, object> { ["provider"] = provider };

        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payloadClaims));

        var signingInput = $"{header}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
        return $"{signingInput}.{signature}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
