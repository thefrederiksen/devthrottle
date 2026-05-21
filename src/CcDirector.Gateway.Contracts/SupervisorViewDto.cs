namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Phase 4b: observability into the SessionStatusSupervisor for a single session.
/// Returned by <c>GET /sessions/{sid}/supervisor</c> on both the Director Control API
/// and the Gateway (via read-through forward). Lets the merged Session View show what
/// the supervisor sees, why the dot is the color it is, and a timestamped log of the
/// most recent decisions.
/// </summary>
public sealed class SupervisorViewDto
{
    /// <summary>Session ID echoed back so callers can correlate without an extra trip.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Current color the supervisor has written: green/blue/yellow/red/unknown.</summary>
    public string CurrentColor { get; set; } = "";

    /// <summary>Short human-readable reason the supervisor recorded for the current color.</summary>
    public string CurrentReason { get; set; } = "";

    /// <summary>UTC timestamp when the current color was set.</summary>
    public DateTime? Since { get; set; }

    /// <summary>
    /// Recent supervisor decisions, newest first. Ring-buffered at 50 entries on the
    /// Director. Lets the UI render a timestamped audit trail without polling.
    /// </summary>
    public List<SupervisorEventDto> Events { get; set; } = new();

    /// <summary>
    /// Latest TurnSummary the supervisor has consumed, if any. The Session View
    /// renders this for the "what just happened" pane. Null if no summary yet.
    /// </summary>
    public TurnSummary? LatestTurnSummary { get; set; }
}

/// <summary>
/// One row in <see cref="SupervisorViewDto.Events"/>.
/// </summary>
public sealed class SupervisorEventDto
{
    public DateTime At { get; set; }
    public string OldColor { get; set; } = "";
    public string NewColor { get; set; } = "";
    public string Reason { get; set; } = "";
}
