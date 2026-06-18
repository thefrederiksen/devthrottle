using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation.Providers;

/// <summary>
/// Streaming dictation provider that talks to OpenAI's Realtime
/// transcription API over a WebSocket. Pushes audio chunks as they arrive
/// and surfaces partial transcripts (<see cref="IDictationProvider.OnPartial"/>)
/// as the model emits deltas. <see cref="StopAsync"/> commits the audio
/// buffer and waits for the final transcription event.
///
/// Audio format: PCM16 mono at 24 kHz. The caller is responsible for
/// producing audio in that shape; the browser path can do this with an
/// AudioWorklet, and the desktop path with NAudio's WaveInEvent. This
/// provider does NOT decode webm/opus; for browser MediaRecorder output
/// the batch <see cref="OpenAiTranscriptionProvider"/> is the right
/// choice instead.
///
/// Threading: <see cref="PushAudioAsync"/> serializes WebSocket sends via
/// an internal SemaphoreSlim so it can be called from arbitrary threads.
/// A background read loop drains incoming frames and dispatches them.
/// </summary>
public sealed class OpenAiRealtimeProvider : IDictationProvider
{
    private const string DefaultEndpoint = "wss://api.openai.com/v1/realtime?intent=transcription";
    public const string DefaultModel = "gpt-4o-transcribe";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    // Connect policy (issue #189). A failing upgrade used to hang until the
    // remote edge gave up (~15s observed during the 2026-06-06 OpenAI 504
    // blip) and a single transient failure went straight to the user. Each
    // attempt is bounded by ConnectTimeout, and one automatic retry runs
    // before the failure surfaces. The capture-first pipeline keeps buffering
    // mic audio while this happens, so a successful retry loses no speech.
    // Internal-settable so tests can use fast values against a fake server.
    internal TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    internal TimeSpan ConnectRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    internal int ConnectAttempts { get; set; } = 2;

    private readonly string _apiKey;
    private readonly string _model;
    private readonly Uri _endpoint;

    private readonly SemaphoreSlim _sendGate = new(initialCount: 1, maxCount: 1);
    private readonly object _stateGate = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private TaskCompletionSource<string>? _finalTcs;
    private StringBuilder _runningTranscript = new();
    private bool _started;
    private bool _stopped;
    private bool _disposed;

    public event Action<string>? OnPartial;

    public OpenAiRealtimeProvider(
        string? apiKey = null,
        string model = DefaultModel,
        string? endpoint = null)
    {
        _apiKey = ResolveApiKey(apiKey);
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _endpoint = new Uri(string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint);
    }

