namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /directors on the Gateway. Spawns a new Director process.
/// </summary>
public sealed class LaunchDirectorRequest
{
    /// <summary>Optional workspace name to open. If empty, opens with default state.</summary>
    public string? Workspace { get; set; }

    /// <summary>How long to wait for the new Director to register itself (ms). Default 30000.</summary>
    public int TimeoutMs { get; set; } = 30_000;
}

/// <summary>
/// Body of DELETE /directors/{id}.
/// </summary>
public sealed class ShutdownDirectorRequest
{
    /// <summary>If true, kill the process if the graceful shutdown fails or hangs.</summary>
    public bool Force { get; set; } = false;

    /// <summary>How long to wait for graceful shutdown before forcing (if Force=true). Default 15000.</summary>
    public int TimeoutMs { get; set; } = 15_000;

    /// <summary>
    /// REQUIRED. Why this Director is being shut down. Logged on the Gateway so a
    /// post-mortem can always answer "who stopped it and why" (issue #212 W6).
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Live-session gate (issue #212 W6): when the target Director has live sessions,
    /// this must match their count or the request is rejected with 409 (the response
    /// lists the sessions). A caller may not take down sessions it did not know existed.
    /// </summary>
    public int? ConfirmSessions { get; set; }
}
