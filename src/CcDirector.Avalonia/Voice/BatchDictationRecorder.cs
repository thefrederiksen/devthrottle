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
/// microphone audio locally for the whole turn and sends it to the Gateway transcription owner exactly
/// once, after the user stops. There is deliberately
/// NO realtime/streaming transcription and NO live partial preview: the realtime
/// model lightly rewords phrasing and the live-partials experience is exactly the
/// partial transcription the product removed, so desktop dictation now matches the
/// agreed whole-audio-batch flow.
///
/// Lifecycle (driven by <see cref="SpeakDialog"/>):
///
///   var rec = new BatchDictationRecorder(options, deviceNumber, deviceDescription);
///   rec.OnAudioBands     += bands => /* drive equalizer */ ;
///   rec.OnInputRms       += rms   => /* low-level hint */ ;
///   rec.OnCaptureStarted += ()    => /* driver asked to start (setup done) */ ;
///   rec.OnCaptureLive    += ()    => /* first real audio in: flip to RECORDING + ready cue */ ;
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
/// No browser, no WebSocket, no localhost hop, no realtime socket.
/// </summary>
public sealed class BatchDictationRecorder : IAsyncDisposable
{
    private readonly int _micDeviceNumber;

    // Builds the audio source for a device number. Production builds a NAudio
    // MicAudioCapture; tests inject a fake to drive the capture/stop sequencing
    // without a real microphone (the IAudioSource seam).
    private readonly Func<int, IAudioSource> _audioSourceFactory;

    // Test seam: replaces the post-snapshot transcription (WAV wrap, Gateway
    // transcription call, audit log) with a stub that receives the
    // snapshotted PCM. Null in production, where the real pipeline runs. Lets a test
    // assert exactly which captured bytes reach transcription - i.e. that the tail is
    // not clipped - without any network.
    private readonly Func<byte[], string, CancellationToken, Task<DictationResult>>? _transcribeOverride;

    private IAudioSource? _mic;

    // The whole-turn PCM16 accumulator. Every captured chunk is appended here in
    // capture order; nothing leaves the machine until TranscribeAsync wraps the
    // whole buffer in one WAV blob and sends it to the Gateway transcription endpoint.
    //
    // Its size is bounded by the recorder being DISPOSED when its owner is finished with it - see
    // DisposeAsync and the close handling in SpeakDialog. An earlier version of this change also
    // added a 30-minute hard ceiling here as a second line of defence. That ceiling was removed: it
    // needed its own microphone shutdown, which had to coordinate with the normal stop and with
    // disposal, and two review rounds found four separate defects in that coordination - including
    // silently discarding everything the user said past the limit. A safety net that has caused four
    // defects and prevented none is not making anything safer. Correct ownership is the fix.
    private readonly MemoryStream _audio = new();
    private readonly object _audioLock = new();

    private bool _started;
    private bool _stopped;
    private bool _disposed;

    // Elapsed recording time, reported on the captured audio for the capture-health line.
    private System.Diagnostics.Stopwatch? _recordingStopwatch;

    /// <summary>
    /// How long capture keeps running after Send, Insert or Pause before the microphone is asked to stop
    /// (issue #2927). The end of a word said on the click has not reached the capture buffer yet when the
    /// click lands, so stopping at once clipped it. The stop drain and the transcription run-out pad are
    /// unchanged; this covers the one case they cannot - sound that has not been captured yet.
    /// </summary>
    public const int StopTailMs = 250;

