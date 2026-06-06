namespace CcDirector.AgentBrain;

/// <summary>
/// The single exception type the brain throws for protocol-level failures: a dead or
/// wedged hosted CLI, timeouts waiting for quiet or for the transcript, or a session
/// that is not in a usable state. The message always carries the operation and the
/// observed state so the caller (or its log) can see exactly what went wrong.
/// No fallbacks: callers recover with <see cref="IAgentBrain.RestartAsync"/>.
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
