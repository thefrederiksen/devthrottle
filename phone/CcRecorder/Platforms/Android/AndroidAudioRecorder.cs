using System.Security.Cryptography;
using System.Text.Json;
using Android.Content;
using Android.Media;
using CcRecorder.Recording;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using Application = Android.App.Application;

namespace CcRecorder.Platforms.Android;

/// <summary>
/// Android <see cref="IAudioRecorder"/>. Records AAC into rolling ~1-minute
/// .m4a segments, finalizing each to disk and appending it to manifest.json so
/// a crash loses at most the current open segment. A foreground service keeps
/// the process alive across screen lock and backgrounding.
/// </summary>
public sealed class AndroidAudioRecorder : IAudioRecorder
{
    // 1-minute segments: maximum crash safety, every piece well under the
    // transcription API's per-file size limit. See the plan doc.
    private static readonly TimeSpan SegmentLength = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private MediaRecorder? _recorder;
    private System.Threading.Timer? _rollTimer;
    private LocalManifest? _manifest;
    private string _recordingDir = "";
    private DateTime _startedUtc;
    private DateTime _segmentStartedUtc;
    private int _segmentIndex;
    private bool _paused;
    private TimeSpan _pausedAccum;
    private DateTime _pauseStartedUtc;

    public bool IsRecording { get; private set; }
    public bool IsPaused => _paused;
    public LocalManifest? Current => _manifest;

    public TimeSpan Elapsed
    {
        get
        {
            if (!IsRecording) return TimeSpan.Zero;
            var raw = DateTime.UtcNow - _startedUtc - _pausedAccum;
            if (_paused) raw -= DateTime.UtcNow - _pauseStartedUtc;
            return raw < TimeSpan.Zero ? TimeSpan.Zero : raw;
        }
    }

    public event EventHandler? Changed;

