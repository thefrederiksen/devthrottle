namespace CcRecorder.Recording;

/// <summary>One finalized audio segment on the phone.</summary>
public sealed class ChunkInfo
{
    public int Index { get; set; }
    public string File { get; set; } = "";
    public long StartMs { get; set; }
    public long DurationMs { get; set; }
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// True once the server has confirmed this segment. Persisted in
    /// manifest.json so a retry after a connection drop resumes at the first
    /// unsent segment instead of re-sending bytes the server already has.
    /// </summary>
    public bool Uploaded { get; set; }
}

/// <summary>A note typed during recording, offset from recording start.</summary>
public sealed class NoteInfo
{
    public long TMs { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>
/// Per-recording sidecar manifest. Persisted as manifest.json next to the
/// audio segments and uploaded to the Gateway on completion. Mirrors the
/// server-side <c>RecordingManifest</c> contract.
/// </summary>
public sealed class LocalManifest
{
    public string RecordingId { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? EndedAt { get; set; }
    public int SampleRateHz { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public string Codec { get; set; } = "aac-m4a";
    public List<ChunkInfo> Chunks { get; set; } = new();
    public List<NoteInfo> Notes { get; set; } = new();

    // UPLOAD state - purely about getting the audio bytes onto the server.
    // One of: Queued, Uploading, Uploaded, Retry. "Uploaded" means every
    // segment is safely on the server; it has nothing to do with transcription.
    public string State { get; set; } = "Queued";
    public string? UploadError { get; set; }

    // TRANSCRIPTION state - a separate server job that runs after upload. Null
    // until upload finishes. One of: Transcribing, Transcribed, Failed. A
    // transcription failure NEVER changes the upload State above: the audio is
    // already safe on the server.
    public string? TranscriptionState { get; set; }
    public string? TranscriptError { get; set; }
    public string? VaultDocId { get; set; }
    public string? Transcript { get; set; }

    // Human-readable progress for the upload pass currently in flight (e.g.
    // "Sending segment 3/5"). Persisted so the library row reflects it live
    // whether the upload is driven by the app or the background WorkManager
    // worker; cleared when the upload reaches a terminal state.
    public string? UploadProgress { get; set; }

    // Structured progress so the UI can draw a determinate bar, not just text.
    // Phase is "sending" (pushing segments) or "transcribing" (server is
    // turning them into text); null when no upload is in flight. Current/Total
    // are segment counts for the active phase. All cleared on a terminal state.
    public string? UploadPhase { get; set; }
    public int UploadCurrent { get; set; }
    public int UploadTotal { get; set; }
}

/// <summary>Lightweight library-row view of a recording on disk.</summary>
public sealed record RecordingSummary(
    string RecordingId,
    string Title,
    string StartedAt,
    int SegmentCount,
    long DurationMs,
    string State,
    string? VaultDocId,
    string? Transcript,
    string? UploadError,
    string? UploadProgress,
    string? UploadPhase,
    int UploadCurrent,
    int UploadTotal,
    string? TranscriptionState,
    string? TranscriptError);
