namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One finalized audio segment in a phone recording. The phone records into
/// short rolling segments (default ~1 minute) so a crash loses at most the
/// current open segment and each piece stays under the transcription API's
/// per-file size limit.
/// </summary>
public sealed record RecordingChunkInfo(
    int Index,
    string File,
    long StartMs,
    long DurationMs,
    long Bytes,
    string Sha256);

/// <summary>
/// A note the user typed while recording, stamped with the millisecond offset
/// from the start of the recording so it can be placed on the transcript
/// timeline.
/// </summary>
public sealed record RecordingNote(
    long TMs,
    string Text);

/// <summary>
/// The full per-recording manifest the phone uploads. Mirrors the on-device
/// sidecar JSON. Sent on <c>complete</c>; the header fields alone are sent on
/// <c>register</c>.
/// </summary>
public sealed record RecordingManifest(
    string RecordingId,
    string Title,
    string DeviceId,
    string StartedAt,
    string? EndedAt,
    int SampleRateHz,
    int Channels,
    string Codec,
    List<RecordingChunkInfo> Chunks,
    List<RecordingNote> Notes);

/// <summary>
/// Body of <c>POST /ingest/recording</c>. Registers a recording before any
/// chunks are uploaded. Idempotent on <see cref="RecordingId"/>.
/// </summary>
public sealed record RecordingRegisterRequest(
    string RecordingId,
    string Title,
    string DeviceId,
    string StartedAt,
    string Codec,
    int SampleRateHz,
    int Channels);

/// <summary>
/// One row in the Gateway transcripts list: enough to render the list and
/// link to the transcript text + audio segments.
/// </summary>
public sealed record RecordingListItem(
    string RecordingId,
    string Title,
    string StartedAt,
    string State,
    int Segments,
    long DurationMs,
    bool HasTranscript,
    string? TranscriptPath,
    bool InVault,
    string? Subtitle,
    string? Summary);

/// <summary>
/// Body of <c>PATCH /ingest/recording/{id}/meta</c>. Updates the human-readable
/// metadata a person or an external agent attaches to a transcript. Every field
/// is optional: a null field is left unchanged, a non-null field is applied
/// (subtitle/summary may be set to empty to clear them; a blank title is
/// ignored so a transcript never loses its title).
/// </summary>
public sealed record RecordingMetaUpdate(
    string? Title,
    string? Subtitle,
    string? Summary);

/// <summary>
/// Response of <c>GET /ingest/recording/{id}/status</c>. <see cref="State"/>
/// is one of: receiving, queued, transcribing, cleaning, transcribed, error.
/// Transcription runs in a background worker, so <c>complete</c> returns
/// "queued" immediately. <see cref="Attempts"/> is the number of full-job
/// attempts made; when a job fails with attempts remaining,
/// <see cref="NextRetryAtUtc"/> is the ISO-8601 UTC time the worker will retry.
/// </summary>
public sealed record RecordingStatusDto(
    string RecordingId,
    string Title,
    string State,
    int ChunksReceived,
    int ChunksTotal,
    int ChunksTranscribed,
    string? VaultDocId,
    string? Error,
    string? Transcript = null,
    int Attempts = 0,
    string? NextRetryAtUtc = null);
