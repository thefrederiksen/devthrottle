namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The outcome words a factory activity row may carry (Website Business Factory, product track). A closed
/// list: anything else is refused with the list itself, so a business tool that writes a new word finds out
/// on its first write rather than leaving a row nobody can filter or colour.
/// </summary>
public static class FactoryActivityOutcome
{
    /// <summary>A factory agent (or its trigger) started work.</summary>
    public const string Started = "started";
    /// <summary>A rule allowed the action.</summary>
    public const string Allowed = "allowed";
    /// <summary>The factory agent asked a person before acting.</summary>
    public const string Asked = "asked";
    /// <summary>A rule blocked the action.</summary>
    public const string Blocked = "blocked";
    /// <summary>The factory agent handed the matter up to a person.</summary>
    public const string Escalated = "escalated";
    /// <summary>The action was carried out.</summary>
    public const string Done = "done";
    /// <summary>The work was sent back to an earlier step.</summary>
    public const string SentBack = "sent-back";
    /// <summary>A check ran and found nothing to do.</summary>
    public const string NothingToDo = "nothing-to-do";
    /// <summary>The factory agent is paused, so nothing was started.</summary>
    public const string Paused = "paused";
    /// <summary>The action or check failed.</summary>
    public const string Failed = "failed";

    /// <summary>Every legal outcome, in the order the refusal lists them.</summary>
    public static readonly string[] All =
    {
        Started, Allowed, Asked, Blocked, Escalated, Done, SentBack, NothingToDo, Paused, Failed,
    };
}

/// <summary>
/// Body of <c>POST /gateway/factory/activity</c>: one row of the append-only factory activity record.
/// </summary>
public sealed class AppendFactoryActivityRequest
{
    /// <summary>The factory the row belongs to (for example "website-business"). Required.</summary>
    public string? Factory { get; set; }

    /// <summary>The factory agent that acted (for example "front-desk"). Required.</summary>
    public string? FactoryAgent { get; set; }

    /// <summary>The version of the factory agent's definition that acted, when definitions exist.</summary>
    public string? FactoryAgentVersion { get; set; }

    /// <summary>The session that did the work, when there was one. A trigger check has none.</summary>
    public string? SessionId { get; set; }

    /// <summary>What happened, in one plain sentence. Required, at most 500 characters.</summary>
    public string? What { get; set; }

    /// <summary>One of <see cref="FactoryActivityOutcome.All"/>. Required.</summary>
    public string? Outcome { get; set; }

    /// <summary>What the row is about - for example the business name. Optional.</summary>
    public string? Subject { get; set; }

    /// <summary>A link to the evidence (never a copy of it). Optional.</summary>
    public string? Link { get; set; }

    /// <summary>Who acted. When omitted, the Gateway stamps the calling session.</summary>
    public string? Actor { get; set; }

    /// <summary>The row this one corrects. A correction is a NEW row; the old row is never changed.</summary>
    public Guid? CorrectsId { get; set; }

    /// <summary>When it happened. Defaults to the time the Gateway recorded it.</summary>
    public DateTime? OccurredUtc { get; set; }
}

/// <summary>One recorded factory activity row, as the Gateway returns it.</summary>
public sealed class FactoryActivityDto
{
    public Guid Id { get; set; }
    public string Factory { get; set; } = "";
    public string FactoryAgent { get; set; } = "";
    public string? FactoryAgentVersion { get; set; }
    public string? SessionId { get; set; }
    public string What { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? Subject { get; set; }
    public string? Link { get; set; }
    public string Actor { get; set; } = "";
    public Guid? CorrectsId { get; set; }
    public DateTime OccurredUtc { get; set; }
    public DateTime RecordedUtc { get; set; }
}

/// <summary>One page of <c>GET /gateway/factory/activity</c>.</summary>
public sealed class FactoryActivityPage
{
    /// <summary>The rows on this page, in the order asked for.</summary>
    public List<FactoryActivityDto> Rows { get; set; } = new();

    /// <summary>How many rows were skipped before this page.</summary>
    public int Offset { get; set; }

    /// <summary>The page size that was applied.</summary>
    public int Limit { get; set; }

    /// <summary>True when at least one more row matches after this page.</summary>
    public bool HasMore { get; set; }
}
