namespace CcDirector.AgentBrain;

/// <summary>
/// The brain contract: a warm Claude Code session you can ask, reset, and recover -
/// independent of WHERE the session lives. Two implementations:
///
///   - <see cref="AgentBrainClient"/> - remote-controls a session owned by a CC
///     Director over its Control API (REST). Requires a running Director.
///   - HostedAgent (CcDirector.HostedAgent) - owns its claude.exe directly via an
///     embedded pseudoconsole. No external process dependency; many per host process.
///
/// Callers (the panel, the gateway brief agent) code against this interface and do
/// not care which transport is underneath.
/// </summary>
public interface IAgentBrain : IDisposable
{
    /// <summary>Identifier of the live session: the Director session GUID for the REST
    /// client, the claude-internal session id for the hosted agent. Null before start.</summary>
    string? SessionId { get; }

    /// <summary>Send a prompt and return the full reply text from the transcript.</summary>
    Task<AskResult> AskAsync(string prompt, CancellationToken ct = default);

    /// <summary>Abort the current turn (Esc for Claude); the session stays alive and
    /// prompt-ready. Safe to call while an AskAsync is in flight on another task.</summary>
    Task CancelAsync(CancellationToken ct = default);

    /// <summary>Reset the conversation context without restarting the process (/clear).</summary>
    Task<ClearResult> ClearAsync(CancellationToken ct = default);

    /// <summary>Hard recovery: kill the current session and bring up a fresh one.</summary>
    Task RestartAsync(CancellationToken ct = default);

    /// <summary>Terminate the session.</summary>
    Task KillAsync(CancellationToken ct = default);

    /// <summary>Liveness + activity + token usage snapshot.</summary>
    Task<BrainHealth> GetHealthAsync(CancellationToken ct = default);
}
