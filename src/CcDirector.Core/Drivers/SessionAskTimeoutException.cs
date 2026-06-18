namespace CcDirector.Core.Drivers;

/// <summary>
/// Raised by <see cref="SessionAskRunner.AskAsync"/> when the agent does not produce a
/// reply within the caller's timeout. A distinct, logged type (not a generic timeout)
/// so callers can tell "the agent was too slow" apart from "the agent is unsupported"
/// or "the answer block was missing" - issue #509 requires the timeout path to surface
/// rather than hang.
/// </summary>
public sealed class SessionAskTimeoutException : Exception
{
    public SessionAskTimeoutException(string message) : base(message)
    {
    }
}
