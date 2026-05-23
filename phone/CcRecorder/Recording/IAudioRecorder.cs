namespace CcRecorder.Recording;

/// <summary>
/// Cross-platform contract for the offline recorder. The Android
/// implementation runs a foreground service so capture survives screen lock
/// and backgrounding, rotating a new finalized segment on a fixed interval so
/// a crash loses at most one segment.
/// </summary>
public interface IAudioRecorder
{
    bool IsRecording { get; }

    /// <summary>The recording currently in progress, or null when idle.</summary>
    LocalManifest? Current { get; }

    /// <summary>Elapsed time of the in-progress recording.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Fires on any state change (start, segment added, note added, stop).</summary>
    event EventHandler? Changed;

    /// <summary>Begin a new recording. Returns immediately; capture continues in the service.</summary>
    Task StartAsync(string title);

    /// <summary>Attach a timestamped note to the in-progress recording.</summary>
    void AddNote(string text);

    /// <summary>Stop and finalize the in-progress recording.</summary>
    Task StopAsync();

    /// <summary>All recordings stored on the device, newest first.</summary>
    IReadOnlyList<RecordingSummary> ListRecordings();

    /// <summary>Absolute path to a recording's folder (manifest + segments).</summary>
    string RecordingFolder(string recordingId);

    /// <summary>Load a recording's manifest from disk, or null if missing.</summary>
    LocalManifest? LoadManifest(string recordingId);

    /// <summary>
    /// Persist the outcome of an upload onto a recording's manifest so the
    /// library can show it as a transcript (and survive app restarts).
    /// </summary>
    void ApplyUploadResult(string recordingId, string state, string? vaultDocId, string? transcript, string? error);

    /// <summary>
    /// Upload every queued/failed recording to the Gateway, one at a time,
    /// updating each manifest's state. Safe to call from anywhere (app or a
    /// background worker); serialized internally and idempotent. No-ops when
    /// offline or unconfigured.
    /// </summary>
    Task ProcessUploadQueueAsync();
}
