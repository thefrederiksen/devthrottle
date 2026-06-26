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
/// - <see cref="Stop"/> halts capture immediately and may DISCARD audio still
///   sitting in the driver's buffers. Use it only on paths that throw the clip
///   away anyway (cancel, teardown).
/// - <see cref="StopAsync"/> halts capture and returns only AFTER the source has
///   delivered its final buffered audio - the tail of speech - via
///   <see cref="OnAudioChunk"/>. A whole-clip transcription path MUST stop with
///   this and only snapshot the buffer once it returns, or the last words the
///   user spoke (still in the driver's buffers at the moment of the stop) are
///   clipped. This is the no-loss counterpart to <see cref="Stop"/>.
/// </summary>
public interface IAudioSource
{
    /// <summary>
    /// Human-readable name of the capture device this source is reading from
    /// (e.g. "Microphone (FDUCE SL40 Audio Device)"). Used so a no-audio failure
    /// can name the exact device the user needs to check, instead of leaking the
    /// transcription provider's opaque internal error.
    /// </summary>
    string Description { get; }

    /// <summary>Fires for every captured audio buffer. PCM16 little-endian. Raised on the source's own thread.</summary>
    event Action<byte[]>? OnAudioChunk;

    /// <summary>Begin capturing. Must be synchronous so capture starts before any network connection completes.</summary>
    void Start();

    /// <summary>
    /// Stop capturing IMMEDIATELY, discarding any audio still buffered in the
    /// driver. Only for paths that discard the clip (cancel/teardown). For a
    /// whole-clip transcription use <see cref="StopAsync"/> so the tail is kept.
    /// </summary>
    void Stop();

    /// <summary>
    /// Stop capturing and return only AFTER the source has delivered its final
    /// buffered audio via <see cref="OnAudioChunk"/>, so the caller can snapshot
    /// the whole recording - including the trailing words - without clipping the
    /// end. <paramref name="drainTimeout"/> bounds the wait so a wedged driver
    /// that never signals completion cannot hang the caller; on timeout the
    /// source proceeds with whatever it has delivered.
    /// </summary>
    Task StopAsync(TimeSpan drainTimeout);
}