    public async Task StartAsync(string sttPrompt, CancellationToken ct = default)
    {
        FileLog.Write($"[OpenAiRealtimeProvider] StartAsync: endpoint={_endpoint}, model={_model}");
        ThrowIfDisposed();
        lock (_stateGate)
        {
            if (_started && !_stopped)
                throw new InvalidOperationException("Provider is already in a session. Call StopAsync first.");
            _runningTranscript = new StringBuilder();
            _finalTcs = null;
            _started = true;
            _stopped = false;
        }

        ClientWebSocket ws;
        try
        {
            ws = await ConnectWithRetryAsync(ct);
        }
        catch
        {
            lock (_stateGate)
            {
                _started = false;
                _stopped = true;
            }
            throw;
        }

        _ws = ws;
        _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));

        // Configure the session with the vocabulary prompt.
        var configFrame = OpenAiRealtimeProtocol.BuildSessionUpdate(_model, sttPrompt ?? "");
        try
        {
            await SendTextAsync(configFrame, ct);
        }
        catch (InvalidOperationException ex) when (_ws?.State != WebSocketState.Open)
        {
            // Exception TRANSLATION, not silencing: the server closed the connection
            // immediately after the HTTP upgrade (rate limit, transient backend problem).
            // The raw InvalidOperationException text ("WebSocket state is CloseReceived,
            // expected Open") is accurate but tells the user nothing actionable.
            // DictationConnectException carries "try again" wording the UI can surface.
            FileLog.Write($"[OpenAiRealtimeProvider] StartAsync: server closed immediately after connect: state={_ws?.State}, error={ex.Message}");
            throw new DictationConnectException(
                lastError: $"server closed immediately after connecting ({_ws?.State})",
                attempts: 1,
                inner: ex);
        }
    }

    public async Task PushAudioAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (chunk.Length == 0) return;
        if (_ws is null) throw new InvalidOperationException("PushAudioAsync called outside of a session.");
        var frame = OpenAiRealtimeProtocol.BuildAudioAppend(chunk);
        await SendTextAsync(frame, ct);
    }

    public async Task<string> StopAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_started) throw new InvalidOperationException("StopAsync called without an active session.");

        TaskCompletionSource<string> tcs;
        lock (_stateGate)
        {
            if (_stopped) throw new InvalidOperationException("Session already stopped.");
            _stopped = true;
            // Use RunContinuationsAsynchronously so any continuations posted
            // by the read loop do not run inline and re-enter our locks.
            tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _finalTcs = tcs;
        }

        try
        {
            await SendTextAsync(OpenAiRealtimeProtocol.BuildAudioCommit(), ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OpenAiRealtimeProvider] StopAsync: commit failed: {ex.Message}");
            tcs.TrySetException(ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(StopTimeout);
        try
        {
            await using var _ = timeoutCts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException(
                    $"Did not receive transcription completed event within {StopTimeout.TotalSeconds}s")));
            return await tcs.Task;
        }
        finally
        {
            await CloseAndCleanupAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await CloseAndCleanupAsync();
        _sendGate.Dispose();
    }

    // ===== internals =========================================================

    /// <summary>
    /// Establish the realtime WebSocket with a per-attempt timeout and a
    /// bounded automatic retry. Caller cancellation propagates immediately
    /// (never retried, never rewrapped); exhausting the attempt budget throws
    /// <see cref="DictationConnectException"/> with a human-readable message
    /// and the last raw error as the inner exception.
    /// </summary>
    private async Task<ClientWebSocket> ConnectWithRetryAsync(CancellationToken ct)
    {
        Exception lastError = new InvalidOperationException("no connect attempt was made");
        for (int attempt = 1; attempt <= ConnectAttempts; attempt++)
        {
            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _apiKey);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ConnectTimeout);
            try
            {
                await ws.ConnectAsync(_endpoint, timeoutCts.Token);
                if (attempt > 1)
                    FileLog.Write($"[OpenAiRealtimeProvider] connect attempt {attempt}/{ConnectAttempts} succeeded");
                return ws;
            }
            catch (Exception ex)
            {
                ws.Dispose();

                // The CALLER cancelled (dialog closed, app shutting down):
                // stop immediately - retrying would be working for nobody.
                if (ct.IsCancellationRequested) throw;

                // Our per-attempt timeout fired: name the real problem instead
                // of surfacing a bare OperationCanceledException.
                lastError = ex is OperationCanceledException && timeoutCts.IsCancellationRequested
                    ? new TimeoutException(
                        $"Connecting to {_endpoint.Host} timed out after {ConnectTimeout.TotalSeconds:0}s")
                    : ex;

                FileLog.Write($"[OpenAiRealtimeProvider] connect attempt {attempt}/{ConnectAttempts} FAILED: {lastError.Message}");
                if (attempt < ConnectAttempts)
                    await Task.Delay(ConnectRetryDelay, ct);
            }
        }

        throw new DictationConnectException(lastError.Message, ConnectAttempts, lastError);
    }

    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        if (_ws is null) throw new InvalidOperationException("WebSocket is not open.");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open)
                throw new InvalidOperationException($"WebSocket state is {_ws.State}, expected Open.");
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            try { _sendGate.Release(); } catch { /* disposed */ }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var ws = _ws!;
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                string text;
                try
                {
                    text = await ReadFullMessageAsync(ws, buffer, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[OpenAiRealtimeProvider] read loop error: {ex.Message}");
                    _finalTcs?.TrySetException(ex);
                    return;
                }

                if (text.Length == 0) continue;

                var evt = OpenAiRealtimeProtocol.Parse(text);
                switch (evt)
                {
                    case DeltaEvent delta:
                        _runningTranscript.Append(delta.Delta);
                        try { OnPartial?.Invoke(_runningTranscript.ToString()); }
                        catch (Exception ex) { FileLog.Write($"[OpenAiRealtimeProvider] OnPartial handler threw: {ex.Message}"); }
                        break;

                    case CompletedEvent done:
                        var final = string.IsNullOrEmpty(done.Transcript) ? _runningTranscript.ToString() : done.Transcript;
                        _finalTcs?.TrySetResult(final);
                        break;

                    case ErrorEvent err:
                        FileLog.Write($"[OpenAiRealtimeProvider] server error: {err.Message}");
                        _finalTcs?.TrySetException(new InvalidOperationException("OpenAI Realtime API error: " + err.Message));
                        break;

                    case OtherEvent other:
                        // Informational frames (session.updated, etc.). No-op.
                        if (!string.IsNullOrEmpty(other.Type))
                            FileLog.Write($"[OpenAiRealtimeProvider] event: {other.Type}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OpenAiRealtimeProvider] read loop unexpected: {ex}");
            _finalTcs?.TrySetException(ex);
        }
    }

    private static async Task<string> ReadFullMessageAsync(WebSocket ws, byte[] buf, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                FileLog.Write($"[OpenAiRealtimeProvider] server closed: status={result.CloseStatus}, desc={result.CloseStatusDescription ?? "(none)"}");
                throw new InvalidOperationException(
                    $"OpenAI Realtime server closed the connection: {result.CloseStatus} - {result.CloseStatusDescription ?? "(no description)"}");
            }
            ms.Write(buf, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task CloseAndCleanupAsync()
    {
        var ws = _ws;
        var cts = _readLoopCts;
        var readTask = _readLoopTask;
        _ws = null;
        _readLoopCts = null;
        _readLoopTask = null;

        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch (Exception ex) { FileLog.Write($"[OpenAiRealtimeProvider] close error: {ex.Message}"); }
            ws.Dispose();
        }

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        if (readTask is not null)
        {
            try { await readTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { FileLog.Write($"[OpenAiRealtimeProvider] read task wait: {ex.Message}"); }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpenAiRealtimeProvider));
    }

    private static string ResolveApiKey(string? explicitKey)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey)) return explicitKey.Trim();
        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(env))
            throw new InvalidOperationException(
                "OpenAI API key not provided and OPENAI_API_KEY environment variable is not set.");
        return env.Trim();
    }
}
