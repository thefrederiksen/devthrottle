using CcDirector.Core.Dictation;
using CcDirector.Core.Utilities;
using NAudio.Dsp;
using NAudio.Wave;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Optional UI-meter extension to <see cref="IAudioSource"/>: a source that also
/// emits the per-band spectrum and input RMS the Speak dialog's equalizer and
/// "speak up" hint render. Kept off the Core <see cref="IAudioSource"/> contract
/// because those are desktop-UI cosmetics, not part of the capture/no-loss
/// guarantee. <see cref="BatchDictationRecorder"/> subscribes to these when the
/// source provides them and works fine without them (e.g. a headless test source).
/// </summary>
public interface IAudioMeterSource : IAudioSource
{
    /// <summary>Fires per chunk with a per-band (0..1) spectrum for a UI equalizer.</summary>
    event Action<double[]>? OnAudioBands;

    /// <summary>Fires per chunk with the raw int16 RMS amplitude (0..32767) for a level hint.</summary>
    event Action<double>? OnInputRms;
}

/// <summary>
/// Mic capture wrapper around NAudio's WaveInEvent, configured for the
/// format the OpenAI Realtime transcription API expects: 24 kHz, 16-bit,
/// mono PCM. Fires <see cref="OnAudioChunk"/> for every buffer the
/// driver delivers, on NAudio's background thread.
///
/// Also fires <see cref="OnAudioBands"/> with a per-band (0..1) spectrum so
/// the UI can drive an independent-bar equalizer without doing its own DSP.
/// Each band is one slice of the speech spectrum (low to high), so the bars
/// move independently the way a real frequency analyzer does, rather than all
/// rising and falling together off a single energy number.
/// </summary>
public sealed class MicAudioCapture : IAudioMeterSource, IDisposable
{
    public const int SampleRate = 24_000;
    public const int BitsPerSample = 16;
    public const int Channels = 1;

    /// <summary>Number of equalizer bars / spectrum bands emitted per chunk.</summary>
    public const int BandCount = 9;

    // FFT window. 1024 samples @ 24 kHz = ~43 ms, comfortably inside the 50 ms
    // capture buffer. Bin width = 24000 / 1024 = 23.4375 Hz.
    private const int FftSize = 1024;
    private const int FftM = 10; // log2(1024)
    private const double BinWidthHz = (double)SampleRate / FftSize;

    // Per-band magnitude shaping. Calibrated to a HEALTHY speaking level, not a
    // quiet one: the gate trims idle hiss, and the ceiling sits at loud, well-
    // projected speech. Deliberately quiet input therefore reads low (the bars
    // stay short) so the meter honestly shows "you are too quiet" rather than
    // being scaled up to flatter a faint signal. The sqrt curve below lifts
    // normal speech most of the way up. Band magnitudes are linear in input
    // amplitude, so these map roughly: healthy normal speech ~60% bar height,
    // loud ~95%, room-quiet speech well under a third.
    private const double BandNoiseFloor = 0.0003;
    private const double BandCeiling = 0.018;

    // Speech-band edges (Hz) for the bars, log-spaced low->high. Speech energy
    // clusters in the low end, so log spacing spreads visible motion across all
    // bars instead of pinning it to the leftmost one or two.
    private static readonly double[] BandEdgesHz = BuildLogBandEdges(80.0, 5000.0, BandCount);
    private static readonly double[] HannWindow = BuildHannWindow(FftSize);

    private static readonly WaveFormat CaptureFormat = new(SampleRate, BitsPerSample, Channels);

    private readonly WaveInEvent _waveIn;
    private readonly int _deviceNumber;
    private readonly string _description;
    private bool _started;
    private bool _disposed;

    // Completed by OnRecordingStopped so StopAsync can wait for NAudio to deliver
    // its final buffered audio before the caller snapshots the buffer. Null except
    // during an in-flight StopAsync.
    private TaskCompletionSource<bool>? _stoppedSignal;

    /// <summary>
    /// Name of the capture device this instance reads from. Resolved once at
    /// construction (a UI-thread boundary) and cached, so reads from the Core
    /// dictation pipeline are a simple field access that cannot throw.
    /// See <see cref="IAudioSource.Description"/>.
    /// </summary>
    public string Description => _description;

    /// <summary>Fires for every chunk of audio captured. PCM16 little-endian.</summary>
    public event Action<byte[]>? OnAudioChunk;

    /// <summary>Fires for every chunk with a per-band (0..1) spectrum of length <see cref="BandCount"/>. For UI equalizers.</summary>
    public event Action<double[]>? OnAudioBands;

    /// <summary>Fires for every chunk with the raw int16 RMS amplitude (0..32767). Drives the low-level "speak up" hint.</summary>
    public event Action<double>? OnInputRms;

