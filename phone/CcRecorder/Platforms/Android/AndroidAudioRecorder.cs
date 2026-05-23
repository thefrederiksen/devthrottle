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

    public bool IsRecording { get; private set; }
    public LocalManifest? Current => _manifest;
    public TimeSpan Elapsed => IsRecording ? DateTime.UtcNow - _startedUtc : TimeSpan.Zero;
    public event EventHandler? Changed;

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
            IsRecording = true;

            StartForegroundService();
            StartSegment();
            SaveManifest();

            _rollTimer = new System.Threading.Timer(_ => RollSegment(), null, SegmentLength, SegmentLength);
        }
        RaiseChanged();
        return Task.CompletedTask;
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

    public async Task ProcessUploadQueueAsync()
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet) return;
        var server = Preferences.Get("gateway_url", "").Trim();
        var token = Preferences.Get("gateway_token", "").Trim();
        if (string.IsNullOrWhiteSpace(server)) return;

        if (!await _uploadGate.WaitAsync(0)) return; // a run is already in progress
        try
        {
            foreach (var summary in ListRecordings())
            {
                if (summary.State is not ("Queued" or "Retry" or "Uploading")) continue;
                if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet) break;
                await UploadOneAsync(summary.RecordingId, server, token);
            }
        }
        finally { _uploadGate.Release(); }
    }

    private async Task UploadOneAsync(string recordingId, string server, string token)
    {
        var m = LoadManifest(recordingId);
        if (m is null || m.Chunks.Count == 0) return;
        if (NormalizeState(m.State) == "Uploaded") return;

        ApplyUploadResult(recordingId, "Uploading", m.VaultDocId, m.Transcript, null);
        try
        {
            var uploader = new IngestUploader(server, token);
            var result = await uploader.UploadAsync(m, RecordingFolder(recordingId));
            ApplyUploadResult(recordingId, "Uploaded", result.VaultDocId, result.Transcript, null);
        }
        catch (Exception ex)
        {
            // Stays on the phone and in the queue; WorkManager / next open retries.
            ApplyUploadResult(recordingId, "Retry", null, null, ex.Message);
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
                    m.Chunks.Sum(c => c.DurationMs), state, m.VaultDocId, m.Transcript));
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

    public LocalManifest? LoadManifest(string recordingId)
    {
        var path = Path.Combine(RecordingFolder(recordingId), "manifest.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<LocalManifest>(File.ReadAllText(path)); }
        catch { return null; }
    }

    public void ApplyUploadResult(string recordingId, string state, string? vaultDocId, string? transcript, string? error)
    {
        var path = Path.Combine(RecordingFolder(recordingId), "manifest.json");
        if (!File.Exists(path)) return;
        var m = LoadManifest(recordingId);
        if (m is null) return;
        m.State = state;
        m.VaultDocId = vaultDocId;
        m.Transcript = transcript;
        m.UploadError = error;
        File.WriteAllText(path, JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        RaiseChanged();
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
