using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Modal dialog for dictation. Four primary actions:
///
///   Cancel - close, no text.
///   Pause  - stop the mic, run cleanup on what's been said so far, hold
///            the cleaned text in an accumulator, swap the button to
///            "Resume". A fresh SpeakService is created when Resume is
///            clicked so the user can keep talking; subsequent pauses
///            append more cleaned text to the accumulator.
///   Stop   - finalize (cleanup if currently recording, otherwise just
///            use the accumulator), close with <see cref="ResultText"/>
///            populated and <see cref="ShouldSubmit"/> set to FALSE so
///            the caller inserts the text at the caret but does not
///            auto-submit. Use when the user wants to review/edit
///            before sending.
///   Send   - same as Stop but closes with <see cref="ShouldSubmit"/>
///            set to TRUE so the caller auto-submits the prompt.
///
/// All audio capture and library orchestration happens in-process via
/// <see cref="SpeakService"/>. No browser, no localhost WebSocket roundtrip.
/// </summary>
public partial class SpeakDialog : Window
{
    private enum Stage { Connecting, Recording, Paused, Transcribing, Failed }

    private readonly AgentOptions _options;
    private readonly Border[] _bars;
    private readonly double[] _barTargets = new double[9];

    // Decaying peak of the raw int16 input RMS, used to decide whether the
    // "speak up" hint should show. Decays per chunk so the hint reacts to
    // recent speech rather than flickering on every 50 ms buffer.
    private double _recentPeakRms;
    private const double VoicePresentRms = 20.0;   // below this = silence, don't nag
    private const double HealthyRms = 600.0;        // at/above this = loud enough, hide hint
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _eqTimer;

    // Time accumulator. _t0 is the start of the current recording segment;
    // _elapsedBeforeSegment is the total time accumulated across previous
    // pause cycles. Display = (now - _t0) + _elapsedBeforeSegment.
    private DateTime _t0;
    private TimeSpan _elapsedBeforeSegment = TimeSpan.Zero;

    // The dialog opens in Connecting: mic capture starts immediately (capture-
    // first pipeline, no audio is lost) but the transcription backend is not
    // connected yet. SwitchToRecording happens once the connect completes.
    private Stage _stage = Stage.Connecting;
    private SpeakService? _service;
    private string _accumulatedText = "";
    private string _currentPartial = "";

    // WaveIn device number the current SpeakService captures from. Defaults to
    // the Windows default mic; overridden by the persisted choice on open and by
    // the user via the mic selector. _suppressMicChange guards the programmatic
    // selection we make while populating the ComboBox from firing a restart.
    private int _selectedDeviceNumber = MicDevices.DefaultDeviceNumber;
    private bool _suppressMicChange;

    /// <summary>
    /// The text the user accepted (cleaned, possibly spanning multiple
    /// pause/resume cycles). Null if cancelled.
    /// </summary>
    public string? ResultText { get; private set; }

    /// <summary>
    /// True when the dialog closed via Send; the caller should auto-submit
    /// the prompt. False when the dialog closed via Cancel (or errored).
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
        // user complete the whole dictation flow with the keyboard only:
        // Ctrl+H from the main window opens this dialog, talk, Enter sends.
        // Escape = Cancel for the same reason.
        //
        // Registered on the TUNNEL phase so the window decides BEFORE the now-
        // editable transcript TextBox: during recording (read-only box) Enter
        // still sends, but while reviewing in the focused editable box we let
        // Enter tunnel through to insert a newline instead of sending.
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
        // The XAML carries the Connecting text/colors so there is no flash of
        // "RECORDING"; this applies the rest (disabled commit buttons, yellow
        // bars) through the same code path every later reconnect uses.
        SwitchToConnecting();
        try
        {
            // Resolve the saved mic choice to a current device index BEFORE the
            // first capture starts, so we record from the right device from the
            // very first frame. Then show the device list in the selector.
            _selectedDeviceNumber = MicDevices.ResolveByName(LoadPersistedMicName());
            PopulateMicSelector();
            // The XAML opens in the Connecting visual state (CONNECTING label,
            // commit buttons disabled). StartNewServiceAsync returns once the
            // transcription backend is actually connected.
            await StartNewServiceAsync();
            SwitchToRecording();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] StartAsync FAILED: {ex.Message}");
            // DictationConnectException already carries a human-readable
            // message (transient service problem, try again); other failures
            // get a prefix naming the operation that failed.
            var msg = ex is DictationConnectException
                ? ex.Message
                : "Failed to start recording: " + ex.Message;
            SwitchToFailed(msg);
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
        var svc = new SpeakService(_options, _selectedDeviceNumber);
        svc.OnPartial += OnPartial;
        svc.OnStateChanged += OnStateChanged;
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
    /// service and starts a fresh one on the new device. Already-accumulated
    /// cleaned text (from prior pauses) is kept; the in-flight, not-yet-cleaned
    /// partial is discarded because it came from the device being abandoned.
    /// </summary>
    private async Task ChangeDeviceAsync(int deviceNumber)
    {
        _selectedDeviceNumber = deviceNumber;
        FileLog.Write($"[SpeakDialog] ChangeDevice: {deviceNumber} ({MicDevices.DescribeDevice(deviceNumber)})");
        await DisposeServiceAsync();
        _currentPartial = "";
        // Fresh device = fresh timer. Reset BEFORE the connect so the ticking
        // Connecting-stage timer shows the new segment, not stale time.
        _elapsedBeforeSegment = TimeSpan.Zero;
        _t0 = DateTime.UtcNow;
        SwitchToConnecting();
        await StartNewServiceAsync();
        SwitchToRecording();
        RenderTranscript();
    }