    /// <param name="deviceNumber">
    /// WaveIn device number to capture from. Defaults to
    /// <see cref="MicDevices.DefaultDeviceNumber"/> (WAVE_MAPPER), which opens the
    /// Windows default capture device rather than the fixed device 0 that
    /// NAudio's <c>WaveInEvent</c> would otherwise use.
    /// </param>
    public MicAudioCapture(int deviceNumber = MicDevices.DefaultDeviceNumber, int bufferMilliseconds = 50)
    {
        _deviceNumber = deviceNumber;
        _description = MicDevices.DescribeDevice(deviceNumber);
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
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
            FileLog.Write($"[MicAudioCapture] Start: device={_deviceNumber} ({Description}), {SampleRate}Hz {BitsPerSample}-bit mono");
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

    /// <summary>
    /// Stop capture and return only AFTER NAudio has delivered its final buffered
    /// audio - the trailing words the user just spoke. WaveInEvent keeps capturing
    /// for up to one buffer (<see cref="MicAudioCapture(int,int)"/>'s
    /// bufferMilliseconds) after StopRecording, raises the last
    /// <see cref="OnAudioChunk"/> events on its worker thread, and only then fires
    /// RecordingStopped. Awaiting that event is what guarantees the whole tail is in
    /// the buffer before the caller snapshots it; a fire-and-forget <see cref="Stop"/>
    /// followed by an immediate snapshot would clip the end of the speech.
    ///
    /// The <paramref name="drainTimeout"/> is a backstop so a wedged driver that never
    /// raises RecordingStopped cannot hang the transcription path - on timeout we
    /// proceed with whatever audio was delivered.
    /// </summary>
    public async Task StopAsync(TimeSpan drainTimeout)
    {
        if (!_started) return;

        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stoppedSignal = signal;
        try
        {
            _waveIn.StopRecording();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MicAudioCapture] StopAsync error: {ex.Message}");
            signal.TrySetResult(false);
        }
        _started = false;

        var finished = await Task.WhenAny(signal.Task, Task.Delay(drainTimeout)).ConfigureAwait(false);
        if (finished != signal.Task)
            FileLog.Write($"[MicAudioCapture] StopAsync: RecordingStopped not seen within {drainTimeout.TotalMilliseconds:F0}ms; proceeding with captured audio");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

        try { OnAudioChunk?.Invoke(chunk); }
        catch (Exception ex) { FileLog.Write($"[MicAudioCapture] OnAudioChunk handler threw: {ex.Message}"); }

        var bands = ComputeBands(chunk);
        try { OnAudioBands?.Invoke(bands); }
        catch (Exception ex) { FileLog.Write($"[MicAudioCapture] OnAudioBands handler threw: {ex.Message}"); }

        double rawRms = ComputeInt16Rms(chunk);
        try { OnInputRms?.Invoke(rawRms); }
        catch (Exception ex) { FileLog.Write($"[MicAudioCapture] OnInputRms handler threw: {ex.Message}"); }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            FileLog.Write($"[MicAudioCapture] Recording stopped with error: {e.Exception.Message}");
        // RecordThread raises this in its finally AFTER the last DataAvailable has
        // been delivered, so by here every captured chunk is already appended.
        _stoppedSignal?.TrySetResult(true);
    }

    /// <summary>
    /// Per-band spectrum of a PCM16 chunk, each band shaped to 0..1 for a UI
    /// equalizer. Windows the most recent <see cref="FftSize"/> samples, runs
    /// an FFT, averages magnitude within each log-spaced speech band, then
    /// applies a noise gate + sqrt perceptual curve.
    /// </summary>
    private double[] ComputeBands(byte[] pcm)
    {
        var bands = new double[BandCount];
        if (pcm.Length < 2) return bands;

        int sampleCount = pcm.Length / 2;
        var buf = new Complex[FftSize];

        // Right-align the most recent samples into the FFT buffer; zero-pad the
        // front if the chunk is shorter than the window (rare tail chunks).
        int take = Math.Min(sampleCount, FftSize);
        int srcStart = sampleCount - take;
        int destStart = FftSize - take;
        for (int i = 0; i < FftSize; i++)
        {
            double s = 0.0;
            if (i >= destStart)
            {
                int sampleIdx = srcStart + (i - destStart);
                int b = sampleIdx * 2;
                short v = (short)(pcm[b] | (pcm[b + 1] << 8));
                s = (v / 32768.0) * HannWindow[i];
            }
            buf[i].X = (float)s;
            buf[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftM, buf);

        int usableBins = FftSize / 2;
        for (int band = 0; band < BandCount; band++)
        {
            int loBin = Math.Max(1, (int)(BandEdgesHz[band] / BinWidthHz));
            int hiBin = Math.Min(usableBins - 1, (int)(BandEdgesHz[band + 1] / BinWidthHz));
            if (hiBin < loBin) hiBin = loBin;

            double sum = 0.0;
            int n = 0;
            for (int bin = loBin; bin <= hiBin; bin++)
            {
                double re = buf[bin].X;
                double im = buf[bin].Y;
                sum += Math.Sqrt(re * re + im * im);
                n++;
            }
            double mag = n > 0 ? sum / n : 0.0;
            bands[band] = ShapeBand(mag);
        }

        return bands;
    }

    /// <summary>Root-mean-square of a PCM16 chunk on the raw int16 scale (0..32767).</summary>
    private static double ComputeInt16Rms(byte[] pcm)
    {
        if (pcm.Length < 2) return 0.0;
        long sumSq = 0;
        int samples = pcm.Length / 2;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            sumSq += (long)s * s;
        }
        return Math.Sqrt(sumSq / (double)samples);
    }

    private static double ShapeBand(double mag)
    {
        if (mag <= BandNoiseFloor) return 0.0;
        double norm = (mag - BandNoiseFloor) / (BandCeiling - BandNoiseFloor);
        norm = Math.Clamp(norm, 0.0, 1.0);
        return Math.Sqrt(norm);
    }

    private static double[] BuildHannWindow(int size)
    {
        var w = new double[size];
        for (int i = 0; i < size; i++)
            w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (size - 1)));
        return w;
    }

    private static double[] BuildLogBandEdges(double lowHz, double highHz, int bands)
    {
        var edges = new double[bands + 1];
        double logLo = Math.Log(lowHz);
        double logHi = Math.Log(highHz);
        for (int i = 0; i <= bands; i++)
        {
            double t = (double)i / bands;
            edges[i] = Math.Exp(logLo + (logHi - logLo) * t);
        }
        return edges;
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
