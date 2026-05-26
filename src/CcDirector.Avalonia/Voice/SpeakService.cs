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
    private DictationPipeline? _pipeline;

    private bool _started;
    private bool _stopped;
    private bool _disposed;

    public event Action<double[]>? OnAudioBands;
    public event Action<double>? OnInputRms;
    public event Action<string>? OnPartial;
    public event Action<ConnectionState>? OnStateChanged;

    /// <summary>Fires the instant the microphone starts capturing, before the transcription connection completes. The UI anchors its recording timer to this so the displayed time tracks real capture, not the pre-connect setup.</summary>
    public event Action? OnCaptureStarted;

    /// <summary>Fires once the transcription backend is connected and buffered audio is streaming.</summary>
    public event Action? OnConnected;

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

        // Build the mic and wire the UI meters BEFORE handing it to the pipeline.
        // The equalizer/level meters are driven straight off NAudio and do not
        // depend on the transcription connection, so the bars move from the
        // first captured frame - honest visual confirmation that we are
        // recording even while the backend is still connecting.
        _mic = new MicAudioCapture();
        _mic.OnAudioBands += bands => OnAudioBands?.Invoke(bands);
        _mic.OnInputRms += rms => OnInputRms?.Invoke(rms);

        // The pipeline is the load-bearing fix: it starts mic capture FIRST and
        // buffers everything captured during the (slow) connect, then drains it
        // in order. No audio is lost to connection latency. See DictationPipeline.
        _pipeline = new DictationPipeline(_mic, _session);
        _pipeline.OnPartial += partial => OnPartial?.Invoke(partial);
        _pipeline.OnStateChanged += state => OnStateChanged?.Invoke(state);
        _pipeline.OnCaptureStarted += () => OnCaptureStarted?.Invoke();
        _pipeline.OnConnected += () => OnConnected?.Invoke();

        await _pipeline.StartAsync(profile, ct);
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

        if (_pipeline is null)
            throw new InvalidOperationException("Pipeline is null after Start - should be unreachable");

        // The pipeline stops capture, drains every captured chunk to the
        // provider, then commits. It owns mic stop; we do not stop the mic
        // separately or we would race the drain.
        return await _pipeline.StopAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pipeline is not null) await _pipeline.DisposeAsync();
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
