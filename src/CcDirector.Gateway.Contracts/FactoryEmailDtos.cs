namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One kept factory email (Factory Control, step 4): the 08:00 report or a 16:00 / midnight check of one account, as
/// composed, and what became of it. The answer of <c>GET /gateway/factory-email/reports</c>, which the factory page
/// reads to show yesterday's report again.
/// </summary>
public sealed class FactoryEmailReportDto
{
    /// <summary>The slot's key: the boundary's local date and hour, for example <c>2026-10-11T08</c>.</summary>
    public string SlotKey { get; set; } = "";

    /// <summary><c>morning-report</c> or <c>check</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The shift boundary the slot is at (UTC).</summary>
    public DateTime BoundaryUtc { get; set; }

    /// <summary>When it was composed (UTC).</summary>
    public DateTime ComposedAtUtc { get; set; }

    /// <summary><c>sent</c>, <c>nothing-to-send</c>, <c>failed</c> or <c>not-supported</c>.</summary>
    public string State { get; set; } = "";

    /// <summary>Why nothing was sent, or why the send failed, in words; null once sent.</summary>
    public string? Detail { get; set; }

    /// <summary>The subject line; empty when there was nothing to send.</summary>
    public string Subject { get; set; } = "";

    /// <summary>The body as email-safe HTML, exactly as composed; empty when there was nothing to send. Left out of a
    /// list (null) unless it was asked for.</summary>
    public string? Html { get; set; }

    /// <summary>How many problems it lists.</summary>
    public int ProblemCount { get; set; }

    /// <summary>When it was sent (UTC), or null.</summary>
    public DateTime? SentAtUtc { get; set; }
}

/// <summary>The answer of <c>GET /gateway/factory-email/reports</c>: the account's kept factory emails, newest first.</summary>
public sealed class FactoryEmailReportsDto
{
    /// <summary>How many are listed.</summary>
    public int Count { get; set; }

    /// <summary>The kept emails, newest first.</summary>
    public List<FactoryEmailReportDto> Reports { get; set; } = new();
}

/// <summary>The answer of <c>GET</c> and <c>PUT /gateway/factory-email</c>: whether this account's factory email is on.</summary>
public sealed class FactoryEmailSwitchDto
{
    /// <summary>True when the 08:00 report and the 16:00 / midnight checks are sent to this account.</summary>
    public bool Enabled { get; set; }
}
