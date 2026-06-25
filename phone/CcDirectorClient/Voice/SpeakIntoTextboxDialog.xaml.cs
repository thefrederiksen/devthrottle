namespace CcDirectorClient.Voice;

/// <summary>What the user picked on the speak-into-textbox dialog.</summary>
public enum SpeakAction
{
    /// <summary>Throw the recording away; leave the text box untouched.</summary>
    Cancel,
    /// <summary>Drop the transcript into the text box, no auto-send.</summary>
    Insert,
    /// <summary>Drop the transcript into the text box AND auto-send.</summary>
    Send,
}

/// <summary>
/// Outcome of one speak-into-textbox dialog turn. <see cref="Text"/> is the final,
/// dictionary-corrected transcript (the accumulation of every checkpointed segment,
/// plus any hand edits the user made while paused). It is empty for
/// <see cref="SpeakAction.Cancel"/>.
/// </summary>
public sealed record SpeakDictationResult(SpeakAction Action, string Text);

/// <summary>
/// Speak-into-textbox dictation dialog (Mode B), the phone twin of the desktop
/// SpeakDialog and the web overlay - same canonical contract,
/// docs/architecture/dictation/DICTATION_UX_SPEC.md.
///
/// Whole-clip BATCH with a Pause checkpoint. No text appears while talking; text is
/// produced only at a checkpoint. Pause stops the mic, transcribes the current segment,
/// appends it to the transcript and shows it (editable) without ending the turn - Resume
/// is disabled until that transcription finishes, then it starts a fresh segment that
/// appends to the (possibly edited) text. Insert/Send transcribe the final segment, append
/// and commit; from Paused they commit the edited text in the box.
///
/// Transcription happens INSIDE the dialog via an injected delegate, so the dialog can
/// show and edit the transcript without knowing anything about the session/Director. The
/// delegate is the only thing the host wires in (it calls the Director's /voice/utterance
/// transcribe path). Always completes with a <see cref="SpeakDictationResult"/>.
/// </summary>
public partial class SpeakIntoTextboxDialog : ContentPage
{
    private static readonly TimeSpan TimerInterval = TimeSpan.FromMilliseconds(100);

    private static readonly Color RecordingColor = Color.FromArgb("#F44747");
    private static readonly Color AmberColor = Color.FromArgb("#DCDCAA");

    private enum Stage { Recording, Transcribing, Paused }

    private readonly IUtteranceRecorder _recorder;
    private readonly Func<UtteranceAudio, CancellationToken, Task<string>> _transcribe;
    private readonly TaskCompletionSource<SpeakDictationResult> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IDispatcherTimer? _timer;

    // _segmentStartUtc anchors the current recording segment; _elapsedBefore holds the
    // total capture time of all prior segments so the timer shows the running total.
    private DateTime _segmentStartUtc;
    private TimeSpan _elapsedBefore = TimeSpan.Zero;

    private Stage _stage = Stage.Recording;
    private bool _pauseEnabled;
    private bool _busy;       // guards the transcribe window against re-entrant taps
    private bool _resolved;

    // The accumulated, corrected transcript across every checkpointed segment so far.
    // Grows on each Pause / commit-from-recording; re-seeded from the editable box on Resume.
    private string _accumulatedText = "";

    public SpeakIntoTextboxDialog(IUtteranceRecorder recorder, Func<UtteranceAudio, CancellationToken, Task<string>> transcribe)
    {
        InitializeComponent();
        _recorder = recorder;
        _transcribe = transcribe ?? throw new ArgumentNullException(nameof(transcribe));
    }

    /// <summary>Completes with the user's chosen action plus the final transcript.</summary>
    public Task<SpeakDictationResult> Result => _tcs.Task;

