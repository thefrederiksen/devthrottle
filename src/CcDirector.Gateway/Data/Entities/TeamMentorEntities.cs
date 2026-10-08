namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE BLOCK OF THE MENTOR'S WEEKLY PAGE: what one person in a team worked on in one ISO week, how it went, where it
/// went badly and why, and one thing to try next week (devthrottle_internal#2305). One row per (team, week, person).
///
/// The SAME row is served to the person and to the team's Owner and Managers, so the two readers can never be shown
/// two different texts. The quotes are the person's own prompts, copied verbatim by the Gateway from the prompt log at
/// the moment the block was written - never written by a model, and not dependent on the log's retention afterwards.
///
/// The team IS a tenant, so <c>tenant_id</c> is the team id, and the tenant query filter keeps one team's blocks
/// from another's. Personally identifying (the subject and the text about a person): never logged.
/// </summary>
public sealed class TeamMentorBlockEntity : TenantScopedEntity
{
    /// <summary>The ISO week, <c>YYYY-Www</c>, in the team's own time zone.</summary>
    public string Week { get; set; } = "";

    /// <summary>The account subject of the person the block is about.</summary>
    public string PersonSubject { get; set; } = "";

    /// <summary><c>good</c>, <c>mixed</c> or <c>hard</c>.</summary>
    public string Tone { get; set; } = "";

    public string WorkedOn { get; set; } = "";

    public string? HowItWent { get; set; }

    public string? WentBadlyAndWhy { get; set; }

    public string OneThingToTry { get; set; } = "";

    /// <summary>The quoted prompts as a JSON array of <c>{ promptId, at, text }</c> - at most two, so a bounded
    /// sub-document rather than its own table.</summary>
    public string QuotesJson { get; set; } = "[]";

    /// <summary>When the Gateway wrote the block (UTC).</summary>
    public DateTime WrittenAtUtc { get; set; }

    /// <summary>The model that wrote the block's words, for the record.</summary>
    public string Model { get; set; } = "";

    /// <summary>The showcase tag this row was written under by the administrator showcase route
    /// (<see cref="Teams.TeamShowcase"/>), or null for every real row. Removing a showcase deletes exactly the rows
    /// carrying its tag.</summary>
    public string? ShowcaseTag { get; set; }
}

/// <summary>
/// WHAT THE MENTOR DID FOR ONE PERSON IN ONE WEEK (devthrottle_internal#2305), one row per (team, week, member):
/// <c>written</c>, <c>no-sessions</c>, <c>no-prompts</c>, <c>refused</c> (the model's answer was not accepted, with
/// the reason), or <c>model-failed</c> (the model could not be reached, with the reason). A refused answer writes no
/// block; this row is the record of it, so it can be queried rather than only read in a log file.
/// </summary>
public sealed class TeamMentorOutcomeEntity : TenantScopedEntity
{
    public string Week { get; set; } = "";

    public string PersonSubject { get; set; } = "";

    public string Outcome { get; set; } = "";

    /// <summary>Why, for <c>refused</c> (the KIND of refusal and counts) and <c>model-failed</c> (the exception's type);
    /// null otherwise. Never text the model wrote.</summary>
    public string? Reason { get; set; }

    public DateTime AtUtc { get; set; }
}

/// <summary>
/// THE "ALREADY RAN" MARKER for one team's Mentor week (devthrottle_internal#2305): written once the run has visited
/// every member, so a Gateway restart never runs a team's week twice. A week whose model could not be reached is left
/// unmarked and tried again on later ticks until the following week has closed; a week the Gateway was down across
/// for longer than that is not written.
/// </summary>
public sealed class TeamMentorRunEntity : TenantScopedEntity
{
    public string Week { get; set; } = "";

    /// <summary>The team's time zone the week was cut in.</summary>
    public string TimeZone { get; set; } = "";

    public DateTime RanAtUtc { get; set; }

    /// <summary>How many blocks the run wrote.</summary>
    public int BlocksWritten { get; set; }
}
