using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Dictation.Providers;
using CcDirector.Core.Utilities;
using static CcDirector.Core.Configuration.TranscriptionTransport;

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
    private IDictationProvider? _provider;
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

        // CAPTURE FIRST - open the mic and start buffering BEFORE any network work
        // (the key-vault fetch, the dictionary fetch, and the OpenAI WebSocket
        // connect all happen afterwards). The mic, its UI meters, and the
        // pipeline's buffer are the only things capture needs; none of them
        // depend on the key or the connection. So the bars move and every byte is
        // captured from the very first frame, and the dialog can flip to RECORDING
        // the instant capture is live - the network round-trips that used to delay
        // the mic now overlap with live, buffered capture and lose nothing.
        _mic = new MicAudioCapture(_micDeviceNumber);
        _mic.OnAudioBands += bands => OnAudioBands?.Invoke(bands);
        _mic.OnInputRms += rms => OnInputRms?.Invoke(rms);

        _pipeline = new DictationPipeline(_mic);
        _pipeline.OnPartial += partial => OnPartial?.Invoke(partial);
        _pipeline.OnStateChanged += state => OnStateChanged?.Invoke(state);
        _pipeline.OnCaptureStarted += () => OnCaptureStarted?.Invoke();
        _pipeline.OnConnected += () => OnConnected?.Invoke();

        // The mic is live from here on. Anything the user says during the
        // resolve+connect below is buffered in order by the pipeline.
        _pipeline.StartCapture();

        try
        {
            // Resolve the full routing target for this session (mode, transport, key, model, base
            // URL) from the Gateway vault or local settings. No routing means dictation is
            // unavailable; surface a mode-appropriate message instead of a raw connect error.
            // Overlapped with live capture now so the mic is already buffering while we wait.
            var routing = await _keyResolver.ResolveEndpointAsync(ct);
            if (routing is null)
                throw new InvalidOperationException(_keyResolver.UnavailableMessage);

            // Pull the latest glossary from the Gateway when connected and refresh the local cache
            // file (#253); when standalone or the Gateway is unreachable this is a no-op and the
            // loader below reads the existing cache. Resolving per-session is the hot-reload path.
            await _dictionaryResolver.ResolveAsync(ct);
            var dictPath = _options.ResolveDictationDictionaryPath();
            _dictionary = new DictionaryLoader(dictPath, watch: false);

            FileLog.Write($"[SpeakService] routing: mode={routing.Mode.ToConfigString()} transport={routing.Transport.ToConfigString()} model={routing.Model} baseUrl={routing.BaseUrl}");

            // Route by transport (issue #513): realtime for BYO/OpenAI, batch for DevThrottle/Groq.
            // The batch path wraps the raw PCM mic audio in a WAV container before the upload
            // because Groq's Whisper API requires a properly-formed audio file. Cleanup and live
            // preview are only available on the realtime path; DevThrottle/batch skips both.
            LivePreviewTranscriber? preview;
            if (routing.Transport == Batch)
            {
                _provider = new OpenAiTranscriptionProvider(
                    apiKey: routing.ApiKey,
                    model: routing.Model,
                    audioContentType: "audio/wav",
                    audioFileName: "audio.wav",
                    baseUrl: routing.BaseUrl,
                    wrapPcmInWav: true,
                    pcmSampleRate: MicAudioCapture.SampleRate,
                    pcmChannels: MicAudioCapture.Channels,
                    pcmBitsPerSample: MicAudioCapture.BitsPerSample);
                _cleanup = null;
                preview = null;
            }
            else
            {
                _provider = new OpenAiRealtimeProvider(apiKey: routing.ApiKey, model: routing.Model);
                _cleanup = new CleanupOrchestrator(
                    apiKey: routing.ApiKey,
                    model: _options.DictationCleanupModel);
                // Live transcript preview (#215): re-transcribes the growing clip every few seconds
                // so the dialog shows the words while the user is still talking.
                preview = new LivePreviewTranscriber(
                    apiKey: routing.ApiKey,
                    model: _options.DictationPreviewModel);
            }

            _audioBuffer = new AudioBuffer(spillDirectory: ResolveBufferSpillDir());
            // The session owns and disposes both the provider and the preview.
            _session = new DictationSession(_dictionary, _provider, _cleanup, _audioBuffer, preview);

            // Connect (the slow part): the buffered audio captured during the
            // resolve+connect drains in capture order the moment the link is up.
            await _pipeline.ConnectAsync(_session, profile, ct);
        }
        catch
        {
            // Capture is already live. Tear the mic and everything built so far
            // down so a failed start never orphans the microphone. DisposeAsync
            // is idempotent and null-safe for the half-built state.
            await DisposeAsync();
            throw;
        }

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
