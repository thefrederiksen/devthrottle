using Android.Media;
using CcDirectorClient.Voice;
using AndroidApp = Android.App.Application;

namespace CcDirectorClient.Platforms.Android;

/// <summary>
/// Captures one push-to-talk utterance as a single AAC/.m4a clip in the app cache
/// and returns its bytes for an immediate voice round-trip. Separate from the
/// offline recorder, which rolls segments to disk for deferred upload; here the
/// clip is short and consumed right away.
/// </summary>
public sealed class AndroidUtteranceRecorder : IUtteranceRecorder
{
    private const string Mime = "audio/mp4";

    private readonly object _gate = new();
    private MediaRecorder? _recorder;
    private string _path = "";

    public bool IsRecording { get; private set; }

    public double ReadLevel()
    {
        lock (_gate)
        {
            if (!IsRecording || _recorder is null) return 0;
            try
            {
                // MaxAmplitude is 0..32767, peak since last read. Square-root
                // shaping makes the meter feel linear to the ear (mirrors the
                // offline recorder's level meter).
                var amp = _recorder.MaxAmplitude;
                if (amp <= 0) return 0;
                return Math.Clamp(Math.Sqrt(amp / 32767.0), 0, 1);
            }
            catch
            {
                return 0;
            }
        }
    }

    public Task StartAsync()
    {
        lock (_gate)
        {
            if (IsRecording) throw new InvalidOperationException("already recording an utterance");

            var dir = AndroidApp.Context.CacheDir?.AbsolutePath
                      ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, $"utterance-{Guid.NewGuid():N}.m4a");

            var rec = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? new MediaRecorder(AndroidApp.Context)
                : CreateLegacyRecorder();

            rec.SetAudioSource(AudioSource.Mic);
            rec.SetOutputFormat(OutputFormat.Mpeg4);
            rec.SetAudioEncoder(AudioEncoder.Aac);
            rec.SetAudioSamplingRate(16000);
            rec.SetAudioChannels(1);
            rec.SetAudioEncodingBitRate(64000);
            rec.SetOutputFile(_path);
            rec.Prepare();
            rec.Start();

            _recorder = rec;
            IsRecording = true;
            ClientLog.Write($"[AndroidUtteranceRecorder] StartAsync: path={_path}");
        }
        return Task.CompletedTask;
    }

    public async Task<UtteranceAudio> StopAsync()
    {
        string path;
        lock (_gate)
        {
            if (!IsRecording || _recorder is null)
                throw new InvalidOperationException("not recording an utterance");

            try { _recorder.Stop(); }
            finally
            {
                _recorder.Reset();
                _recorder.Release();
                _recorder = null;
                IsRecording = false;
            }
            path = _path;
        }

        if (!File.Exists(path))
            throw new InvalidOperationException("utterance capture produced no audio file");

        var bytes = await File.ReadAllBytesAsync(path);
        try { File.Delete(path); } catch { /* cache file; OS reclaims it */ }

        if (bytes.Length == 0)
            throw new InvalidOperationException("utterance capture produced an empty clip");

        ClientLog.Write($"[AndroidUtteranceRecorder] StopAsync: bytes={bytes.Length}");
        return new UtteranceAudio(bytes, Mime);
    }

#pragma warning disable CA1422 // legacy ctor only on pre-31 devices
    private static MediaRecorder CreateLegacyRecorder() => new();
#pragma warning restore CA1422
}
