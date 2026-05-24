namespace CcDirectorClient.Voice;

/// <summary>
/// Speaks reply text aloud using the device's native text-to-speech engine. The
/// reply is synthesized on-device (no network /tts fetch) so it stays reliable
/// when backgrounded or on weak signal. <see cref="SpeakAsync"/> completes only
/// when the utterance has finished playing, so the conductor can wait before
/// listening for the user's reply. Named to avoid colliding with MAUI's own
/// Microsoft.Maui.Media.ITextToSpeech.
/// </summary>
public interface IReplySpeaker
{
    /// <summary>Initialize the engine. Safe to call more than once; returns true when ready.</summary>
    Task<bool> InitAsync();

    /// <summary>
    /// Speak <paramref name="text"/> and complete when playback finishes (or is
    /// stopped/cancelled). No-op for empty text.
    /// </summary>
    Task SpeakAsync(string text, CancellationToken ct = default);

    /// <summary>Stop any in-progress speech immediately.</summary>
    void Stop();
}
