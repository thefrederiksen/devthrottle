namespace CcDirector.AgentBrain;

/// <summary>
/// Configuration for an <see cref="AgentBrainClient"/>. The only required fields are
/// <see cref="DirectorUrl"/> and <see cref="RepoPath"/>; everything else has working
/// defaults tuned by the issue #172 spike (playground/headless-brain/RESULTS.md).
/// </summary>
public sealed class AgentBrainOptions
{
    /// <summary>Base URL of the Director's Control API, e.g. "http://127.0.0.1:7886". Required.</summary>
    public string DirectorUrl { get; set; } = "";

    /// <summary>Working directory for the brain session. Required for CreateSession/Restart.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>Bearer token for the Control API. Null disables the Authorization header
    /// (loopback Directors accept that).</summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// Seconds the terminal must be byte-silent before a send is allowed. Sends into a
    /// repainting composer (e.g. right after /clear) lose their trailing Enter - the spike's
    /// swallowed-Enter race. Gated on the Director's server-side idle clock, never a sleep.
    /// </summary>
    public double QuietSeconds { get; set; } = 2.0;

    /// <summary>Max seconds to wait for the quiet gate before giving up.</summary>
    public double QuietTimeoutSeconds { get; set; } = 30.0;

    /// <summary>Max seconds to wait for a freshly created session to accept prompts.</summary>
    public double CreateTimeoutSeconds { get; set; } = 120.0;

    /// <summary>Max seconds AskAsync waits for the reply to land in the transcript.</summary>
    public double AskTimeoutSeconds { get; set; } = 300.0;

    /// <summary>Max seconds ClearAsync waits for the new claude session id to appear.</summary>
    public double ClearTimeoutSeconds { get; set; } = 60.0;

    /// <summary>Seconds between state polls.</summary>
    public double PollIntervalSeconds { get; set; } = 0.25;

    /// <summary>
    /// Consecutive seconds the transcript must be stable (no new widgets) before AskAsync
    /// accepts the last Text widget as the final answer. Guards multi-block replies
    /// (thinking + text + more text) without waiting for the terminal-state detector's
    /// 10s quiet threshold.
    /// </summary>
    public double ReplyStableSeconds { get; set; } = 1.5;

    /// <summary>Sink for diagnostic log lines. Defaults to a daily file under
    /// %LOCALAPPDATA%\cc-director\logs\agent-brain\. Replace in tests.</summary>
    public Action<string>? Log { get; set; }
}
