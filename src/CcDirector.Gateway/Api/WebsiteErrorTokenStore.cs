using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The website's error-report credential (the Error Logging mission, issue #3675), minted BY this Gateway and kept
/// as a hash on its durable storage.
///
/// WHY THE GATEWAY MINTS IT. The hosted Gateway's app settings may be changed only by the deploy workflow, which sets
/// the Teams switches and nothing else, so a secret read from an app setting would have no legal way in. Instead an
/// administrator asks the Gateway for one (<see cref="WebsiteErrorEndpoints.TokenPath"/>, behind the administrator
/// service token); the Gateway answers with the value ONCE, keeps only its SHA-256, and the owner puts the value into
/// the website's environment. Minting again rotates it: the old value stops working at once.
///
/// WHAT IT CAN DO. Write error reports with component "website" through <see cref="WebsiteErrorEndpoints.Path"/>,
/// and nothing else. It is checked by no other route.
/// </summary>
internal sealed class WebsiteErrorTokenStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _file;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();

    public WebsiteErrorTokenStore(string folder, Func<DateTime>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _file = Path.Combine(folder, "token.json");
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    private sealed record Stored(
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("minted_utc")] DateTime MintedUtc);

    /// <summary>Mint a new token, replacing any before it. Returns the value - the only time it exists outside the
    /// caller's hands - and when it was minted.</summary>
    public (string Token, DateTime MintedUtc) Mint()
    {
        var token = "dtwe_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var minted = _clock();
        var stored = new Stored(HashOf(token), minted);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            // Written beside and moved over, so a reader never sees half a file.
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json));
            File.Move(temp, _file, overwrite: true);
        }
        FileLog.Write($"[WebsiteErrorTokenStore] minted a new website error token at {minted:O}; any earlier one no longer works");
        return (token, minted);
    }

    /// <summary>
    /// Whether <paramref name="presented"/> is the current token. Null when no token has been minted yet - which the
    /// route answers differently from a wrong token, in its log, so the owner can tell "not set up" from "wrong".
    /// Compared in fixed time over the digests.
    /// </summary>
    public bool? Matches(string presented)
    {
        Stored? stored;
        lock (_lock)
        {
            if (!File.Exists(_file)) return null;
            stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file));
        }
        if (stored is null || string.IsNullOrWhiteSpace(stored.Sha256))
            throw new InvalidDataException($"the website error token file {_file} is not readable; mint a new token");
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(HashOf(presented.Trim())),
            Encoding.ASCII.GetBytes(stored.Sha256));
    }

    internal static string HashOf(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
