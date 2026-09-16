namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One note or answer the owner sent on a dev report (<c>dev_report_items</c>), with its delivery state.
/// The fields are the contract's item shape (<c>packages/client-core/src/devreports/CONTRACT.md</c> section 3);
/// the anchor is stored as the contract's anchor object in JSON text. <see cref="ClientItemId"/> is the
/// page's id and the idempotency key: unique per (tenant, report, client item id).
/// </summary>
public sealed class DevReportItemEntity : GatewayMintedKeyEntity
{
    public Guid ReportId { get; set; }

    /// <summary>Denormalised from the report, so a turn end can find everything held for one session.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The page's item id ("n3", "a1").</summary>
    public string ClientItemId { get; set; } = "";

    /// <summary>note or answer.</summary>
    public string Kind { get; set; } = "";

    /// <summary>A note's text, verbatim. Empty for an answer.</summary>
    public string Text { get; set; } = "";

    /// <summary>A note's anchor as the contract's JSON object. Null for an answer.</summary>
    public string? AnchorJson { get; set; }

    public string QuestionId { get; set; } = "";

    public string Question { get; set; } = "";

    public string OptionValue { get; set; } = "";

    public string OptionLabel { get; set; } = "";

    /// <summary>An answer's comment, verbatim.</summary>
    public string Comment { get; set; } = "";

    /// <summary>One of <see cref="DevReports.DevReportItemStates"/>' statuses.</summary>
    public string Status { get; set; } = "";

    /// <summary>The words for <see cref="Status"/>, from <see cref="DevReports.DevReportItemStates"/>.</summary>
    public string StatusLabel { get; set; } = "";

    /// <summary>The order the owner sent items in: a per-report counter, so a drain keeps send order.</summary>
    public long Sequence { get; set; }

    /// <summary>The kind of credential the owner sent it with (device or machine token), for the delivered
    /// prompt's provenance.</summary>
    public string SenderKind { get; set; } = "";

    /// <summary>When the owner sent it (UTC).</summary>
    public DateTime SentAtUtc { get; set; }

    /// <summary>When it went into the session (UTC), or null while it has not.</summary>
    public DateTime? DeliveredAtUtc { get; set; }

    /// <summary>The client item id of the later answer that replaced this one, or null.</summary>
    public string? ReplacedBy { get; set; }
}
