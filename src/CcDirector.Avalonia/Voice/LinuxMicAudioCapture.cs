using System.Diagnostics;
using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// One running recorder, as <see cref="LinuxMicAudioCapture"/> sees it: a stream of raw PCM that ends when the
/// recorder does. Production is a process; tests supply a stream they feed by hand.
/// </summary>
internal interface ILinuxRecorder : IDisposable
{
    /// <summary>What is recording, for the log.</summary>
    string Name { get; }

    /// <summary>The recorder's PCM. Reading returns 0 once the recorder has finished and everything is read.</summary>
    Stream Output { get; }

    /// <summary>The last thing the recorder said on its error stream, or null.</summary>
    string? LastErrorLine { get; }

    /// <summary>Ask the recorder to finish: it flushes the audio it is holding and ends <see cref="Output"/>.</summary>
    void Interrupt();

    /// <summary>End the recorder now, discarding what it is holding.</summary>
    void Kill();
}

/// <summary>The production recorder: <c>pw-record</c> or <c>arecord</c> writing headerless PCM to its standard output.</summary>
internal sealed class LinuxRecorderProcess : ILinuxRecorder
{
    private const int Sigint = 2;

    private readonly Process _process;
    private volatile string? _lastErrorLine;

    public LinuxRecorderProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        Name = $"{fileName} {string.Join(' ', arguments)}";
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"{fileName} could not be started.");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) _lastErrorLine = e.Data;
        };
        _process.BeginErrorReadLine();
    }

    public string Name { get; }

    public Stream Output => _process.StandardOutput.BaseStream;

    public string? LastErrorLine => _lastErrorLine;

    public void Interrupt()
    {
        if (_process.HasExited) return;
        if (kill(_process.Id, Sigint) != 0)
            throw new InvalidOperationException($"interrupting the recorder failed, errno={Marshal.GetLastPInvokeError()}");
    }

    public void Kill()
    {
        if (!_process.HasExited) _process.Kill();
    }

    public void Dispose() => _process.Dispose();

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}

/// <summary>
/// Microphone capture on Linux, in the same capture format as <see cref="MicAudioCapture"/>: 24 kHz, 16-bit,
/// mono PCM. It starts the sound system's own recorder (<c>pw-record</c>, or <c>arecord</c> where PipeWire has
/// no audio) and reads headerless PCM from its standard output on a dedicated thread, firing
/// <see cref="OnAudioChunk"/> for every buffer and the same equalizer bands and input level the Windows
/// capture emits.
///
/// The no-loss stop (<see cref="StopAsync"/>) interrupts the recorder, which flushes what it has captured and
/// exits, and returns only once the reading thread has reached the end of the pipe - so the tail of speech is
/// in the caller's buffer before it is snapshotted.
/// </summary>
public sealed class LinuxMicAudioCapture : IAudioMeterSource, IAudioCaptureDiagnostics, IDisposable
{
    // The kernel holds this much of the recorder's output while the reading thread is busy.
    private const int PipeCapacityBytes = 65_536;

    // Starts the recorder. Called at Start, never at construction: a capture is built on the interface
    // thread, where nothing may be looked up and nothing may fail (issue #2929).
    private readonly Func<ILinuxRecorder> _startRecorder;
    private readonly string _description;
    private readonly int _bufferMilliseconds;
    private readonly int _chunkBytes;
    private readonly int _numberOfBuffers;
    private readonly double _dropRiskGapMs;

    private ILinuxRecorder? _recorder;
    private Thread? _reader;
    private bool _started;
    private bool _disposed;

    // Set by Stop, which discards: nothing still in the pipe is raised after it.
    private volatile bool _discard;

    // Completed by the reading thread once the recorder's output has ended and every chunk has been delivered.
    private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Capture-health counters, written on the reading thread only; see MicAudioCapture.
    private readonly Stopwatch _captureClock = new();
    private long _capturedBytes;
    private int _callbackCount;
    private double _lastCallbackStartMs = -1.0;
    private double _maxCallbackGapMs;
    private int _longGapCount;
    private double _maxHandlerMs;
    private double _totalHandlerMs;

    public string Description => _description;

    public event Action<byte[]>? OnAudioChunk;

    public event Action<double[]>? OnAudioBands;

    public event Action<double>? OnInputRms;

