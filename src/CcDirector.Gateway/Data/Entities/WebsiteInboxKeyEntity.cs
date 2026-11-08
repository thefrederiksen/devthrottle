namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// THE WEBSITE'S KEY TO ONE ACCOUNT'S YOUR INBOX, in the <c>website_inbox_keys</c> table (Factory Inbox mission, brief E,
/// 8 November 2026: "I would want all of mine as in-mail"). The website (devthrottle.com, on Vercel) presents it to
/// <c>POST /gateway/website/inbox</c> to ADD one message to the owner's Your inbox, and it can do nothing else.
///
/// GLOBAL, NOT TENANT-SCOPED, for the reason <see cref="SessionKeyEntity"/> is: a presented key is resolved by its SHA-256
/// hash BEFORE any account is known, and the account is then READ OFF the matched row. <see cref="TenantId"/> is a plain
/// data column, written when the owner made the key from his own phone or Cockpit, and it is the only account the key
/// can ever write to. The request names no account.
///
/// ONE KEY PER ACCOUNT, enforced by the primary key: making a new one replaces the hash, so the old key stops working at
/// once. Revoking keeps the row with the time it was revoked; nothing here is deleted.
/// </summary>
public sealed class WebsiteInboxKeyEntity
{
    /// <summary>The account the key writes to, and the primary key.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>The lower-case hexadecimal SHA-256 of the key - the only form ever stored. Never shown again.</summary>
    public string KeyHash { get; set; } = "";

    /// <summary>When this key was made (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The owner's device that made it.</summary>
    public string CreatedByDeviceId { get; set; } = "";

    /// <summary>When it was revoked (UTC), or null while it works.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>When the website last used it to write a message (UTC), or null when it never has.</summary>
    public DateTime? LastUsedAtUtc { get; set; }
}
