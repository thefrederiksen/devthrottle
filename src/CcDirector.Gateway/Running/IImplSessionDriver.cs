namespace CcDirector.Gateway.Running;

/// <summary>
/// The seam the queue runner (#274) uses to start one implementation session on a Director and read
/// its transcript - exactly the three operations the runner needs and nothing else. Production wraps
/// the Gateway's existing <see cref="Discovery.DirectorEndpointClient"/> (see
/// <see cref="DirectorImplSessionDriver"/>); tests supply a fake so the sequencing logic is provable
/// without a live Director. Keeping the runner behind this seam is also what keeps ALL runner logic
/// at the Gateway level - the Director host gains nothing (criterion 7).
/// </summary>
public interface IImplSessionDriver
{
    /// <summary>
    /// Start one implementation session on the target Director, seeded with
    /// <paramref name="seedPrompt"/> as its first prompt (built by the item's
    /// <see cref="ISourceAdapter"/>, issue #300 - e.g. <c>/implementation-loop 262</c> for github,
    /// <c>/implementation-loop --source devops 1203</c> for devops). Returns the new session id on
    /// success, or null with a reason on failure. This is the ONLY way the runner starts work for an
    /// item, so an item whose source has no adapter is started simply by never calling this for it.
    /// </summary>
    /// <param name="itemId">The work item id, for logging/diagnostics only.</param>
    /// <param name="seedPrompt">The session's seed prompt (PrePrompt), built per source.</param>
    /// <param name="ct">Cancellation.</param>
    Task<(string? sessionId, string? error)> StartImplementationSessionAsync(
        string itemId, string seedPrompt, CancellationToken ct);

    /// <summary>
    /// Read the current cleaned transcript text for the session. The runner scans it for the
    /// <c>IMPL-LOOP-TERMINAL</c> sentinel (child 1, #272). Returns null when the transcript could
    /// not be read (the runner retries rather than guessing).
    /// </summary>
    Task<string?> ReadTranscriptAsync(string sessionId, CancellationToken ct);
}
