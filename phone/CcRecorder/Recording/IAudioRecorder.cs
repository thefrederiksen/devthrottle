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

    /// <summary>True while recording is paused (capture suspended, not stopped).</summary>
    bool IsPaused { get; }

    /// <summary>The recording currently in progress, or null when idle.</summary>
    LocalManifest? Current { get; }

    /// <summary>Elapsed time of the in-progress recording, excluding paused time.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Fires on any state change (start, segment added, note added, stop).</summary>
    event EventHandler? Changed;

    /// <summary>Begin a new recording. Returns immediately; capture continues in the service.</summary>
    Task StartAsync(string title);

    /// <summary>
    /// Update the in-progress recording's title. Lets the user edit the title at
    /// any point while recording; the value present when they stop is the one
    /// that's saved. No-op when not recording.
    /// </summary>
    void SetTitle(string title);

    /// <summary>Pause capture (keeps the session open). No-op if not recording or already paused.</summary>
    void Pause();

    /// <summary>Resume a paused capture. No-op if not paused.</summary>
    void Resume();

    /// <summary>
    /// Current microphone level in 0..1 for the live "is it hearing me" meter.
    /// Reading samples the recorder's peak since the last call; returns 0 when
    /// idle or paused.
    /// </summary>
    double ReadLevel();

    /// <summary>Attach a timestamped note to the in-progress recording.</summary>
    void AddNote(string text);

    /// <summary>Stop and finalize the in-progress recording.</summary>
    Task StopAsync();

    /// <summary>All recordings stored on the device, newest first.</summary>
    IReadOnlyList<RecordingSummary> ListRecordings();

    /// <summary>Absolute path to a recording's folder (manifest + segments).</summary>
    string RecordingFolder(string recordingId);

    /// <summary>Recording currently playing back, or null.</summary>
    string? PlayingRecordingId { get; }

    /// <summary>Play a recording's audio (all segments in order) on the phone speaker.</summary>
    void Play(string recordingId);

    /// <summary>Stop any in-progress playback.</summary>
    void StopPlayback();

    /// <summary>Load a recording's manifest from disk, or null if missing.</summary>
    LocalManifest? LoadManifest(string recordingId);

    /// <summary>
    /// Upload every queued/failed recording to the Gateway, one at a time,
    /// updating each manifest's state. Safe to call from anywhere (app or a
    /// background worker); serialized internally and idempotent. No-ops when
    /// offline or unconfigured.
    /// </summary>
    Task ProcessUploadQueueAsync();

    /// <summary>
    /// Hand the upload queue to the OS background scheduler (Android
    /// WorkManager), which runs under a wakelock so the upload survives the app
    /// being backgrounded, frozen by battery management, or swiped closed. Use
    /// this for reliable draining; <see cref="ProcessUploadQueueAsync"/> is the
    /// foreground path for immediate UI feedback. Idempotent.
    /// </summary>
    void EnqueueBackgroundUpload();
}
