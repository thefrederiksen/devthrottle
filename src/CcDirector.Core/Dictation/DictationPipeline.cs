using System.Threading.Channels;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Capture-first orchestration that ties an <see cref="IAudioSource"/> to a
/// <see cref="DictationSession"/> with a hard guarantee: every byte the mic
/// captures reaches the transcription provider, in order, no matter how slow
/// the connection to the provider is.
///
/// WHY THIS EXISTS
/// ---------------
/// The original desktop flow connected to OpenAI's Realtime WebSocket FIRST and
/// only started the microphone AFTER the connect returned. The connect is a
/// full TLS handshake to api.openai.com - hundreds of milliseconds on a warm
/// link, several seconds on a cold/slow one. Anything the user said during that
/// window was never captured and was lost forever. Users lost the first
/// sentence(s) of their dictation.
///
/// THE FIX
/// -------
/// 1. Start capturing the instant <see cref="StartCapture"/> is called
///    (synchronous <see cref="IAudioSource.Start"/>), BEFORE any network work -
///    key-vault fetch, dictionary fetch, and the provider connect all happen
///    afterwards in <see cref="ConnectAsync"/>. The audio source is the only
///    dependency capture needs, so the pipeline does NOT require the session at
///    construction: capture can begin while the key is still being resolved and
///    the session does not yet exist.
/// 2. Every captured chunk is written to an ordered, unbounded channel.
/// 3. A single pump task waits until the provider session is connected, then
///    drains the channel FIFO and pushes each chunk to the session. Chunks
///    captured during the connect window were buffered in the channel, so they
///    are delivered first, in capture order, the moment the link is up.
/// 4. <see cref="StopAsync"/> stops capture, drains every remaining chunk, and
///    only then commits - so the final transcript covers the whole recording
///    including the opening words.
///
/// A single FIFO channel + single reader is what makes ordering provable: the
/// channel preserves write order and the pump is the only consumer, so the
/// sequence the provider sees is exactly the sequence the mic produced. The
/// diagnostics counters (<see cref="CapturedBytes"/>, <see cref="DeliveredBytes"/>,
/// <see cref="PrimedChunkCount"/>) exist so tests can prove the no-loss
/// invariant and prove the pre-connection window was actually exercised.
///
/// This class does NOT own the lifetime of the injected audio source or
/// session - the caller created them and disposes them. The pipeline only
/// orchestrates Start/Stop and runs the pump.
/// </summary>
public sealed class DictationPipeline : IAsyncDisposable
{
    private readonly IAudioSource _audio;
    // Bound at ConnectAsync, not construction: capture starts before the session
    // (and the key it needs) exists. The pump only touches it after _connected is
    // released, which ConnectAsync does only once _session is set.
    private DictationSession? _session;

