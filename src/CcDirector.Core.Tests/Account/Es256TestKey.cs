using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// A freshly-generated elliptic-curve P-256 signing key for tests. It signs ES256 JSON Web Tokens
/// the way the DevThrottle backend (Supabase) does - header declaring ES256 with a key id, payload
/// with an <c>exp</c> claim, and a 64-byte "R || S" signature - and exports its PUBLIC half as a
/// JSON Web Key Set document shaped like the one the backend publishes, so the validator can be
/// proven against a real asymmetric signature without any fixed key material in the repository.
/// </summary>
internal sealed class Es256TestKey : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public Es256TestKey(string keyId = "test-es256-key") => KeyId = keyId;

    public string KeyId { get; }

    /// <summary>The public half of this key as a JSON Web Key Set document (the backend-published shape).</summary>
    public string PublicKeySetJson()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    alg = "ES256",
                    crv = "P-256",
                    kid = KeyId,
                    kty = "EC",
                    use = "sig",
                    x = Base64Url(parameters.Q.X!),
                    y = Base64Url(parameters.Q.Y!),
                },
            },
        });
    }

    /// <summary>
    /// Creates a valid ES256 token signed with this key, expiring at <paramref name="expiresAtUtc"/>.
    /// The header carries this key's id unless <paramref name="keyIdOverride"/> substitutes another
    /// (to prove the unknown-key-id path) or <paramref name="includeKeyId"/> omits it entirely (to
    /// prove the no-key-id path).
    /// </summary>
    public string CreateToken(DateTime expiresAtUtc, bool includeKeyId = true, string? keyIdOverride = null)
    {
        var headerClaims = new Dictionary<string, object>
        {
            ["alg"] = "ES256",
            ["typ"] = "JWT",
        };
        if (includeKeyId)
            headerClaims["kid"] = keyIdOverride ?? KeyId;

        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(headerClaims));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = "test-user",
            ["exp"] = new DateTimeOffset(expiresAtUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
        }));

        var signingInput = $"{header}.{payload}";
        var signature = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    public void Dispose() => _key.Dispose();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
