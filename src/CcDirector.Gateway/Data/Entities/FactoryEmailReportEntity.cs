namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE FACTORY EMAIL SLOT of one account (Factory Control, step 4), in the <c>factory_email_reports</c> table: the
/// 08:00 report or a 16:00 / midnight check, as composed, and what became of it.
///
/// It is two things at once, on purpose. It is the record that a slot was HANDLED - one row per account and slot,
/// unique, so a restart or a deploy never sends a slot twice. And it is the composed report KEPT, so the factory page
/// can show yesterday's 08:00 report again exactly as it was sent.
/// </summary>
public sealed class FactoryEmailReportEntity : GatewayMintedKeyEntity
{
    /// <summary>The slot's key, unique per account: the boundary's local date and hour, for example <c>2026-10-11T08</c>.</summary>
    public string SlotKey { get; set; } = "";

    /// <summary><c>morning-report</c> or <c>check</c> (<c>Factory.Email.FactoryEmailKinds</c>).</summary>
    public string Kind { get; set; } = "";

    /// <summary>The shift boundary the slot is at (UTC).</summary>
    public DateTime BoundaryUtc { get; set; }

    /// <summary>When it was composed (UTC).</summary>
    public DateTime ComposedAtUtc { get; set; }

    /// <summary><c>sent</c>, <c>nothing-to-send</c>, <c>failed</c> or <c>not-supported</c>
    /// (<c>Factory.Email.FactoryEmailStates</c>).</summary>
    public string State { get; set; } = "";

    /// <summary>Why nothing was sent, or why a send failed, in words. Null once sent.</summary>
    public string? Detail { get; set; }

    /// <summary>The subject line; empty when there was nothing to send.</summary>
    public string Subject { get; set; } = "";

    /// <summary>The body as composed (email-safe HTML); empty when there was nothing to send.</summary>
    public string Html { get; set; } = "";

    /// <summary>The same body as plain text.</summary>
    public string Text { get; set; } = "";

    /// <summary>How many problems it lists.</summary>
    public int ProblemCount { get; set; }

    /// <summary>How many sends were tried.</summary>
    public int Attempts { get; set; }

    /// <summary>When it was sent (UTC), or null.</summary>
    public DateTime? SentAtUtc { get; set; }

    /// <summary>The mail provider's message id, when the send returned one.</summary>
    public string? ProviderId { get; set; }
}