    // The ready cue, per recorder start (issue #2925). Guarded by _audioLock, because the cue is started on the
    // interface thread, its end is reported on the playback thread, and chunks append on the capture thread.
    //
    // The cue is FOUND in the captured audio at snapshot time, never placed by a clock (mission phase 6, ruling
    // K1): see ReadyCue in CcDirector.Core. Five inspections showed every clock - capture callbacks, a dropped
    // capture buffer, the playback report, NAudio's padded output buffer - has slack that either leaves cue or
    // erases words.
    //   _cuePlaying        - the dialog ASKED the output device to play the cue into this recording. It only
    //                        arms the wait below; it is not evidence that anything sounded.
    //   _cuePlayingAt      - when, so a stop waits for the playback report no longer than the cue's own allowance.
    //   _cueCompleted      - THE GATE (mission phase 7, ruling L1): playback reported successful completion, so
    //                        the whole cue is known to have sounded. Only then is the audio searched and a match
    //                        blanked. A cue that failed part-way, never started, or never reported leaves it
    //                        false and blanks nothing: a partial cue can still score over the threshold, and the
    //                        full-length blank would take the owner's first word (inspection six, findings 1, 2).
    //   _cueFailure        - why playback did not complete, when the player said so; logged at the snapshot.
    //   _firstAudioEndByte - the capture position when the first audio arrived, which is when the cue is
    //                        triggered; the search reaches ReadyCue.SearchAfterFirstAudioMs past it.
    //   _cueEndReported    - completes on either playback report, success or failure. For the wait only: a Send
    //                        inside the cue keeps capturing until it, so the whole cue is in the audio.
    private bool _cuePlaying;
    private long _cuePlayingAt;
    private bool _cueCompleted;
    private string? _cueFailure;
    private long? _firstAudioEndByte;
    private readonly TaskCompletionSource _cueEndReported = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Monotonic time source for the cue's wait allowance; injectable so tests move the clock instead of sleeping.
    private readonly TimeProvider _time;

    /// <summary>Fires for every captured chunk with a per-band (0..1) spectrum for the UI equalizer.</summary>
    public event Action<double[]>? OnAudioBands;

    /// <summary>Fires for every captured chunk with the raw int16 RMS amplitude, driving the "speak up" hint.</summary>
    public event Action<double>? OnInputRms;

    /// <summary>
    /// Fires the instant the microphone is asked to start capturing (right after
    /// <c>StartRecording</c> returns). This is the SETUP moment, before any audio has
    /// actually been delivered by the driver. There is no separate "connected" event
    /// because there is no network connect before capture - transcription happens once,
    /// after the user stops.
    /// </summary>
    public event Action? OnCaptureStarted;

    /// <summary>
    /// Fires exactly ONCE, when the first buffer of real audio actually lands in the
    /// capture buffer - the honest "the microphone is now hearing your voice" moment,
    /// as opposed to <see cref="OnCaptureStarted"/> which only means the driver was
    /// asked to start. The desktop dialog holds its "GETTING READY" state until this
    /// fires, then flips to RECORDING and plays the ready cue together, so neither the
    /// red state nor the sound ever claims the mic is live before audio is flowing.
    /// May fire on NAudio's worker thread, so a UI handler must marshal to the UI thread.
    /// </summary>
    public event Action? OnCaptureLive;

    // One-shot latch for OnCaptureLive: set the first time a non-empty chunk is
    // appended, so the "mic is live" signal is raised once per recorder, not per chunk.
    private bool _captureLiveRaised;

    // Start-latency clock (issue #2928), as a Stopwatch timestamp taken just before the microphone is
    // asked to start recording.
    private long _startRecordingAt;

    /// <summary>
    /// When the user asked for this recording (the click that opened, resumed or switched the microphone),
    /// as a <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> value. Set before <see cref="StartAsync"/>;
    /// when unset, the first-audio log line reports the click time as unknown rather than inventing one.
    /// </summary>
    public long? RequestedAtTimestamp { get; set; }

    /// <summary>
    /// Milliseconds from asking the microphone to start recording to its first audio, once that audio has
    /// arrived; null before. This is the device's own wake-up time - what the Speak dialog's selector shows.
    /// </summary>
    public int? StartToFirstAudioMs { get; private set; }

    /// <summary>The name of the device this recorder captures from, once started; null before.</summary>
    public string? DeviceDescription => _mic?.Description;

    /// <summary>
    /// Fires as transcription progresses with (completedParts, totalParts). A long recording is split
    /// into several bounded transcription requests; this lets the dialog show "transcribing part N of
    /// M" instead of a silent wait. A short clip reports a single part. May fire on a background
    /// thread, so a UI handler must marshal to the UI thread.
    /// </summary>
    public event Action<int, int>? OnTranscriptionProgress;

    // Interlocked disposal gate, so teardown runs exactly once even if two callers dispose
    // concurrently. _disposed stays for the ObjectDisposedException guards below.
    private int _disposedFlag;

