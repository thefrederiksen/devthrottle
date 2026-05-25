using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Dictation.Providers;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// One-shot orchestrator that ties <see cref="MicAudioCapture"/> to the
/// in-process dictation library and returns a final transcript. The
/// caller (the SpeakDialog) drives lifecycle:
///
///   var svc = new SpeakService(options);
///   svc.OnAudioBands    += bands => /* drive equalizer */ ;
///   svc.OnPartial       += text  => /* live preview */ ;
///   svc.OnStateChanged  += s     => /* recording / buffering / transcribing */ ;
///   await svc.StartAsync(profile, ct);
///   // user talks
///   var result = await svc.StopAsync(ct);
///   // result.CleanedTranscript is what we hand back to the prompt input
///
/// No browser, no WebSocket, no localhost hop. The C# library runs in
/// the same process as the Avalonia UI.
/// </summary>
public sealed class SpeakService : IAsyncDisposable
{
    private readonly AgentOptions _options;

    private MicAudioCapture? _mic;
    private DictionaryLoader? _dictionary;
    private OpenAiRealtimeProvider? _provider;
    private CleanupOrchestrator? _cleanup;
    private AudioBuffer? _audioBuffer;
    private DictationSession? _session;

    private bool _started;
    private bool _stopped;
    private bool _disposed;

    public event Action<double[]>? OnAudioBands;
    public event Action<double>? OnInputRms;
    public event Action<string>? OnPartial;
    public event Action<ConnectionState>? OnStateChanged;

    public SpeakService(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task StartAsync(string profile = "default", CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpeakService));
        if (_started) throw new InvalidOperationException("SpeakService already started");
        FileLog.Write($"[SpeakService] StartAsync: profile={profile}");

        var dictPath = _options.ResolveDictationDictionaryPath();
        _dictionary = new DictionaryLoader(dictPath, watch: false);

        _provider = new OpenAiRealtimeProvider(apiKey: _options.ResolveOpenAiKey());
        _cleanup = new CleanupOrchestrator(
            apiKey: _options.ResolveOpenAiKey(),
            model: _options.DictationCleanupModel);
        _audioBuffer = new AudioBuffer(spillDirectory: ResolveBufferSpillDir());
        _session = new DictationSession(_dictionary, _provider, _cleanup, _audioBuffer);

        _session.OnPartial += partial => OnPartial?.Invoke(partial);
        _session.OnStateChanged += state => OnStateChanged?.Invoke(state);

        await _session.StartAsync(profile, ct);

        _mic = new MicAudioCapture();
        _mic.OnAudioChunk += chunk =>
        {
            // PushAudio is async but we are inside NAudio's worker thread. Fire-and-forget
            // is fine; if the provider's WebSocket isn't keeping up we'll buffer or fail
            // through DictationSession's existing error handling.
            _ = _session.PushAudioAsync(chunk);
        };
        _mic.OnAudioBands += bands => OnAudioBands?.Invoke(bands);
        _mic.OnInputRms += rms => OnInputRms?.Invoke(rms);
        _mic.Start();
        _started = true;
    }

    /// <summary>Stop the mic, drain through the provider, run cleanup, return the result.</summary>
    public async Task<TranscriptResult> StopAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpeakService));
        if (!_started) throw new InvalidOperationException("SpeakService not started");
        if (_stopped) throw new InvalidOperationException("SpeakService already stopped");
        _stopped = true;

        FileLog.Write("[SpeakService] StopAsync");

        try { _mic?.Stop(); } catch { /* tolerate */ }

        if (_session is null)
            throw new InvalidOperationException("Session is null after Start - should be unreachable");

        return await _session.StopAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mic?.Dispose(); } catch { }
        if (_session is not null) await _session.DisposeAsync();
        _cleanup?.Dispose();
        _audioBuffer?.Dispose();
        _dictionary?.Dispose();
    }

    private static string ResolveBufferSpillDir()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-director", "dictation", "buffer", Guid.NewGuid().ToString("N"));
    }
}
