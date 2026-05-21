using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Dictation.Providers;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// High-level facade that wires the dictation library together: dictionary,
/// provider, offline buffer, cleanup.
///
/// Typical lifecycle:
/// <code>
///   await session.StartAsync("default");
///   while (recording) await session.PushAudioAsync(chunk);
///   TranscriptResult result = await session.StopAsync();
/// </code>
///
/// Phase 4 additions:
/// - <see cref="AudioBuffer"/> backed offline fallback. If a provider call
///   fails with a recognized transient error (network), the chunk is routed
///   to the buffer and the session moves to <see cref="ConnectionState.Buffering"/>.
///   Subsequent pushes stay in the buffer until <see cref="TryReconnectAsync"/>
///   succeeds.
/// - <see cref="State"/> and <see cref="OnStateChanged"/> let consumers
///   surface "recording", "buffering", "reconnecting", etc. to the UI.
/// - The buffer can be disk-backed via the <paramref name="bufferSpillDir"/>
///   constructor parameter (see <see cref="AudioBuffer"/>).
///
/// Single-session per instance: one start/stop cycle.
/// </summary>
public sealed class DictationSession : IAsyncDisposable
{
    private readonly DictionaryLoader _dictionary;
    private readonly IDictationProvider _provider;
    private readonly CleanupOrchestrator _cleanup;
    private readonly AudioBuffer _audioBuffer;
    private readonly bool _ownsAudioBuffer;
    private readonly object _stateGate = new();

    private string _profile = "default";
    private string _sttPrompt = "";
    private bool _started;
    private bool _stopped;
    private bool _disposed;
    private ConnectionState _state = ConnectionState.Idle;

    public event Action<string>? OnPartial;
    public event Action<ConnectionState>? OnStateChanged;

    /// <summary>Current connection state. Thread-safe getter.</summary>
    public ConnectionState State { get { lock (_stateGate) return _state; } }

    /// <summary>True if the session currently has audio in the offline buffer.</summary>
    public bool HasBufferedAudio => !_audioBuffer.IsEmpty;

    /// <summary>Exposed for diagnostics and tests.</summary>
    internal AudioBuffer Buffer => _audioBuffer;

