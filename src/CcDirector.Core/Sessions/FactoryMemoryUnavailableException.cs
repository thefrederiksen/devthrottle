namespace CcDirector.Core.Sessions;

/// <summary>
/// A factory session could not be given its factory's memory, so it was not opened (Factory Memory mission, phase
/// 3a, section 5.4). The message is the whole answer the caller sees, in the words a person acts on - the same
/// shape as the pooled-worktree refusal, which is the precedent for a create that ends in its own words.
/// </summary>
public sealed class FactoryMemoryUnavailableException : InvalidOperationException
{
    public FactoryMemoryUnavailableException(string factory, string reason, Exception? inner = null)
        : base($"No session was opened. It belongs to the factory '{factory}', and a factory session never starts " +
               $"without its memory; the memory could not be put in place: {reason}", inner)
    {
        Factory = factory;
        Reason = reason;
    }

    /// <summary>The factory whose memory could not be had.</summary>
    public string Factory { get; }

    /// <summary>Why, on its own - the message says it in full.</summary>
    public string Reason { get; }
}
