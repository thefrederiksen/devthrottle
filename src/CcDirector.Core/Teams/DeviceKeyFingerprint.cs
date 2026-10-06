using System.Security.Cryptography;
using System.Text;

namespace CcDirector.Core.Teams;

/// <summary>
/// A one-way fingerprint of a device key - the SHA-256 of the key, in hex: enough to tell two keys apart, useless for
/// using either. What a Director writes beside a fact that holds only for ONE key (the team file, review RM-F6; the
/// suggested-name record, review RM-F8), never the key itself.
/// </summary>
public static class DeviceKeyFingerprint
{
    /// <summary>The fingerprint of <paramref name="deviceKey"/>.</summary>
    public static string Of(string deviceKey)
    {
        if (string.IsNullOrEmpty(deviceKey))
            throw new ArgumentException("deviceKey is required", nameof(deviceKey));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceKey)));
    }

    /// <summary>Whether <paramref name="deviceKey"/> is the key <paramref name="fingerprint"/> was taken of. No key, or
    /// no fingerprint, is never a match.</summary>
    public static bool Matches(string? fingerprint, string? deviceKey) =>
        !string.IsNullOrEmpty(fingerprint) && !string.IsNullOrEmpty(deviceKey)
        && string.Equals(fingerprint, Of(deviceKey), StringComparison.Ordinal);
}