    /// <summary>
    /// Terminal error state: the dictation backend is not running and there is
    /// no recoverable text in play. Freezes the timer (UpdateTimer ignores
    /// Failed), parks the equalizer gray, hides every action except Close.
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
        // Ticks while audio is actually being captured: Recording, and
        // Connecting (capture-first - the mic is live and that audio WILL be
        // transcribed once the backend connects). Frozen in every other stage,
        // most importantly Failed: the timer counting up next to an ERROR
        // label read as "still recording" when nothing was (issue #189).
        if (_stage != Stage.Recording && _stage != Stage.Connecting) return;
        var elapsed = (DateTime.UtcNow - _t0) + _elapsedBeforeSegment;
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
        // From NAudio's worker thread. Track a decaying peak on the UI thread
        // and re-evaluate the hint.
        Dispatcher.UIThread.Post(() =>
        {
            _recentPeakRms = Math.Max(rms, _recentPeakRms * 0.97);
            UpdateLevelHint();
        });
    }

    private void UpdateLevelHint()
    {
        // Only nag while actually recording, and only when there is voice
        // present (so we don't badger during silent pauses) but it is
        // consistently too quiet for clean capture. The meter itself is
        // calibrated to a healthy level, so a short bar plus this hint together
        // tell the user to speak up rather than us scaling the meter to flatter
        // a faint signal.
        // Hint disabled for now: the "speak louder / move closer" nag fired too
        // often and annoyed users, so we keep it hidden rather than show it.
        LevelHint.Text = "";
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

    private void OnPartial(string partial)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _currentPartial = partial ?? "";
            RenderTranscript();
        });
    }

    private void RenderTranscript()
    {
        // When empty, leave the box blank so the TextBox Watermark
        // ("(your words will appear here)") shows through. Foreground stays the
        // normal transcript color; the watermark renders in its own muted style.
        var combined = DictationText.Join(_accumulatedText, _currentPartial);
        TranscriptText.Text = combined;
        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
    }

    private void OnStateChanged(ConnectionState state)
    {
        FileLog.Write($"[SpeakDialog] state -> {state}");
        if (state != ConnectionState.Buffering) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_stage != Stage.Recording) return;
            StatusLabel.Text = "RECORDING - no connection";
            LevelHint.Text = "Connection to transcription service lost. Press Stop now to save what was captured.";
        });
    }

    /// <summary>
    /// Fired by the service the instant the microphone actually starts
    /// capturing (before the transcription backend has finished connecting).
    ///
    /// Two things happen here. We re-anchor the elapsed-time origin so the
    /// displayed timer tracks REAL capture, not the dialog-open-to-capture
    /// setup. And we flip the dialog to the RED "RECORDING" state immediately:
    /// capture is live and the capture-first pipeline is buffering every byte in
    /// order, so we ARE truly recording now even though the transcription
    /// backend may still be connecting. Showing red here (instead of waiting for
    /// the connect) makes the green->red transition feel instant while being
    /// MORE honest - red means "your audio is being captured", which is the
    /// guarantee that actually matters. Commit actions stay disabled until the
    /// connect completes (see SwitchToCapturing).
    /// </summary>
    private void OnServiceCaptureStarted()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Only anchor the very first segment's origin. Pause/Resume manage
            // _t0 themselves via _elapsedBeforeSegment. The first segment's
            // capture starts during the Connecting stage (capture-first).
            if ((_stage == Stage.Connecting || _stage == Stage.Recording)
                && _elapsedBeforeSegment == TimeSpan.Zero)
                _t0 = DateTime.UtcNow;

            // Flip to red the moment capture is confirmed live. Guarded to the
            // Connecting stage so a later transition (SwitchToRecording on
            // connect, or a Cancel/Stop that already advanced the stage) is not
            // clobbered by this posted callback arriving late.
            if (_stage == Stage.Connecting)
                SwitchToCapturing();
        });
    }

    private async void PrimaryButton_Click(object? sender, RoutedEventArgs e)
    {
        // Send: finalize (cleanup if recording), close with ShouldSubmit=true.
        if (_stage == Stage.Recording)
        {
            await FinalizeFromRecordingAsync(submitOnClose: true);
        }
        else if (_stage == Stage.Paused)
        {
            // Use the (possibly edited) text from the review box, not the raw
            // accumulator, so the user's corrections are what gets sent.
            var reviewed = TranscriptText.Text;
            ResultText = string.IsNullOrWhiteSpace(reviewed) ? null : reviewed;
            ShouldSubmit = ResultText != null;
            Close();
        }
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        // Stop: finalize (cleanup if recording), close with ShouldSubmit=false.
        // Caller inserts the text at the caret but does NOT auto-submit so the
        // user can review/edit before sending.
        if (_stage == Stage.Recording)
        {
            await FinalizeFromRecordingAsync(submitOnClose: false);
        }
        else if (_stage == Stage.Paused)
        {
            // Use the (possibly edited) text from the review box.
            var reviewed = TranscriptText.Text;
            ResultText = string.IsNullOrWhiteSpace(reviewed) ? null : reviewed;
            ShouldSubmit = false;
            Close();
        }
    }

    private async void PauseButton_Click(object? sender, RoutedEventArgs e)
    {
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
        ResultText = null;
        ShouldSubmit = false;
        Close();
    }

    /// <summary>
    /// Stop the current SpeakService, run cleanup, append to the accumulator,
    /// and close the dialog. Used by Send while recording.
    /// </summary>
    private async Task FinalizeFromRecordingAsync(bool submitOnClose)
    {
        SwitchToTranscribing();
        try
        {
            var svc = _service;
            if (svc is null)
            {
                ResultText = string.IsNullOrWhiteSpace(_accumulatedText) ? null : _accumulatedText;
                ShouldSubmit = submitOnClose && ResultText != null;
                Close();
                return;
            }
            var result = await svc.StopAsync();
            await DisposeServiceAsync();
            _accumulatedText = DictationText.Join(_accumulatedText, result.CleanedTranscript ?? "");
            ResultText = string.IsNullOrWhiteSpace(_accumulatedText) ? null : _accumulatedText;
            ShouldSubmit = submitOnClose && ResultText != null;
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] StopAsync FAILED: {ex.Message}");
            SwitchToFailed("Transcription failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Stop the current SpeakService, run cleanup, append to the accumulator,
    /// and stay in the dialog showing "PAUSED". The user can resume (start
    /// a new SpeakService) or send (close with accumulated text).
    /// </summary>
    private async Task PauseAsync()
    {
        FileLog.Write("[SpeakDialog] PauseAsync");
        // Freeze the timer first so the displayed elapsed time stops at the
        // moment the user clicked Pause, not at the moment cleanup finishes.
        _elapsedBeforeSegment += DateTime.UtcNow - _t0;
        SwitchToTranscribing();
        PauseButton.IsEnabled = false;
        try
        {
            var svc = _service;
            if (svc is null)
            {
                // No active service - already in some degraded state. Just
                // switch UI to Paused with whatever is accumulated.
                SwitchToPaused();
                return;
            }
            var result = await svc.StopAsync();
            await DisposeServiceAsync();
            _accumulatedText = DictationText.Join(_accumulatedText, result.CleanedTranscript ?? "");
            _currentPartial = "";
            SwitchToPaused();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] PauseAsync FAILED: {ex.Message}");
            SwitchToFailed("Pause failed: " + ex.Message);
        }
    }

    private async Task ResumeAsync()
    {
        FileLog.Write("[SpeakDialog] ResumeAsync");

        // Re-seed the accumulator from the (possibly edited) text box so the
        // user's corrections survive: new speech appends onto the edited text
        // rather than the pre-edit cleaned transcript.
        _accumulatedText = TranscriptText.Text ?? "";
        _currentPartial = "";

        // Anchor the new segment's origin BEFORE the connect: capture starts
        // immediately (capture-first), so the connect window is recorded audio
        // and the ticking Connecting-stage timer should count it.
        _t0 = DateTime.UtcNow;
        SwitchToConnecting();

        try
        {
            await StartNewServiceAsync();
            SwitchToRecording();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] ResumeAsync FAILED: {ex.Message}");
            // NOT SwitchToFailed: the accumulated text is still good. Fall back
            // to Paused so Send/Insert keep working and Resume can be retried.
            // The error must NOT go into the text box - in the Paused stage the
            // box content IS what Send submits, so writing the error there
            // would send the error message as the prompt.
            SwitchToPaused();
            StatusLabel.Text = "ERROR - could not resume";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            LevelHint.Text = "Reconnect failed - your text is kept. Try Resume again, or Send what you have.";
        }
    }

    /// <summary>
    /// The brief opening state, shown from the moment the dialog opens until the
    /// mic is confirmed capturing (a few milliseconds). Yellow, commit actions
    /// disabled. As soon as capture is live the dialog flips to the red
    /// <see cref="SwitchToCapturing"/> state; once the backend connects it
    /// becomes the fully-live <see cref="SwitchToRecording"/> state.
    /// Cancel stays available throughout.
    /// </summary>
    private void SwitchToConnecting()
    {
        _stage = Stage.Connecting;
        StatusLabel.Text = "CONNECTING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TranscriptText.IsReadOnly = true;
        PrimaryButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        MicSelector.IsEnabled = false;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        LevelHint.Text = "";
    }

    private void SwitchToTranscribing()
    {
        _stage = Stage.Transcribing;
        StatusLabel.Text = "TRANSCRIBING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TranscriptText.IsReadOnly = true;
        PrimaryButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        MicSelector.IsEnabled = false;
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 34.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        LevelHint.Text = "";
    }

    private void SwitchToPaused()
    {
        _stage = Stage.Paused;
        StatusLabel.Text = "PAUSED - reviewing";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        PauseButton.Content = "Resume";
        PauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        PrimaryButton.IsEnabled = true;
        MicSelector.IsEnabled = true;
        // Park the equalizer bars at a low resting height while paused.
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 8.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
        LevelHint.Text = "";
        RenderTranscript();
        // Now editable for review: let the user fix mis-heard words. Park the
        // caret at the end so appended typing / Resume continues naturally.
        TranscriptText.IsReadOnly = false;
        TranscriptText.CaretIndex = TranscriptText.Text?.Length ?? 0;
    }

    /// <summary>
    /// Capture is live but the transcription backend is not connected yet. Shows
    /// the RED "RECORDING" state - the mic is genuinely capturing and every byte
    /// is buffered in order (capture-first pipeline), so this is an honest
    /// "recording" indication. Commit actions (Insert/Send/Pause) and the mic
    /// selector stay disabled until the connect completes: there is nothing to
    /// transcribe or commit yet, and switching mics mid-connect would race the
    /// service start. A small sub-status notes the backend is still connecting.
    /// Cancel stays available. <see cref="SwitchToRecording"/> takes over the
    /// instant the connect completes.
    /// </summary>
    private void SwitchToCapturing()
    {
        _stage = Stage.Recording;
        StatusLabel.Text = "RECORDING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        TranscriptText.IsReadOnly = true;
        // Commit/pause/mic disabled until the link is up; Cancel stays enabled.
        PrimaryButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        PauseButton.Content = BuildPauseIcon();
        MicSelector.IsEnabled = false;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        // Fresh segment: re-evaluate loudness from scratch.
        _recentPeakRms = 0.0;
        // Reuse the (otherwise empty) hint row to note the backend is still
        // connecting. ASCII only per the project no-Unicode rule.
        LevelHint.Text = "connecting transcription...";
    }

    private void SwitchToRecording()
    {
        _stage = Stage.Recording;
        StatusLabel.Text = "RECORDING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        // Read-only while recording: RenderTranscript drives the box from live
        // partials, so the user can't edit until they pause.
        TranscriptText.IsReadOnly = true;
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
    /// Two-bar pause glyph as Avalonia shapes. Avoids the Unicode pause symbol
    /// per the project-wide no-Unicode rule. Used when SwitchToRecording
    /// reclaims the PauseButton content after a Resume.
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
