using System.Security.Cryptography;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Util;

/// <summary>
/// Loads (or generates and stores) the gateway bearer token used to protect
/// write endpoints. Stored in plain text at:
///     %LOCALAPPDATA%\cc-director\config\director\gateway-token.txt
/// </summary>
public static class GatewayAuth
{
    public static string TokenFile { get; } =
        Path.Combine(CcStorage.Config(), "director", "gateway-token.txt");

    /// <summary>Read the existing token from disk or generate a new one and persist it.</summary>
    public static string LoadOrCreate()
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
