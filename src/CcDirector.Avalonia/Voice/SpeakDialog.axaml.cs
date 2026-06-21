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
/// Modal dialog for desktop dictation, migrated to whole-audio batch (issue #589).
///
/// The microphone captures the whole turn locally; NO text appears while the user
/// is speaking (there is no live partial preview and no realtime streaming socket).
/// When the user commits, the dialog enters the TRANSCRIBING state and sends the
/// whole clip ONCE through the shared <see cref="BatchDictationRecorder"/> batch pipeline,
/// which transcribes via the user-selected method and applies the dictionary
/// corrector only. The transcript appears only after transcription completes.
///
/// Commit actions:
///
///   Cancel - close, no text. An interrupted/cancelled turn produces no transcript.
///   Insert - stop, transcribe the whole clip once, close with
///            <see cref="ResultText"/> populated and <see cref="ShouldSubmit"/>
///            FALSE so the caller inserts the text at the caret without auto-submit.
///   Send   - same as Insert but closes with <see cref="ShouldSubmit"/> TRUE so the
///            caller auto-submits the prompt.
///   Review - stop, transcribe the whole clip once, then show the final text in the
///            dialog (editable) for review. From review, Insert/Send commit the
///            reviewed text WITHOUT re-recording (whole-audio batch is a single
///            capture, so there is no pause/resume segmenting).
///
/// All audio capture and transcription happen in-process via
/// <see cref="BatchDictationRecorder"/>. No browser, no localhost WebSocket
/// roundtrip, no realtime socket.
/// </summary>
public partial class SpeakDialog : Window
{
    private enum Stage { Recording, Transcribing, Review, Failed }

    private readonly AgentOptions _options;
    private readonly Border[] _bars;
    private readonly double[] _barTargets = new double[9];

    // Decaying peak of the raw int16 input RMS, tracked while recording.
    private double _recentPeakRms;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _eqTimer;

    // Recording-time accumulator. _t0 is the start of capture; the displayed
    // elapsed time is (now - _t0) while recording, then frozen.
    private DateTime _t0;

    // The dialog opens straight into recording: capture starts immediately
    // (capture-first; no network connect precedes capture in the batch flow).
    private Stage _stage = Stage.Recording;
    private BatchDictationRecorder? _service;

    // The final, dictionary-corrected transcript produced by the single batch
    // transcription. Populated only after transcription completes - never during
    // recording (no live preview).
    private string _finalText = "";

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
        // BEFORE the editable review TextBox: while reviewing in the focused box
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
            if (_stage == Stage.Review && TranscriptText.IsFocused)
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
    /// service and starts a fresh one on the new device. The whole-audio capture
    /// restarts on the new device (the abandoned device's buffered audio is
    /// discarded - mixing two devices' audio into one clip is not meaningful).
    /// </summary>
    private async Task ChangeDeviceAsync(int deviceNumber)
    {
        _selectedDeviceNumber = deviceNumber;
        FileLog.Write($"[SpeakDialog] ChangeDevice: {deviceNumber} ({MicDevices.DescribeDevice(deviceNumber)})");
        await DisposeServiceAsync();
        // Fresh device = fresh capture and fresh timer.
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
        ReviewButton.IsVisible = false;
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
        if (_stage != Stage.Recording) return;
        var elapsed = DateTime.UtcNow - _t0;
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
    /// Re-anchors the elapsed-time origin so the displayed timer tracks REAL
    /// capture, not the dialog-open-to-capture setup.
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
        // Send: transcribe the whole clip (if still recording), close with ShouldSubmit=true.
        if (_stage == Stage.Recording)
        {
            await TranscribeAndCloseAsync(submitOnClose: true);
        }
        else if (_stage == Stage.Review)
        {
            // Use the (possibly edited) reviewed text so the user's corrections are sent.
            CommitReviewedText(submitOnClose: true);
        }
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        // Insert: transcribe the whole clip (if still recording), close with
        // ShouldSubmit=false. The caller inserts the text at the caret without
        // auto-submitting so the user can review/edit in the prompt before sending.
        if (_stage == Stage.Recording)
        {
            await TranscribeAndCloseAsync(submitOnClose: false);
        }
        else if (_stage == Stage.Review)
        {
            CommitReviewedText(submitOnClose: false);
        }
    }

