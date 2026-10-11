using System.Security.Cryptography;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Util;

/// <summary>
/// Loads (or generates and stores) the gateway bearer token used to protect
/// write endpoints. Stored in plain text at:
///     %LOCALAPPDATA%\cc-director\config\director\gateway-token.txt
///
/// Instance-owned so the host constructs one and hands it to the caller that needs it, rather than
/// minting through a process-wide static. The on-disk path and the load-or-create semantics are
/// unchanged. <see cref="DefaultTokenFile"/> stays a pure static path accessor for read-only callers
/// (the self-update helper reads the file directly and must NEVER mint one).
/// </summary>
public sealed class GatewayAuth
{
    /// <summary>The default on-disk token path. A pure path - resolving it never reads or writes a token.</summary>
    public static string DefaultTokenFile { get; } = TokenFileUnder(CcStorage.Root());

    /// <summary>The token file under <paramref name="storageRoot"/>: what a host whose root that is uses.</summary>
    public static string TokenFileUnder(string storageRoot) =>
        Path.Combine(CcStorage.Config(storageRoot), "director", "gateway-token.txt");

    /// <summary>This store's token file path.</summary>
    public string TokenFile { get; }

    /// <param name="tokenFile">Override the token file (tests). Defaults to <see cref="DefaultTokenFile"/>.</param>
    public GatewayAuth(string? tokenFile = null)
    {
        TokenFile = string.IsNullOrWhiteSpace(tokenFile) ? DefaultTokenFile : tokenFile;
    }

    /// <summary>Read the existing token from disk or generate a new one and persist it.</summary>
    public string LoadOrCreate()
    {
        try
        {
            if (File.Exists(TokenFile))
            {
                var existing = File.ReadAllText(TokenFile).Trim();
                if (!string.IsNullOrEmpty(existing))
                {
                    FileLog.Write($"[GatewayAuth] Loaded token from {TokenFile}");
                    return existing;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
            var token = GenerateToken();
            File.WriteAllText(TokenFile, token);
            FileLog.Write($"[GatewayAuth] Generated new token at {TokenFile}");
            return token;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayAuth] LoadOrCreate FAILED: {ex.Message}");
            throw;
        }
    }

    private static string GenerateToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        // URL-safe base64 without padding
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
