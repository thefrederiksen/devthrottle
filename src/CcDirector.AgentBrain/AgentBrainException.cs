namespace CcDirector.AgentBrain;

/// <summary>
/// The single exception type AgentBrainClient throws for protocol-level failures:
/// unexpected HTTP status, timeouts waiting for session state, or a session that is
/// not in a usable state. The message always carries the operation, endpoint and
/// observed state so the caller (or its log) can see exactly what went wrong.
/// No fallbacks: callers recover with <see cref="AgentBrainClient.RestartAsync"/>.
/// </summary>
public sealed class AgentBrainException : Exception
{
    public AgentBrainException(string message) : base(message)
    {
    }

    public AgentBrainException(string message, Exception inner) : base(message, inner)
    {
    }
}
