using System.Text.Json.Serialization;

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
    [JsonIgnore] public bool Uploaded { get; set; }
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

    // Upload queue state. The phone never deletes a recording regardless of
    // this value. One of: Queued, Uploading, Uploaded, Retry.
    public string State { get; set; } = "Queued";
    public string? VaultDocId { get; set; }
    public string? Transcript { get; set; }
    public string? UploadError { get; set; }
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
    string? Transcript);
