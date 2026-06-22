using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// Migrates a Director that still holds a local DevThrottle credential onto the Gateway-centralized
/// model (Gateway Centralization Phase 2, issue #642). The Gateway is the single account authority now,
/// so the Director must hold NO credential of its own: any pre-existing
/// <c>%LOCALAPPDATA%\cc-director\config\director\devthrottle-credential.bin</c> left by an older build
/// is ignored AND deleted on the first run of the new build, with a log line.
///
/// This is deliberately a one-line, fail-loud deletion (no fallback): if the stale blob exists and
/// cannot be removed, the failure is surfaced to the caller's log rather than silently swallowed. It
/// targets ONLY the Director's per-install blob - never the Gateway's own credential blob
/// (<see cref="CcStorage.GatewayDevThrottleCredentialBlob"/>), which the Gateway legitimately keeps.
/// </summary>
public static class DevThrottleCredentialMigration
{
    /// <summary>
    /// Deletes a pre-existing Director credential blob if one is present, returning true when a blob was
    /// found and deleted and false when there was nothing to delete. The blob path defaults to the
    /// Director's credential location (<see cref="CcStorage.DevThrottleCredentialBlob"/>); tests pass an
    /// explicit path to a temporary file. The credential is never read or decrypted here - the Director
    /// no longer trusts a local credential, so the migration only removes the stale file.
    /// </summary>
    /// <param name="blobPath">
    /// The credential blob to remove. Defaults to the Director's credential path. Tests inject a
    /// temporary path so the migration is provable without touching the real install.
    /// </param>
    /// <returns>True when a stale blob was present and deleted; false when none existed.</returns>
    public static bool DeleteStaleDirectorCredential(string? blobPath = null)
    {
        var path = blobPath ?? CcStorage.DevThrottleCredentialBlob();
        FileLog.Write($"[DevThrottleCredentialMigration] DeleteStaleDirectorCredential: checking for a stale Director credential blob at {path}");

        if (!File.Exists(path))
        {
            FileLog.Write("[DevThrottleCredentialMigration] DeleteStaleDirectorCredential: no Director credential blob present (nothing to migrate)");
            return false;
        }

        File.Delete(path);
        FileLog.Write($"[DevThrottleCredentialMigration] DeleteStaleDirectorCredential: deleted stale Director credential blob at {path} (the Gateway is the account authority now, issue #642)");
        return true;
    }
}
