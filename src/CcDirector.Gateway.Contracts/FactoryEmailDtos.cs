namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One kept factory email (Factory Control, step 4): an 08:00 report or a 16:00 / midnight check of one account, as it
/// was composed. A row of <c>GET /gateway/factory-emails</c>, which the factory page reads to list the emails that
/// covered a factory; the body itself comes from <c>GET /gateway/factory-emails/{id}</c>.
/// </summary>
public sealed class FactoryEmailDto
{
    /// <summary>The kept email's id - what <c>GET /gateway/factory-emails/{id}</c> takes.</summary>
    public string Id { get; set; } = "";

    /// <summary>The slot's key: the boundary's local date and hour, for example <c>2026-10-11T08</c>.</summary>
    public string SlotKey { get; set; } = "";

    /// <summary>The slot's local time: <c>08:00</c>, <c>16:00</c> or <c>00:00</c>.</summary>
    public string Slot { get; set; } = "";

    /// <summary><c>morning-report</c> or <c>check</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The shift boundary the slot is at (UTC).</summary>
    public DateTime BoundaryUtc { get; set; }

    /// <summary><c>sent</c>, <c>nothing-to-send</c>, <c>failed</c> or <c>not-supported</c>. A list returns only
    /// <c>sent</c> unless every state was asked for.</summary>
    public string State { get; set; } = "";

    /// <summary>When it was sent (UTC), or null.</summary>
    public DateTime? SentUtc { get; set; }

    /// <summary>The subject line - the email's title; empty when there was nothing to send.</summary>
    public string Title { get; set; } = "";

    /// <summary>The ids of the factories it covers.</summary>
    public List<string> Factories { get; set; } = new();

    /// <summary>How many problems it lists.</summary>
    public int ProblemCount { get; set; }

    /// <summary>Why nothing was sent, or why the send failed, in words; null once sent.</summary>
    public string? Detail { get; set; }

    /// <summary>The body as email-safe HTML, exactly as sent. Only on <c>GET /gateway/factory-emails/{id}</c>; null in a
    /// list.</summary>
    public string? Html { get; set; }
}

/// <summary>The answer of <c>GET /gateway/factory-emails</c>: kept factory emails, newest first.</summary>
public sealed class FactoryEmailsDto
{
    /// <summary>How many are listed.</summary>
    public int Count { get; set; }

    /// <summary>The factory the list was narrowed to, or null for the whole account.</summary>
    public string? Factory { get; set; }

    /// <summary>The kept emails, newest first.</summary>
    public List<FactoryEmailDto> Emails { get; set; } = new();
}

/// <summary>The answer of <c>GET</c> and <c>PUT /gateway/factory-email</c>: whether this account's factory email is on.</summary>
public sealed class FactoryEmailSwitchDto
{
    /// <summary>True when the 08:00 report and the 16:00 / midnight checks are sent to this account.</summary>
    public bool Enabled { get; set; }
}
