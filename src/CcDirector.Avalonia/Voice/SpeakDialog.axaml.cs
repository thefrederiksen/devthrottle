using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Modal dialog for desktop dictation. Whole-audio batch (issue #589) with a
/// Pause checkpoint restored on top of it. See the canonical contract in
/// docs/architecture/dictation/DICTATION_UX_SPEC.md.
///
/// The microphone captures the whole segment locally; NO text appears while the
/// user is speaking (there is no live partial preview and no realtime streaming
/// socket). Text is produced only at a CHECKPOINT - Pause, Insert, or Send - by
/// sending the whole captured segment ONCE through the shared
/// <see cref="BatchDictationRecorder"/> batch pipeline, which transcribes via the
/// user-selected method and applies the dictionary corrector only.
///
/// Pause is a checkpoint, not an ending: it transcribes the current segment,
/// appends it to the accumulated transcript, and shows it (editable) without
/// ending the turn. Resume is disabled while that transcription runs ("you cannot
/// resume until it has been transcribed"); afterwards Resume starts a fresh
/// segment that appends to the (possibly edited) text. The visible transcript is
/// the accumulation of every transcribed segment.
///
/// Commit actions:
///
///   Cancel - close, no text. An interrupted/cancelled turn produces no transcript.
///   Insert - transcribe the current segment (if recording), append, close with
///            <see cref="ResultText"/> populated and <see cref="ShouldSubmit"/>
///            FALSE so the caller inserts the text at the caret without auto-submit.
///   Send   - same as Insert but closes with <see cref="ShouldSubmit"/> TRUE so the
///            caller auto-submits the prompt.
///   Pause  - transcribe the current segment, append, stay in the dialog showing
///            PAUSED (editable); Resume to keep talking, or Insert/Send to commit.
///
/// All audio capture and transcription happen in-process via
/// <see cref="BatchDictationRecorder"/>. No browser, no localhost WebSocket
/// roundtrip, no realtime socket.
/// </summary>
public partial class SpeakDialog : Window
{
    private enum Stage { Recording, Transcribing, Paused, Failed }

    private readonly AgentOptions _options;
    private readonly Border[] _bars;
    private readonly double[] _barTargets = new double[9];

    // Decaying peak of the raw int16 input RMS, tracked while recording.
    private double _recentPeakRms;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _eqTimer;

    // Recording-time accumulator. _t0 is the start of the CURRENT segment's
    // capture; _elapsedBeforeSegment holds the total capture time of all prior
    // segments. The displayed elapsed time is
    // _elapsedBeforeSegment + (now - _t0) while recording, then frozen.
    private DateTime _t0;
    private TimeSpan _elapsedBeforeSegment = TimeSpan.Zero;

    // The dialog opens straight into recording: capture starts immediately
    // (capture-first; no network connect precedes capture in the batch flow).
    private Stage _stage = Stage.Recording;
    private BatchDictationRecorder? _service;

    // The accumulated, dictionary-corrected transcript across every checkpointed
    // segment so far. Grows on each Pause / commit-from-recording. Never populated
    // during recording (no live preview). While PAUSED the user may edit the box,
    // and Resume re-seeds this from the box so edits are preserved, not rewritten.
    private string _accumulatedText = "";

    // WaveIn device number the current recorder captures from. Defaults to
    // the Windows default mic; overridden by the persisted choice on open and by
    // the user via the mic selector. _suppressMicChange guards the programmatic
    // selection we make while populating the ComboBox from firing a restart.
    private int _selectedDeviceNumber = MicDevices.DefaultDeviceNumber;
    private bool _suppressMicChange;

    /// <summary>
    /// The text the user accepted (dictionary-corrected). Null if cancelled.
    /// </summary>
    public string? ResultText { get; private set; }

    /// <summary>
    /// True when the dialog closed via Send; the caller should auto-submit the
    /// prompt. False when closed via Insert or Cancel.
    /// </summary>
    public bool ShouldSubmit { get; private set; }

    public SpeakDialog(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        InitializeComponent();
        _bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8 };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => UpdateTimer();

        // Decay the equalizer bars at a steady rate so they fall smoothly.
        // OnAudioBands sets per-bar target heights; this timer animates toward them.
        _eqTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _eqTimer.Tick += (_, _) => StepEqualizer();

        Opened += async (_, _) => await OnDialogOpenedAsync();
        Closed += (_, _) => _ = OnDialogClosedAsync();

        // Window-level Enter = Send (insert transcript + auto-submit). Lets the
        // user complete the whole dictation flow with the keyboard only.
        // Escape = Cancel. Registered on the TUNNEL phase so the window decides
        // BEFORE the editable paused TextBox: while reviewing in the focused box
        // we let Enter tunnel through to insert a newline instead of sending.
        AddHandler(KeyDownEvent, SpeakDialog_KeyDown, RoutingStrategies.Tunnel);
    }

    private void SpeakDialog_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;

        if (e.Key == Key.Enter)
        {
            // While reviewing edits in the focused transcript box, Enter inserts
            // a newline (let it tunnel to the TextBox) rather than sending.
            if (_stage == Stage.Paused && TranscriptText.IsFocused)
                return;

            // Only fire when Send is actually actionable. During Transcribing
            // PrimaryButton is disabled; during the error path it is hidden.
            if (PrimaryButton.IsVisible && PrimaryButton.IsEnabled)
            {
                FileLog.Write("[SpeakDialog] Enter -> PrimaryButton (Send)");
                e.Handled = true;
                PrimaryButton_Click(this, new RoutedEventArgs());
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            FileLog.Write("[SpeakDialog] Escape -> CancelButton");
            e.Handled = true;
            CancelButton_Click(this, new RoutedEventArgs());
        }
    }

    private async Task OnDialogOpenedAsync()
    {
        _t0 = DateTime.UtcNow;
        _timer.Start();
        _eqTimer.Start();
        try
        {
            // Resolve the saved mic choice to a current device index BEFORE the
            // first capture starts, so we record from the right device from the
            // very first frame. Then show the device list in the selector.
            _selectedDeviceNumber = MicDevices.ResolveByName(LoadPersistedMicName());
            PopulateMicSelector();
            await StartNewServiceAsync();
            SwitchToRecording();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] StartAsync FAILED: {ex.Message}");
            SwitchToFailed("Failed to start recording: " + ex.Message);
        }
    }

    private async Task OnDialogClosedAsync()
    {
        _timer.Stop();
        _eqTimer.Stop();
        await DisposeServiceAsync();
    }

    private async Task StartNewServiceAsync()
    {
        var svc = new BatchDictationRecorder(_options, _selectedDeviceNumber);
        svc.OnAudioBands += OnAudioBands;
        svc.OnInputRms += OnInputRms;
        svc.OnCaptureStarted += OnServiceCaptureStarted;
        await svc.StartAsync("default");
        _service = svc;
    }

    private async Task DisposeServiceAsync()
    {
        var svc = _service;
        _service = null;
        if (svc is null) return;
        try { await svc.DisposeAsync(); }
        catch (Exception ex) { FileLog.Write($"[SpeakDialog] dispose error: {ex.Message}"); }
    }

    /// <summary>Fill the mic selector with available devices and select the active one.</summary>
    private void PopulateMicSelector()
    {
        var devices = MicDevices.Enumerate();
        _suppressMicChange = true;
        MicSelector.ItemsSource = devices;
        int idx = 0;
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].Number == _selectedDeviceNumber) { idx = i; break; }
        }
        MicSelector.SelectedIndex = idx;
        _suppressMicChange = false;
    }

    private async void MicSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Ignore the programmatic selection PopulateMicSelector makes, and any
        // no-op re-selection of the device already in use.
        if (_suppressMicChange) return;
        if (MicSelector.SelectedItem is not MicDevice device) return;
        if (device.Number == _selectedDeviceNumber) return;

        try
        {
            // Persist the device NAME (indices reorder across replugs); the
            // Windows-default entry is stored as empty so it keeps tracking the
            // OS default rather than pinning to whatever it maps to today.
            PersistMicName(device.Number == MicDevices.DefaultDeviceNumber ? null : device.Name);
            await ChangeDeviceAsync(device.Number);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] MicSelector_SelectionChanged FAILED: {ex.Message}");
            SwitchToFailed("Could not switch microphone: " + ex.Message);
        }
    }

    /// <summary>
    /// Switch the live capture to a different device. Tears down the current
    /// service and starts a fresh one on the new device. The current segment's
    /// audio is discarded (mixing two devices' audio into one clip is not
    /// meaningful), but the already-accumulated transcript from earlier segments
    /// is kept. The new segment's capture restarts the segment timer.
    /// </summary>
    private async Task ChangeDeviceAsync(int deviceNumber)
    {
        _selectedDeviceNumber = deviceNumber;
        FileLog.Write($"[SpeakDialog] ChangeDevice: {deviceNumber} ({MicDevices.DescribeDevice(deviceNumber)})");
        await DisposeServiceAsync();
        // Fresh device = fresh segment capture and fresh segment timer origin.
        _t0 = DateTime.UtcNow;
        await StartNewServiceAsync();
        SwitchToRecording();
    }

    /// <summary>
    /// Terminal error state: recording or transcription failed. Freezes the timer,
    /// parks the equalizer gray, hides every action except Close.
    /// </summary>
    private void SwitchToFailed(string message)
    {
        _stage = Stage.Failed;
        TranscriptText.Text = message;
        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        TranscriptText.IsReadOnly = true;
        StatusLabel.Text = "ERROR";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
        PrimaryButton.IsVisible = false;
        StopButton.IsVisible = false;
        PauseButton.IsVisible = false;
        MicSelector.IsEnabled = false;
        CancelButton.Content = "Close";
        // Park the bars: nothing is being captured, the meter must not dance.
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 8.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
        LevelHint.Text = "";
    }

    private static string? LoadPersistedMicName()
    {
        var config = CcDirectorConfigService.ReadRaw();
        var name = config["dictation"]?["mic_device_name"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static void PersistMicName(string? name)
    {
        var patch = new JsonObject
        {
            ["dictation"] = new JsonObject { ["mic_device_name"] = name ?? "" },
        };
        CcDirectorConfigService.MergePatch(patch);
        FileLog.Write($"[SpeakDialog] PersistMicName: '{name ?? "(default)"}'");
    }

    private void UpdateTimer()
    {
        // Ticks only while audio is being captured (Recording). Frozen in every
        // other stage, most importantly Failed: a timer counting up next to an
        // ERROR label reads as "still recording" when nothing is (issue #189).
        // Shows the TOTAL capture across all segments so the user sees how long
        // they have dictated, not just the current segment.
        if (_stage != Stage.Recording) return;
        var elapsed = _elapsedBeforeSegment + (DateTime.UtcNow - _t0);
        var s = (int)elapsed.TotalSeconds;
        var tenths = elapsed.Milliseconds / 100;
        TimerLabel.Text = $"{s / 60}:{(s % 60):D2}.{tenths}";
    }

    private void OnAudioBands(double[] bands)
    {
        // Driven from NAudio's worker thread. Each band drives its own bar so
        // the bars move independently (real spectrum) rather than as one hill.
        // Update targets on the UI thread; the eqTimer animates toward them.
        Dispatcher.UIThread.Post(() =>
        {
            const double maxH = 92.0;
            const double minH = 8.0;
            int n = Math.Min(_barTargets.Length, bands.Length);
            for (int i = 0; i < n; i++)
            {
                double level = Math.Clamp(bands[i], 0.0, 1.0);
                _barTargets[i] = minH + (maxH - minH) * level;
            }
        });
    }

    private void OnInputRms(double rms)
    {
        // From NAudio's worker thread. Track a decaying peak on the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            _recentPeakRms = Math.Max(rms, _recentPeakRms * 0.97);
        });
    }

    private void StepEqualizer()
    {
        // Ease current height toward target. Faster up than down so loud beats
        // pop and quiet stretches decay smoothly.
        for (int i = 0; i < _bars.Length; i++)
        {
            var current = _bars[i].Height;
            var target = _barTargets[i];
            var diff = target - current;
            double step = diff >= 0 ? diff * 0.7 : diff * 0.32;
            var next = current + step;
            if (next < 8.0) next = 8.0;
            _bars[i].Height = next;
        }
    }

    /// <summary>
    /// Fired by the service the instant the microphone actually starts capturing.
    /// Re-anchors the current segment's elapsed-time origin so the displayed timer
    /// tracks REAL capture, not the dialog-open-to-capture (or resume) setup. The
    /// prior segments' time is preserved in <see cref="_elapsedBeforeSegment"/>.
    /// </summary>
    private void OnServiceCaptureStarted()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_stage == Stage.Recording)
                _t0 = DateTime.UtcNow;
        });
    }

    private async void PrimaryButton_Click(object? sender, RoutedEventArgs e)
    {
        // Send: transcribe the current segment (if recording) and append, then
        // close with ShouldSubmit=true.
        if (_stage == Stage.Recording)
        {
            await FinalizeFromRecordingAsync(submitOnClose: true);
        }
        else if (_stage == Stage.Paused)
        {
            // Use the (possibly edited) text from the box so the user's
            // corrections are what gets sent.
            CommitPausedText(submitOnClose: true);
        }
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        // Insert: transcribe the current segment (if recording) and append, then
        // close with ShouldSubmit=false. The caller inserts the text at the caret
        // without auto-submitting so the user can review/edit in the prompt.
        if (_stage == Stage.Recording)
        {
            await FinalizeFromRecordingAsync(submitOnClose: false);
        }
        else if (_stage == Stage.Paused)
        {
            CommitPausedText(submitOnClose: false);
        }
    }

    private async void PauseButton_Click(object? sender, RoutedEventArgs e)
    {
        // Pause (while recording) is the checkpoint; Resume (while paused) starts
        // a fresh segment. The button is disabled while transcribing.
        if (_stage == Stage.Recording)
        {
            await PauseAsync();
        }
        else if (_stage == Stage.Paused)
        {
            await ResumeAsync();
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        // Cancel/Close: no text. An interrupted or cancelled turn produces no transcript.
        ResultText = null;
        ShouldSubmit = false;
        Close();
    }

    /// <summary>
    /// Stop the mic, transcribe the current segment ONCE through the shared batch
    /// pipeline, append it to the accumulated transcript, and close the dialog with
    /// the result. Used by Send and Insert directly from recording.
    /// </summary>
    private async Task FinalizeFromRecordingAsync(bool submitOnClose)
    {
        SwitchToTranscribing();
        try
        {
            var segment = await TranscribeSegmentAsync();
            _accumulatedText = DictationText.Join(_accumulatedText, segment);
            ResultText = string.IsNullOrWhiteSpace(_accumulatedText) ? null : _accumulatedText;
            ShouldSubmit = submitOnClose && ResultText is not null;
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] FinalizeFromRecording FAILED: {ex.Message}");
            SwitchToFailed(ex.Message);
        }
    }

    /// <summary>
    /// Checkpoint while recording: freeze the timer, transcribe the current segment
    /// ONCE, append it to the accumulated transcript, and stay in the dialog showing
    /// PAUSED (editable). Resume starts a fresh segment; Insert/Send commit. Resume
    /// is disabled for the duration of the transcription.
    /// </summary>
    private async Task PauseAsync()
    {
        FileLog.Write("[SpeakDialog] PauseAsync");
        // Freeze the segment time first so the displayed elapsed time stops at the
        // moment the user clicked Pause, not when transcription finishes, and so
        // the next segment adds onto this total.
        _elapsedBeforeSegment += DateTime.UtcNow - _t0;
        SwitchToTranscribing();
        try
        {
            var segment = await TranscribeSegmentAsync();
            _accumulatedText = DictationText.Join(_accumulatedText, segment);
            SwitchToPaused();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] PauseAsync FAILED: {ex.Message}");
            SwitchToFailed("Pause failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Resume from PAUSED: re-seed the accumulator from the (possibly edited) box so
    /// the user's corrections survive, then start a fresh recording segment that
    /// will append onto it.
    /// </summary>
    private async Task ResumeAsync()
    {
        FileLog.Write("[SpeakDialog] ResumeAsync");

        // Re-seed the accumulator from the (possibly edited) text box so new
        // speech appends onto the edited text rather than the pre-edit transcript.
        _accumulatedText = TranscriptText.Text ?? "";

        // Anchor the new segment's origin BEFORE capture starts; OnServiceCaptureStarted
        // re-anchors it to the real capture instant once the mic is live.
        _t0 = DateTime.UtcNow;

        try
        {
            await StartNewServiceAsync();
            SwitchToRecording();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] ResumeAsync FAILED: {ex.Message}");
            // NOT SwitchToFailed: the accumulated text is still good. Fall back to
            // Paused so Send/Insert keep working and Resume can be retried. The
            // error must NOT go into the text box - in the Paused stage the box
            // content IS what Send submits, so writing the error there would send
            // the error message as the prompt.
            SwitchToPaused();
            StatusLabel.Text = "ERROR - could not resume";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            LevelHint.Text = "Could not resume - your text is kept. Try Resume again, or Send what you have.";
        }
    }

    /// <summary>
    /// Run the single batch transcription on the current segment's captured clip.
    /// The service is consumed (stopped) and disposed here, so a segment cannot be
    /// transcribed twice. Returns the dictionary-corrected transcript for the segment.
    /// </summary>
    private async Task<string> TranscribeSegmentAsync()
    {
        var svc = _service;
        if (svc is null)
            throw new InvalidOperationException("No active recording to transcribe.");

        var result = await svc.TranscribeAsync();
        await DisposeServiceAsync();
        FileLog.Write($"[SpeakDialog] segment transcribed: corrected={result.DictionaryWordsCorrected} words, len={result.CleanedTranscript.Length}");
        return result.CleanedTranscript;
    }

    /// <summary>Close with the (possibly edited) text currently in the box.</summary>
    private void CommitPausedText(bool submitOnClose)
    {
        var reviewed = TranscriptText.Text;
        ResultText = string.IsNullOrWhiteSpace(reviewed) ? null : reviewed;
        ShouldSubmit = submitOnClose && ResultText is not null;
        Close();
    }

    private void SwitchToTranscribing()
    {
        _stage = Stage.Transcribing;
        StatusLabel.Text = "TRANSCRIBING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        // Show the text accumulated from prior segments (if any) while the current
        // segment transcribes; the new segment's text is not available until the
        // batch call completes. Read-only - no editing mid-transcription.
        TranscriptText.Text = _accumulatedText;
        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        TranscriptText.IsReadOnly = true;
        PrimaryButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        // Resume stays disabled here: "you cannot resume until it has been transcribed".
        PauseButton.IsEnabled = false;
        MicSelector.IsEnabled = false;
        LevelHint.Text = "Transcribing what you have said so far...";
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 34.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
    }

    private void SwitchToPaused()
    {
        _stage = Stage.Paused;
        StatusLabel.Text = "PAUSED - reviewing";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TranscriptText.Text = _accumulatedText;
        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        // Editable for review: let the user fix mis-heard words before committing
        // or resuming. Park the caret at the end so appended typing / Resume
        // continues naturally.
        TranscriptText.IsReadOnly = false;
        TranscriptText.CaretIndex = TranscriptText.Text?.Length ?? 0;
        TranscriptText.Focus();
        // Pause becomes Resume; commit actions stay available.
        PauseButton.Content = "Resume";
        PauseButton.IsEnabled = true;
        PrimaryButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        MicSelector.IsEnabled = true;
        // Park the equalizer bars at a low resting height while paused.
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 8.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
        LevelHint.Text = "";
    }

    private void SwitchToRecording()
    {
        _stage = Stage.Recording;
        StatusLabel.Text = "RECORDING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        // Read-only and empty while recording: NO live preview. The transcript
        // box stays blank (its explanatory watermark shows through) until the next
        // checkpoint. Prior-segment text is restored only at the next Pause/commit.
        TranscriptText.Text = "";
        TranscriptText.IsReadOnly = true;
        // Restore the two-bar Pause glyph (it may currently read "Resume").
        PauseButton.Content = BuildPauseIcon();
        PauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        PrimaryButton.IsEnabled = true;
        MicSelector.IsEnabled = true;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        // Fresh segment: re-evaluate loudness from scratch.
        _recentPeakRms = 0.0;
        LevelHint.Text = "";
    }

    /// <summary>
    /// Two-bar pause glyph as Avalonia shapes. Avoids the Unicode pause symbol per
    /// the project-wide no-Unicode rule. Matches the markup-built glyph in
    /// SpeakDialog.axaml so the button looks identical whether the content came
    /// from XAML (initial) or from here (after a Resume).
    /// </summary>
    private static Control BuildPauseIcon()
    {
        var fill = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        var sp = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            Spacing = 5,
        };
        sp.Children.Add(new global::Avalonia.Controls.Shapes.Rectangle { Width = 4, Height = 14, Fill = fill });
        sp.Children.Add(new global::Avalonia.Controls.Shapes.Rectangle { Width = 4, Height = 14, Fill = fill });
        return sp;
    }
}
