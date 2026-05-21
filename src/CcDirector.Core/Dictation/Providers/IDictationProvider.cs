namespace CcDirector.Core.Dictation.Providers;

/// <summary>
/// Transport-agnostic interface for the speech-to-text engine the dictation
/// library talks to.
///
/// Two implementations are planned:
///
/// - <c>OpenAiTranscriptionProvider</c> (Phase 1): batch transcription against
///   <c>/v1/audio/transcriptions</c>. Accumulates audio in memory; transcribes
///   on <see cref="StopAsync"/>. No partial transcripts.
/// - <c>OpenAiRealtimeProvider</c> (Phase 3): WebSocket streaming against the
///   Realtime API. Surfaces partial transcripts as soon as the model emits
///   them.
///
/// The <see cref="DictationSession"/> facade is written against this interface,
/// so swapping providers does not change consumers.
/// </summary>
public interface IDictationProvider : IAsyncDisposable
{
    /// <summary>
    /// Start a transcription session. The <paramref name="sttPrompt"/> is the
    /// vocabulary-packed prompt parameter that biases the model toward the
    /// user's glossary at decode time.
    /// </summary>
    Task StartAsync(string sttPrompt, CancellationToken ct = default);

    /// <summary>
    /// Submit a chunk of audio. The chunk format is provider-specific; for
    /// the OpenAI providers, this is a complete audio container (WAV, MP3,
    /// M4A) for the batch variant, or PCM frames for the realtime variant.
    /// </summary>
    Task PushAudioAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default);

    /// <summary>
    /// Signal end of audio and return the final transcript. After this call
    /// the provider may be reused only after a fresh <see cref="StartAsync"/>.
    /// </summary>
    Task<string> StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Fires when the provider has a partial transcript to share. Streaming
    /// providers fire repeatedly during a session; batch providers may stay
    /// silent or fire only once just before <see cref="StopAsync"/> returns.
    /// </summary>
    event Action<string>? OnPartial;
}
