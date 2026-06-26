using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Whole-audio dictation recorder for the desktop Speak dialog (issue #589).
///
/// This is the migrated, BATCH-ONLY dictation path. It captures EVERY byte of
/// microphone audio locally for the whole turn and transcribes it exactly once,
/// after the user stops, through the ONE shared
/// <see cref="BatchTranscriptionPipeline"/> (issue #587). There is deliberately
/// NO realtime/streaming transcription and NO live partial preview: the realtime
/// model lightly rewords phrasing and the live-partials experience is exactly the
/// partial transcription the product removed, so desktop dictation now matches the
/// agreed whole-audio-batch flow.
///
/// Lifecycle (driven by <see cref="SpeakDialog"/>):
///
///   var rec = new BatchDictationRecorder(options);
///   rec.OnAudioBands     += bands => /* drive equalizer */ ;
///   rec.OnInputRms       += rms   => /* low-level hint */ ;
///   rec.OnCaptureStarted += ()    => /* flip the UI to RECORDING */ ;
///   await rec.StartAsync();   // mic opens, audio buffers locally
///   // user talks (no text appears - no live preview)
///   var result = await rec.TranscribeAsync();  // ONE batch transcription call
///   // result.CleanedTranscript is what we hand back to the prompt input
///
/// The completeness gate (issue #586) is enforced here as "whole audio in": an
/// empty capture (an interrupted turn that produced no audio) fails loud with
/// <see cref="NoAudioCapturedException"/> rather than transcribing partial input,
/// and the shared pipeline itself refuses an empty blob. The only
/// post-transcription transform is the validated dictionary corrector, so a turn
/// with no dictionary term comes back byte-identical to the raw transcription.
///
/// No browser, no WebSocket, no localhost hop, no realtime socket. The shared C#
/// pipeline runs in the same process as the Avalonia UI. This is deliberately a
/// SEPARATE class from <see cref="SpeakService"/> (which still serves the
/// continuous-listening wake-word and voice-command surfaces); dictation no longer
/// shares the streaming orchestrator.
/// </summary>
public sealed class BatchDictationRecorder : IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly OpenAiKeyResolver _keyResolver;
    private readonly DictionaryResolver _dictionaryResolver;
    private readonly int _micDeviceNumber;

    // Builds the audio source for a device number. Production builds a NAudio
    // MicAudioCapture; tests inject a fake to drive the capture/stop sequencing
    // without a real microphone (the IAudioSource seam).
    private readonly Func<int, IAudioSource> _audioSourceFactory;

    // Test seam: replaces the post-snapshot transcription (key + dictionary resolve,
    // WAV wrap, shared batch pipeline, audit log) with a stub that receives the
    // snapshotted PCM. Null in production, where the real pipeline runs. Lets a test
    // assert exactly which captured bytes reach transcription - i.e. that the tail is
    // not clipped - without any network.
    private readonly Func<byte[], string, CancellationToken, Task<DictationResult>>? _transcribeOverride;

    private IAudioSource? _mic;

    // The whole-turn PCM16 accumulator. Every captured chunk is appended here in
    // capture order; nothing leaves the machine until TranscribeAsync wraps the
    // whole buffer in one WAV blob and sends it through the shared batch pipeline.
    private readonly MemoryStream _audio = new();
    private readonly object _audioLock = new();

    private bool _started;
    private bool _stopped;
    private bool _disposed;

    // Session-record state so the desktop path leaves the same JSONL audit trail
    // as the other dictation surfaces (issue #190).
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private string _profile = "default";
    private DateTime _sessionStartUtc;
    private System.Diagnostics.Stopwatch? _recordingStopwatch;

    /// <summary>Fires for every captured chunk with a per-band (0..1) spectrum for the UI equalizer.</summary>
    public event Action<double[]>? OnAudioBands;

    /// <summary>Fires for every captured chunk with the raw int16 RMS amplitude, driving the "speak up" hint.</summary>
    public event Action<double>? OnInputRms;

    /// <summary>
    /// Fires the instant the microphone actually starts capturing. The UI anchors
    /// its recording timer to this and flips to the RECORDING state. There is no
    /// separate "connected" event because there is no network connect before
    /// capture - transcription happens once, after the user stops.
    /// </summary>
    public event Action? OnCaptureStarted;

    /// <param name="micDeviceNumber">
    /// WaveIn device number to capture from. Defaults to
    /// <see cref="MicDevices.DefaultDeviceNumber"/> (the Windows default mic).
    /// </param>
    public BatchDictationRecorder(AgentOptions options, int micDeviceNumber = MicDevices.DefaultDeviceNumber)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // Resolve the OpenAI key + transcription method by mode: Gateway vault when attached to a
        // Gateway, the local Settings > Voice key when standalone. The user-selected method (base
        // URL, key, model) governs every transcription path - there is no fixed realtime model.
        _keyResolver = new OpenAiKeyResolver(options);
        // Resolve the dictation dictionary the same way: the Gateway's shared glossary when
        // attached, the local cache when standalone (#253). A Cockpit edit reaches this Director.
        _dictionaryResolver = new DictionaryResolver(options);
        _micDeviceNumber = micDeviceNumber;
        _audioSourceFactory = static device => new MicAudioCapture(device);
        _transcribeOverride = null;
    }

    /// <summary>
    /// Test-only constructor (the IAudioSource seam). Injects the audio source so the
    /// capture-and-stop sequencing can be driven by a fake, and the transcription so
    /// the snapshotted PCM can be inspected, both without a real mic or the network.
    /// </summary>
    internal BatchDictationRecorder(
        AgentOptions options,
        Func<int, IAudioSource> audioSourceFactory,
        Func<byte[], string, CancellationToken, Task<DictationResult>> transcribeOverride,
        int micDeviceNumber = MicDevices.DefaultDeviceNumber)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _keyResolver = new OpenAiKeyResolver(options);
        _dictionaryResolver = new DictionaryResolver(options);
        _micDeviceNumber = micDeviceNumber;
        _audioSourceFactory = audioSourceFactory ?? throw new ArgumentNullException(nameof(audioSourceFactory));
        _transcribeOverride = transcribeOverride ?? throw new ArgumentNullException(nameof(transcribeOverride));
    }

    /// <summary>
    /// Open the microphone and start buffering audio locally. Returns once capture
    /// is live. No transcription happens here - the whole clip is transcribed once
    /// on <see cref="TranscribeAsync"/>.
    /// </summary>
    public async Task StartAsync(string profile = "default", CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BatchDictationRecorder));
        if (_started) throw new InvalidOperationException("BatchDictationRecorder already started");
        FileLog.Write($"[BatchDictationRecorder] StartAsync: profile={profile}, device={_micDeviceNumber}");

        // CAPTURE FIRST - open the mic and start buffering every byte locally. There
        // is no network work before capture: the method, key, and dictionary are
        // resolved later, at TranscribeAsync, for the single batch transcription. So
        // the bars move and audio is captured from the very first frame and the
        // dialog can flip to RECORDING the instant capture is live.
        _mic = _audioSourceFactory(_micDeviceNumber);
        _mic.OnAudioChunk += AppendChunk;
        // Equalizer + level hint are optional UI cosmetics: wire them only when the
        // source actually emits them (the real mic does; a headless test source need not).
        if (_mic is IAudioMeterSource meter)
        {
            meter.OnAudioBands += RaiseAudioBands;
            meter.OnInputRms += RaiseInputRms;
        }

        try
        {
            _mic.Start();
        }
        catch
        {
            // A failed start must never orphan the microphone. DisposeAsync is
            // idempotent and null-safe for this half-built state.
            await DisposeAsync();
            throw;
        }

        _profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
        _sessionStartUtc = DateTime.UtcNow;
        _recordingStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _started = true;

        // Capture is confirmed live. Let the UI anchor its timer and flip to RECORDING.
        OnCaptureStarted?.Invoke();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Stop the microphone, then transcribe the whole captured clip exactly once
    /// through the shared batch pipeline (the user-selected method), applying the
    /// dictionary corrector only. Returns the raw transcript, the corrected
    /// transcript, and how many dictionary words were corrected.
    ///
    /// Enforces the completeness gate (issue #586): a turn that captured no audio
    /// fails loud with <see cref="NoAudioCapturedException"/> rather than producing
    /// a partial/empty transcript. Transcription failures throw so the caller
    /// surfaces them - a missing transcript is a real failure, not papered over.
    /// </summary>
    public async Task<DictationResult> TranscribeAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BatchDictationRecorder));
        if (!_started) throw new InvalidOperationException("BatchDictationRecorder not started");
        if (_stopped) throw new InvalidOperationException("BatchDictationRecorder already stopped");
        _stopped = true;

        FileLog.Write("[BatchDictationRecorder] TranscribeAsync");

        // Stop the mic and WAIT for NAudio to flush its final buffered audio before
        // snapshotting. WaveInEvent keeps capturing for up to one buffer after the
        // stop and delivers the trailing words via AppendChunk on its worker thread,
        // then raises RecordingStopped; StopAsync completes on that event, so the
        // whole tail of speech is appended to _audio before the snapshot below.
        // Detaching the handler BEFORE the drain (the old order) discarded that tail
        // and clipped the end of the user's speech. Only after the drain do we detach,
        // so no genuinely-late chunk can race the snapshot; the lock guards it anyway.
        var device = _mic?.Description ?? MicDevices.DescribeDevice(_micDeviceNumber);
        if (_mic is not null)
        {
            await _mic.StopAsync(TimeSpan.FromMilliseconds(750));
            _mic.OnAudioChunk -= AppendChunk;
        }
        _recordingStopwatch?.Stop();

        byte[] pcm;
        lock (_audioLock)
        {
            pcm = _audio.ToArray();
        }

        // Completeness gate: an empty capture can never produce a real transcript.
        // Fail explicitly so an interrupted turn re-records rather than silently
        // returning empty text (issue #586). NoAudioCapturedException names the
        // device the user must check.
        if (pcm.Length == 0)
        {
            FileLog.Write("[BatchDictationRecorder] TranscribeAsync: no audio captured; refusing to transcribe (completeness gate)");
            throw new NoAudioCapturedException(device);
        }

        // Test seam: hand the snapshotted PCM to the injected stub instead of the real
        // network pipeline. The empty-audio gate above still runs first, so the stub
        // only ever sees a non-empty capture - exactly what the real path transcribes.
        if (_transcribeOverride is not null)
            return await _transcribeOverride(pcm, device, ct);

        // Resolve the user-selected transcription method (base URL, key, model). No
        // routing means dictation is unavailable; surface the mode-appropriate message.
        var routing = await _keyResolver.ResolveEndpointAsync(ct);
        if (routing is null)
            throw new InvalidOperationException(_keyResolver.UnavailableMessage);

        // Pull the latest glossary from the Gateway when connected (refreshing the
        // local cache as a side effect, #253); falls back to the local cache offline.
        var dictionary = await _dictionaryResolver.ResolveAsync(ct);

        // Wrap the whole captured PCM in one WAV blob and transcribe ONCE through the
        // shared batch pipeline. The dictionary corrector is the only text transform.
        var wav = WavWriter.WrapPcm16(
            pcm, MicAudioCapture.SampleRate, MicAudioCapture.Channels, MicAudioCapture.BitsPerSample);

        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        using var pipeline = new BatchTranscriptionPipeline(cleanupModel: _options.DictationCleanupModel);
        var batch = await pipeline.TranscribeAsync(wav, "dictation.wav", routing, dictionary, _profile, ct);
        stopWatch.Stop();

        FileLog.Write($"[BatchDictationRecorder] transcribed: rawLen={batch.RawTranscript.Length}, "
            + $"correctedLen={batch.CorrectedTranscript.Length}, dictionaryApplied={batch.DictionaryApplied}, "
            + $"changed={batch.ChangedWords.Count}, method={routing.Mode.ToConfigString()}, model={routing.Model}");

        // Same JSONL audit record the other dictation surfaces write so a desktop
        // dictation incident keeps its raw text for forensics. Fire-and-forget off
        // the UI-facing path; failures are logged inside TryAppend.
        var record = new DictationSessionRecord(
            TimestampUtc: _sessionStartUtc.ToString("o"),
            SessionId: _sessionId,
            Profile: _profile,
            VocabularyTermCount: dictionary.Vocabulary.Count,
            MistranscriptionPatternCount: dictionary.CommonMistranscriptions.Count,
            RecordingDurationMs: _recordingStopwatch?.ElapsedMilliseconds ?? 0,
            StopToTranscribedMs: stopWatch.ElapsedMilliseconds,
            StopToCleanedMs: stopWatch.ElapsedMilliseconds,
            AudioBytesReceived: pcm.Length,
            RawTranscript: batch.RawTranscript,
            CleanedTranscript: batch.CorrectedTranscript,
            CleanupApplied: batch.DictionaryApplied,
            CleanupReason: batch.Reason,
            CleanupModel: _options.DictationCleanupModel,
            RemoteIp: null,
            ClientError: null,
            Source: "desktop-speak");
        _ = Task.Run(() => DictationSessionLog.TryAppend(record));

        return new DictationResult(
            RawTranscript: batch.RawTranscript,
            CleanedTranscript: batch.CorrectedTranscript,
            DictionaryWordsCorrected: batch.ChangedWords.Count);
    }

    /// <summary>Append one captured PCM16 chunk to the whole-turn buffer. Runs on NAudio's thread.</summary>
    private void AppendChunk(byte[] chunk)
    {
        if (chunk.Length == 0) return;
        lock (_audioLock)
        {
            _audio.Write(chunk, 0, chunk.Length);
        }
    }

    // Named so they can be unsubscribed in DisposeAsync; forward the source's UI-meter
    // events to this recorder's own events for the dialog's equalizer and level hint.
    private void RaiseAudioBands(double[] bands) => OnAudioBands?.Invoke(bands);
    private void RaiseInputRms(double rms) => OnInputRms?.Invoke(rms);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_mic is not null)
        {
            _mic.OnAudioChunk -= AppendChunk;
            if (_mic is IAudioMeterSource meter)
            {
                meter.OnAudioBands -= RaiseAudioBands;
                meter.OnInputRms -= RaiseInputRms;
            }
            // Stop discards any undrained tail - fine here: Dispose is the cancel/teardown
            // path. The no-loss drain happens in TranscribeAsync via StopAsync. IAudioSource
            // is not itself IDisposable, so release the concrete resource when it is.
            _mic.Stop();
            if (_mic is IDisposable disposable)
                disposable.Dispose();
        }
        _audio.Dispose();
        await ValueTask.CompletedTask;
    }
}

/// <summary>
/// The result of one whole-audio desktop dictation turn (issue #589): the raw
/// transcript, the dictionary-corrected transcript, and how many dictionary words
/// were corrected. <see cref="CleanedTranscript"/> equals <see cref="RawTranscript"/>
/// byte-for-byte whenever no dictionary term matched.
/// </summary>
public sealed record DictationResult(string RawTranscript, string CleanedTranscript, int DictionaryWordsCorrected);

/// <summary>
/// Minimal RIFF/WAV container writer for raw PCM16. The desktop mic delivers raw
/// PCM that the transcription API cannot accept without a header, so the whole
/// captured clip is wrapped before the single batch upload. Delegates to the
/// shared <see cref="PcmWav"/> so the byte layout lives in exactly one place.
/// </summary>
internal static class WavWriter
{
    public static byte[] WrapPcm16(byte[]? pcm, int sampleRate, int channels, int bitsPerSample)
    {
        if (pcm is null) throw new ArgumentNullException(nameof(pcm));
        return PcmWav.Wrap(pcm, sampleRate, channels, bitsPerSample);
    }
}
