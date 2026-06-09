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
    private readonly OpenAiKeyResolver _keyResolver;
    private readonly DictionaryResolver _dictionaryResolver;
    private readonly int _micDeviceNumber;

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

    // Session-record state so the desktop path leaves the same JSONL audit
    // trail as the /dictate endpoint (issue #190: the 2026-06-06 incidents
    // went through this path and their raw transcripts were lost because
    // nothing here logged them).
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private string _profile = "default";
    private DateTime _sessionStartUtc;
    private System.Diagnostics.Stopwatch? _recordingStopwatch;

    public event Action<double[]>? OnAudioBands;
    public event Action<double>? OnInputRms;
    public event Action<string>? OnPartial;
    public event Action<ConnectionState>? OnStateChanged;

    /// <summary>Fires the instant the microphone starts capturing, before the transcription connection completes. The UI anchors its recording timer to this so the displayed time tracks real capture, not the pre-connect setup.</summary>
    public event Action? OnCaptureStarted;

    /// <summary>Fires once the transcription backend is connected and buffered audio is streaming.</summary>
    public event Action? OnConnected;

    /// <param name="micDeviceNumber">
    /// WaveIn device number to capture from. Defaults to
    /// <see cref="MicDevices.DefaultDeviceNumber"/> (the Windows default mic).
    /// </param>
    public SpeakService(AgentOptions options, int micDeviceNumber = MicDevices.DefaultDeviceNumber)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // Resolve the OpenAI key by mode: Gateway vault when attached to a Gateway, the local
        // Settings > Voice key when standalone (docs/architecture/gateway/GATEWAY_KEY_VAULT.md).
        _keyResolver = new OpenAiKeyResolver(options);
        // Resolve the dictation dictionary the same way: the Gateway's shared glossary when
        // attached, the local cache when standalone (#253). A Cockpit edit reaches this Director.
        _dictionaryResolver = new DictionaryResolver(options);
        _micDeviceNumber = micDeviceNumber;
    }

    public async Task StartAsync(string profile = "default", CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpeakService));
        if (_started) throw new InvalidOperationException("SpeakService already started");
        FileLog.Write($"[SpeakService] StartAsync: profile={profile}");

        // Resolve the key once for this session (Gateway vault or local key, per mode). No key
        // means dictation is unavailable; surface where to set one instead of a raw connect error.
        var apiKey = await _keyResolver.ResolveAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(_keyResolver.UnavailableMessage);

        // Pull the latest glossary from the Gateway when connected and refresh the local cache
        // file (#253); when standalone or the Gateway is unreachable this is a no-op and the
        // loader below reads the existing cache. Resolving per-session is the hot-reload path.
        await _dictionaryResolver.ResolveAsync(ct);
        var dictPath = _options.ResolveDictationDictionaryPath();
        _dictionary = new DictionaryLoader(dictPath, watch: false);

        _provider = new OpenAiRealtimeProvider(apiKey: apiKey);
        _cleanup = new CleanupOrchestrator(
            apiKey: apiKey,
            model: _options.DictationCleanupModel);
        _audioBuffer = new AudioBuffer(spillDirectory: ResolveBufferSpillDir());
        // Live transcript preview (#215): re-transcribes the growing clip
        // every few seconds so the dialog shows the words while the user is
        // still talking. The session owns and disposes it.
        var preview = new LivePreviewTranscriber(
            apiKey: apiKey,
            model: _options.DictationPreviewModel);
        _session = new DictationSession(_dictionary, _provider, _cleanup, _audioBuffer, preview);

        // Build the mic and wire the UI meters BEFORE handing it to the pipeline.
        // The equalizer/level meters are driven straight off NAudio and do not
        // depend on the transcription connection, so the bars move from the
        // first captured frame - honest visual confirmation that we are
        // recording even while the backend is still connecting.
        _mic = new MicAudioCapture(_micDeviceNumber);
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
        _profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
        _sessionStartUtc = DateTime.UtcNow;
        _recordingStopwatch = System.Diagnostics.Stopwatch.StartNew();
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
        _recordingStopwatch?.Stop();
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _pipeline.StopAsync(ct);
        stopWatch.Stop();

        // Same JSONL audit record the /dictate endpoint writes, so a desktop
        // dictation incident keeps its raw text for forensics. Fire-and-forget
        // off the UI-facing path; failures are logged inside TryAppend.
        if (_dictionary is null)
            throw new InvalidOperationException("Dictionary is null after Start - should be unreachable");
        var dict = _dictionary.Current;
        var record = new DictationSessionRecord(
            TimestampUtc: _sessionStartUtc.ToString("o"),
            SessionId: _sessionId,
            Profile: _profile,
            VocabularyTermCount: dict.Vocabulary.Count,
            MistranscriptionPatternCount: dict.CommonMistranscriptions.Count,
            RecordingDurationMs: _recordingStopwatch?.ElapsedMilliseconds ?? 0,
            StopToTranscribedMs: stopWatch.ElapsedMilliseconds,
            StopToCleanedMs: stopWatch.ElapsedMilliseconds,
            AudioBytesReceived: 0, // mic bytes are not tracked on this path
            RawTranscript: result.RawTranscript,
            CleanedTranscript: result.CleanedTranscript,
            CleanupApplied: result.CleanupApplied,
            CleanupReason: result.CleanupFailureReason,
            CleanupModel: _options.DictationCleanupModel,
            RemoteIp: null,
            ClientError: null,
            Source: "desktop-speak");
        _ = Task.Run(() => DictationSessionLog.TryAppend(record));

        return result;
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