    /// <param name="audioBuffer">Optional buffer to use. If null, an in-memory default is created and owned by the session.</param>
    public DictationSession(
        DictionaryLoader dictionary,
        IDictationProvider provider,
        CleanupOrchestrator cleanup,
        AudioBuffer? audioBuffer = null)
    {
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));

        if (audioBuffer is null)
        {
            _audioBuffer = new AudioBuffer();
            _ownsAudioBuffer = true;
        }
        else
        {
            _audioBuffer = audioBuffer;
            _ownsAudioBuffer = false;
        }

        _provider.OnPartial += ForwardPartial;
    }

    /// <summary>Open a transcription session bound to a dictionary profile.</summary>
    public async Task StartAsync(string profile = "default", CancellationToken ct = default)
    {
        FileLog.Write($"[DictationSession] StartAsync: profile={profile}");
        if (_disposed) throw new ObjectDisposedException(nameof(DictationSession));
        if (_started) throw new InvalidOperationException("Session already started.");

        _profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
        _sttPrompt = DictionaryLoader.BuildSttPrompt(_dictionary.Current);
        await _provider.StartAsync(_sttPrompt, ct);
        _started = true;
        _stopped = false;
        ChangeState(ConnectionState.Connected);
    }

    /// <summary>
    /// Push a chunk of audio. On a transient provider error, the chunk is
    /// routed to the offline buffer and the session transitions to
    /// <see cref="ConnectionState.Buffering"/>. Call
    /// <see cref="TryReconnectAsync"/> to attempt recovery.
    /// </summary>
    public async Task PushAudioAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DictationSession));
        if (!_started || _stopped) throw new InvalidOperationException("Session is not active.");
        if (chunk.Length == 0) return;

        // Already degraded: skip the provider call and just buffer.
        var current = State;
        if (current == ConnectionState.Buffering || current == ConnectionState.Reconnecting)
        {
            _audioBuffer.Append(chunk.ToArray());
            return;
        }

        try
        {
            await _provider.PushAudioAsync(chunk, ct);
        }
        catch (Exception ex) when (ShouldBuffer(ex))
        {
            FileLog.Write($"[DictationSession] PushAudio failed with transient error, switching to buffering: {ex.GetType().Name}: {ex.Message}");
            _audioBuffer.Append(chunk.ToArray());
            ChangeState(ConnectionState.Buffering);
        }
    }

    /// <summary>
    /// Attempt to re-establish the provider and drain the offline buffer.
    /// Returns true when the buffer is fully delivered and the session is
    /// back in <see cref="ConnectionState.Connected"/>. Returns false if a
    /// subsequent provider call fails; remaining audio stays in the buffer
    /// for the next attempt.
    /// </summary>
    public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DictationSession));
        if (!_started || _stopped) throw new InvalidOperationException("Session is not active.");

        var current = State;
        if (current == ConnectionState.Connected) return true;
        if (current != ConnectionState.Buffering) return false;

        ChangeState(ConnectionState.Reconnecting);
        FileLog.Write($"[DictationSession] TryReconnectAsync: draining {_audioBuffer.ChunkCount} buffered chunks");

        var chunks = _audioBuffer.DrainAll();
        var failedAt = -1;
        for (int i = 0; i < chunks.Count; i++)
        {
            try
            {
                await _provider.PushAudioAsync(chunks[i], ct);
            }
            catch (Exception ex) when (ShouldBuffer(ex))
            {
                FileLog.Write($"[DictationSession] TryReconnectAsync: failed at chunk {i}/{chunks.Count}: {ex.Message}");
                failedAt = i;
                break;
            }
        }

        if (failedAt >= 0)
        {
            for (int i = failedAt; i < chunks.Count; i++)
                _audioBuffer.Append(chunks[i]);
            ChangeState(ConnectionState.Buffering);
            return false;
        }

        ChangeState(ConnectionState.Connected);
        return true;
    }

    /// <summary>
    /// Stop streaming audio, run the cleanup pass, and return the final
    /// result. If the session is in <see cref="ConnectionState.Buffering"/>
    /// when Stop is called, one reconnect attempt is made first so the
    /// buffered audio has a chance to be transcribed. If reconnect fails,
    /// the provider is still called with whatever audio it already has and
    /// the failure reason is preserved in the result.
    /// </summary>
    public async Task<TranscriptResult> StopAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DictationSession));
        if (!_started) throw new InvalidOperationException("StopAsync called without an active session.");
        if (_stopped) throw new InvalidOperationException("Session already stopped.");

        FileLog.Write($"[DictationSession] StopAsync: profile={_profile}, state={State}");

        // Run reconnect BEFORE flipping _stopped so the buffered audio gets a
        // chance to be transcribed through TryReconnectAsync. Any further
        // PushAudio from a misbehaving caller would race here, but the
        // single-session contract means that does not happen in practice.
        string? bufferFailureReason = null;
        if (State == ConnectionState.Buffering)
        {
            var ok = await TryReconnectAsync(ct);
            if (!ok)
                bufferFailureReason = $"{_audioBuffer.ChunkCount} chunks remained in offline buffer at stop";
        }

        _stopped = true;

        string raw;
        try
        {
            raw = await _provider.StopAsync(ct);
        }
        catch (Exception ex) when (ShouldBuffer(ex))
        {
            FileLog.Write($"[DictationSession] StopAsync: provider failed: {ex.Message}");
            ChangeState(ConnectionState.Failed);
            return new TranscriptResult(
                RawTranscript: "",
                CleanedTranscript: "",
                ProfileUsed: _profile,
                CleanupApplied: false,
                CleanupFailureReason: "provider stop failed: " + ex.Message);
        }

        var dict = _dictionary.Current;
        var cleanup = await _cleanup.CleanAsync(raw, dict, _profile, ct);

        var reason = cleanup.Reason;
        if (bufferFailureReason is not null)
            reason = reason is null ? bufferFailureReason : reason + "; " + bufferFailureReason;

        var result = new TranscriptResult(
            RawTranscript: raw,
            CleanedTranscript: cleanup.Text,
            ProfileUsed: _profile,
            CleanupApplied: cleanup.Applied,
            CleanupFailureReason: reason);

        ChangeState(ConnectionState.Idle);
        FileLog.Write($"[DictationSession] StopAsync done: raw_len={raw.Length}, cleaned_len={cleanup.Text.Length}, applied={cleanup.Applied}");
        return result;
    }

    private void ChangeState(ConnectionState next)
    {
        ConnectionState before;
        lock (_stateGate)
        {
            if (_state == next) return;
            before = _state;
            _state = next;
        }
        FileLog.Write($"[DictationSession] state: {before} -> {next}");
        OnStateChanged?.Invoke(next);
    }

    private void ForwardPartial(string partial) => OnPartial?.Invoke(partial);

    /// <summary>
    /// Exception types that should trigger the offline buffer fallback. We
    /// intentionally do NOT catch programming errors (ArgumentException,
    /// InvalidOperationException, etc.) because those indicate bugs we
    /// want to surface, not transient failures we want to mask.
    /// </summary>
    private static bool ShouldBuffer(Exception ex)
        => ex is HttpRequestException
           || ex is WebSocketException
           || ex is SocketException
           || ex is IOException
           || ex is TimeoutException
           || ex is TaskCanceledException;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _provider.OnPartial -= ForwardPartial;
        await _provider.DisposeAsync();
        if (_ownsAudioBuffer) _audioBuffer.Dispose();
    }
}
