using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// Windows implementation of the operating system credential store, backed by Windows Data
/// Protection (the DPAPI) through <see cref="ProtectedData"/>. The token pair is serialized to
/// JSON, encrypted with the current user's data-protection key, and written as an opaque binary
/// blob under the Director config directory. Because the blob is encrypted at rest, the raw token
/// string never appears in plain text on disk, and only the same Windows user on the same machine
/// can decrypt it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProtectedTokenStore : IProtectedTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _blobPath;

    /// <summary>
    /// Creates the store. The default location is the encrypted credential blob under the Director
    /// config directory; tests pass an explicit path to a temporary directory.
    /// </summary>
    public WindowsProtectedTokenStore(string? blobPath = null)
    {
        _blobPath = blobPath ?? CcStorage.DevThrottleCredentialBlob();
        FileLog.Write($"[WindowsProtectedTokenStore] ctor: blobPath={_blobPath}");
    }

    public bool HasTokens => File.Exists(_blobPath);

    public void Save(DevThrottleTokens tokens)
    {
        FileLog.Write("[WindowsProtectedTokenStore] Save: encrypting token pair to credential store");

        if (tokens is null)
            throw new ArgumentNullException(nameof(tokens));
        if (string.IsNullOrEmpty(tokens.AccessToken))
            throw new ArgumentException("Access token is required", nameof(tokens));

        var dir = Path.GetDirectoryName(_blobPath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {_blobPath}");
        Directory.CreateDirectory(dir);

        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens, JsonOptions));
        var encrypted = ProtectedData.Protect(plain, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_blobPath, encrypted);

        FileLog.Write($"[WindowsProtectedTokenStore] Save: wrote {encrypted.Length} encrypted bytes");
    }

    public DevThrottleTokens? Load()
    {
        FileLog.Write($"[WindowsProtectedTokenStore] Load: blobPath={_blobPath}, exists={File.Exists(_blobPath)}");

        if (!File.Exists(_blobPath))
            return null;

        var encrypted = File.ReadAllBytes(_blobPath);
        var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        var tokens = JsonSerializer.Deserialize<DevThrottleTokens>(Encoding.UTF8.GetString(plain), JsonOptions);

        FileLog.Write($"[WindowsProtectedTokenStore] Load: decrypted token pair (hasTokens={tokens is not null})");
        return tokens;
    }

    public void Clear()
    {
        FileLog.Write($"[WindowsProtectedTokenStore] Clear: blobPath={_blobPath}, exists={File.Exists(_blobPath)}");
        if (File.Exists(_blobPath))
            File.Delete(_blobPath);
    }
}
