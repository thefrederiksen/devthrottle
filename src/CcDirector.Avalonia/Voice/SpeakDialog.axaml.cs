using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Configuration;
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
    private enum Stage { Recording, Paused, Transcribing }

    private readonly AgentOptions _options;
    private readonly Border[] _bars;
    private readonly double[] _barTargets = new double[9];
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _eqTimer;

    // Time accumulator. _t0 is the start of the current recording segment;
    // _elapsedBeforeSegment is the total time accumulated across previous
    // pause cycles. Display = (now - _t0) + _elapsedBeforeSegment.
    private DateTime _t0;
    private TimeSpan _elapsedBeforeSegment = TimeSpan.Zero;

    private Stage _stage = Stage.Recording;
    private SpeakService? _service;
    private string _accumulatedText = "";
    private string _currentPartial = "";

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
        // OnAudioLevel sets target heights; this timer animates toward them.
        _eqTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _eqTimer.Tick += (_, _) => StepEqualizer();

        Opened += async (_, _) => await OnDialogOpenedAsync();
        Closed += (_, _) => _ = OnDialogClosedAsync();

        // Window-level Enter = Send (insert transcript + auto-submit). Lets the
        // user complete the whole dictation flow with the keyboard only:
        // Ctrl+H from the main window opens this dialog, talk, Enter sends.
        // Escape = Cancel for the same reason.
        KeyDown += SpeakDialog_KeyDown;
    }

    private void SpeakDialog_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;

        if (e.Key == Key.Enter)
        {
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
            await StartNewServiceAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] StartAsync FAILED: {ex.Message}");
            TranscriptText.Text = "Failed to start recording: " + ex.Message;
            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            StatusLabel.Text = "ERROR";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            PrimaryButton.IsVisible = false;
            StopButton.IsVisible = false;
            PauseButton.IsVisible = false;
            CancelButton.Content = "Close";
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
        var svc = new SpeakService(_options);
        svc.OnPartial += OnPartial;
        svc.OnStateChanged += OnStateChanged;
        svc.OnAudioLevel += OnAudioLevel;
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

    private void UpdateTimer()
    {
        if (_stage != Stage.Recording) return;
        var elapsed = (DateTime.UtcNow - _t0) + _elapsedBeforeSegment;
        var s = (int)elapsed.TotalSeconds;
        var tenths = elapsed.Milliseconds / 100;
        TimerLabel.Text = $"{s / 60}:{(s % 60):D2}.{tenths}";
    }

    private void OnAudioLevel(double level)
    {
        // Driven from NAudio's worker thread. Update target heights on the UI
        // thread; the eqTimer animates from current to target.
        Dispatcher.UIThread.Post(() =>
        {
            // Symmetric wave: center bar gets full level, edges get a fraction.
            const double maxH = 48.0;
            const double minH = 6.0;
            int n = _barTargets.Length;
            int center = n / 2;
            for (int i = 0; i < n; i++)
            {
                double distFromCenter = Math.Abs(i - center);
                double t = 1.0 - (distFromCenter / center) * 0.6;
                double h = minH + (maxH - minH) * level * t;
                _barTargets[i] = h;
            }
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
            double step = diff >= 0 ? diff * 0.7 : diff * 0.20;
            var next = current + step;
            if (next < 6.0) next = 6.0;
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
        var combined = JoinText(_accumulatedText, _currentPartial);
        if (string.IsNullOrEmpty(combined))
        {
            TranscriptText.Text = "(your words will appear here)";
            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }
        else
        {
            TranscriptText.Text = combined;
            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        }
    }

    private static string JoinText(string left, string right)
    {
        if (string.IsNullOrEmpty(left)) return right ?? "";
        if (string.IsNullOrEmpty(right)) return left;
        var leftEndsWithSpace = char.IsWhiteSpace(left[^1]);
        var rightStartsWithSpace = char.IsWhiteSpace(right[0]);
        if (leftEndsWithSpace || rightStartsWithSpace) return left + right;
        return left + " " + right;
    }

    private void OnStateChanged(ConnectionState state)
    {
        FileLog.Write($"[SpeakDialog] state -> {state}");
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
            // Already have the accumulated cleaned text; just close.
            ResultText = string.IsNullOrWhiteSpace(_accumulatedText) ? null : _accumulatedText;
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
            ResultText = string.IsNullOrWhiteSpace(_accumulatedText) ? null : _accumulatedText;
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
            _accumulatedText = JoinText(_accumulatedText, result.CleanedTranscript ?? "");
            ResultText = string.IsNullOrWhiteSpace(_accumulatedText) ? null : _accumulatedText;
            ShouldSubmit = submitOnClose && ResultText != null;
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] StopAsync FAILED: {ex.Message}");
            TranscriptText.Text = "Transcription failed: " + ex.Message;
            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            StatusLabel.Text = "ERROR";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            PrimaryButton.IsVisible = false;
            StopButton.IsVisible = false;
            PauseButton.IsVisible = false;
            CancelButton.Content = "Close";
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
            _accumulatedText = JoinText(_accumulatedText, result.CleanedTranscript ?? "");
            _currentPartial = "";
            SwitchToPaused();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] PauseAsync FAILED: {ex.Message}");
            TranscriptText.Text = "Pause failed: " + ex.Message;
            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            StatusLabel.Text = "ERROR";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            PauseButton.IsVisible = false;
        }
    }

    private async Task ResumeAsync()
    {
        FileLog.Write("[SpeakDialog] ResumeAsync");
        PauseButton.IsEnabled = false;
        StatusLabel.Text = "STARTING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        try
        {
            await StartNewServiceAsync();
            _t0 = DateTime.UtcNow;
            SwitchToRecording();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] ResumeAsync FAILED: {ex.Message}");
            TranscriptText.Text = "Could not resume: " + ex.Message;
            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            StatusLabel.Text = "ERROR";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            PauseButton.IsVisible = false;
        }
    }

    private void SwitchToTranscribing()
    {
        _stage = Stage.Transcribing;
        StatusLabel.Text = "TRANSCRIBING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        PrimaryButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 18.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
    }

    private void SwitchToPaused()
    {
        _stage = Stage.Paused;
        StatusLabel.Text = "PAUSED";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        PauseButton.Content = "Resume";
        PauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        PrimaryButton.IsEnabled = true;
        // Park the equalizer bars at a low resting height while paused.
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 6.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
        RenderTranscript();
    }

    private void SwitchToRecording()
    {
        _stage = Stage.Recording;
        StatusLabel.Text = "RECORDING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        PauseButton.Content = BuildPauseIcon();
        PauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        PrimaryButton.IsEnabled = true;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
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
