namespace CcDirector.Core.Dictation;

/// <summary>
/// Thrown when the dictation provider could not establish its connection to
/// the transcription service, after exhausting the automatic retry budget.
///
/// WHY THIS EXISTS
/// ---------------
/// On 2026-06-06 OpenAI's realtime endpoint had a ~90s blip where its edge
/// returned HTTP 504 to the WebSocket upgrade. The raw failure surfaced to the
/// user as "The server returned status code '504' when status code '101' was
/// expected" - technically accurate, humanly useless. This exception names the
/// actual situation (transient service/network problem, try again) while
/// preserving the raw error and the original exception for the log. It also
/// gives the UI a type to recognize "connect failed" distinctly from other
/// dictation failures. See issue #189.
/// </summary>
public sealed class DictationConnectException : Exception
{
    /// <summary>How many connection attempts were made before giving up.</summary>
    public int Attempts { get; }

    public DictationConnectException(string lastError, int attempts, Exception inner)
        : base(BuildMessage(lastError, attempts), inner)
    {
        Attempts = attempts;
    }

    private static string BuildMessage(string lastError, int attempts)
        => $"Could not connect to the transcription service ({attempts} attempts). "
           + "This is usually a temporary service or network problem - wait a moment and try again. "
           + $"Last error: {lastError}";
}
