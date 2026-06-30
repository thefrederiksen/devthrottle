using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Append-only JSONL log of every completed dictation session. The data
/// captured here is the raw material for offline analysis: prompt tuning,
/// dictionary curation, latency tracking, regression sniffing.
///
/// One file per calendar day (UTC) under
/// <c>%LOCALAPPDATA%/cc-director/dictation/sessions/YYYY-MM-DD.jsonl</c>.
/// Each session ends in exactly one line. Failures during a session
/// still produce a record so we can audit them later.
///
/// Thread-safe via a static lock around the file open + write. Sessions
/// log on a background thread so the endpoint never blocks on disk.
/// </summary>
public static class DictationSessionLog
{
    private static readonly object _gate = new();

    /// <summary>
    /// Append one session record to today's JSONL file. Fire-and-forget.
    /// Any failure is logged via <see cref="FileLog"/> and swallowed -
    /// session logging must never break a live dictation flow.
    /// </summary>
    public static void TryAppend(DictationSessionRecord record)
    {
        try
        {
            var dir = ResolveDir();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
            var line = JsonSerializer.Serialize(record) + Environment.NewLine;
            lock (_gate)
            {
                File.AppendAllText(path, line);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationSessionLog] append FAILED: {ex.Message}");
        }
    }

    private static string ResolveDir()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-director", "dictation", "sessions");
    }
}

/// <summary>
/// One row in the dictation session log. Field names are stable (JSONL
/// readers depend on them); add new optional fields rather than renaming.
/// </summary>
public sealed record DictationSessionRecord(
    string TimestampUtc,
    string SessionId,
    string Profile,
    int VocabularyTermCount,
    int MistranscriptionPatternCount,
    long RecordingDurationMs,
    long StopToTranscribedMs,
    long StopToCleanedMs,
    int AudioBytesReceived,
    string RawTranscript,
    string CleanedTranscript,
    bool CleanupApplied,
    string? CleanupReason,
    string CleanupModel,
    string? RemoteIp,
    string? ClientError,
    string? Source = null,
    // Capture-health diagnostics (issue #863). Optional, default 0 so every existing
    // reader and the non-NAudio record writers (Gateway, web dictate) keep working.
    // ExpectedAudioBytes is rate x recording-duration; comparing it to AudioBytesReceived
    // gives the audio-loss deficit. The gap/handler fields localize the loss: large gaps
    // with small handler time = upstream under-delivery (e.g. Remote Desktop redirection);
    // large handler time = local capture-thread stall.
    long ExpectedAudioBytes = 0,
    int CaptureCallbackCount = 0,
    double MaxCaptureCallbackGapMs = 0,
    int LongCaptureGapCount = 0,
    double MaxCaptureHandlerMs = 0,
    int CaptureBufferCount = 0,
    int CaptureBufferMs = 0,
    // Duration-based capture-health for the compressed browser path (mobile MediaRecorder), where a
    // fixed bytes/sec yardstick does not apply: the recording wall-clock the mic was open versus the
    // decoded audio duration of the captured clip. A gap between them is dropped audio. (The raw-PCM
    // surfaces - desktop, overlay - measure loss in bytes above and leave these 0.)
    double RecordedWallMs = 0,
    double DecodedAudioSeconds = 0);
