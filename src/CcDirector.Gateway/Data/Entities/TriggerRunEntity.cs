namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One check of one trigger, whatever it came to (the Website Business Factory mission, product track). Rows
/// in the <c>trigger_runs</c> table are only ever added; the store keeps at least the newest 500 per trigger
/// and drops only what is older than that. A deleted trigger's rows are kept.
/// </summary>
public sealed class TriggerRunEntity : GatewayMintedKeyEntity
{
    /// <summary>The trigger's id.</summary>
    public Guid TriggerId { get; set; }

    /// <summary>When the check finished, on the Director's clock (UTC).</summary>
    public DateTime CheckedUtc { get; set; }

    /// <summary>When the Gateway recorded it (UTC), stamped by the Gateway.</summary>
    public DateTime RecordedUtc { get; set; }

    /// <summary>One of <c>TriggerRunOutcome</c>.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>What the check counted, or null when it failed before counting.</summary>
    public int? Count { get; set; }

    /// <summary>The session started, on a <c>started</c> row; the session still alive, on a
    /// <c>skipped-running</c> row; otherwise null.</summary>
    public string? SessionId { get; set; }

    /// <summary>Why, on a <c>failed</c> row. Capped in length.</summary>
    public string? Reason { get; set; }

    /// <summary>The Director that ran the check.</summary>
    public string DirectorId { get; set; } = "";
}
