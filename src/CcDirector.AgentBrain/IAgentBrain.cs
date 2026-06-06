namespace CcDirector.AgentBrain;

/// <summary>
/// The brain contract: a warm Claude Code session you can ask, reset, and recover.
/// The implementation is HostedAgent (CcDirector.HostedAgent): it owns its claude.exe
/// directly via an embedded pseudoconsole - no external process dependency, many per
/// host process. (The original Director-REST transport was retired by issue #184: the
/// brain must not depend on a Director being up.)
///
/// Callers (the panel, the gateway brief agent) code against this interface, which is
/// also the test seam for consumers.
/// </summary>
public interface IAgentBrain : IDisposable
{
    /// <summary>The agent-internal session id of the live session (changes on every
    /// context clear). Null before start.</summary>
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
