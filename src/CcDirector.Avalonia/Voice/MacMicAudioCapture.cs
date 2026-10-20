using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CcDirector.Core.Dictation;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// macOS microphone capture, built on AudioToolbox's AudioQueue. Delivers the same
/// 24 kHz / 16-bit / mono PCM as the Windows and Linux captures, so everything
/// downstream - the recorder, the meters, the Gateway that transcribes the clip -
/// is unchanged.
///
/// WHY THIS EXISTS
/// ---------------
/// <see cref="MicAudioCapture"/> is NAudio's WaveInEvent, which P/Invokes winmm.dll,
/// and <see cref="MicDevices"/> reaches Windows Core Audio through NAudio. Neither
/// exists on macOS, so a Mac fell through to the Windows capture and could not record
/// a byte; <see cref="SpeakDialog"/> told the user dictation was not offered here.
/// Linux solved this by driving the sound system's own recorder as a subprocess. macOS
/// needs no subprocess: AudioQueue is the supported recording C API, in the OS, so the
/// capture stays in-process and has no tool to install.
///
/// PERMISSION
/// ----------
/// macOS gates the microphone behind TCC, and a refused microphone does NOT fail the
/// open: the queue starts, runs at the right rate, and every sample it delivers is
/// zero. Digital silence is therefore the denial signature on this platform, and
/// <see cref="IAudioSilenceReport"/> reports it so the recording is refused with a
/// failure that names the real cause rather than blaming the device. For the prompt to be offered at all the app bundle
/// must carry NSMicrophoneUsageDescription (scripts/package-mac-app.sh): without that
/// key macOS refuses access and never asks.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacMicAudioCapture : IAudioMeterSource, IAudioCaptureDiagnostics, IAudioSilenceReport, IDisposable
{
    private const string AudioToolbox =
        "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    private const uint FormatLinearPcm = 0x6C70636D; // 'lpcm'
    private const uint FlagIsSignedInteger = 1 << 2;
    private const uint FlagIsPacked = 1 << 3;

    // Buffers in flight. Three lets one fill while one drains and one waits, matching
    // the depth the Windows capture asks NAudio for.
    private const int NumberOfBuffers = 3;

    private readonly string _description;
    private readonly int _bufferMilliseconds;
    private readonly int _bufferBytes;
    private readonly double _dropRiskGapMs;
    private readonly AudioQueueInputCallback _callback; // field: must outlive the native queue
    private readonly object _gate = new();

    // Completed by the capture callback once the queue has stopped and the last buffer was delivered.
    private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Capture-health counters, written on the AudioQueue callback thread only; see MicAudioCapture.
    private readonly Stopwatch _captureClock = new();
    private long _capturedBytes;
    private int _callbackCount;
    private double _lastCallbackStartMs = -1.0;
    // Capture clock reading of where the FIRST buffer's audio BEGAN - its callback time less the
    // audio it carried - and negative until one arrives. The wait for it is device-open time, not
    // lost audio, so the expected byte count starts here.
    private double _firstAudioMs = -1.0;
    private double _maxCallbackGapMs;
    private int _longGapCount;
    private double _maxHandlerMs;
    private double _totalHandlerMs;
    private int _sawSound;

    private IntPtr _queue;

    // Both guarded by _gate. _started is read on the AudioQueue callback thread to decide whether to
    // re-arm a buffer, so it is written under the same lock - an unsynchronised write was observed
    // being missed by a callback, which re-armed a buffer into a stopping queue. _buffersInFlight
    // counts the buffers the queue still owes us back; the drain is over when it reaches zero.
    private bool _started;
    private int _buffersInFlight;

    private bool _disposed;

    // Set by Stop, which discards: nothing still in flight is raised after it.
    private volatile bool _discard;

    public string Description => _description;

    public event Action<byte[]>? OnAudioChunk;

    public event Action<double[]>? OnAudioBands;

    public event Action<double>? OnInputRms;

    /// <summary>
    /// True once any captured sample was non-zero. An every-bit-zero recording is what a microphone
    /// macOS has refused looks like, and what a muted device usually looks like; a live microphone in
    /// a quiet room normally still carries a faint noise floor, though aggressive noise suppression
    /// can in principle flatten one to zero. Either way such a clip carries no speech, so refusing it
    /// with a named cause beats sending silence to the Gateway and handing back an empty transcript.
    /// </summary>
    public bool SawSound => Interlocked.CompareExchange(ref _sawSound, 0, 0) != 0;

    /// <summary>
    /// What to tell the user when this capture heard nothing but silence. macOS refuses the
    /// microphone by substituting silence rather than by failing the open, so the grant in
    /// System Settings is the first thing to check, and a muted headset the second.
    /// </summary>
    public string SilentCaptureExplanation =>
        "Open System Settings > Privacy & Security > Microphone and switch Director on, check the "
        + "microphone's own mute button, then dictate again.";

    /// <summary>
    /// Build the capture for the system default input. The device name is resolved by the
    /// caller OFF the interface thread (issue #2929) and passed in; this constructor
    /// queries nothing and cannot fail.
    /// </summary>
    public static MacMicAudioCapture ForDevice(int deviceNumber, string description)
        => new(description);

    public MacMicAudioCapture(string description, int bufferMilliseconds = 50)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A resolved device description is required.", nameof(description));

        _description = description;
        _bufferMilliseconds = bufferMilliseconds;
        int bytesPerMs = MicAudioCapture.SampleRate * MicAudioCapture.Channels * (MicAudioCapture.BitsPerSample / 8) / 1000;
        _bufferBytes = bytesPerMs * bufferMilliseconds;
        _dropRiskGapMs = (double)NumberOfBuffers * bufferMilliseconds;
        _callback = OnBufferFilled;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MacMicAudioCapture));
        if (_started) return;

        var format = new AudioStreamBasicDescription
        {
            SampleRate = MicAudioCapture.SampleRate,
            FormatId = FormatLinearPcm,
            FormatFlags = FlagIsSignedInteger | FlagIsPacked,
            BytesPerPacket = (uint)(MicAudioCapture.BitsPerSample / 8 * MicAudioCapture.Channels),
            FramesPerPacket = 1,
            BytesPerFrame = (uint)(MicAudioCapture.BitsPerSample / 8 * MicAudioCapture.Channels),
            ChannelsPerFrame = MicAudioCapture.Channels,
            BitsPerChannel = MicAudioCapture.BitsPerSample,
            Reserved = 0,
        };

        try
        {
            // A null run loop delivers the callback on an AudioQueue-owned thread: capture
            // must not depend on the interface thread pumping a run loop.
            Check(AudioQueueNewInput(ref format, _callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out _queue),
                  "AudioQueueNewInput");

            for (int i = 0; i < NumberOfBuffers; i++)
            {
                Check(AudioQueueAllocateBuffer(_queue, (uint)_bufferBytes, out IntPtr buffer), "AudioQueueAllocateBuffer");
                Check(AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero), "AudioQueueEnqueueBuffer");
                lock (_gate) { _buffersInFlight++; }
            }

            lock (_gate) { _started = true; }
            _captureClock.Restart();
            Check(AudioQueueStart(_queue, IntPtr.Zero), "AudioQueueStart");
            FileLog.Write($"[MacMicAudioCapture] Start: {Description}, "
                + $"{MicAudioCapture.SampleRate}Hz {MicAudioCapture.BitsPerSample}-bit mono, "
                + $"{NumberOfBuffers}x{_bufferMilliseconds}ms buffers");
        }
        catch (Exception ex)
        {
            _started = false;
            FileLog.Write($"[MacMicAudioCapture] Start FAILED: {ex.Message}");
            throw;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
        }
        _discard = true;
        StopQueue("Stop", immediate: true);
        OnCaptureEnded();
    }

    public async Task StopAsync(TimeSpan drainTimeout)
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
        }

        // Not immediate: the queue plays out the buffers it is holding and the callback
        // delivers that tail before completing _ended. The audio already captured when
        // the user pressed stop is the end of the sentence they spoke, and _ended waits
        // for EVERY buffer to come back - not just the first one after the stop, which
        // left two more still in flight and their audio arriving after the caller had
        // already snapshotted the clip (measured against a real device: 4,288 bytes of
        // the tail landed 28 and 66 ms after this method returned).
        StopQueue("StopAsync", immediate: false);

        var finished = await Task.WhenAny(_ended.Task, Task.Delay(drainTimeout)).ConfigureAwait(false);
        if (finished != _ended.Task)
        {
            FileLog.Write($"[MacMicAudioCapture] StopAsync: the queue did not finish within "
                + $"{drainTimeout.TotalMilliseconds:F0}ms; proceeding with captured audio");
            _discard = true;
            StopQueue("StopAsync drain timeout", immediate: true);
        }

        OnCaptureEnded();
    }

    /// <summary>
    /// Called by AudioQueue on its own thread for every filled buffer. This is an unmanaged
    /// entry point: an exception escaping here would tear the process down, so everything
    /// is caught and logged.
    /// </summary>
    private void OnBufferFilled(IntPtr userData, IntPtr queue, IntPtr bufferRef, IntPtr startTime,
                                uint packetCount, IntPtr packetDescs)
    {
        bool accountedFor = false;
        try
        {
            // Delivery is caught SEPARATELY from the buffer bookkeeping below. When both sat under one
            // outer catch, anything thrown while delivering left the buffer unaccounted for, so
            // _buffersInFlight never reached zero, the drain could not finish normally, and StopAsync
            // waited out its whole timeout. A throwing SUBSCRIBER cannot do this - Deliver catches
            // those around each event - so the reachable throws are the marshalling here and the band
            // and level arithmetic inside Deliver, which is narrow but costs nothing to close.
            if (!_discard)
            {
                try
                {
                    var buffer = Marshal.PtrToStructure<AudioQueueBuffer>(bufferRef);
                    int recorded = (int)buffer.AudioDataByteSize;
                    if (recorded > 0)
                    {
                        var chunk = new byte[recorded];
                        Marshal.Copy(buffer.AudioData, chunk, 0, recorded);
                        Deliver(chunk);
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[MacMicAudioCapture] delivering a captured buffer FAILED: {ex.Message}");
                }
            }

            // Re-arm for the next fill. Skipped once stopping: enqueueing into a stopping queue
            // fails, and this callback is then the last one this buffer will ever make.
            bool stopping;
            lock (_gate) { stopping = !_started; }
            if (!stopping)
            {
                int status = AudioQueueEnqueueBuffer(queue, bufferRef, 0, IntPtr.Zero);
                if (status == 0) return;
                // The buffer is not back in the queue, so it is not coming back again either:
                // counting it as returned is what keeps the drain from waiting out its timeout.
                FileLog.Write($"[MacMicAudioCapture] re-enqueue FAILED: OSStatus {status}");
            }
            accountedFor = true;
            NoteBufferReturned();
        }
        catch (Exception ex)
        {
            // Last resort: an exception escaping an unmanaged callback would take the process down.
            FileLog.Write($"[MacMicAudioCapture] capture callback FAILED: {ex.Message}");
            if (!accountedFor) NoteBufferReturned();
        }
    }

    /// <summary>
    /// A buffer the queue will not hand us again. The drain is over once every one of them is back,
    /// because the buffers still in flight at the stop are carrying the END of what the user just
    /// said - returning from <see cref="StopAsync"/> before they land clips it off, which is the
    /// tail-clipping failure the other two captures already guard against (issue #2927).
    /// </summary>
    private void NoteBufferReturned()
    {
        lock (_gate)
        {
            if (_buffersInFlight > 0) _buffersInFlight--;
            if (_buffersInFlight == 0) _ended.TrySetResult(true);
        }
    }

    private void Deliver(byte[] chunk)
    {
        double startMs = _captureClock.Elapsed.TotalMilliseconds;
        if (_lastCallbackStartMs >= 0)
        {
            double gap = startMs - _lastCallbackStartMs;
            if (gap > _maxCallbackGapMs) _maxCallbackGapMs = gap;
            if (gap > _dropRiskGapMs) _longGapCount++;
        }
        if (_firstAudioMs < 0) _firstAudioMs = MicAudioCapture.FirstAudioStartMs(startMs, chunk.Length);
        _lastCallbackStartMs = startMs;
        _callbackCount++;
        _capturedBytes += chunk.Length;

        if (Interlocked.CompareExchange(ref _sawSound, 0, 0) == 0 && HasNonZeroSample(chunk))
            Interlocked.Exchange(ref _sawSound, 1);

        try { OnAudioChunk?.Invoke(chunk); }
        catch (Exception ex) { FileLog.Write($"[MacMicAudioCapture] OnAudioChunk handler FAILED: {ex.Message}"); }

        var bands = MicAudioCapture.ComputeBands(chunk);
        try { OnAudioBands?.Invoke(bands); }
        catch (Exception ex) { FileLog.Write($"[MacMicAudioCapture] OnAudioBands handler FAILED: {ex.Message}"); }

        double rawRms = MicAudioCapture.ComputeInt16Rms(chunk);
        try { OnInputRms?.Invoke(rawRms); }
        catch (Exception ex) { FileLog.Write($"[MacMicAudioCapture] OnInputRms handler FAILED: {ex.Message}"); }

        double handlerMs = _captureClock.Elapsed.TotalMilliseconds - startMs;
        _totalHandlerMs += handlerMs;
        if (handlerMs > _maxHandlerMs) _maxHandlerMs = handlerMs;
    }

    private void OnCaptureEnded()
    {
        if (!_captureClock.IsRunning) return;
        _captureClock.Stop();
        var h = GetCaptureHealth();
        FileLog.Write($"[MacMicAudioCapture] capture-health: capturedBytes={h.CapturedBytes}, expectedBytes={h.ExpectedBytes}, "
            + $"deficit={h.DeficitFraction:P1}, callbacks={h.CallbackCount}, maxGapMs={h.MaxCallbackGapMs:F0}, "
            + $"longGaps(>{_dropRiskGapMs:F0}ms)={h.LongGapCount}, maxHandlerMs={h.MaxHandlerMs:F1}, "
            + $"totalHandlerMs={h.TotalHandlerMs:F0}, elapsedMs={h.ElapsedMs:F0}, firstAudioMs={h.FirstAudioMs:F0}, deliveringMs={h.DeliveringMs:F0}, unaccountedAtStart={h.UnaccountedAtStartFraction:P1}, device=\"{Description}\"");

        if (h.CapturedBytes > 0 && !SawSound)
            FileLog.Write("[MacMicAudioCapture] every captured sample was zero - the microphone is muted, or "
                + "macOS has not granted this app microphone access (System Settings > Privacy & Security > Microphone)");
    }

    public CaptureHealth GetCaptureHealth()
    {
        double elapsedMs = _captureClock.Elapsed.TotalMilliseconds;
        return new CaptureHealth(
            CapturedBytes: _capturedBytes,
            ExpectedBytes: MicAudioCapture.ExpectedBytesWhileDelivering(elapsedMs, _firstAudioMs),
            CallbackCount: _callbackCount,
            MaxCallbackGapMs: _maxCallbackGapMs,
            LongGapCount: _longGapCount,
            MaxHandlerMs: _maxHandlerMs,
            TotalHandlerMs: _totalHandlerMs,
            ElapsedMs: elapsedMs,
            NumberOfBuffers: NumberOfBuffers,
            BufferMilliseconds: _bufferMilliseconds,
            FirstAudioMs: _firstAudioMs);
    }

    /// <summary>True if any byte of the chunk is non-zero. Byte-wise is enough: a non-zero sample has a non-zero byte.</summary>
    private static bool HasNonZeroSample(byte[] chunk)
    {
        foreach (var b in chunk)
        {
            if (b != 0) return true;
        }
        return false;
    }

    private void StopQueue(string caller, bool immediate)
    {
        lock (_gate)
        {
            if (_queue == IntPtr.Zero) return;
        }
        int status = AudioQueueStop(_queue, immediate);
        if (status != 0)
            FileLog.Write($"[MacMicAudioCapture] {caller}: stopping the queue FAILED: OSStatus {status}");
        if (immediate) _ended.TrySetResult(true);
    }

    private static void Check(int status, string call)
    {
        if (status == 0) return;
        throw new InvalidOperationException(
            $"{call} failed with OSStatus {status} ({FourCharCode(status)}). The microphone could not be opened.");
    }

    /// <summary>Render an OSStatus as the four-character code Apple documents it by, when it is one.</summary>
    private static string FourCharCode(int status)
    {
        var bytes = BitConverter.GetBytes(status);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        foreach (var b in bytes)
        {
            if (b < 0x20 || b > 0x7E) return status.ToString();
        }
        return "'" + System.Text.Encoding.ASCII.GetString(bytes) + "'";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _discard = true;
        try { Stop(); }
        catch (Exception ex) { FileLog.Write($"[MacMicAudioCapture] Stop during dispose FAILED: {ex.Message}"); }

        IntPtr queue;
        lock (_gate)
        {
            queue = _queue;
            _queue = IntPtr.Zero;
        }
        if (queue != IntPtr.Zero)
            AudioQueueDispose(queue, true);
    }

    // ===== AudioToolbox interop ============================================

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AudioQueueInputCallback(IntPtr userData, IntPtr queue, IntPtr buffer,
                                                  IntPtr startTime, uint packetCount, IntPtr packetDescs);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    /// <summary>
    /// Mirror of AudioToolbox's AudioQueueBuffer. Field order and types must match the C
    /// struct exactly; the runtime inserts the same alignment padding the C compiler does,
    /// so the pointer fields land where AudioQueue wrote them.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewInput(ref AudioStreamBasicDescription format,
                                                 AudioQueueInputCallback callback, IntPtr userData,
                                                 IntPtr callbackRunLoop, IntPtr callbackRunLoopMode,
                                                 uint flags, out IntPtr queue);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bufferByteSize, out IntPtr buffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetDescCount, IntPtr packetDescs);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStop(IntPtr queue, bool immediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueDispose(IntPtr queue, bool immediate);
}
