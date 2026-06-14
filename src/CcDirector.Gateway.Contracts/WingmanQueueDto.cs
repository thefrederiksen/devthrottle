namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A read-only snapshot of the ONE-brain wingman pipeline (issue #239) - the whole
/// <c>GatewayTurnBriefAgent</c> stamping machine seen at one instant: who is being read
/// right now, who is waiting behind them, the most recent briefs, and the brain's health.
///
/// This is OBSERVABILITY ONLY: it is assembled by reading the agent's live state and never
/// changes any queue semantics. Served by <c>GET /wingman/queue</c> and rendered by the
/// fleet-level Cockpit Wingman Pipeline page. The whole point is that the 2026-06-07
/// poisoned-brain outage (degraded stubs for ~6 hours, found only by log forensics) becomes
/// visible at a glance through <see cref="WingmanBrainHealth.ConsecutiveRejections"/> and the
/// degraded-brief count in <see cref="Recent"/>.
/// </summary>
public sealed class WingmanQueueDto
{
    /// <summary>The session currently being read by the brain, or null when the pipeline is idle.</summary>
    public WingmanInFlightItem? InFlight { get; set; }

    /// <summary>The ordered list of sessions waiting behind the in-flight one (turn briefs and
    /// explain deep dives, distinguished by <see cref="WingmanQueueItem.Kind"/>). Empty when idle.</summary>
    public List<WingmanQueueItem> Queue { get; set; } = new();

    /// <summary>The most recent completed briefs (bounded last-N), newest first.</summary>
    public List<WingmanRecentBrief> Recent { get; set; } = new();

    /// <summary>The warm brain's health: pid, model, liveness, the poisoned-brain rejection
    /// counter, and whether a recovery restart is in flight.</summary>
    public WingmanBrainHealth Brain { get; set; } = new();
}

/// <summary>The session the brain is reading right now.</summary>
public sealed class WingmanInFlightItem
{
    public string SessionId { get; set; } = "";

    /// <summary>"brief" (background turn-brief stamping) or "explain" (user-initiated deep dive).</summary>
    public string Kind { get; set; } = "brief";

    /// <summary>Seconds since this session entered the in-flight slot (display only).</summary>
    public double ElapsedSeconds { get; set; }
}

/// <summary>One session waiting in the pipeline.</summary>
public sealed class WingmanQueueItem
{
    public string SessionId { get; set; } = "";

    /// <summary>"brief" (a turn end is queued) or "explain" (an explain deep dive is queued).
    /// Explain requests outrank turn briefs in the drain order (issue #217).</summary>
    public string Kind { get; set; } = "brief";
}

/// <summary>One recently completed brief, for the "recent" list.</summary>
public sealed class WingmanRecentBrief
{
    public string SessionId { get; set; } = "";
    public int TurnNumber { get; set; }
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>True when the brief came from a degrade tier, not the wingman - the poisoned-brain tell.</summary>
    public bool Degraded { get; set; }

    /// <summary>Generator identity that wrote the brief, e.g. "gateway-brain/opus" or "stub".</summary>
    public string Model { get; set; } = "";
}

/// <summary>The warm brain's health block.</summary>
public sealed class WingmanBrainHealth
{
    /// <summary>PID of the hosted brain process; 0 before first use / after death.</summary>
    public int Pid { get; set; }

    /// <summary>The model the brain is pinned to (e.g. "opus").</summary>
    public string Model { get; set; } = "";

    /// <summary>True when the brain process is running and prompt-accepting.</summary>
    public bool Alive { get; set; }

    /// <summary>Process lifecycle status: NotStarted / Starting / Running / Exiting / Exited / Failed.</summary>
    public string Status { get; set; } = "";

    /// <summary>Consecutive validation rejections (the poisoned-brain signal, issue #208). A streak
    /// reaching the threshold means the brain is presumed poisoned and gets restarted.</summary>
    public int ConsecutiveRejections { get; set; }

    /// <summary>The rejection streak length that triggers a recovery restart (so the page can show
    /// "2 / 3" honestly without hard-coding the threshold).</summary>
    public int RejectionThreshold { get; set; }

    /// <summary>True while a brain recovery (RestartAsync) is in flight.</summary>
    public bool RecoveryInFlight { get; set; }
}