    /// <summary>
    /// Convenience: push the dialog modally on <paramref name="navigation"/> and await the
    /// result. The dialog pops itself before the task completes. <paramref name="transcribe"/>
    /// turns one captured segment into corrected text (the host wires in the Director's
    /// transcribe call).
    /// </summary>
    public static async Task<SpeakDictationResult> PromptAsync(
        INavigation navigation, IUtteranceRecorder recorder,
        Func<UtteranceAudio, CancellationToken, Task<string>> transcribe)
    {
        var dialog = new SpeakIntoTextboxDialog(recorder, transcribe);
        await navigation.PushModalAsync(dialog, animated: false);
        return await dialog.Result;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            if (!_recorder.IsRecording)
                await _recorder.StartAsync();
            Bars.Start(_recorder);
            _segmentStartUtc = DateTime.UtcNow;
            _elapsedBefore = TimeSpan.Zero;
            TimerLabel.Text = "0:00";
            SwitchToRecording();
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimerInterval;
            _timer.Tick += OnTimerTick;
            _timer.Start();
            ClientLog.Write("[SpeakIntoTextboxDialog] mic on");
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[SpeakIntoTextboxDialog] StartAsync FAILED: {ex.Message}");
            await ResolveAsync(SpeakAction.Cancel, "");
        }
    }

    protected override void OnDisappearing()
    {
        StopTimer();
        Bars.Stop();
        base.OnDisappearing();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // Only the live recording segment advances the clock; it is frozen while
        // transcribing and paused. Shows the running total across all segments.
        if (_stage != Stage.Recording) return;
        var elapsed = _elapsedBefore + (DateTime.UtcNow - _segmentStartUtc);
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes >= 100) elapsed = TimeSpan.FromMinutes(99) + TimeSpan.FromSeconds(59);
        TimerLabel.Text = elapsed.ToString(@"m\:ss");
    }

    // ===== button handlers ===================================================

    private async void OnPauseTapped(object? sender, TappedEventArgs e)
    {
        if (_busy) return;
        if (_stage == Stage.Recording)
        {
            if (!_pauseEnabled) return;
            ClientLog.Write("[SpeakIntoTextboxDialog] PAUSE");
            await PauseAsync();
        }
        else if (_stage == Stage.Paused)
        {
            ClientLog.Write("[SpeakIntoTextboxDialog] RESUME");
            await ResumeAsync();
        }
    }

    private async void OnInsertClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        ClientLog.Write("[SpeakIntoTextboxDialog] INSERT");
        await CommitAsync(SpeakAction.Insert);
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        ClientLog.Write("[SpeakIntoTextboxDialog] SEND");
        await CommitAsync(SpeakAction.Send);
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        ClientLog.Write("[SpeakIntoTextboxDialog] CANCEL");
        try
        {
            if (_recorder.IsRecording)
                _ = await _recorder.StopAsync();
        }
        catch { /* discarding a half-captured clip */ }
        await ResolveAsync(SpeakAction.Cancel, "");
    }

    protected override bool OnBackButtonPressed()
    {
        // Hardware back / system back gesture is a Cancel so a back press never
        // leaves a hot mic.
        if (_resolved) return base.OnBackButtonPressed();
        OnCancelClicked(this, EventArgs.Empty);
        return true;
    }

    // ===== checkpoint flow ===================================================

    /// <summary>
    /// Pause: freeze the timer, transcribe the current segment, append it, and park in
    /// PAUSED (editable). On a transcription failure the accumulated text is kept and the
    /// reason is shown so the user can Resume to retry or commit what they have.
    /// </summary>
    private async Task PauseAsync()
    {
        _elapsedBefore += DateTime.UtcNow - _segmentStartUtc;
        SwitchToTranscribing();
        var segment = await TranscribeCurrentSegmentAsync();
        if (segment is not null)
            _accumulatedText = JoinText(_accumulatedText, segment);
        SwitchToPaused();
    }

    /// <summary>
    /// Resume: re-seed the accumulator from the (possibly edited) box so the user's edits
    /// survive, then start a fresh recording segment that appends onto it.
    /// </summary>
    private async Task ResumeAsync()
    {
        _accumulatedText = TranscriptEditor.Text ?? "";
        try
        {
            if (!_recorder.IsRecording)
                await _recorder.StartAsync();
            Bars.Start(_recorder);
            _segmentStartUtc = DateTime.UtcNow;
            SwitchToRecording();
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[SpeakIntoTextboxDialog] ResumeAsync FAILED: {ex.Message}");
            // Keep the accumulated text; fall back to Paused so the user can Send what they
            // have or try Resume again. The error must NOT go into the editor (that text is
            // what Send commits), so it goes in the hint row.
            SwitchToPaused();
            HintLabel.Text = "Could not resume - your text is kept. Try Resume again, or Send what you have.";
        }
    }

    /// <summary>
    /// Insert/Send. From Recording: transcribe the final segment, append, and commit. From
    /// Paused: commit the (edited) text in the box. A transcription failure parks in PAUSED
    /// with the reason rather than committing partial text.
    /// </summary>
    private async Task CommitAsync(SpeakAction action)
    {
        if (_stage == Stage.Recording)
        {
            _elapsedBefore += DateTime.UtcNow - _segmentStartUtc;
            SwitchToTranscribing();
            var segment = await TranscribeCurrentSegmentAsync();
            if (segment is null)
            {
                // Transcription failed; do not commit. Stay paused with what we have.
                SwitchToPaused();
                return;
            }
            _accumulatedText = JoinText(_accumulatedText, segment);
            await ResolveAsync(action, _accumulatedText);
        }
        else if (_stage == Stage.Paused)
        {
            await ResolveAsync(action, TranscriptEditor.Text ?? "");
        }
    }

    /// <summary>
    /// Stop the recorder and transcribe the just-captured segment. Returns the corrected
    /// text, or null when there was no audio or transcription failed (the reason is shown
    /// in the hint row). The recorder is consumed here, so a segment cannot be transcribed
    /// twice.
    /// </summary>
    private async Task<string?> TranscribeCurrentSegmentAsync()
    {
        UtteranceAudio? audio;
        try
        {
            audio = _recorder.IsRecording ? await _recorder.StopAsync() : null;
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[SpeakIntoTextboxDialog] StopAsync FAILED: {ex.Message}");
            HintLabel.Text = "Could not capture the recording.";
            return null;
        }

        if (audio is null || audio.Bytes is null || audio.Bytes.Length == 0)
        {
            HintLabel.Text = "No audio captured - speak, then pause.";
            return null;
        }

        try
        {
            var text = await _transcribe(audio, CancellationToken.None);
            return (text ?? "").Trim();
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[SpeakIntoTextboxDialog] transcribe FAILED: {ex.Message}");
            HintLabel.Text = "Transcription failed: " + ex.Message;
            return null;
        }
    }

    // ===== stage transitions =================================================

    private void SwitchToRecording()
    {
        _stage = Stage.Recording;
        _busy = false;
        StatusLabel.Text = "RECORDING";
        StatusLabel.TextColor = RecordingColor;
        // Empty + read-only while recording: no live preview. The placeholder explains why.
        TranscriptEditor.Text = "";
        TranscriptEditor.IsReadOnly = true;
        SetPauseGlyph(showResume: false);
        SetPauseEnabled(true);
        CancelButton.IsEnabled = true;
        InsertButton.IsEnabled = true;
        SendButton.IsEnabled = true;
        HintLabel.Text = "";
    }

    private void SwitchToTranscribing()
    {
        _stage = Stage.Transcribing;
        _busy = true;
        StatusLabel.Text = "TRANSCRIBING";
        StatusLabel.TextColor = AmberColor;
        Bars.Stop();
        TranscriptEditor.IsReadOnly = true;
        // Resume stays disabled here: "you cannot resume until it has been transcribed".
        SetPauseEnabled(false);
        InsertButton.IsEnabled = false;
        SendButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        HintLabel.Text = "Transcribing what you have said so far...";
    }

    private void SwitchToPaused()
    {
        _stage = Stage.Paused;
        _busy = false;
        StatusLabel.Text = "PAUSED";
        StatusLabel.TextColor = AmberColor;
        TranscriptEditor.Text = _accumulatedText;
        // Editable for review: let the user fix mis-heard words before committing/resuming.
        TranscriptEditor.IsReadOnly = false;
        SetPauseGlyph(showResume: true);
        SetPauseEnabled(true);
        InsertButton.IsEnabled = true;
        SendButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        if (string.IsNullOrEmpty(HintLabel.Text))
            HintLabel.Text = "";
    }

    private void SetPauseGlyph(bool showResume)
    {
        PauseGlyph.IsVisible = !showResume;
        ResumeLabel.IsVisible = showResume;
    }

    private void SetPauseEnabled(bool enabled)
    {
        _pauseEnabled = enabled;
        PauseButton.Opacity = enabled ? 1.0 : 0.55;
    }

    // ===== plumbing ==========================================================

    /// <summary>
    /// Join two transcript fragments with exactly one separating space, unless either side
    /// already supplies the boundary whitespace - so an edited transcript is extended, never
    /// rewritten. Mirrors the desktop DictationText.Join.
    /// </summary>
    private static string JoinText(string left, string right)
    {
        if (string.IsNullOrEmpty(left)) return right ?? "";
        if (string.IsNullOrEmpty(right)) return left;
        var leftEndsWithSpace = char.IsWhiteSpace(left[^1]);
        var rightStartsWithSpace = char.IsWhiteSpace(right[0]);
        if (leftEndsWithSpace || rightStartsWithSpace) return left + right;
        return left + " " + right;
    }

    private async Task ResolveAsync(SpeakAction action, string text)
    {
        if (_resolved) return;
        _resolved = true;
        StopTimer();
        try
        {
            if (_recorder.IsRecording)
                _ = await _recorder.StopAsync();
        }
        catch { /* best effort - we are leaving */ }
        _tcs.TrySetResult(new SpeakDictationResult(action, (text ?? "").Trim()));
        try { await Navigation.PopModalAsync(animated: false); }
        catch (Exception ex) { ClientLog.Write($"[SpeakIntoTextboxDialog] PopModalAsync FAILED: {ex.Message}"); }
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        _timer.Tick -= OnTimerTick;
        _timer.Stop();
        _timer = null;
    }
}
