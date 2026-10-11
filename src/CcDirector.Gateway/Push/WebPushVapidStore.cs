using System.Security.Cryptography;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Push;

/// <summary>
/// Holds the single VAPID (Voluntary Application Server Identification) key pair the Gateway uses
/// to sign Web Push messages to the mobile Progressive Web App (the app-icon "needs you" dot).
///
/// The key pair is generated ONCE with the platform's own P-256 elliptic-curve crypto and then
/// persisted to <c>%LOCALAPPDATA%\cc-director\config\gateway\webpush-vapid.json</c> so it survives
/// a Gateway restart - a push subscription a phone made against one public key keeps working only
/// while the Gateway keeps signing with the matching private key. The file holds the PRIVATE key,
/// so it is a secret: it lives under the per-user config root (locked to the current user) and is
/// NEVER logged (security rule DT-05). The PUBLIC key is not a secret - the phone needs it to
/// subscribe - and is served to the app by <see cref="Api.WebPushEndpoints"/>.
///
/// The two keys are stored as unpadded base64url strings, the encoding the Web Push ecosystem and
/// <c>Lib.Net.Http.WebPush</c> expect: the public key is the 65-byte uncompressed EC point
/// (<c>0x04 || X || Y</c>) and the private key is the 32-byte scalar <c>d</c>.
/// </summary>
public sealed class WebPushVapidStore
{
    private const int FieldSize = 32; // P-256 coordinate / scalar size in bytes.

    private readonly string _storePath;

    /// <summary>The 65-byte uncompressed public key, unpadded base64url. Safe to hand to clients.</summary>
    public string PublicKey { get; }

    /// <summary>The 32-byte private scalar, unpadded base64url. SECRET - never log or serve this.</summary>
    public string PrivateKey { get; }

    public WebPushVapidStore() : this(null) { }

    /// <summary>The key file under <paramref name="storageRoot"/>: what a host whose root that is uses.</summary>
    public static string PathUnder(string storageRoot) =>
        Path.Combine(CcStorage.ToolConfig(storageRoot, "gateway"), "webpush-vapid.json");

    /// <param name="storePath">Override the key file (tests pass an isolated temp path); production
    /// omits it for the shared default under the gateway config root.</param>
    public WebPushVapidStore(string? storePath)
    {
        _storePath = string.IsNullOrWhiteSpace(storePath)
            ? PathUnder(CcStorage.Root())
            : storePath;

        var loaded = Load();
        if (loaded is not null)
        {
            PublicKey = loaded.Value.publicKey;
            PrivateKey = loaded.Value.privateKey;
            FileLog.Write($"[WebPushVapidStore] Loaded VAPID key pair from {_storePath}");
            return;
        }

        (PublicKey, PrivateKey) = Generate();
        Save(PublicKey, PrivateKey);
        FileLog.Write($"[WebPushVapidStore] Generated a new VAPID key pair at {_storePath}");
    }

    /// <summary>The on-disk key file path.</summary>
    public string StorePath => _storePath;

    /// <summary>
    /// Generate a fresh P-256 VAPID key pair and return both keys as unpadded base64url. Uses the
    /// platform crypto directly (no external dependency): the public key is the uncompressed point,
    /// the private key is the raw scalar.
    /// </summary>
    private static (string publicKey, string privateKey) Generate()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(includePrivateParameters: true);

        var x = LeftPad(p.Q.X!, FieldSize);
        var y = LeftPad(p.Q.Y!, FieldSize);
        var d = LeftPad(p.D!, FieldSize);

        var point = new byte[1 + FieldSize + FieldSize];
        point[0] = 0x04; // uncompressed-point marker
        Array.Copy(x, 0, point, 1, FieldSize);
        Array.Copy(y, 0, point, 1 + FieldSize, FieldSize);

        return (Base64Url(point), Base64Url(d));
    }

    private (string publicKey, string privateKey)? Load()
    {
        if (!File.Exists(_storePath)) return null;
        var json = File.ReadAllText(_storePath);
        if (string.IsNullOrWhiteSpace(json)) return null;

        var record = JsonSerializer.Deserialize<VapidRecord>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if (record is null
            || string.IsNullOrWhiteSpace(record.PublicKey)
            || string.IsNullOrWhiteSpace(record.PrivateKey))
            return null;

        return (record.PublicKey, record.PrivateKey);
    }

    private void Save(string publicKey, string privateKey)
    {
        var dir = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(
            new VapidRecord { PublicKey = publicKey, PrivateKey = privateKey },
            new JsonSerializerOptions { WriteIndented = true });

        // Atomic replace (temp file + move) so a crash mid-write never leaves a half-written key file.
        var temp = _storePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _storePath, overwrite: true);
    }

    /// <summary>Left-pad (or trim leading zero bytes) so a curve field is exactly <paramref name="size"/> bytes.</summary>
    private static byte[] LeftPad(byte[] src, int size)
    {
        if (src.Length == size) return src;
        if (src.Length > size) return src[(src.Length - size)..];
        var padded = new byte[size];
        Array.Copy(src, 0, padded, size - src.Length, src.Length);
        return padded;
    }

    /// <summary>Unpadded, URL-safe base64 - the encoding VAPID keys use on the wire.</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private sealed class VapidRecord
    {
        public string PublicKey { get; set; } = "";
        public string PrivateKey { get; set; } = "";
    }
}
