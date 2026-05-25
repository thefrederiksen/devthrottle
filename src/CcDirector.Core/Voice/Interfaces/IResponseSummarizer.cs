namespace CcDirector.Core.Voice.Interfaces;

/// <summary>
/// Service for summarizing Claude responses into conversational form for TTS.
/// </summary>
public interface IResponseSummarizer
{
    /// <summary>
    /// Summarize a Claude response into a short, conversational form suitable for speech.
    /// </summary>
    /// <param name="response">The full Claude response text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A short, conversational summary (2-3 sentences).</returns>
    Task<string> SummarizeAsync(string response, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turn a chunk of raw, recent terminal activity from a still-running agent
    /// turn into a brief spoken progress note ("Still going. It's been editing
    /// the recorder and running tests."). Concepts only, no code/paths/symbols.
    /// Returns an empty string when there is nothing meaningful to say or the note
    /// could not be produced; the caller speaks nothing in that case rather than
    /// reading raw terminal text aloud.
    /// </summary>
    /// <param name="recentActivity">Cleaned tail of the session's terminal output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A one-to-two sentence spoken progress note, or empty string.</returns>
    Task<string> SummarizeProgressAsync(string recentActivity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the summarizer is available (e.g., Claude CLI is installed).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Error message if not available.
    /// </summary>
    string? UnavailableReason { get; }
}
