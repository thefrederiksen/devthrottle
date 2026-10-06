namespace CcDirector.Gateway.Contracts;

/// <summary>One skill the library held that did not reach the agent, and what stopped it.</summary>
public sealed class SkillPlacementProblemDto
{
    /// <summary>The skill id the library holds.</summary>
    public string SkillId { get; set; } = "";

    /// <summary>The directory it should have appeared in, so the fix is actionable without a support
    /// round trip. A path, never any file's content.</summary>
    public string Target { get; set; } = "";

    /// <summary>"Shadowed" (a directory DevThrottle did not write occupies the name), "HeldByAnotherSource"
    /// (another Director's library installed the name and keeps it) or "LinkFailed".</summary>
    public string Fault { get; set; } = "";
}

/// <summary>What one Director reports about one agent family after placing skills.</summary>
public sealed class SkillPlacementReportDto
{
    /// <summary>The agent family this report is about.</summary>
    public string AgentKind { get; set; } = "";

    /// <summary>How many skills the library held on that machine.</summary>
    public int Held { get; set; }

    /// <summary>How many the agent could actually read afterwards.</summary>
    public int Reachable { get; set; }

    /// <summary>The Gateway had never been reached, so nothing was placed for a reason that is not a
    /// placement failure.</summary>
    public bool StoreMissing { get; set; }

    /// <summary>Everything the library held that did not arrive.</summary>
    public List<SkillPlacementProblemDto> Problems { get; set; } = new();

    /// <summary>When the Director observed this.</summary>
    public DateTime ObservedAtUtc { get; set; }
}

/// <summary>A Director's push of one or more per-agent placement reports.</summary>
public sealed class SkillPlacementPushRequest
{
    /// <summary>The reporting Director.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Its machine's display name.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>One report per agent family placed.</summary>
    public List<SkillPlacementReportDto> Reports { get; set; } = new();
}

/// <summary>What the Gateway answers a push with.</summary>
public sealed class SkillPlacementPushResponse
{
    /// <summary>How many reports were stored.</summary>
    public int Stored { get; set; }

    /// <summary>When the Gateway received them.</summary>
    public DateTime ReceivedAtUtc { get; set; }
}

/// <summary>One stored row, as the Cockpit reads it.</summary>
public sealed class SkillPlacementRowDto
{
    public string DirectorId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string AgentKind { get; set; } = "";
    public int Held { get; set; }
    public int Reachable { get; set; }
    public bool StoreMissing { get; set; }
    public List<SkillPlacementProblemDto> Problems { get; set; } = new();
    public DateTime ObservedAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }

    /// <summary>
    /// THE VERDICT, COMPUTED ON THE GATEWAY. Whether a row means "this is fine" or "somebody needs to
    /// look at this" is decided once, here, and the client renders it verbatim - a client that re-derives
    /// it will, the first time it meets a row it did not expect, render something plausible instead of
    /// something true. "ok", "stale" (the Gateway has never been reached by that Director), or "broken".
    /// </summary>
    public string Status { get; set; } = "ok";

    /// <summary>The finished sentence to display. Composed here for the same reason as
    /// <see cref="Status"/>.</summary>
    public string Message { get; set; } = "";
}

/// <summary>The Cockpit's view of skill placement across the whole account.</summary>
public sealed class SkillPlacementListResponse
{
    public List<SkillPlacementRowDto> Rows { get; set; } = new();

    /// <summary>True when any row is broken - the one thing a badge needs, already decided.</summary>
    public bool AnyBroken { get; set; }
}