    public void Pause()
    {
        lock (_gate)
        {
            if (!IsRecording || _paused) return;
            _rollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            try { _recorder?.Pause(); } catch { /* device may not support; treated as best-effort */ }
            _pauseStartedUtc = DateTime.UtcNow;
            _paused = true;
        }
        RaiseChanged();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!IsRecording || !_paused) return;
            _pausedAccum += DateTime.UtcNow - _pauseStartedUtc;
            try { _recorder?.Resume(); } catch { }
            _rollTimer?.Change(SegmentLength, SegmentLength);
            _paused = false;
        }
        RaiseChanged();
    }

    public double ReadLevel()
    {
        lock (_gate)
        {
            if (!IsRecording || _paused || _recorder is null) return 0;
            try
            {
                // MaxAmplitude is 0..32767, peak since last read. Square-root
                // shaping makes the meter feel linear to the ear.
                var amp = _recorder.MaxAmplitude;
                if (amp <= 0) return 0;
                return Math.Clamp(Math.Sqrt(amp / 32767.0), 0, 1);
            }
            catch { return 0; }
        }
    }

    private static string RootDir =>
        Path.Combine(global::Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath
                     ?? FileSystem.AppDataDirectory, "recordings");

    public string RecordingFolder(string recordingId) => Path.Combine(RootDir, recordingId);

    public AndroidAudioRecorder()
    {
        // Make sure the periodic background-upload safety net is scheduled on
        // every app (or worker) start. Idempotent.
        try { UploadScheduler.EnsurePeriodic(global::Android.App.Application.Context); }
        catch { /* scheduling is best-effort; the in-app queue still runs */ }
    }

    public Task StartAsync(string title)
    {
        StopPlayback(); // don't play and record at once
        lock (_gate)
        {
            if (IsRecording) return Task.CompletedTask;

            _manifest = new LocalManifest
            {
                Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle() : title,
                DeviceId = global::Android.OS.Build.Model ?? "android",
                StartedAt = DateTime.UtcNow.ToString("o"),
                Codec = "aac-m4a",
                SampleRateHz = 16000,
                Channels = 1,
            };
            _recordingDir = RecordingFolder(_manifest.RecordingId);
            Directory.CreateDirectory(_recordingDir);
            _startedUtc = DateTime.UtcNow;
            _segmentIndex = 0;
            _paused = false;
            _pausedAccum = TimeSpan.Zero;
            IsRecording = true;

            StartForegroundService();
            StartSegment();
            SaveManifest();

            _rollTimer = new System.Threading.Timer(_ => RollSegment(), null, SegmentLength, SegmentLength);
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    public void SetTitle(string title)
    {
        lock (_gate)
        {
            if (!IsRecording || _manifest is null) return;
            _manifest.Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle() : title.Trim();
            SaveManifest();
        }
        RaiseChanged();
    }

    public void AddNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            if (_manifest is null) return;
            _manifest.Notes.Add(new NoteInfo
            {
                TMs = (long)(DateTime.UtcNow - _startedUtc).TotalMilliseconds,
                Text = text.Trim(),
            });
            SaveManifest();
        }
        RaiseChanged();
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (!IsRecording) return Task.CompletedTask;
            _rollTimer?.Dispose();
            _rollTimer = null;
            if (_paused) { _recorder?.Resume(); _paused = false; } // so FinalizeSegment can stop cleanly
            FinalizeSegment();
            IsRecording = false;
            if (_manifest is not null)
            {
                _manifest.EndedAt = DateTime.UtcNow.ToString("o");
                _manifest.State = "Queued"; // queued for background upload; never deleted
                SaveManifest();
            }
            StopForegroundService();
        }
        // Hand the queue to WorkManager so it uploads even if the app is
        // swiped closed (and after reboot), constrained to network availability.
        UploadScheduler.EnqueueNow(global::Android.App.Application.Context);
        RaiseChanged();
        return Task.CompletedTask;
    }

    private static readonly SemaphoreSlim _uploadGate = new(1, 1);

    public void EnqueueBackgroundUpload()
        => UploadScheduler.EnqueueNow(global::Android.App.Application.Context);

    public async Task ProcessUploadQueueAsync()
    {
        // Do NOT gate on NetworkAccess == Internet. The Gateway is reachable
        // over Tailscale, which can ride a network that Android reports as
        // "constrained"/"local" (or even on mobile data with no public
        // internet). Only skip when there is no network radio at all; otherwise
        // attempt and let failures fall back to Retry.
        if (Connectivity.Current.NetworkAccess == NetworkAccess.None) return;

        // A fresh install (or a reinstall that wiped preferences) has no saved
        // URL. Seed the built-in default so the recording uploads instead of
        // silently sitting in the queue. The UI keeps the field editable.
        var server = Preferences.Get("gateway_url", "").Trim();
        if (string.IsNullOrWhiteSpace(server))
        {
            server = RecorderDefaults.GatewayUrl;
            Preferences.Set("gateway_url", server);
        }
        var token = Preferences.Get("gateway_token", "").Trim();

        if (!await _uploadGate.WaitAsync(0)) return; // a run is already in progress
        try
        {
            foreach (var summary in ListRecordings())
            {
                if (!RecordingUploadGate.NeedsUpload(summary.State, summary.Completed)) continue;
                if (Connectivity.Current.NetworkAccess == NetworkAccess.None) break;
                await UploadOneAsync(summary.RecordingId, server, token);
            }

            // Sync deletions: the computer is the master record. A recording
            // that was confirmed on the server and has since been removed there
            // is removed from the phone too.
            await ReconcileServerDeletionsAsync(server, token);
        }
        finally { _uploadGate.Release(); }
    }

    /// <summary>
    /// One-way deletion sync, server -> phone. The server is authoritative:
    /// any recording that this phone has fully delivered (uploaded AND completed)
    /// but that no longer exists on the server is deleted locally. Recordings whose
    /// audio is not yet confirmed, OR whose notes have not yet been delivered
    /// (complete not acknowledged), are never touched - we never lose audio or notes
    /// that are not safely on the server yet. If the server list cannot be fetched,
    /// nothing is deleted (uncertainty must not destroy local data).
    /// </summary>
    private async Task ReconcileServerDeletionsAsync(string server, string token)
    {
        var uploader = new IngestUploader(server, token);
        var serverIds = await uploader.ListServerRecordingIdsAsync();
        if (serverIds is null) return; // server state unknown -> never delete

        foreach (var summary in ListRecordings())
        {
            // Only fully-delivered recordings: audio uploaded AND the notes/complete call
            // acknowledged. A recording still owing its notes must never be deleted.
            if (!RecordingUploadGate.IsDeletable(summary.State, summary.Completed)) continue;
            if (serverIds.Contains(summary.RecordingId)) continue; // still on the server
            DeleteLocalRecording(summary.RecordingId);
        }
    }

    /// <summary>Permanently delete a recording's local folder (audio + manifest).</summary>
    private void DeleteLocalRecording(string recordingId)
    {
        var dir = RecordingFolder(recordingId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        RaiseChanged();
    }

    private async Task UploadOneAsync(string recordingId, string server, string token)
    {
        var m = LoadManifest(recordingId);
        if (m is null || m.Chunks.Count == 0) return;
        // Fully delivered = audio uploaded AND the complete/notes call acknowledged.
        // State=="Uploaded" alone is NOT terminal: the notes ride on the complete call,
        // which must still be confirmed (see the Completed flag).
        if (RecordingUploadGate.IsFullyDelivered(NormalizeState(m.State), m.Completed)) return;

        // One owner of manifest.json for the whole pass: the uploader mutates
        // this same object (per-segment Uploaded flags + progress text) and we
        // persist it here, so the terminal-state writes can never race the
        // uploader's progress writes on the same file.
        void Persist()
        {
            WriteManifest(recordingId, m);
            RaiseChanged();
        }

        void ClearProgress()
        {
            m.UploadProgress = null;
            m.UploadPhase = null;
            m.UploadCurrent = 0;
            m.UploadTotal = 0;
        }

        var uploader = new IngestUploader(server, token);

        // ---- Step 1: UPLOAD the audio bytes. Skipped when the audio is already fully on
        // the server (a prior pass got the bytes up but the complete call below had not yet
        // landed) - we resume straight to Step 2 instead of re-sending anything. ----
        if (RecordingUploadGate.ShouldUploadAudio(NormalizeState(m.State)))
        {
            m.State = "Uploading";
            m.UploadError = null;
            m.UploadProgress = "Starting...";
            m.UploadPhase = "sending";
            m.UploadCurrent = 0;
            m.UploadTotal = m.Chunks.Count;
            Persist();
            try
            {
                await uploader.UploadSegmentsAsync(m, RecordingFolder(recordingId), Persist);
                // Every segment is on the server. The audio is safe - regardless of
                // whatever the complete call and transcription do next.
                m.State = "Uploaded";
                m.UploadError = null;
                ClearProgress();
                Persist();
            }
            catch (Exception ex)
            {
                // Only a byte-transfer failure lands here. Stays queued; WorkManager
                // / next open retries, resuming from the first unsent segment.
                m.State = "Retry";
                m.UploadError = ex.Message;
                ClearProgress();
                Persist();
                return;
            }
        }

        // ---- Step 2: COMPLETE = deliver the manifest (the NOTES) to the server and trigger
        // server-side transcription. This is NOT best-effort: it is the only call that carries
        // the notes, so it MUST be retried until the server acknowledges it. Its failure leaves
        // Completed=false, so the recording stays in the upload queue for the next pass - the
        // notes are never stranded. The audio is already safe either way. Server-side
        // transcription then runs (or retries) entirely on the Gateway. ----
        m.TranscriptionState = "Transcribing";
        m.TranscriptError = null;
        Persist();
        try
        {
            var result = await uploader.TranscribeAsync(m, Persist);
            // The server acknowledged the complete call (HTTP 202): the notes are delivered and
            // transcription is queued. This is the phone's terminal condition.
            m.Completed = true;
            m.TranscriptionState = "Transcribed";
            m.VaultDocId = result.VaultDocId;
            m.Transcript = result.Transcript;
            m.TranscriptError = null;
            ClearProgress();
            Persist();
        }
        catch (IncompleteUploadException incomplete)
        {
            // The server's audio completeness gate (issue #586) refused the complete call: some
            // segments the phone believed uploaded are missing or hash-mismatched on the server. Re-arm
            // exactly the named segments the phone still holds (clear their Uploaded flag) and put the
            // recording back into the audio-upload phase so the next pass re-sends ONLY those bytes and
            // then re-completes (issue #591). Without this the recording would retry complete forever
            // against a gate it can never pass. Zero audio loss: only segments the phone has are
            // re-sent; the notes ride on the eventual successful complete, never stranded.
            var resend = RecordingUploadGate.RequeueIndicesForResend(
                incomplete.MissingOrBadIndices, m.Chunks.Select(c => c.Index));
            foreach (var chunk in m.Chunks.Where(c => resend.Contains(c.Index)))
                chunk.Uploaded = false;
            m.State = "Retry";                 // re-enter the audio-upload phase on the next pass
            m.TranscriptionState = "Failed";
            m.TranscriptError = incomplete.Message;
            ClearProgress();
            Persist();
        }
        catch (Exception ex)
        {
            // The complete/notes call did not land. Leave Completed=false so the next pass
            // retries it - the notes are NOT lost, just not delivered yet. The audio stays
            // safely "Uploaded".
            m.TranscriptionState = "Failed";
            m.TranscriptError = ex.Message;
            ClearProgress();
            Persist();
        }
    }

    // ===== segment rotation =================================================

    private void RollSegment()
    {
        lock (_gate)
        {
            if (!IsRecording) return;
            FinalizeSegment();
            StartSegment();
            SaveManifest();
        }
        RaiseChanged();
    }

    private void StartSegment()
    {
        var file = $"{_segmentIndex:D4}.m4a";
        var path = Path.Combine(_recordingDir, file);

        var rec = OperatingSystem.IsAndroidVersionAtLeast(31)
            ? new MediaRecorder(global::Android.App.Application.Context)
            : CreateLegacyRecorder();

        rec.SetAudioSource(AudioSource.Mic);
        rec.SetOutputFormat(OutputFormat.Mpeg4);
        rec.SetAudioEncoder(AudioEncoder.Aac);
        rec.SetAudioSamplingRate(16000);
        rec.SetAudioChannels(1);
        rec.SetAudioEncodingBitRate(64000);
        rec.SetOutputFile(path);
        rec.Prepare();
        rec.Start();

        _recorder = rec;
        _segmentStartedUtc = DateTime.UtcNow;
    }

#pragma warning disable CA1422 // legacy ctor only on pre-31 devices
    private static MediaRecorder CreateLegacyRecorder() => new();
#pragma warning restore CA1422

    private void FinalizeSegment()
    {
        if (_recorder is null || _manifest is null) return;
        var file = $"{_segmentIndex:D4}.m4a";
        var path = Path.Combine(_recordingDir, file);
        try
        {
            _recorder.Stop();
        }
        catch
        {
            // A segment stopped almost immediately can throw; the partial file
            // is still on disk. We keep whatever was captured.
        }
        finally
        {
            _recorder.Reset();
            _recorder.Release();
            _recorder = null;
        }

        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            _manifest.Chunks.Add(new ChunkInfo
            {
                Index = _segmentIndex,
                File = file,
                StartMs = (long)(_segmentStartedUtc - _startedUtc).TotalMilliseconds,
                DurationMs = (long)(DateTime.UtcNow - _segmentStartedUtc).TotalMilliseconds,
                Bytes = bytes.Length,
                Sha256 = Sha256Hex(bytes),
            });
            _segmentIndex++;
        }
    }

    // ===== persistence + library ============================================

    private void SaveManifest()
    {
        if (_manifest is null) return;
        var path = Path.Combine(_recordingDir, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(_manifest,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public IReadOnlyList<RecordingSummary> ListRecordings()
    {
        var root = RootDir;
        if (!Directory.Exists(root)) return Array.Empty<RecordingSummary>();
        var list = new List<RecordingSummary>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var mf = Path.Combine(dir, "manifest.json");
            if (!File.Exists(mf)) continue;
            try
            {
                var m = JsonSerializer.Deserialize<LocalManifest>(File.ReadAllText(mf));
                if (m is null) continue;
                var state = m.EndedAt is null ? "Recording" : NormalizeState(m.State);
                list.Add(new RecordingSummary(
                    m.RecordingId, m.Title, m.StartedAt, m.Chunks.Count,
                    m.Chunks.Sum(c => c.DurationMs), state, m.VaultDocId, m.Transcript,
                    m.UploadError, m.UploadProgress, m.UploadPhase, m.UploadCurrent, m.UploadTotal,
                    m.TranscriptionState, m.TranscriptError, m.Completed));
            }
            catch { /* skip unreadable manifest */ }
        }
        return list.OrderByDescending(r => r.StartedAt).ToList();
    }

    // Map any legacy state strings onto the queue vocabulary so old recordings
    // display consistently: Queued -> Uploading -> Uploaded (or Retry).
    private static string NormalizeState(string s) => s switch
    {
        "Local" => "Queued",
        "Filed" => "Uploaded",
        "Error" => "Retry",
        "" => "Queued",
        _ => s,
    };

    // ===== playback =========================================================

    private MediaPlayer? _player;
    private List<string> _playQueue = new();
    private int _playIndex;

    public string? PlayingRecordingId { get; private set; }

    public void Play(string recordingId)
    {
        StopPlayback();
        var m = LoadManifest(recordingId);
        if (m is null) return;
        var dir = RecordingFolder(recordingId);
        _playQueue = m.Chunks.OrderBy(c => c.Index)
            .Select(c => Path.Combine(dir, c.File))
            .Where(File.Exists)
            .ToList();
        if (_playQueue.Count == 0) return;

        PlayingRecordingId = recordingId;
        _playIndex = 0;
        StartCurrentSegment();
        RaiseChanged();
    }

    private void StartCurrentSegment()
    {
        if (_playIndex >= _playQueue.Count) { StopPlayback(); return; }
        var p = new MediaPlayer();
        p.Completion += (_, _) =>
        {
            _playIndex++;
            try { _player?.Reset(); _player?.Release(); } catch { }
            _player = null;
            StartCurrentSegment(); // chain to the next segment
        };
        p.SetDataSource(_playQueue[_playIndex]);
        p.Prepare();
        p.Start();
        _player = p;
    }

    public void StopPlayback()
    {
        if (_player is not null)
        {
            try { _player.Stop(); } catch { }
            try { _player.Reset(); _player.Release(); } catch { }
            _player = null;
        }
        if (PlayingRecordingId is not null)
        {
            PlayingRecordingId = null;
            RaiseChanged();
        }
    }

    public LocalManifest? LoadManifest(string recordingId)
    {
        var path = Path.Combine(RecordingFolder(recordingId), "manifest.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<LocalManifest>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions _manifestJson = new() { WriteIndented = true };

    /// <summary>Persist a recording's manifest to disk (the upload pass's single writer).</summary>
    private void WriteManifest(string recordingId, LocalManifest m)
    {
        var path = Path.Combine(RecordingFolder(recordingId), "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(m, _manifestJson));
    }

    // ===== helpers ==========================================================

    private static void StartForegroundService()
    {
        var ctx = global::Android.App.Application.Context;
        var intent = new Intent(ctx, typeof(RecorderForegroundService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            ctx.StartForegroundService(intent);
        else
            ctx.StartService(intent);
    }

    private static void StopForegroundService()
    {
        var ctx = global::Android.App.Application.Context;
        ctx.StopService(new Intent(ctx, typeof(RecorderForegroundService)));
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static string DefaultTitle()
        => "Recording " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