    // Unbounded so capture is never blocked by a slow or not-yet-open
    // connection; single reader because the pump is the sole consumer, which is
    // what guarantees FIFO delivery order.
    private readonly Channel<byte[]> _channel =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false, // the audio source raises chunks on its own thread
        });

    // Released once the provider session is connected. The pump blocks on this
    // so it never pushes audio before the session can accept it, while the
    // channel keeps buffering captured audio in the meantime.
    private readonly TaskCompletionSource _connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    private long _capturedBytes;
    private long _deliveredBytes;
    private int _primedChunkCount;

    private bool _captureStarted;
    private bool _connectStarted;
    private bool _stopped;
    private bool _disposed;

    /// <summary>Total bytes the audio source produced across the session.</summary>
    public long CapturedBytes => Interlocked.Read(ref _capturedBytes);

    /// <summary>Total bytes the pump pushed to the session. After a full Start/Stop cycle this MUST equal <see cref="CapturedBytes"/>.</summary>
    public long DeliveredBytes => Interlocked.Read(ref _deliveredBytes);

    /// <summary>Count of chunks captured BEFORE the connection completed (i.e. priming-buffered). Proves the pre-connect window was exercised.</summary>
    public int PrimedChunkCount => Volatile.Read(ref _primedChunkCount);

    /// <summary>Fires (on the caller's thread) the moment capture has started, before the connection is attempted. Lets the UI anchor its "recording" timer to real capture start.</summary>
    public event Action? OnCaptureStarted;

    /// <summary>Fires once the provider session is connected and buffered audio is being drained.</summary>
    public event Action? OnConnected;

    /// <summary>Forwarded from the session: partial transcripts as the provider emits them.</summary>
    public event Action<string>? OnPartial;

    /// <summary>Forwarded from the session: connection-state transitions.</summary>
    public event Action<ConnectionState>? OnStateChanged;

    public DictationPipeline(IAudioSource audio)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
    }

    /// <summary>
    /// Start capturing immediately, then connect. Returns once the provider
    /// session is connected and live streaming is underway; audio captured
    /// during the connect is buffered and flushed in order. Throws if the
    /// connection fails (capture is stopped first). Convenience wrapper over
    /// <see cref="StartCapture"/> + <see cref="ConnectAsync"/> for callers that
    /// already have the session ready before they want any capture.
    /// </summary>
    public async Task StartAsync(DictationSession session, string profile = "default", CancellationToken ct = default)
    {
        StartCapture();
        await ConnectAsync(session, profile, ct);
    }

    /// <summary>
    /// CAPTURE FIRST. Synchronous and non-blocking: opens the audio source and
    /// begins buffering every captured chunk into the ordered channel BEFORE any
    /// network work (key fetch, dictionary fetch, provider connect). The mic is
    /// live the instant this returns, so the UI can honestly show "recording".
    /// Audio captured from here until <see cref="ConnectAsync"/> releases the
    /// pump is held in the channel and delivered in capture order - nothing is
    /// lost to the time the key resolves or the connection establishes.
    /// </summary>
    public void StartCapture()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DictationPipeline));
        if (_captureStarted) throw new InvalidOperationException("DictationPipeline capture already started.");
        FileLog.Write("[DictationPipeline] StartCapture");
        _captureStarted = true;

        // The very first sample is buffered before we spend a single millisecond
        // on the key, the dictionary, or the connection.
        _audio.OnAudioChunk += OnChunk;
        _audio.Start();
        try { OnCaptureStarted?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[DictationPipeline] OnCaptureStarted handler threw: {ex.Message}"); }

        // The pump blocks on _connected until ConnectAsync releases it, buffering
        // everything the mic produces in the meantime.
        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Bind the session and connect (the slow part). Returns once the provider
    /// session is live and the buffered audio is draining in capture order, then
    /// live. Must be called after <see cref="StartCapture"/>. If the connect
    /// fails, capture is stopped and the error surfaces - no silent degradation.
    /// </summary>
    public async Task ConnectAsync(DictationSession session, string profile = "default", CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DictationPipeline));
        if (!_captureStarted) throw new InvalidOperationException("ConnectAsync called before StartCapture.");
        if (_connectStarted) throw new InvalidOperationException("DictationPipeline already connecting.");
        _connectStarted = true;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.OnPartial += ForwardPartial;
        _session.OnStateChanged += ForwardState;
        FileLog.Write($"[DictationPipeline] ConnectAsync: profile={profile}");

        try
        {
            await _session.StartAsync(profile, ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationPipeline] ConnectAsync: session connect FAILED: {ex.Message}");
            _audio.Stop();
            _channel.Writer.TryComplete(ex);
            _connected.TrySetException(ex);
            try { _pumpCts?.Cancel(); } catch { /* disposed */ }
            throw;
        }

        // Release the pump: buffered audio drains in capture order, then live.
        _connected.TrySetResult();
        try { OnConnected?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[DictationPipeline] OnConnected handler threw: {ex.Message}"); }
        FileLog.Write($"[DictationPipeline] ConnectAsync done: primed_chunks={PrimedChunkCount}, captured_bytes={CapturedBytes}");
    }

    /// <summary>
    /// Stop capture, deliver every remaining captured chunk to the provider,
    /// then commit and return the final result. Guarantees the transcript
    /// reflects all captured audio (including the opening words).
    /// </summary>
    public async Task<TranscriptResult> StopAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DictationPipeline));
        if (!_captureStarted) throw new InvalidOperationException("StopAsync called before StartCapture.");
        if (_session is null) throw new InvalidOperationException("StopAsync called before ConnectAsync - no session to commit to.");
        if (_stopped) throw new InvalidOperationException("DictationPipeline already stopped.");
        _stopped = true;
        FileLog.Write($"[DictationPipeline] StopAsync: captured_bytes={CapturedBytes}, delivered_so_far={DeliveredBytes}");

        // Halt capture, then tell the pump no more chunks are coming so it
        // drains the channel and exits.
        _audio.OnAudioChunk -= OnChunk;
        _audio.Stop();
        _channel.Writer.TryComplete();

        // Wait for the pump to deliver everything that was captured. This is the
        // load-bearing step for the no-loss guarantee: we do NOT commit until
        // the last captured chunk has been pushed to the provider.
        if (_pumpTask is not null)
        {
            try { await _pumpTask; }
            catch (Exception ex) { FileLog.Write($"[DictationPipeline] StopAsync: pump ended with: {ex.Message}"); }
        }

        FileLog.Write($"[DictationPipeline] StopAsync: drained. captured={CapturedBytes}, delivered={DeliveredBytes}");

        // Fail honestly, naming the device, when the mic produced nothing. We do
        // this BEFORE committing to the provider: an empty commit makes the
        // provider reject the buffer with an opaque "buffer too small / 0.00ms"
        // error that tells the user nothing about the real cause (a silent or
        // wrong microphone). See NoAudioCapturedException.
        if (CapturedBytes == 0)
        {
            FileLog.Write($"[DictationPipeline] StopAsync: NO AUDIO captured from '{_audio.Description}' - not committing empty buffer");
            throw new NoAudioCapturedException(_audio.Description);
        }

        return await _session.StopAsync(ct);
    }

    /// <summary>Audio-source callback. Runs on the source's thread; just meters and enqueues.</summary>
    private void OnChunk(byte[] chunk)
    {
        if (chunk.Length == 0) return;
        Interlocked.Add(ref _capturedBytes, chunk.Length);
        // A chunk seen before the connection completed was captured during the
        // priming window - the exact audio the old code dropped.
        if (!_connected.Task.IsCompleted)
            Interlocked.Increment(ref _primedChunkCount);
        // TryWrite always succeeds on an unbounded channel that is still open.
        if (!_channel.Writer.TryWrite(chunk))
            FileLog.Write("[DictationPipeline] OnChunk: channel closed, chunk dropped");
    }

    /// <summary>
    /// Single consumer: wait for the connection, then push captured chunks to
    /// the session in the exact order they were captured.
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await _connected.Task.WaitAsync(ct);
            // _connected is only released by ConnectAsync AFTER _session is set,
            // so the session is guaranteed non-null here.
            await foreach (var chunk in _channel.Reader.ReadAllAsync(ct))
            {
                await _session!.PushAudioAsync(chunk, ct);
                Interlocked.Add(ref _deliveredBytes, chunk.Length);
            }
        }
        catch (OperationCanceledException)
        {
            // Pipeline cancelled/disposed before connecting; nothing to flush.
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationPipeline] PumpAsync error: {ex.Message}");
            throw;
        }
    }

    private void ForwardPartial(string partial) => OnPartial?.Invoke(partial);
    private void ForwardState(ConnectionState state) => OnStateChanged?.Invoke(state);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_session is not null)
        {
            _session.OnPartial -= ForwardPartial;
            _session.OnStateChanged -= ForwardState;
        }
        _audio.OnAudioChunk -= OnChunk;
        try { _audio.Stop(); } catch (Exception ex) { FileLog.Write($"[DictationPipeline] Dispose: audio stop threw: {ex.Message}"); }

        _channel.Writer.TryComplete();
        _connected.TrySetCanceled();
        if (_pumpCts is not null)
        {
            try { _pumpCts.Cancel(); } catch { /* already disposed */ }
        }
        if (_pumpTask is not null)
        {
            try { await _pumpTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { FileLog.Write($"[DictationPipeline] Dispose: pump wait: {ex.Message}"); }
        }
        _pumpCts?.Dispose();
    }
}
