using CcDirector.Core.Dictation;

namespace CcDirector.Core.Recording;

/// <summary>
/// Transcribes a single finalized audio segment and runs the final cleanup
/// pass. Abstracted so <see cref="RecordingIngestService"/> can be unit-tested
/// with a fake that returns canned text instead of calling OpenAI.
/// </summary>
public interface IRecordingTranscriber
{
    /// <summary>
    /// Transcribe one audio segment to raw text. <paramref name="contentType"/>
    /// and <paramref name="fileName"/> tell the transcription API how to decode
    /// the bytes (the file extension matters).
    /// </summary>
    Task<string> TranscribeChunkAsync(
        byte[] audio,
        string contentType,
        string fileName,
        CancellationToken ct = default);

    /// <summary>
    /// Run the assembled raw transcript through the cleanup pass (vocabulary +
    /// known-mistranscription glossary). Returns a <see cref="CleanupOutcome"/>
    /// that always carries something safe to ship.
    /// </summary>
    Task<CleanupOutcome> CleanupAsync(string rawTranscript, CancellationToken ct = default);
}
