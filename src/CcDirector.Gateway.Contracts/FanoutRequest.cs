namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /fanout on the Gateway. Sends one prompt to many sessions in parallel.
/// </summary>
public sealed class FanoutRequest
{
    /// <summary>Session GUIDs (Director-internal Ids) to send the prompt to. Must be non-empty.</summary>
    public List<string> SessionIds { get; set; } = new();

    /// <summary>Prompt text to send to every selected session.</summary>
    public string Text { get; set; } = "";

    /// <summary>If true (default), append Enter after the text.</summary>
    public bool AppendEnter { get; set; } = true;

    /// <summary>If true (default), wait for every session to return to Idle (or timeout) before responding.</summary>
    public bool WaitForIdle { get; set; } = true;

    /// <summary>Per-session timeout in milliseconds. Default 300000 (5 min).</summary>
    public int TimeoutMs { get; set; } = 300_000;
}

/// <summary>
/// Response from POST /fanout.
/// </summary>
public sealed class FanoutResponse
{
    /// <summary>Per-session results in the same order as the request's SessionIds.</summary>
    public List<FanoutResult> Results { get; set; } = new();

    /// <summary>UTC timestamp the fan-out started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp all results were collected (or timeouts hit).</summary>
    public DateTime FinishedAt { get; set; }
}

/// <summary>
/// One row of a fan-out response.
/// </summary>
public sealed class FanoutResult
{
    /// <summary>The session this row corresponds to.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The Director that owned the session at dispatch time (informational).</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>idle | timeout | failed | not_found .</summary>
    public string Status { get; set; } = "";

    /// <summary>Cleaned output produced by the session for this prompt.</summary>
    public string Output { get; set; } = "";

    /// <summary>Error message if Status == failed or not_found.</summary>
    public string? Error { get; set; }

    /// <summary>Elapsed milliseconds for this session's round-trip.</summary>
    public long ElapsedMs { get; set; }
}
