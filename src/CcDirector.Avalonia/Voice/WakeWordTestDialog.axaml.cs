using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Sandbox for the "wingman / wingman send / wingman cancel" hands-free grammar.
/// Records one complete utterance, transcribes it through the Gateway-owned dictation path, pipes the
/// final transcript into a <see cref="WakeWordEngine"/>, and renders every stage of the pipeline into
/// debug panels.
///
/// SAFETY: a committed prompt is written to an on-screen textbox, NEVER to a live
/// session. This is a tuning harness for the detection grammar, not a shipping
/// control surface.
///
/// The held-trailing-token rule in the engine means a phrase ending in "...send"
/// will not commit until the engine is flushed; this dialog flushes on a debounce
/// after ~800 ms of partial silence (the natural pause after "wingman send").
/// </summary>
public partial class WakeWordTestDialog : Window
{
    private const int DebounceMs = 800;

    private readonly AgentOptions _options;
    private readonly Border[] _bars;
    private readonly double[] _barTargets = new double[9];

    private readonly DispatcherTimer _eqTimer;
    private readonly DispatcherTimer _debounceTimer;

    private BatchDictationRecorder? _recorder;
    private WakeWordEngine _engine;
    private bool _listening;
    private int _emittedCount;

    // Set on the interface thread the moment the window closes. The close handler disposes only the published
    // recorder, so a start that was still waiting on the device query when the window closed must see this and
    // build nothing - or dispose what it built - because no later close will ever come for it.
    private bool _closed;

    /// <summary>
    /// TEST SEAM: resolves the default microphone's name in place of the Windows query, so a test can hold the
    /// query while the window closes (inspection six, finding 3). Null in production.
    /// </summary>
    internal Func<Task<string>>? DescribeDefaultDeviceForTests;

    /// <summary>
    /// TEST SEAM: builds the recorder from the resolved device name in place of a real microphone. Null in
    /// production.
    /// </summary>
    internal Func<string, BatchDictationRecorder>? RecorderFactoryForTests;

    public WakeWordTestDialog(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        InitializeComponent();

        _bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8 };

        _engine = new WakeWordEngine();
        _engine.OnEvent += OnEngineEvent;

        _eqTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _eqTimer.Tick += (_, _) => StepEqualizer();

        // Single-shot debounce: restarted on every partial, fires Flush once the
        // stream goes quiet so a phrase ending in "send"/"cancel" settles.
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounceTimer.Tick += OnDebounceTick;

