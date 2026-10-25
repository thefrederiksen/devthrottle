namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// What the owner has SEEN and CLEARED of one alert (the Alerts mission, section 5.2): one row per (account, alert) in
/// <c>alert_marks</c>. This is the only state Alerts stores - the alerts themselves are read-time views of the records
/// their features own, so a mark never copies a request and can never drift from it.
///
/// Marks are per account, not per member: every request today is answered by the account owner.
/// </summary>
public sealed class AlertMarkEntity : TenantScopedEntity
{
    /// <summary>The alert id, <c>"{kind}:{sourceId}"</c>.</summary>
    public string AlertId { get; set; } = "";

    /// <summary>The alert's <c>UpdatedAtUtc</c> when the owner last saw it (UTC), or null when never seen. An alert that
    /// changed after this is unseen again.</summary>
    public DateTime? SeenVersionUtc { get; set; }

    /// <summary>When the owner cleared the alert (UTC), or null. A cleared alert is not listed again.</summary>
    public DateTime? ClearedAtUtc { get; set; }

    /// <summary>When this row was last written (UTC). Rows older than the longest source retention are swept.</summary>
    public DateTime TouchedAtUtc { get; set; }
}
