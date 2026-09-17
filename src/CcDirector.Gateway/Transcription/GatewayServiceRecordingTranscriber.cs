using CcDirector.Core.Dictation;
using CcDirector.Core.Recording;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The phone Notes worker's <see cref="IRecordingTranscriber"/> (issue #839). It is a thin adapter
/// onto the single <see cref="GatewayTranscriptionService"/>, so the recorder uses exactly the same
/// resolve-mode-and-key path and the same hosted provider-compatible batch endpoint as every other
/// batch caller - it no longer resolves the key itself.
///
/// Each finalized one-minute segment is transcribed RAW (no per-segment correction); the assembled
/// concatenation runs through the validated dictionary corrector ONCE, so the assembled transcript is
/// provably the per-segment raw concatenation plus dictionary edits only.
///
/// The adapter is built per tenant (by <see cref="Api.RecordingEndpoints"/>) and carries that tenant
/// into the cleanup call, so the corrector reads THAT tenant's glossary through the one
/// <see cref="TenantGlossary"/> owner - the same mechanism every live-dictation caller uses
/// (issue #2482).
/// </summary>
internal sealed class GatewayServiceRecordingTranscriber : IRecordingTranscriber
{
    private readonly GatewayTranscriptionService _service;
    private readonly TenantId _tenant;

    public GatewayServiceRecordingTranscriber(GatewayTranscriptionService service, TenantId tenant)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _tenant = tenant;
    }

    public Task<string> TranscribeChunkAsync(byte[] audio, string contentType, string fileName, CancellationToken ct = default)
        => _service.TranscribeSegmentUncorrectedAsync(audio, fileName, contentType, ct);

    public Task<CleanupOutcome> CleanupAsync(string rawTranscript, CancellationToken ct = default)
        => _service.CleanupAsync(rawTranscript, _tenant, ct);
}