        Closed += (_, _) =>
        {
            _closed = true;
            _ = OnClosedAsync();
        };
    }

    // ===== listen lifecycle =================================================

    private async void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[WakeWordTestDialog] BtnStart_Click");
        if (_listening) return;

        var wake = (WakeWordBox.Text ?? "").Trim();
        try
        {
            _engine = new WakeWordEngine(string.IsNullOrWhiteSpace(wake) ? "wingman" : wake);
        }
        catch (ArgumentException ex)
        {
            AppendLog($"ERROR: invalid wake word: {ex.Message}");
            return;
        }
        _engine.OnEvent += OnEngineEvent;

        RawTranscriptText.Text = "";
        SetState(WakeWordState.Idle);

        StartButton.IsEnabled = false;
        WakeWordBox.IsEnabled = false;
        InjectBox.IsEnabled = false;
        FeedButton.IsEnabled = false;

        // Declared outside the try so the catch can dispose the instance it actually built. The old
        // code called DisposeRecorderAsync(), which reads the _recorder FIELD - still null at that
        // point, because the assignment below had not run. So a failed start leaked a recorder that
        // owns a live NAudio capture thread, which roots it forever and keeps it recording.
        BatchDictationRecorder? recorder = null;
        try
        {
            // The device name is a Windows query; resolve it off the interface thread (issue #2929).
            var description = DescribeDefaultDeviceForTests is null
                ? await Task.Run(() => MicDevices.DescribeDevice(MicDevices.DefaultDeviceNumber))
                : await DescribeDefaultDeviceForTests();
            // The window can close while the query runs. Its close handler has already run and found no
            // recorder, so a microphone opened now would have no owner left to stop it (inspection six, finding 3).
            if (_closed)
            {
                FileLog.Write("[WakeWordTestDialog] window closed during the device query; no microphone opened");
                return;
            }
            recorder = RecorderFactoryForTests is null
                ? new BatchDictationRecorder(_options, MicDevices.DefaultDeviceNumber, description)
                : RecorderFactoryForTests(description);
            recorder.OnAudioBands += OnAudioBands;
            await recorder.StartAsync("default");
            // Same for a close that lands while the microphone starts: this start owns the recorder until it is
            // published, so it stops it here.
            if (_closed)
            {
                FileLog.Write("[WakeWordTestDialog] window closed while the microphone started; stopping it");
                recorder.OnAudioBands -= OnAudioBands;
                await recorder.DisposeAsync();
                return;
            }
            _recorder = recorder;
            _listening = true;
            StopButton.IsEnabled = true;
            _eqTimer.Start();
            SetBarColor(Color.FromRgb(0x16, 0xA3, 0x4A));
            AppendLog("listening...");
            FileLog.Write("[WakeWordTestDialog] listening");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WakeWordTestDialog] StartAsync FAILED: {ex.Message}");
            AppendLog($"ERROR: could not start microphone: {ex.Message}");
            if (recorder is not null)
            {
                recorder.OnAudioBands -= OnAudioBands;
                try { await recorder.DisposeAsync(); }
                catch (Exception disposeEx) { FileLog.Write($"[WakeWordTestDialog] failed-start dispose error: {disposeEx.Message}"); }
            }
            _recorder = null;
            StartButton.IsEnabled = true;
            WakeWordBox.IsEnabled = true;
            InjectBox.IsEnabled = true;
            FeedButton.IsEnabled = true;
        }
    }

    private async void BtnStop_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[WakeWordTestDialog] BtnStop_Click");
        await StopListeningAsync();
    }

    private async System.Threading.Tasks.Task StopListeningAsync()
    {
        _debounceTimer.Stop();
        _eqTimer.Stop();
        _listening = false;
        try
        {
            var recorder = _recorder;
            _recorder = null;
            if (recorder is not null)
            {
                try
                {
                    recorder.OnAudioBands -= OnAudioBands;
                    var result = await recorder.TranscribeAsync();
                    OnFinalTranscript(result.CleanedTranscript);
                }
                finally
                {
                    await recorder.DisposeAsync();
                }
            }
            _engine.Flush();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WakeWordTestDialog] stop/transcribe failed: {ex.Message}");
            AppendLog($"ERROR: transcription failed: {ex.Message}");
        }
        ParkBars();
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = true;
        WakeWordBox.IsEnabled = true;
        InjectBox.IsEnabled = true;
        FeedButton.IsEnabled = true;
        AppendLog("stopped.");
    }

    private async System.Threading.Tasks.Task DisposeRecorderAsync()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is null) return;
        recorder.OnAudioBands -= OnAudioBands;
        try { await recorder.DisposeAsync(); }
        catch (Exception ex) { FileLog.Write($"[WakeWordTestDialog] dispose error: {ex.Message}"); }
    }

    private async System.Threading.Tasks.Task OnClosedAsync()
    {
        _debounceTimer.Stop();
        _eqTimer.Stop();
        await DisposeRecorderAsync();
    }

    // ===== transcript -> engine =============================================

    private void OnFinalTranscript(string transcript)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RawTranscriptText.Text = transcript;
            RawScroller.ScrollToEnd();
            try { _engine.Feed(transcript ?? ""); }
            catch (Exception ex) { FileLog.Write($"[WakeWordTestDialog] engine.Feed threw: {ex.Message}"); }
            try { _engine.Flush(); }
            catch (Exception ex) { FileLog.Write($"[WakeWordTestDialog] engine.Flush threw: {ex.Message}"); }
        });
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        try { _engine.Flush(); }
        catch (Exception ex) { FileLog.Write($"[WakeWordTestDialog] Flush on debounce threw: {ex.Message}"); }
    }

    // ===== engine events -> UI ==============================================

    private void OnEngineEvent(WakeWordEvent ev)
    {
        // The engine raises on the thread that called Feed/Flush. Those are the UI
        // thread (OnPartial posts there; the debounce + inject run on it), but post
        // defensively so a future off-thread caller cannot touch UI directly.
        Dispatcher.UIThread.Post(() =>
        {
            switch (ev.Kind)
            {
                case WakeWordEventKind.WakeDetected:
                    SetState(WakeWordState.Capturing);
                    CapturedPromptText.Text = "";
                    AppendLog("WAKE detected -> capturing");
                    break;

                case WakeWordEventKind.BodyUpdated:
                    CapturedPromptText.Text = ev.Text;
                    AppendLog($"body: \"{ev.Text}\"");
                    break;

                case WakeWordEventKind.Committed:
                    SetState(WakeWordState.Idle);
                    CapturedPromptText.Text = "";
                    _emittedCount++;
                    EmittedOutputText.Text = $"[{_emittedCount}] {ev.Text}";
                    EmittedOutputText.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xAA));
                    AppendLog($"COMMIT -> emitted: \"{ev.Text}\"");
                    break;

                case WakeWordEventKind.Cancelled:
                    SetState(WakeWordState.Idle);
                    CapturedPromptText.Text = "";
                    AppendLog("CANCEL -> body discarded");
                    break;

                case WakeWordEventKind.ControlIgnored:
                    AppendLog($"ignored: {ev.Reason}");
                    break;
            }
        });
    }

    // ===== inject (drive engine without a mic) ==============================

    private void BtnFeed_Click(object? sender, RoutedEventArgs e)
    {
        if (_listening) return; // inject is disabled while the mic is live
        var text = (InjectBox.Text ?? "").Trim();
        if (text.Length == 0) return;

        var wake = (WakeWordBox.Text ?? "").Trim();
        try
        {
            _engine = new WakeWordEngine(string.IsNullOrWhiteSpace(wake) ? "wingman" : wake);
        }
        catch (ArgumentException ex)
        {
            AppendLog($"ERROR: invalid wake word: {ex.Message}");
            return;
        }
        _engine.OnEvent += OnEngineEvent;

        SetState(WakeWordState.Idle);
        RawTranscriptText.Text = text;
        AppendLog($"inject (one-shot): \"{text}\"");
        // One-shot: feed the whole typed transcript, then flush to settle the tail.
        _engine.Feed(text);
        _engine.Flush();
        InjectBox.Text = "";
    }

    // ===== clear / close ====================================================

    private void BtnClear_Click(object? sender, RoutedEventArgs e)
    {
        DetectorLogText.Text = "";
        RawTranscriptText.Text = _listening ? "" : "(transcript will appear here)";
        CapturedPromptText.Text = "";
        EmittedOutputText.Text = "(nothing emitted yet)";
        EmittedOutputText.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xAA));
        _emittedCount = 0;
        _engine.Reset();
        SetState(WakeWordState.Idle);
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    private void BtnHelp_Click(object? sender, RoutedEventArgs e)
        => HelpOverlay.IsVisible = !HelpOverlay.IsVisible;

    private void BtnHelpClose_Click(object? sender, RoutedEventArgs e)
        => HelpOverlay.IsVisible = false;

    // ===== UI helpers =======================================================

    private void SetState(WakeWordState state)
    {
        if (state == WakeWordState.Capturing)
        {
            StateLabel.Text = "CAPTURING";
            StateLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            StateBadge.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x1E, 0x1E));
        }
        else
        {
            StateLabel.Text = "IDLE";
            StateLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            StateBadge.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3A));
        }
    }

    private void AppendLog(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var existing = DetectorLogText.Text ?? "";
        var sb = new StringBuilder(existing.Length + line.Length + 16);
        if (existing.Length > 0 && existing != "(detector events will appear here)")
            sb.Append(existing).Append('\n');
        sb.Append(stamp).Append("  ").Append(line);
        DetectorLogText.Text = sb.ToString();
        LogScroller.ScrollToEnd();
    }

    // ===== equalizer (mirrors SpeakDialog) ==================================

    private void OnAudioBands(double[] bands)
    {
        Dispatcher.UIThread.Post(() =>
        {
            const double maxH = 28.0;
            const double minH = 4.0;
            int n = Math.Min(_barTargets.Length, bands.Length);
            for (int i = 0; i < n; i++)
            {
                double level = Math.Clamp(bands[i], 0.0, 1.0);
                _barTargets[i] = minH + (maxH - minH) * level;
            }
        });
    }

    private void StepEqualizer()
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            var current = _bars[i].Height;
            var target = _barTargets[i];
            var diff = target - current;
            double step = diff >= 0 ? diff * 0.7 : diff * 0.32;
            var next = current + step;
            if (next < 4.0) next = 4.0;
            _bars[i].Height = next;
        }
    }

    private void SetBarColor(Color color)
    {
        var brush = new SolidColorBrush(color);
        foreach (var bar in _bars) bar.Background = brush;
    }

    private void ParkBars()
    {
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 4.0;
        foreach (var bar in _bars) { bar.Height = 4.0; bar.Background = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A)); }
    }
}
