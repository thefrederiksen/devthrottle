namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One session's Gateway credential, persisted in the <c>session_keys</c> table (Remove-the-network-port
/// mission, phase 1b). This is the record that lets an agent inside a session call the Gateway as ITSELF
/// rather than with the Director's own account-wide key.
///
/// It holds the session id, the owning tenant, a one-way SHA-256 HASH of the key, and an expiry. It does
/// NOT hold the key: the Director hashes it on the machine that minted it and registers only the hash, so
/// this table can be read in full - by an operator, by a backup, by an attacker who reaches the database -
/// without yielding a credential that can be presented.
///
/// Like <see cref="DeviceCredentialEntity"/> it is a GLOBAL table, deliberately NOT derived from
/// <see cref="TenantScopedEntity"/> and deliberately NOT given the tenant query filter: a presented key is
/// resolved by its hash BEFORE any tenant is known, and the tenant is READ OFF the matched row. Scoping the
/// table by tenant would make that resolution circular - it would need the answer to ask the question.
/// <see cref="TenantId"/> is therefore a plain data column here (whose account this session belongs to),
/// not the ambient-scoping column of a tenant-scoped entity.
/// </summary>
public sealed class SessionKeyEntity
{
    /// <summary>
    /// The session's id (a GUID in "D" form) - the natural primary key. One live key per session: a
    /// re-registration rotates the row rather than adding a second, so a session can never end up with two
    /// credentials of which only one is revocable.
    /// </summary>
    public string SessionId { get; set; } = "";

    /// <summary>
    /// The tenant that owns this session, taken from the tenant the registering Director's tunnel bound to
    /// at Hello - never from the registration payload. This is the value that becomes the calling tenant on
    /// every request the key authenticates, so it is the isolation line for a session key.
    /// </summary>
    public string TenantId { get; set; } = "";

    /// <summary>The Director that owns the session, for diagnostics and for revoking a whole Director's
    /// keys. Not a credential. Inside a team it IS the record of whose a session id is
    /// (<c>SessionKeyRegistry.DirectorOfSession</c>, #3552 review S2-F6), which is why it is never taken over.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>The lower-case hexadecimal SHA-256 of the session key - the ONLY form of the key ever
    /// persisted, verified against, and never reversed. Indexed for the key-to-session lookup that every
    /// authenticated agent request performs; ordinally compared so equality matches SQLite's BINARY
    /// collation exactly on both providers.</summary>
    public string KeyHash { get; set; } = "";

    /// <summary>When this key was registered (UTC).</summary>
    public DateTime IssuedAtUtc { get; set; }

    /// <summary>
    /// When this key stops being accepted (UTC). The BACKSTOP, not the ordinary end of life - a session
    /// key is revoked when its session is reaped. This is what ends a key whose revocation is never
    /// delivered because the Director was killed or the machine went away.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>When this key was revoked (UTC), or null while it is still valid. A revoked row is kept as
    /// a TOMBSTONE rather than deleted, so a re-registration under the same session id cannot silently
    /// revive a key that was deliberately ended - the same stance <see cref="DeviceCredentialEntity"/>
    /// takes.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Why the key was revoked (e.g. <c>session_reaped</c>), or null while it is still valid.</summary>
    public string? RevokedReason { get; set; }
}