    /// <summary>
    /// The capture for a device number from <see cref="LinuxMicDevices"/>. Building it queries nothing and
    /// cannot fail; the sound system and the node to open are taken, when capture starts, from the look that
    /// produced the number - so a microphone unplugged since then fails the start, where the caller reports it.
    /// </summary>
    public static LinuxMicAudioCapture ForDevice(int deviceNumber, string description)
        => new(() =>
        {
            var (backend, nodeName) = LinuxMicDevices.CaptureTargetFor(deviceNumber);
            return backend == LinuxAudioBackend.PipeWire
                ? new LinuxRecorderProcess(LinuxAudioTools.PipeWireRecord, PipeWireArguments(nodeName, LinuxAudioTools.PipeWireNeedsRawFlag))
                : new LinuxRecorderProcess(LinuxAudioTools.AlsaRecord, AlsaArguments());
        }, description);

    /// <param name="startRecorder">Starts the recorder, which must produce 24 kHz 16-bit mono PCM with no header.</param>
    /// <param name="description">The device's display name, resolved by the caller off the interface thread.</param>
    internal LinuxMicAudioCapture(Func<ILinuxRecorder> startRecorder, string description, int bufferMilliseconds = 50)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A resolved device description is required.", nameof(description));
        _startRecorder = startRecorder ?? throw new ArgumentNullException(nameof(startRecorder));
        _description = description;
        _bufferMilliseconds = bufferMilliseconds;
        int bytesPerMs = MicAudioCapture.SampleRate * MicAudioCapture.Channels * (MicAudioCapture.BitsPerSample / 8) / 1000;
        _chunkBytes = bytesPerMs * bufferMilliseconds;
        _numberOfBuffers = PipeCapacityBytes / _chunkBytes;
        _dropRiskGapMs = (double)_numberOfBuffers * _bufferMilliseconds;
    }

    internal static IReadOnlyList<string> PipeWireArguments(string? nodeName, bool rawFlag)
    {
        var arguments = new List<string>
        {
            "--rate", MicAudioCapture.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--channels", "1",
            "--format", "s16",
            "--latency", "50ms",
        };
        if (rawFlag) arguments.Add("--raw");
        if (nodeName is not null)
        {
            arguments.Add("--target");
            arguments.Add(nodeName);
        }
        arguments.Add("-");
        return arguments;
    }

    internal static IReadOnlyList<string> AlsaArguments()
        => ["-q", "-t", "raw", "-f", "S16_LE", "-r", MicAudioCapture.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c", "1", "-"];

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LinuxMicAudioCapture));
        if (_started) return;
        if (_recorder is not null) throw new InvalidOperationException("A Linux capture records once; build a new one to record again.");
        try
        {
            _captureClock.Restart();
            var recorder = _startRecorder();
            _recorder = recorder;

            _reader = new Thread(() => ReadLoop(recorder)) { IsBackground = true, Name = "LinuxMicAudioCapture" };
            _reader.Start();
            _started = true;
            FileLog.Write($"[LinuxMicAudioCapture] Start: {recorder.Name} ({Description}), "
                + $"{MicAudioCapture.SampleRate}Hz {MicAudioCapture.BitsPerSample}-bit mono, chunks={_bufferMilliseconds}ms");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinuxMicAudioCapture] Start FAILED: {ex.Message}");
            throw;
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _discard = true;
        KillRecorder("Stop");
    }

    public async Task StopAsync(TimeSpan drainTimeout)
    {
        if (!_started) return;
        _started = false;

        // Interrupt, not kill: the recorder flushes the audio it is holding and closes its output, and the
        // reading thread delivers that tail before it completes _ended.
        try
        {
            _recorder?.Interrupt();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinuxMicAudioCapture] StopAsync ERROR: {ex.Message}");
        }

        var finished = await Task.WhenAny(_ended.Task, Task.Delay(drainTimeout)).ConfigureAwait(false);
        if (finished != _ended.Task)
        {
            FileLog.Write($"[LinuxMicAudioCapture] StopAsync: the recorder did not finish within {drainTimeout.TotalMilliseconds:F0}ms; proceeding with captured audio");
            KillRecorder("StopAsync");
        }
    }

    private void ReadLoop(ILinuxRecorder recorder)
    {
        try
        {
            var stream = recorder.Output;
            var buffer = new byte[_chunkBytes];
            while (true)
            {
                int filled = 0;
                while (filled < buffer.Length)
                {
                    int read = stream.Read(buffer, filled, buffer.Length - filled);
                    if (read <= 0) break;
                    filled += read;
                }

                // Whole samples only; a lone trailing byte can only be the cut end of the stream.
                int usable = filled & ~1;
                if (usable > 0 && !_discard) Deliver(buffer, usable);
                if (filled < buffer.Length) break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinuxMicAudioCapture] reading the recorder's output FAILED: {ex.Message}");
        }
        finally
        {
            OnCaptureEnded();
        }
    }

    private void Deliver(byte[] buffer, int count)
    {
        double startMs = _captureClock.Elapsed.TotalMilliseconds;
        if (_lastCallbackStartMs >= 0.0)
        {
            double gap = startMs - _lastCallbackStartMs;
            if (gap > _maxCallbackGapMs) _maxCallbackGapMs = gap;
            if (gap > _dropRiskGapMs) _longGapCount++;
        }
        _lastCallbackStartMs = startMs;
        _callbackCount++;
        _capturedBytes += count;

        var chunk = new byte[count];
        Buffer.BlockCopy(buffer, 0, chunk, 0, count);

        try { OnAudioChunk?.Invoke(chunk); }
        catch (Exception ex) { FileLog.Write($"[LinuxMicAudioCapture] OnAudioChunk handler FAILED: {ex.Message}"); }

        var bands = MicAudioCapture.ComputeBands(chunk);
        try { OnAudioBands?.Invoke(bands); }
        catch (Exception ex) { FileLog.Write($"[LinuxMicAudioCapture] OnAudioBands handler FAILED: {ex.Message}"); }

        double rawRms = MicAudioCapture.ComputeInt16Rms(chunk);
        try { OnInputRms?.Invoke(rawRms); }
        catch (Exception ex) { FileLog.Write($"[LinuxMicAudioCapture] OnInputRms handler FAILED: {ex.Message}"); }

        double handlerMs = _captureClock.Elapsed.TotalMilliseconds - startMs;
        _totalHandlerMs += handlerMs;
        if (handlerMs > _maxHandlerMs) _maxHandlerMs = handlerMs;
    }

    private void OnCaptureEnded()
    {
        _captureClock.Stop();
        var h = GetCaptureHealth();
        FileLog.Write($"[LinuxMicAudioCapture] capture-health: capturedBytes={h.CapturedBytes}, expectedBytes={h.ExpectedBytes}, "
            + $"deficit={h.DeficitFraction:P1}, callbacks={h.CallbackCount}, maxGapMs={h.MaxCallbackGapMs:F0}, "
            + $"longGaps(>{_dropRiskGapMs:F0}ms)={h.LongGapCount}, maxHandlerMs={h.MaxHandlerMs:F1}, "
            + $"totalHandlerMs={h.TotalHandlerMs:F0}, elapsedMs={h.ElapsedMs:F0}, device=\"{Description}\"");
        if (h.CapturedBytes == 0)
            FileLog.Write($"[LinuxMicAudioCapture] the recorder delivered no audio; its last message: {_recorder?.LastErrorLine ?? "(none)"}");
        _ended.TrySetResult(true);
    }

    public CaptureHealth GetCaptureHealth()
    {
        double elapsedMs = _captureClock.Elapsed.TotalMilliseconds;
        return new CaptureHealth(
            CapturedBytes: _capturedBytes,
            ExpectedBytes: MicAudioCapture.ExpectedBytes(elapsedMs),
            CallbackCount: _callbackCount,
            MaxCallbackGapMs: _maxCallbackGapMs,
            LongGapCount: _longGapCount,
            MaxHandlerMs: _maxHandlerMs,
            TotalHandlerMs: _totalHandlerMs,
            ElapsedMs: elapsedMs,
            NumberOfBuffers: _numberOfBuffers,
            BufferMilliseconds: _bufferMilliseconds);
    }

    private void KillRecorder(string caller)
    {
        try
        {
            _recorder?.Kill();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinuxMicAudioCapture] {caller}: stopping the recorder FAILED: {ex.Message}");
        }
    }

    /// <summary>Release the recorder. Never throws; see <see cref="MicAudioCapture.Dispose"/> for why that is a contract.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _started = false;
        _discard = true;
        KillRecorder("Dispose");
        try
        {
            // Let the reading thread see the end of the pipe before the pipe is released under it.
            if (_reader is not null) _ended.Task.Wait(TimeSpan.FromMilliseconds(500));
            _recorder?.Dispose();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinuxMicAudioCapture] Dispose: releasing the recorder FAILED: {ex.Message}");
        }
    }
}
