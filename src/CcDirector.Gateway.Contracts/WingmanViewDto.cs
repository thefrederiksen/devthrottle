namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Phase 4b: observability into the SessionStatusWingman for a single session.
/// Returned by <c>GET /sessions/{sid}/wingman</c> on both the Director Control API
/// and the Gateway (via read-through forward). Lets the merged Session View show what
/// the wingman sees, why the dot is the color it is, and a timestamped log of the
/// most recent decisions.
/// </summary>
public sealed class WingmanViewDto
{
    /// <summary>Session ID echoed back so callers can correlate without an extra trip.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Current color the wingman has written: green/blue/yellow/red/unknown.</summary>
    public string CurrentColor { get; set; } = "";

    /// <summary>Short human-readable reason the wingman recorded for the current color.</summary>
    public string CurrentReason { get; set; } = "";

    /// <summary>UTC timestamp when the current color was set.</summary>
    public DateTime? Since { get; set; }

    /// <summary>
    /// Recent wingman decisions, newest first. Ring-buffered at 50 entries on the
    /// Director. Lets the UI render a timestamped audit trail without polling.
    /// </summary>
    public List<WingmanEventDto> Events { get; set; } = new();

    /// <summary>
    /// Latest TurnSummary the wingman has consumed, if any. The Session View
    /// renders this for the "what just happened" pane. Null if no summary yet.
    /// </summary>
    public TurnSummary? LatestTurnSummary { get; set; }

    /// <summary>
    /// The session's stated goal, if one has been set. Null/empty when no goal
    /// is set, in which case goal-tracking is dormant for this session.
    /// </summary>
    public string? Goal { get; set; }

    /// <summary>UTC time the goal was last set, or null if no goal.</summary>
    public DateTime? GoalSetAt { get; set; }

    /// <summary>
    /// Latest goal-tracking verdict: on_track | drifting | complete | unknown.
    /// "unknown" until the first assessment runs (or if no goal is set).
    /// </summary>
    public string GoalState { get; set; } = GoalStates.Unknown;

    /// <summary>Short plain-language reason for <see cref="GoalState"/>.</summary>
    public string GoalReason { get; set; } = "";

    /// <summary>UTC time the goal was last assessed, or null if never.</summary>
    public DateTime? GoalEvaluatedAt { get; set; }
}

/// <summary>
/// One row in <see cref="WingmanViewDto.Events"/>.
/// </summary>
public sealed class WingmanEventDto
{
    public DateTime At { get; set; }
    public string OldColor { get; set; } = "";
    public string NewColor { get; set; } = "";
    public string Reason { get; set; } = "";
}
