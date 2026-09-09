namespace CcDirector.Gateway.Contracts;

/// <summary>The two categories of governance audit event (issue #1771, spine item 4).</summary>
public static class GovernanceAuditCategory
{
    /// <summary>The agent needed a human, and what the human did about it.</summary>
    public const string Intervention = "intervention";

    /// <summary>Permission/sandbox posture: requests, decisions, effective mode, elevated-run brackets.</summary>
    public const string Permission = "permission";

    public static readonly string[] All = { Intervention, Permission };
}

/// <summary>
/// The specific audit event types, grouped by category. Each is an explicitly-recorded event, never inferred
/// from a transcript (issue #1771). <see cref="ForCategory"/> gives the legal types for a category, so an
/// event's type is validated against its category.
/// </summary>
public static class GovernanceAuditEventType
{
    // Intervention category.
    /// <summary>The agent stopped and needs a human (the need/ask).</summary>
    public const string Needed = "needed";
    /// <summary>A human stepped in to unblock the agent.</summary>
    public const string HumanRescued = "human-rescued";
    /// <summary>A human changed the agent's direction.</summary>
    public const string HumanRedirected = "human-redirected";
    /// <summary>A human cancelled the work.</summary>
    public const string HumanCancelled = "human-cancelled";
    /// <summary>The intervention resolved and the agent resumed (the response closed out).</summary>
    public const string Resolved = "resolved";
    /// <summary>
    /// The session was STOPPED - its agent process ended by the stop verb (mission "Stop a session").
    ///
    /// Added deliberately rather than folded into one of the five above, because none of them fits and the
    /// nearest one is a lie. Any session may stop any other in the account, so one agent stopping another is
    /// the ORDINARY case here - and recording that as <see cref="HumanCancelled"/> would put a person in the
    /// trail who was never involved. The Actor is who asked and the Detail is why; the owner accepted
    /// "any session may stop any other" on the explicit ground that it is audited, so this row is
    /// load-bearing rather than decoration.
    /// </summary>
    public const string Stopped = "stopped";

    // Permission category.
    /// <summary>The agent requested a permission or approval (Detail = what: e.g. "bash", "write path").</summary>
    public const string PermissionRequested = "permission-requested";
    /// <summary>A permission request was granted (Actor = who granted).</summary>
    public const string PermissionGranted = "permission-granted";
    /// <summary>A permission request was denied (Actor = who denied).</summary>
    public const string PermissionDenied = "permission-denied";
    /// <summary>The effective sandbox/approval mode observed (Detail = the mode).</summary>
    public const string ModeObserved = "mode-observed";
    /// <summary>An elevated (dangerous-permission) run began. Its end pairs with this to give the duration.</summary>
    public const string ElevatedRunStarted = "elevated-run-started";
    /// <summary>An elevated run ended - the close of the elevated-run bracket.</summary>
    public const string ElevatedRunEnded = "elevated-run-ended";

    private static readonly string[] Intervention =
        { Needed, HumanRescued, HumanRedirected, HumanCancelled, Resolved, Stopped };

    private static readonly string[] Permission =
        { PermissionRequested, PermissionGranted, PermissionDenied, ModeObserved, ElevatedRunStarted, ElevatedRunEnded };

    /// <summary>The legal event types for a category, or empty for an unknown category.</summary>
    public static IReadOnlyList<string> ForCategory(string category) => category switch
    {
        GovernanceAuditCategory.Intervention => Intervention,
        GovernanceAuditCategory.Permission => Permission,
        _ => System.Array.Empty<string>(),
    };
}

/// <summary>
/// One structured governance audit event - an intervention (the agent needed a human, and what the human did)
/// or a permission/sandbox decision. Append-only: this DTO is both the write acknowledgement and the read row.
/// </summary>
public sealed class GovernanceAuditEventDto
{
    public Guid Id { get; set; }
    public string SessionId { get; set; } = "";
    public Guid? RunId { get; set; }
    public string Category { get; set; } = "";
    public string EventType { get; set; } = "";
    public string? Actor { get; set; }
    public string? Detail { get; set; }
    public DateTime OccurredUtc { get; set; }
    public DateTime RecordedUtc { get; set; }
}

/// <summary>
/// Body of an append to the audit trail. One request records one event. <see cref="OccurredUtc"/> is optional
/// (the Gateway stamps the append time when omitted); the recorded time is always server-stamped.
/// A permission decision (granted/denied) and a human intervention require <see cref="Actor"/> - "who decided"
/// is an audit fact that is never null there.
/// </summary>
public sealed class AppendGovernanceAuditEventRequest
{
    public string? SessionId { get; set; }
    public Guid? RunId { get; set; }
    public string? Category { get; set; }
    public string? EventType { get; set; }
    public string? Actor { get; set; }
    public string? Detail { get; set; }
    public DateTime? OccurredUtc { get; set; }
}

/// <summary>Body of a batched append: many audit events in one call, landed all-or-nothing.</summary>
public sealed class AppendGovernanceAuditEventsBatchRequest
{
    public List<AppendGovernanceAuditEventRequest> Events { get; set; } = new();
}
