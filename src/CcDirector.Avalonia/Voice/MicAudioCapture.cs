using CcDirector.Core.Utilities;
using NAudio.Wave;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Mic capture wrapper around NAudio's WaveInEvent, configured for the
/// format the OpenAI Realtime transcription API expects: 24 kHz, 16-bit,
/// mono PCM. Fires <see cref="OnAudioChunk"/> for every buffer the
/// driver delivers, on NAudio's background thread.
///
/// Also fires <see cref="OnAudioLevel"/> with a normalized 0.0-1.0 RMS
/// energy value per chunk so the UI can drive a level meter without
/// having to do its own DSP.
/// </summary>
public sealed class MicAudioCapture : IDisposable
{
    public const int SampleRate = 24_000;
    public const int BitsPerSample = 16;
    public const int Channels = 1;

    private static readonly WaveFormat CaptureFormat = new(SampleRate, BitsPerSample, Channels);

    private readonly WaveInEvent _waveIn;
    private bool _started;
    private bool _disposed;

    /// <summary>Fires for every chunk of audio captured. PCM16 little-endian.</summary>
    public event Action<byte[]>? OnAudioChunk;

    /// <summary>Fires for every chunk with a normalized RMS energy (0..1). For UI level meters.</summary>
    public event Action<double>? OnAudioLevel;

    public MicAudioCapture(int bufferMilliseconds = 50)
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat = CaptureFormat,
            BufferMilliseconds = bufferMilliseconds,
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MicAudioCapture));
        if (_started) return;
        try
        {
            _waveIn.StartRecording();
            _started = true;
            FileLog.Write($"[MicAudioCapture] Start: {SampleRate}Hz {BitsPerSample}-bit mono");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MicAudioCapture] Start FAILED: {ex.Message}");
            throw;
        }
    }

    public void Stop()
    {
        if (!_started) return;
        try
        {
            _waveIn.StopRecording();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MicAudioCapture] Stop error: {ex.Message}");
        }
        _started = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

        try { OnAudioChunk?.Invoke(chunk); }
        catch (Exception ex) { FileLog.Write($"[MicAudioCapture] OnAudioChunk handler threw: {ex.Message}"); }

        var level = ComputeRms(chunk);
        try { OnAudioLevel?.Invoke(level); }
        catch (Exception ex) { FileLog.Write($"[MicAudioCapture] OnAudioLevel handler threw: {ex.Message}"); }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            FileLog.Write($"[MicAudioCapture] Recording stopped with error: {e.Exception.Message}");
    }

    /// <summary>
    /// Root-mean-square energy of a PCM16 chunk, normalized to 0..1.
    /// Cheap and good enough for a visual level meter.
    /// </summary>
    private static double ComputeRms(byte[] pcm)
    {
        if (pcm.Length < 2) return 0.0;
        long sumSq = 0;
        int samples = pcm.Length / 2;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            sumSq += s * s;
        }
        var meanSq = sumSq / (double)samples;
        var rms = Math.Sqrt(meanSq);
        // Normalize against int16 max. Speech rarely hits anywhere near full
        // scale; multiply to bring typical speech into the 0..1 range.
        return Math.Min(1.0, rms / 8000.0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { }
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.Dispose();
    }
}
