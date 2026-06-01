using System.Security.Cryptography;

namespace CcDirector.Setup.Engine;

/// <summary>SHA-256 helpers for verifying downloaded assets against the manifest.</summary>
public static class Hashing
{
    /// <summary>Compute the SHA-256 of a file as an uppercase hex string.</summary>
    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>True when the file's SHA-256 equals <paramref name="expectedHex"/> (case-insensitive).</summary>
    public static bool Sha256Matches(string path, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return false;
        return string.Equals(Sha256OfFile(path), expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
