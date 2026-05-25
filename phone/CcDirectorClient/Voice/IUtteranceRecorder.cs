namespace CcDirectorClient.Voice;

/// <summary>One captured push-to-talk utterance: the encoded audio bytes and its MIME type.</summary>
public sealed record UtteranceAudio(byte[] Bytes, string Mime);

/// <summary>
/// Captures a single push-to-talk utterance to memory. Unlike the offline
/// recorder (rolling segments to disk for later upload), this records one short
/// clip and hands the bytes straight back for an immediate voice round-trip.
/// </summary>
public interface IUtteranceRecorder
{
    /// <summary>True while an utterance is being captured.</summary>
    bool IsRecording { get; }

    /// <summary>
    /// Current microphone level as 0..1 (peak since the last call), for a live
    /// "your voice is being heard" meter. Returns 0 when not recording.
    /// </summary>
    double ReadLevel();

    /// <summary>Begin capturing. Throws if already recording or the mic is unavailable.</summary>
    Task StartAsync();

    /// <summary>
    /// Stop capturing and return the recorded audio. Throws if not recording or if
    /// nothing was captured - an empty clip is a real failure to surface, not a
    /// silent empty upload.
    /// </summary>
    Task<UtteranceAudio> StopAsync();
}
