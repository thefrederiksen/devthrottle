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
/// 1. Start capturing the instant the pipeline starts (synchronous
///    <see cref="IAudioSource.Start"/>), BEFORE the connection is attempted.
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
    private readonly DictationSession _session;

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

    private bool _started;
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

    public DictationPipeline(IAudioSource audio, DictationSession session)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.OnPartial += ForwardPartial;
        _session.OnStateChanged += ForwardState;
    }

    /// <summary>
    /// Start capturing immediately, then connect. Returns once the provider
    /// session is connected and live streaming is underway; audio captured
    /// during the connect is buffered and flushed in order. Throws if the
    /// connection fails (capture is stopped first).
    /// </summary>
    public async Task StartAsync(string profile = "default", CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DictationPipeline));
        if (_started) throw new InvalidOperationException("DictationPipeline already started.");
        FileLog.Write($"[DictationPipeline] StartAsync: profile={profile}");
        _started = true;

        // 1. CAPTURE FIRST. Synchronous; the very first sample is now buffered
        //    before we spend a single millisecond on the connection.
        _audio.OnAudioChunk += OnChunk;
        _audio.Start();
        try { OnCaptureStarted?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[DictationPipeline] OnCaptureStarted handler threw: {ex.Message}"); }

        // 2. Start the pump. It will block on _connected until the session is up,
        //    buffering everything the mic produces in the meantime.
        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);

        // 3. Connect (the slow part). If it fails, stop capture and surface the
        //    error - no silent degradation.
        try
        {
            await _session.StartAsync(profile, ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationPipeline] StartAsync: session connect FAILED: {ex.Message}");
            _audio.Stop();
            _channel.Writer.TryComplete(ex);
            _connected.TrySetException(ex);
            try { _pumpCts.Cancel(); } catch { /* disposed */ }
            throw;
        }

        // 4. Release the pump: buffered audio drains in capture order, then live.
        _connected.TrySetResult();
        try { OnConnected?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[DictationPipeline] OnConnected handler threw: {ex.Message}"); }
        FileLog.Write($"[DictationPipeline] StartAsync done: primed_chunks={PrimedChunkCount}, captured_bytes={CapturedBytes}");
    }

    /// <summary>
    /// Stop capture, deliver every remaining captured chunk to the provider,
    /// then commit and return the final result. Guarantees the transcript
    /// reflects all captured audio (including the opening words).
    /// </summary>
    public async Task<TranscriptResult> StopAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DictationPipeline));
        if (!_started) throw new InvalidOperationException("StopAsync called before StartAsync.");
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
            await foreach (var chunk in _channel.Reader.ReadAllAsync(ct))
            {
                await _session.PushAudioAsync(chunk, ct);
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

        _session.OnPartial -= ForwardPartial;
        _session.OnStateChanged -= ForwardState;
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