    /// <param name="micDeviceNumber">WaveIn device number to capture from (<see cref="MicDevices.DefaultDeviceNumber"/>
    /// is the Windows default mic).</param>
    /// <param name="micDescription">That device's display name, resolved by the caller OFF the interface thread
    /// (issue #2929). Neither this constructor nor the capture it builds queries Windows for it.</param>
    public BatchDictationRecorder(AgentOptions options, int micDeviceNumber, string micDescription)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(micDescription))
            throw new ArgumentException("A resolved device description is required.", nameof(micDescription));
        _micDeviceNumber = micDeviceNumber;
        _audioSourceFactory = device => CreateMicrophone(device, micDescription);
        _transcribeOverride = null;
        _time = TimeProvider.System;
    }

    /// <summary>The production audio source: a NAudio capture carrying the already-resolved device name.</summary>
    internal static IAudioSource CreateMicrophone(int deviceNumber, string description)
        => new MicAudioCapture(deviceNumber, description);

    /// <summary>
    /// Test-only constructor (the IAudioSource seam). Injects the audio source so the
    /// capture-and-stop sequencing can be driven by a fake, and the transcription so
    /// the snapshotted PCM can be inspected, both without a real mic or the network.
    /// </summary>
    internal BatchDictationRecorder(
        AgentOptions options,
        Func<int, IAudioSource> audioSourceFactory,
        Func<byte[], string, CancellationToken, Task<DictationResult>> transcribeOverride,
        int micDeviceNumber = MicDevices.DefaultDeviceNumber,
        TimeProvider? timeProvider = null)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _micDeviceNumber = micDeviceNumber;
        _audioSourceFactory = audioSourceFactory ?? throw new ArgumentNullException(nameof(audioSourceFactory));
        _transcribeOverride = transcribeOverride ?? throw new ArgumentNullException(nameof(transcribeOverride));
        _time = timeProvider ?? TimeProvider.System;
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
            _startRecordingAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _mic.Start();
        }
        catch
        {
            // A failed start must never orphan the microphone. DisposeAsync is
            // idempotent and null-safe for this half-built state.
            await DisposeAsync();
            throw;
        }

        _recordingStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _started = true;

        // Capture is confirmed live. Let the UI anchor its timer and flip to RECORDING.
        OnCaptureStarted?.Invoke();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Tell the recorder the ready cue has been handed to the output device for this recording (issue #2925).
    /// This only arms the stop's wait for the playback report; it does NOT allow blanking (ruling L1) - asking a
    /// device to play is not the device playing. Only <see cref="NoteReadyCueFinished"/> does.
    /// </summary>
    public void NoteReadyCuePlaying()
    {
        lock (_audioLock)
        {
            _cuePlaying = true;
            _cuePlayingAt = _time.GetTimestamp();
        }
        FileLog.Write("[BatchDictationRecorder] ready cue playing");
    }

    /// <summary>
    /// Tell the recorder the output device reported the ready cue played to SUCCESSFUL COMPLETION (issue #2925).
    /// This is the gate (ruling L1): only a recorder told this searches its audio for the cue and blanks a match,
    /// because only now is the whole cue known to have sounded. It positions nothing (ruling K1) - the cue is found
    /// in the audio - and it releases a stop that landed while the cue was still playing. Only the first report,
    /// success or failure, counts. Safe on any thread; a no-op once the recorder is disposed.
    /// </summary>
    public void NoteReadyCueFinished()
    {
        lock (_audioLock)
        {
            if (_disposed || _cueEndReported.Task.IsCompleted) return;
            _cueCompleted = true;
        }
        _cueEndReported.TrySetResult();
        FileLog.Write("[BatchDictationRecorder] ready cue finished playing");
    }

    /// <summary>
    /// Tell the recorder the ready cue did NOT play to completion: an initialisation failure, a playback error
    /// part-way, or no end report within its allowance (ruling L1). Nothing will be blanked - part of a cue, or
    /// none, may be in the audio, and a match on it could erase the owner's words - and a stop waiting for the
    /// cue stops waiting. The reason is logged with the snapshot. Only the first report counts. Safe on any
    /// thread; a no-op once the recorder is disposed.
    /// </summary>
    public void NoteReadyCueFailed(string reason)
    {
        lock (_audioLock)
        {
            if (_disposed || _cueEndReported.Task.IsCompleted) return;
            _cueFailure = reason;
        }
        _cueEndReported.TrySetResult();
        FileLog.Write($"[BatchDictationRecorder] ready cue did not complete: {reason}");
    }

    /// <summary>
    /// Inspection two, finding 2: Send can land before the cue has finished playing. The search only finds a cue
    /// that is wholly in the clip, so when a cue was played and its end is still unreported, keep the microphone
    /// capturing and wait for the report - but no longer than the cue itself is allowed to report it
    /// (<see cref="DesktopAudioCue.PlaybackEndAllowance"/>, counted from when the cue started). Called BEFORE the
    /// microphone is stopped, so the rest of the cue is captured and the stop drain delivers its last buffer.
    /// The dialog has already moved on, so nobody waits on this but the background transcription.
    /// </summary>
    private async Task WaitForUnreportedCueEndAsync()
    {
        TimeSpan remaining;
        lock (_audioLock)
        {
            if (!_cuePlaying || _cueEndReported.Task.IsCompleted) return;
            remaining = DesktopAudioCue.PlaybackEndAllowance - _time.GetElapsedTime(_cuePlayingAt, _time.GetTimestamp());
        }
        if (remaining <= TimeSpan.Zero)
        {
            FileLog.Write("[BatchDictationRecorder] stopping with the ready cue's end unreported and its allowance spent; not waiting");
            return;
        }
        FileLog.Write($"[BatchDictationRecorder] stop requested before the ready cue finished playing; capturing up to "
            + $"{remaining.TotalMilliseconds:F0} ms more for it");
        var finished = await Task.WhenAny(_cueEndReported.Task, Task.Delay(remaining, _time));
        FileLog.Write(finished == _cueEndReported.Task
            ? "[BatchDictationRecorder] ready cue end arrived while waiting at stop"
            : "[BatchDictationRecorder] ready cue end did not arrive within its allowance; nothing will be blanked");
    }

    /// <summary>
    /// Stop the microphone, drain NAudio's final buffered audio, and return the whole captured clip as
    /// a WAV blob WITHOUT transcribing it. This is the durable-dictation split (issue #1130): the
    /// fire-and-forget Send saves these bytes to disk (<see cref="CcDirector.Core.Dictation.DictationRecordingStore"/>)
    /// before its single transcription attempt and keeps the file when transcription fails - so a
    /// failed or slow transcription can never lose the recording. Enforces the same
    /// completeness gate as <see cref="TranscribeAsync"/> (an empty capture throws
    /// <see cref="NoAudioCapturedException"/>), so an interrupted turn with no audio is never persisted.
    /// The recorder is consumed (stopped) here; call this OR <see cref="TranscribeAsync"/>, once.
    /// </summary>
    public async Task<CapturedAudio> StopAndGetWavAsync()
    {
        FileLog.Write("[BatchDictationRecorder] StopAndGetWavAsync");
        var (pcm, _, _) = await StopAndSnapshotAsync();
        // Header + samples + trailing run-out pad (dictation end-word fix) in ONE allocation. The old
        // pad-then-wrap chain made two extra full-size copies of the clip on the Large Object Heap; the
        // unpadded pcm length still stands for any bytes accounting - the pad is silence, not captured audio.
        var wav = WavWriter.WrapPcm16WithRunOut(pcm, MicAudioCapture.SampleRate, MicAudioCapture.Channels, MicAudioCapture.BitsPerSample);
        return new CapturedAudio(wav, _recordingStopwatch?.ElapsedMilliseconds ?? 0);
    }

    /// <summary>
    /// Stop the mic, WAIT for NAudio to flush its final buffered audio, then snapshot the whole-turn PCM.
    /// Shared by <see cref="TranscribeAsync"/> and <see cref="StopAndGetWavAsync"/> so both paths capture
    /// the identical bytes. WaveInEvent keeps capturing for up to one buffer after the stop and delivers
    /// the trailing words via AppendChunk on its worker thread, then raises RecordingStopped; StopAsync
    /// completes on that event, so the whole tail of speech is appended before the snapshot. Detaching
    /// the handler BEFORE the drain (the old order) discarded that tail and clipped the end of speech.
    /// Enforces the completeness gate (issue #586): an empty capture throws <see cref="NoAudioCapturedException"/>.
    /// </summary>
    private async Task<(byte[] Pcm, CaptureHealth? Health, string Device)> StopAndSnapshotAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BatchDictationRecorder));
        if (!_started) throw new InvalidOperationException("BatchDictationRecorder not started");
        if (_stopped) throw new InvalidOperationException("BatchDictationRecorder already stopped");
        _stopped = true;

        var device = _mic?.Description ?? MicDevices.DescribeDevice(_micDeviceNumber);
        CaptureHealth? captureHealth = null;
        if (_mic is not null)
        {
            // Keep capturing for the stop tail first (issue #2927), so the end of a word said on the
            // click is in the clip. The caller has already moved on - the dialog closes or shows
            // TRANSCRIBING at once - so nobody waits on this.
            FileLog.Write($"[BatchDictationRecorder] stop requested: capturing a {StopTailMs} ms tail before stopping the microphone");
            await Task.Delay(StopTailMs);
            // Send inside the cue: keep capturing until it has finished playing, so the search sees all of it.
            await WaitForUnreportedCueEndAsync();
            // The owner can close and dispose the recorder while the tail runs; a disposed recorder has
            // released its microphone and must not be stopped or snapshotted.
            if (_disposed) throw new ObjectDisposedException(nameof(BatchDictationRecorder));
            await _mic.StopAsync(TimeSpan.FromMilliseconds(750));
            // Read capture-health AFTER the drain (counters are final) and BEFORE detaching,
            // so the audit record can carry the per-recording diagnostics (issue #863).
            captureHealth = (_mic as IAudioCaptureDiagnostics)?.GetCaptureHealth();
            _mic.OnAudioChunk -= AppendChunk;
        }
        _recordingStopwatch?.Stop();

        byte[] pcm;
        string? cueNotCompleted;
        long? firstAudioEndByte;
        lock (_audioLock)
        {
            pcm = _audio.ToArray();
            cueNotCompleted = _cueCompleted ? null
                : !_cuePlaying ? "not played"
                : _cueFailure is not null ? $"playback did not complete ({_cueFailure})"
                : "playback never reported completion";
            firstAudioEndByte = _firstAudioEndByte;
        }

        BlankReadyCue(pcm, cueNotCompleted, firstAudioEndByte);

        // Completeness gate: an empty capture can never produce a real transcript.
        // Fail explicitly so an interrupted turn re-records rather than silently
        // returning empty text (issue #586). NoAudioCapturedException names the
        // device the user must check.
        if (pcm.Length == 0)
        {
            FileLog.Write("[BatchDictationRecorder] no audio captured; refusing (completeness gate)");
            throw new NoAudioCapturedException(device);
        }

        return (pcm, captureHealth, device);
    }

    /// <summary>
    /// Write digital silence over the ready cue in the snapshot that goes to transcription (issue #2925, ruling
    /// K1). Blanks only when playback REPORTED SUCCESSFUL COMPLETION of a cue on this recorder (ruling L1) AND
    /// <see cref="ReadyCue.Find"/> finds it in the audio; then exactly the found span (match start minus <see cref="ReadyCue.LeadInMs"/> to match end plus
    /// <see cref="ReadyCue.RingDownMs"/>) is zeroed. Not found - a quiet speaker, a headset, a device that does
    /// not hear its own output - blanks NOTHING: there is no clock-based position behind it, because blanking
    /// what cannot be shown to be the cue could delete the owner's words. Length is unchanged, and the
    /// capture-health counts were read from the unblanked capture. One log line per snapshot either way.
    /// </summary>
    private static void BlankReadyCue(byte[] pcm, string? cueNotCompleted, long? firstAudioEndByte)
    {
        if (cueNotCompleted is not null)
        {
            FileLog.Write($"[BatchDictationRecorder] ready cue: completed=no, {cueNotCompleted}; not searched, blanked nothing");
            return;
        }

        long bytesPerSecond = (long)MicAudioCapture.SampleRate * MicAudioCapture.Channels * (MicAudioCapture.BitsPerSample / 8);
        // The cue is played on first audio, so it cannot start before that position; the search covers the clip
        // from its start to SearchAfterFirstAudioMs past it.
        long searchEnd = (firstAudioEndByte ?? 0) + bytesPerSecond * ReadyCue.SearchAfterFirstAudioMs / 1000;
        var search = ReadyCue.Find(pcm, MicAudioCapture.SampleRate, searchEnd);
        double MsOf(long bytes) => bytes * 1000.0 / bytesPerSecond;
        var matchAt = search.MatchSample < 0 ? "nowhere, clip shorter than the cue" : $"{MsOf(search.MatchSample * 2L):F0} ms";

        if (!search.Found)
        {
            FileLog.Write($"[BatchDictationRecorder] ready cue: completed=yes, NOT found (best score {search.BestScore:F2} at {matchAt}, "
                + $"threshold {ReadyCue.FoundScore:F2}, searched {MsOf(search.SearchedSamples * 2L):F0} ms), blanked nothing");
            return;
        }

        var count = (int)(search.BlankEndByte - search.BlankStartByte);
        Array.Clear(pcm, (int)search.BlankStartByte, count);
        FileLog.Write($"[BatchDictationRecorder] ready cue: completed=yes, found (score {search.BestScore:F2} at {matchAt}), "
            + $"blanked {MsOf(search.BlankStartByte):F0}-{MsOf(search.BlankEndByte):F0} ms ({count} of {pcm.Length} bytes)");
    }

    /// <summary>
    /// Stop the microphone, then transcribe the whole captured clip exactly once
    /// through the Gateway transcription endpoint, applying the dictionary corrector
    /// only. Returns the raw transcript, the corrected
    /// transcript, and how many dictionary words were corrected.
    ///
    /// Enforces the completeness gate (issue #586): a turn that captured no audio
    /// fails loud with <see cref="NoAudioCapturedException"/> rather than producing
    /// a partial/empty transcript. Transcription failures throw so the caller
    /// surfaces them - a missing transcript is a real failure, not papered over.
    /// </summary>
    public async Task<DictationResult> TranscribeAsync(CancellationToken ct = default)
    {
        FileLog.Write("[BatchDictationRecorder] TranscribeAsync");
        var (pcm, captureHealth, device) = await StopAndSnapshotAsync();

        // Test seam: hand the snapshotted PCM to the injected stub instead of the real
        // network pipeline. The empty-audio gate above still runs first, so the stub
        // only ever sees a non-empty capture - exactly what the real path transcribes.
        if (_transcribeOverride is not null)
            return await _transcribeOverride(pcm, device, ct);

        // Wrap the whole captured PCM in one WAV blob and transcribe ONCE through the
        // Gateway transcription endpoint. The dictionary corrector is the only text transform. The
        // trailing-silence run-out (dictation end-word fix) is padded onto the transcription WAV only;
        // pcm.Length below stays the honest captured-byte count for the audit record. Header, samples
        // and pad are written in ONE allocation - the old pad-then-wrap chain made two extra full-size
        // copies of the clip on the Large Object Heap.
        var wav = WavWriter.WrapPcm16WithRunOut(pcm, MicAudioCapture.SampleRate, MicAudioCapture.Channels, MicAudioCapture.BitsPerSample);

        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        var gateway = await new GatewayTranscriptionClient().TranscribeAsync(
            wav, "dictation.wav", "audio/wav", applyCorrection: true, ct);
        stopWatch.Stop();

        FileLog.Write($"[BatchDictationRecorder] transcribed via Gateway: len={gateway.Text.Length}, "
            + $"mode={gateway.Mode}, model={gateway.Model}");

        // Capture-health line (issue #863): a byte deficit paired with large callback GAPS
        // (and small handler self-time) points upstream - the audio was under-delivered
        // before we saw it (e.g. Remote Desktop audio redirection); a deficit paired with
        // large handler self-time points at a local capture-thread stall. This is what tells
        // the two apart so any future fix is aimed at the real cause.
        if (captureHealth is { } ch)
            FileLog.Write($"[BatchDictationRecorder] capture-health: capturedBytes={ch.CapturedBytes}, "
                + $"expectedBytes={ch.ExpectedBytes}, deficit={ch.DeficitFraction:P1}, callbacks={ch.CallbackCount}, "
                + $"maxGapMs={ch.MaxCallbackGapMs:F0}, longGaps={ch.LongGapCount}, maxHandlerMs={ch.MaxHandlerMs:F1}, "
                + $"buffers={ch.NumberOfBuffers}x{ch.BufferMilliseconds}ms");

        // The transcript itself is stored per-tenant on the Gateway (issue #509): this desktop path
        // transcribes THROUGH the Gateway /transcription endpoint above, so its raw and cleaned text already
        // lands in the caller tenant's dictation_transcripts partition via the transcription service. The old
        // host-global dictation/sessions/*.jsonl flat log this used to also write is retired (it had no
        // readers); the capture-health deficit stays a FileLog line above.

        return new DictationResult(
            RawTranscript: gateway.Text,
            CleanedTranscript: gateway.Text,
            DictionaryWordsCorrected: 0);
    }

    /// <summary>Append one captured PCM16 chunk to the whole-turn buffer. Runs on NAudio's thread.</summary>
    private void AppendChunk(byte[] chunk)
    {
        if (chunk.Length == 0) return;
        lock (_audioLock)
        {
            _audio.Write(chunk, 0, chunk.Length);
            // The first buffer is capture live, which is when the dialog plays the cue: the cue cannot start
            // before this position (issue #2925).
            _firstAudioEndByte ??= _audio.Length;
        }

        // The first non-empty chunk is the first real audio the driver delivered:
        // the honest "microphone is now capturing your voice" moment. Raise it once,
        // OUTSIDE the audio lock so a UI handler can never stall capture. AppendChunk
        // runs on NAudio's single capture thread, so the latch needs no synchronization.
        if (!_captureLiveRaised)
        {
            _captureLiveRaised = true;
            LogFirstAudio();
            OnCaptureLive?.Invoke();
        }
    }

    /// <summary>
    /// One line per start (issue #2928): which device, how long from the click to its first audio, and how
    /// long from asking it to start recording. Start latency used to have to be reconstructed from
    /// capture-health byte counts; a slow microphone is now visible in the log on its own.
    /// </summary>
    private void LogFirstAudio()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var startMs = (int)Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(_startRecordingAt, now).TotalMilliseconds);
        StartToFirstAudioMs = startMs;
        var clickMs = RequestedAtTimestamp is { } requested
            ? ((int)Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(requested, now).TotalMilliseconds)).ToString()
            : "unknown";
        FileLog.Write($"[BatchDictationRecorder] first audio: device=\"{_mic?.Description}\", "
            + $"clickToFirstAudioMs={clickMs}, startRecordingToFirstAudioMs={startMs}");
    }

    /// <summary>
    /// The per-start line for a start that NEVER delivered audio (inspection one, finding 3): the dialog's
    /// ready window ran out first. Same shape as the first-audio line, so a slow or dead microphone is visible
    /// in the log by device name and timings, not only as a generic timeout. A no-op once audio has arrived -
    /// that start already has its first-audio line.
    /// </summary>
    public void LogNoAudioWithinReadyWindow()
    {
        if (_captureLiveRaised) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var clickMs = RequestedAtTimestamp is { } requested
            ? ((int)Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(requested, now).TotalMilliseconds)).ToString()
            : "unknown";
        var startMs = _started
            ? ((int)Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(_startRecordingAt, now).TotalMilliseconds)).ToString()
            : "unknown";
        FileLog.Write($"[BatchDictationRecorder] no first audio: device=\"{_mic?.Description}\", "
            + $"clickToTimeoutMs={clickMs}, startRecordingToTimeoutMs={startMs}");
    }

    // Named so they can be unsubscribed in DisposeAsync; forward the source's UI-meter
    // events to this recorder's own events for the dialog's equalizer and level hint.
    private void RaiseAudioBands(double[] bands) => OnAudioBands?.Invoke(bands);
    private void RaiseInputRms(double rms) => OnInputRms?.Invoke(rms);

    public async ValueTask DisposeAsync()
    {
        // Atomic, so two concurrent disposals cannot both pass the check and decrement the live
        // count twice. That count is the leak oracle a test asserts on, so it has to be exact.
        if (Interlocked.Exchange(ref _disposedFlag, 1) == 1) return;
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
/// The whole captured dictation clip as an uploadable WAV blob plus how long it was recorded
/// (issue #1130). Returned by <see cref="BatchDictationRecorder.StopAndGetWavAsync"/> so the durable
/// fire-and-forget Send can persist the bytes to disk before transcribing them.
/// </summary>
public sealed record CapturedAudio(byte[] Wav, long RecordingMs);

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

    /// <summary>
    /// Wrap the clip AND append the transcription run-out pad in a single allocation. Byte-identical
    /// to the old <c>Wrap(WithTrailingSilence(pcm))</c> chain, without the two extra full-size copies
    /// that chain put on the Large Object Heap for a long recording.
    /// </summary>
    public static byte[] WrapPcm16WithRunOut(byte[]? pcm, int sampleRate, int channels, int bitsPerSample)
    {
        if (pcm is null) throw new ArgumentNullException(nameof(pcm));
        return PcmWav.WrapWithTrailingSilence(pcm, sampleRate, channels, bitsPerSample, PcmWav.TrailingSilenceMs);
    }
}
