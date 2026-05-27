namespace CcDirectorClient.Voice;

/// <summary>
/// Plays spoken-reply audio aloud. The reply is synthesized on the Director with
/// OpenAI TTS (the same voice the web voice page uses) and handed here as MP3
/// bytes, so the phone sounds identical to the web rather than using a robotic
/// on-device engine. <see cref="PlayAsync"/> completes only when playback has
/// finished, so the conductor can wait before listening for the user's reply.
/// Named to avoid colliding with MAUI's own Microsoft.Maui.Media.ITextToSpeech.
/// </summary>
public interface IReplySpeaker
{
    /// <summary>
    /// Play <paramref name="audio"/> (MP3 bytes from the Director's /tts endpoint)
    /// and complete when playback finishes (or is stopped/cancelled). No-op for
    /// empty audio. While playing, music is ducked under the voice on Android.
    /// </summary>
    Task PlayAsync(byte[] audio, CancellationToken ct = default);

    /// <summary>Stop any in-progress playback immediately.</summary>
    void Stop();
}
