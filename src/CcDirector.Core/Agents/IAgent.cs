namespace CcDirector.Core.Agents;

/// <summary>
/// Abstraction over a coding-agent CLI that Director can spawn into a terminal session.
/// Implementations describe the executable, its argument conventions for new vs resumed
/// sessions, and which Director features it supports (e.g. preassigned session IDs).
/// </summary>
/// <remarks>
/// The agent itself is a stateless strategy. Per-session state lives on <c>Session</c>.
/// Backends (ConPty, Embedded, etc.) remain agnostic to which agent is running.
/// </remarks>
public interface IAgent
{
    /// <summary>Which agent this is. Persisted on the Session for restart restoration.</summary>
    AgentKind Kind { get; }

    /// <summary>Absolute path to the agent's executable. Passed directly to the backend.</summary>
    string ExecutablePath { get; }

    /// <summary>
    /// True if this agent can accept a Director-generated session ID at spawn time
    /// (e.g. Claude's <c>--session-id &lt;uuid&gt;</c>). False if the agent assigns its own
    /// session ID internally (Pi). When false, Director cannot synchronously link the
    /// Session to an agent-side session file at creation time.
    /// </summary>
    bool SupportsPreassignedSessionId { get; }

    /// <summary>
    /// True if this agent supports Director's Studio mode (stream-json card UI).
    /// False means the New Session dialog should hide Studio mode for this agent.
    /// </summary>
    bool SupportsStudioMode { get; }

    /// <summary>
    /// Build the command-line spec to launch this agent.
    /// </summary>
    /// <param name="userArgs">
    /// Raw extra args supplied by the user (e.g. "--dangerously-skip-permissions").
    /// May be null or empty.
    /// </param>
    /// <param name="resumeSessionId">
    /// If non-null, the agent should resume the named session instead of starting fresh.
    /// Implementations that don't support resume (Pi v1) should ignore this and start fresh.
    /// </param>
    /// <param name="studioMode">
    /// If true, the caller wants Studio mode (stream-json). Implementations that don't
    /// support it should ignore this flag.
    /// </param>
    /// <returns>The args to pass and any preassigned session ID Director should track.</returns>
    AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode);
}