    private async void ReviewButton_Click(object? sender, RoutedEventArgs e)
    {
        // Review: transcribe the whole clip once, then show the final text in the
        // dialog (editable) for review before committing. Only meaningful while
        // recording; in Review it is hidden.
        if (_stage == Stage.Recording)
        {
            await TranscribeForReviewAsync();
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
    /// Stop the mic, transcribe the whole captured clip ONCE through the shared
    /// batch pipeline, and close the dialog with the result. Used by Send and Insert
    /// directly from recording (no review step).
    /// </summary>
    private async Task TranscribeAndCloseAsync(bool submitOnClose)
    {
        SwitchToTranscribing();
        try
        {
            var text = await TranscribeWholeClipAsync();
            ResultText = string.IsNullOrWhiteSpace(text) ? null : text;
            ShouldSubmit = submitOnClose && ResultText is not null;
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] TranscribeAndClose FAILED: {ex.Message}");
            SwitchToFailed(ex.Message);
        }
    }

    /// <summary>
    /// Stop the mic, transcribe the whole captured clip ONCE, then show the final
    /// text in the dialog for review (Send / Insert commit it without re-recording).
    /// </summary>
    private async Task TranscribeForReviewAsync()
    {
        SwitchToTranscribing();
        try
        {
            var text = await TranscribeWholeClipAsync();
            _finalText = text;
            SwitchToReview();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] TranscribeForReview FAILED: {ex.Message}");
            SwitchToFailed(ex.Message);
        }
    }

    /// <summary>
    /// Run the single batch transcription on the whole captured clip. The service
    /// is consumed (stopped) here, so the dialog cannot transcribe twice. Returns
    /// the dictionary-corrected transcript.
    /// </summary>
    private async Task<string> TranscribeWholeClipAsync()
    {
        var svc = _service;
        if (svc is null)
            throw new InvalidOperationException("No active recording to transcribe.");

        var result = await svc.TranscribeAsync();
        await DisposeServiceAsync();
        FileLog.Write($"[SpeakDialog] transcribed: corrected={result.DictionaryWordsCorrected} words, len={result.CleanedTranscript.Length}");
        return result.CleanedTranscript;
    }

    /// <summary>Close with the (possibly edited) reviewed text.</summary>
    private void CommitReviewedText(bool submitOnClose)
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
        // No text appears here: the whole clip has been sent and the final
        // transcript is not available until transcription completes.
        TranscriptText.Text = "";
        TranscriptText.IsReadOnly = true;
        PrimaryButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        ReviewButton.IsEnabled = false;
        MicSelector.IsEnabled = false;
        LevelHint.Text = "Transcribing the whole recording...";
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 34.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
    }

    private void SwitchToReview()
    {
        _stage = Stage.Review;
        StatusLabel.Text = "REVIEW";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TranscriptText.Text = _finalText;
        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        // Editable for review: let the user fix mis-heard words before committing.
        TranscriptText.IsReadOnly = false;
        TranscriptText.CaretIndex = TranscriptText.Text?.Length ?? 0;
        TranscriptText.Focus();
        // Review is a single capture; there is no Resume. Hide the Review button
        // and keep the commit actions (Insert/Send) for the reviewed text.
        ReviewButton.IsVisible = false;
        PrimaryButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        MicSelector.IsEnabled = false;
        // Park the equalizer bars at a low resting height while reviewing.
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
        // box stays blank (its watermark shows through) until transcription
        // completes after the user stops.
        TranscriptText.Text = "";
        TranscriptText.IsReadOnly = true;
        ReviewButton.IsVisible = true;
        ReviewButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        PrimaryButton.IsEnabled = true;
        MicSelector.IsEnabled = true;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        // Fresh segment: re-evaluate loudness from scratch.
        _recentPeakRms = 0.0;
        LevelHint.Text = "";
    }
}
