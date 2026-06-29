using CcDirector.Core.Dictation;
using CcDirector.Core.Recording;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The phone Notes worker's <see cref="IRecordingTranscriber"/> (issue #839). It is a thin adapter
/// onto the single <see cref="GatewayTranscriptionService"/>, so the recorder uses exactly the same
/// resolve-mode-and-key path and the same provider choice (in-process Whisper for on-device mode, or
/// the resolved OpenAI-compatible batch endpoint for the remote modes) as every other batch caller -
/// it no longer resolves the key itself.
///
/// Each finalized one-minute segment is transcribed RAW (no per-segment correction); the assembled
/// concatenation runs through the validated dictionary corrector ONCE, so the assembled transcript is
/// provably the per-segment raw concatenation plus dictionary edits only.
/// </summary>
internal sealed class GatewayServiceRecordingTranscriber : IRecordingTranscriber
{
    private readonly GatewayTranscriptionService _service;

    public GatewayServiceRecordingTranscriber(GatewayTranscriptionService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public Task<string> TranscribeChunkAsync(byte[] audio, string contentType, string fileName, CancellationToken ct = default)
        => _service.TranscribeSegmentRawAsync(audio, fileName, contentType, ct);

    public Task<CleanupOutcome> CleanupAsync(string rawTranscript, CancellationToken ct = default)
        => _service.CleanupAsync(rawTranscript, ct);
}
