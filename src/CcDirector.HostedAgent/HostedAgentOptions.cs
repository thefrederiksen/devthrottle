namespace CcDirector.HostedAgent;

/// <summary>
/// Configuration for a <see cref="HostedAgent"/>. Only <see cref="WorkingDirectory"/>
/// is strictly required; the executable is resolved by the driver when not given.
/// Timeouts and gates default to the values proven by the issue #172 spike.
/// </summary>
public sealed class HostedAgentOptions
{
    /// <summary>Working directory for the hosted agent CLI. Required; must exist.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Full path to the agent executable. Empty = the driver resolves it from
    /// PATH and fails loud when the tool is not installed.</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>Base arguments for the spawn; null/empty = the driver's default (for
    /// Claude: "--dangerously-skip-permissions"). Session-id/resume flags are appended
    /// by the driver.</summary>
    public string? AgentArgs { get; set; }

    /// <summary>Pseudoconsole size. The defaults match a comfortable headless TUI.</summary>
    public short Cols { get; set; } = 120;

    public short Rows { get; set; } = 40;

    /// <summary>Seconds the terminal must be byte-silent before a send is allowed
    /// (the swallowed-Enter guard, measured on the backend's own buffer clock).</summary>
    public double QuietSeconds { get; set; } = 2.0;

    /// <summary>Max seconds to wait for the quiet gate.</summary>
    public double QuietTimeoutSeconds { get; set; } = 30.0;

    /// <summary>Max seconds to wait for the freshly spawned agent to paint and settle.</summary>
    public double StartTimeoutSeconds { get; set; } = 120.0;

    /// <summary>Max seconds AskAsync waits for the reply to land in the transcript.</summary>
    public double AskTimeoutSeconds { get; set; } = 300.0;

    /// <summary>Max seconds ClearAsync waits for the new transcript file to appear.</summary>
    public double ClearTimeoutSeconds { get; set; } = 60.0;

    /// <summary>Seconds between polls.</summary>
    public double PollIntervalSeconds { get; set; } = 0.25;

    /// <summary>Consecutive seconds the transcript must be stable before AskAsync
    /// accepts the last Text widget as the final answer (multi-block reply guard).</summary>
    public double ReplyStableSeconds { get; set; } = 1.5;

    /// <summary>Diagnostic log sink. Defaults to the shared agent-brain daily file.</summary>
    public Action<string>? Log { get; set; }
}
