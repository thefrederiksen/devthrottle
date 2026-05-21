using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Modal dialog for one-shot dictation. Opens already recording; user clicks
/// Stop to finish; dialog awaits cleanup and shows the cleaned transcript;
/// "Use it" closes returning the text, "Cancel" closes returning null.
///
/// All audio capture and library orchestration happens in-process via
/// <see cref="SpeakService"/>. No browser, no localhost WebSocket roundtrip.
/// </summary>
public partial class SpeakDialog : Window
{
    private enum Stage { Recording, Transcribing }

    private readonly AgentOptions _options;
    private readonly SpeakService _service;
    private readonly Border[] _bars;
    private readonly double[] _barTargets = new double[9];
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _eqTimer;
    private DateTime _t0;
    private Stage _stage = Stage.Recording;
    private string? _cleanedResult;

    /// <summary>
    /// The cleaned transcript the user accepted. Null if cancelled.
    /// Read after <c>ShowDialog(...)</c> returns.
    /// </summary>
    public string? ResultText => _cleanedResult;

    public SpeakDialog(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        InitializeComponent();
        _bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8 };
        _service = new SpeakService(options);
        _service.OnPartial += OnPartial;
        _service.OnStateChanged += OnStateChanged;
        _service.OnAudioLevel += OnAudioLevel;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => UpdateTimer();

        // Decay the equalizer bars at a steady rate so they fall smoothly.
        // OnAudioLevel sets target heights; this timer animates toward them.
        _eqTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _eqTimer.Tick += (_, _) => StepEqualizer();

        Opened += async (_, _) => await OnDialogOpenedAsync();
        Closed += (_, _) => _ = OnDialogClosedAsync();
    }

    private async Task OnDialogOpenedAsync()
    {
        _t0 = DateTime.UtcNow;
        _timer.Start();
        _eqTimer.Start();
        try
        {
            await _service.StartAsync("default");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] StartAsync FAILED: {ex.Message}");
            TranscriptText.Text = "Failed to start recording: " + ex.Message;
            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            StatusLabel.Text = "ERROR";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            HintLabel.Text = "Click Cancel to close.";
            PrimaryButton.IsVisible = false;
            CancelButton.Content = "Close";
        }
    }

    private async Task OnDialogClosedAsync()
    {
        _timer.Stop();
        _eqTimer.Stop();
        try { await _service.DisposeAsync(); }
        catch (Exception ex) { FileLog.Write($"[SpeakDialog] dispose error: {ex.Message}"); }
    }

    private void UpdateTimer()
    {
        if (_stage != Stage.Recording) return;
        var elapsed = DateTime.UtcNow - _t0;
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
            if (_stage == Stage.Recording || _stage == Stage.Transcribing)
            {
                TranscriptText.Text = string.IsNullOrEmpty(partial)
                    ? "(your words will appear here)"
                    : partial;
                TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            }
        });
    }

    private void OnStateChanged(ConnectionState state)
    {
        FileLog.Write($"[SpeakDialog] state -> {state}");
    }

    private async void PrimaryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Recording)
            await StopRecordingAsync();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        _cleanedResult = null;
        Close();
    }

    private async Task StopRecordingAsync()
    {
        SwitchToTranscribing();
        try
        {
            var result = await _service.StopAsync();
            // Auto-insert: caller reads ResultText after the dialog closes.
            _cleanedResult = string.IsNullOrWhiteSpace(result.CleanedTranscript)
                ? null
                : result.CleanedTranscript;
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] StopAsync FAILED: {ex.Message}");
            TranscriptText.Text = "Transcription failed: " + ex.Message;
            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            StatusLabel.Text = "ERROR";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            HintLabel.Text = "Click Cancel to close.";
            PrimaryButton.IsVisible = false;
            CancelButton.Content = "Close";
        }
    }

    private void SwitchToTranscribing()
    {
        _stage = Stage.Transcribing;
        StatusLabel.Text = "TRANSCRIBING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        HintLabel.Text = "Running cleanup pass through gpt-4.1-nano...";
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = "Wait...";
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 18.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
    }
}
