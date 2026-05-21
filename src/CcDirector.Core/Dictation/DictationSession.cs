using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Dictation.Providers;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// High-level facade that wires the four pieces of the dictation library
/// together: dictionary, provider, optional offline buffer, cleanup.
///
/// Typical lifecycle:
/// <code>
///   await session.StartAsync(profile: "default", ct);
///   while (recording) await session.PushAudioAsync(chunk, ct);
///   TranscriptResult result = await session.StopAsync(ct);
///   // result.CleanedTranscript is what gets typed into the focused window
/// </code>
///
/// The class is single-session: one start/stop cycle per instance is the
/// expected usage. Hot reloads of the dictionary are picked up on the next
/// <see cref="StartAsync"/>.
/// </summary>
public sealed class DictationSession : IAsyncDisposable
{
    private readonly DictionaryLoader _dictionary;
    private readonly IDictationProvider _provider;
    private readonly CleanupOrchestrator _cleanup;

    private string _profile = "default";
    private bool _started;
    private bool _stopped;
    private bool _disposed;

    /// <summary>
    /// Forwarded from the underlying provider. Streaming providers fire
    /// repeatedly; the Phase 1 batch provider fires once just before
    /// <see cref="StopAsync"/> returns.
    /// </summary>
    public event Action<string>? OnPartial;

    public DictationSession(
        DictionaryLoader dictionary,
        IDictationProvider provider,
        CleanupOrchestrator cleanup)
    {
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));

        _provider.OnPartial += ForwardPartial;
    }

    /// <summary>Open a transcription session bound to a dictionary profile.</summary>
    public async Task StartAsync(string profile = "default", CancellationToken ct = default)
    {
        FileLog.Write($"[DictationSession] StartAsync: profile={profile}");
        if (_disposed) throw new ObjectDisposedException(nameof(DictationSession));
        if (_started) throw new InvalidOperationException("Session already started.");

        _profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
        var sttPrompt = DictionaryLoader.BuildSttPrompt(_dictionary.Current);
        await _provider.StartAsync(sttPrompt, ct);
        _started = true;
        _stopped = false;
    }

    /// <summary>Push a chunk of audio at the active provider.</summary>
    public Task PushAudioAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DictationSession));
        if (!_started || _stopped) throw new InvalidOperationException("Session is not active.");
        return _provider.PushAudioAsync(chunk, ct);
    }

    /// <summary>
    /// Stop streaming audio, run the cleanup pass, and return the final
    /// result. Always returns a non-null result: on cleanup failure the
    /// raw transcript is preserved in <see cref="TranscriptResult.CleanedTranscript"/>.
    /// </summary>
    public async Task<TranscriptResult> StopAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DictationSession));
        if (!_started) throw new InvalidOperationException("StopAsync called without an active session.");
        if (_stopped) throw new InvalidOperationException("Session already stopped.");
        _stopped = true;

        FileLog.Write($"[DictationSession] StopAsync: profile={_profile}");

        var raw = await _provider.StopAsync(ct);
        var dict = _dictionary.Current;
        var cleanup = await _cleanup.CleanAsync(raw, dict, _profile, ct);

        var result = new TranscriptResult(
            RawTranscript: raw,
            CleanedTranscript: cleanup.Text,
            ProfileUsed: _profile,
            CleanupApplied: cleanup.Applied,
            CleanupFailureReason: cleanup.Reason);

        FileLog.Write($"[DictationSession] StopAsync done: raw_len={raw.Length}, cleaned_len={cleanup.Text.Length}, applied={cleanup.Applied}");
        return result;
    }

    private void ForwardPartial(string partial) => OnPartial?.Invoke(partial);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _provider.OnPartial -= ForwardPartial;
        await _provider.DisposeAsync();
    }
}
