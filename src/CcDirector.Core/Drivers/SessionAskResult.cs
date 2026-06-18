namespace CcDirector.Core.Drivers;

/// <summary>
/// Outcome of a single <see cref="SessionAskRunner"/> ask. A throwaway agentic question
/// answered over a real driver-backed session (open, submit, read the reply from the
/// transcript, tear down), so the work bills against the user's subscription rather than
/// the metered one-shot path. See issue #509.
/// </summary>
public sealed class SessionAskResult
{
    /// <summary>
    /// The answer text the agent emitted inside the documented delimiter pair
    /// (<see cref="SessionAskRunner.AnswerBeginMarker"/> ..
    /// <see cref="SessionAskRunner.AnswerEndMarker"/>), with the surrounding markers and
    /// the leading/trailing whitespace removed. Never null on a successful ask.
    /// </summary>
    public string Answer { get; init; } = "";

    /// <summary>
    /// The full reply text the agent produced (the last Text widget of the turn) before
    /// the answer block was extracted from it. Useful for diagnostics and for callers
    /// that want to inspect the surrounding prose.
    /// </summary>
    public string RawReply { get; init; } = "";

    /// <summary>Wall-clock seconds from submit until the reply landed in the transcript.</summary>
    public double ReplySeconds { get; init; }
}
