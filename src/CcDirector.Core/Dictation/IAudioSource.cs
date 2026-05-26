namespace CcDirector.Core.Dictation;

/// <summary>
/// Transport-agnostic microphone (or replay) source the dictation pipeline
/// captures from. Kept deliberately tiny so the capture-first orchestration in
/// <see cref="DictationPipeline"/> can be unit-tested without a real audio
/// device: the desktop path implements this over NAudio's WaveInEvent, while
/// tests implement it with a fake that emits known PCM chunks on demand.
///
/// Contract:
/// - <see cref="Start"/> begins capture synchronously and (near) immediately;
///   the very first audio sample MUST be available without an await so the
///   pipeline can start capturing before the slow transcription connection
///   completes. This is the whole point of the abstraction - no captured audio
///   is lost to connection latency.
/// - <see cref="OnAudioChunk"/> fires for every captured buffer, on the
///   source's own thread. Chunks are PCM16 little-endian.
/// - <see cref="Stop"/> halts capture; no further <see cref="OnAudioChunk"/>
///   events fire after it returns.
/// </summary>
public interface IAudioSource
{
    /// <summary>Fires for every captured audio buffer. PCM16 little-endian. Raised on the source's own thread.</summary>
    event Action<byte[]>? OnAudioChunk;

    /// <summary>Begin capturing. Must be synchronous so capture starts before any network connection completes.</summary>
    void Start();

    /// <summary>Stop capturing. No further <see cref="OnAudioChunk"/> events fire after this returns.</summary>
    void Stop();
}
