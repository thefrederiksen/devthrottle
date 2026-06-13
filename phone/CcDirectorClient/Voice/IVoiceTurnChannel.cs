namespace CcDirectorClient.Voice;

/// <summary>
/// The submit/poll seam the Gateway async voice-turn pipeline rides on (issue #378).
/// Extracted from <see cref="DirectorVoiceClient"/> (which implements it over real HTTP)
/// so the resilient submit-retry + poll-retry/backoff loop in <see cref="VoiceTurnRunner"/>
/// can be exercised with a fake channel off-device (issue #405). The runner depends only
/// on this interface, never on the concrete HTTP client, so a unit test can make submit or
/// poll throw, return a 404/410, or land a terminal stage on demand.
/// </summary>
public interface IVoiceTurnChannel
{
    /// <summary>
    /// Submit a recorded utterance to the Gateway and return the turn id to poll with.
    /// May throw on a transport failure; the runner decides whether to retry.
    /// </summary>
    Task<VoiceTurnSubmitResult> SubmitVoiceTurnAsync(
        string gatewayBase, string sessionId, byte[] audio, string mime, CancellationToken ct = default);

    /// <summary>
    /// Poll the Gateway for the result of a previously submitted turn. On a non-2xx
    /// response it throws <see cref="VoiceTurnHttpException"/> carrying the status code
    /// so the runner can tell a transient failure (network / 5xx) from a terminal one
    /// (404 expired, 410 session gone).
    /// </summary>
    Task<VoiceTurnPollResult> PollVoiceTurnAsync(
        string gatewayBase, string sessionId, string turnId, CancellationToken ct = default);
}
