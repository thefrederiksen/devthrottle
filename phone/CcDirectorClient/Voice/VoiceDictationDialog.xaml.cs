namespace CcDirectorClient.Voice;

/// <summary>
/// Voice-mode dictation dialog (Mode A). Owns the recording lifecycle for one
/// utterance and returns the captured audio on SUBMIT or null on CANCEL.
///
/// Pushed as a modal page with a semi-transparent background so the page behind
/// dims into a backdrop and the centred card looks like a dialog rather than a
/// new screen. The bouncing-bars equaliser runs while the mic is recording so
/// the user has a constant visual that the mic is hearing them.
///
/// Issue #347 additions:
///   - Elapsed m:ss timer, 100 ms ticks, starts when recording starts.
///   - RECORDING label (red, 24pt) -> "Recorded" (green) on SUBMIT.
///   - Pulsing red dot at ~1 Hz during recording.
///   - Amber "Speak up" hint when mic level is below VoicePresentLevel for > 2 s.
/// </summary>
public partial class VoiceDictationDialog : ContentPage
{
    private static readonly TimeSpan TimerInterval  = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PulseInterval  = TimeSpan.FromMilliseconds(500);

    // ReadLevel() returns 0..1. Below this threshold and the mic is considered
    // silent / too quiet for clean capture (mirrors the desktop VoicePresentRms
    // threshold: desktop uses 20.0 / 32767 raw int16, mobile maps to ~0.025 on
    // the sqrt-scaled 0..1 range: sqrt(20/32767) ~ 0.025).
    private const double VoicePresentLevel = 0.025;
    // How long the level must stay below VoicePresentLevel before the hint fires.
    private static readonly TimeSpan LowLevelGracePeriod = TimeSpan.FromSeconds(2);

    private readonly IUtteranceRecorder _recorder;
    private readonly TaskCompletionSource<UtteranceAudio?> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IDispatcherTimer? _timer;
    private IDispatcherTimer? _pulseTimer;
    private DateTime _startedUtc;
    private DateTime? _lowLevelSince;
    private bool _pulseVisible;

    // Guards against the user double-tapping SUBMIT (or the OS firing an extra
    // Disappearing after a button click). The mic is stopped exactly once and
    // the page is popped exactly once.
    private bool _resolved;

    public VoiceDictationDialog(IUtteranceRecorder recorder)
    {
        InitializeComponent();
        _recorder = recorder;
    }

    /// <summary>
    /// Completes with the captured audio on SUBMIT, or null on CANCEL, back-gesture,
    /// or a mic that could not start.
    /// </summary>
    public Task<UtteranceAudio?> Result => _tcs.Task;

    /// <summary>
    /// Convenience: push the dialog modally on <paramref name="navigation"/> and
    /// await its result. The dialog pops itself before the task completes so the
    /// caller never sees the dialog still on the stack.
    /// </summary>
    public static async Task<UtteranceAudio?> PromptAsync(
        INavigation navigation, IUtteranceRecorder recorder)
    {
        var dialog = new VoiceDictationDialog(recorder);
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

            // Start elapsed timer.
            _startedUtc = DateTime.UtcNow;
            TimerLabel.Text = "0:00";
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimerInterval;
            _timer.Tick += OnTimerTick;
            _timer.Start();

            // Start pulsing dot.
            _pulseVisible = true;
            PulseDot.Opacity = 1.0;
            _pulseTimer = Dispatcher.CreateTimer();
            _pulseTimer.Interval = PulseInterval;
            _pulseTimer.Tick += OnPulseTick;
            _pulseTimer.Start();

            ClientLog.Write("[VoiceDictationDialog] mic on");
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[VoiceDictationDialog] StartAsync FAILED: {ex.Message}");
            // The mic could not start (denied permission mid-flight, busy, etc.).
            // Resolve as a Cancel so the caller can surface a permission prompt or
            // back off - we never silently return an "empty" success.
            await ResolveAsync(null);
        }
    }

    protected override void OnDisappearing()
    {
        StopTimers();
        Bars.Stop();
        base.OnDisappearing();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // Update elapsed label.
        var elapsed = DateTime.UtcNow - _startedUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes >= 100) elapsed = TimeSpan.FromMinutes(99) + TimeSpan.FromSeconds(59);
        TimerLabel.Text = elapsed.ToString(@"m\:ss");

        // Level-hint tracking: sample the recorder once per 100 ms tick.
        // The hint fires when input has been continuously below VoicePresentLevel
        // for at least LowLevelGracePeriod - enough time to distinguish a brief
        // silent pause from genuinely inaudible input.
        if (!_resolved)
        {
            var level = _recorder.ReadLevel();
            if (level < VoicePresentLevel)
            {
                // Level is low: start (or keep) tracking the quiet stretch.
                _lowLevelSince ??= DateTime.UtcNow;

                if (DateTime.UtcNow - _lowLevelSince.Value >= LowLevelGracePeriod)
                {
                    LevelHintLabel.Text = "Speak up or move closer to the mic";
                    LevelHintLabel.IsVisible = true;
                }
            }
            else
            {
                // Level is healthy: reset the quiet-streak tracker and hide the hint.
                _lowLevelSince = null;
                LevelHintLabel.Text = "";
                LevelHintLabel.IsVisible = false;
            }
        }
    }

    private void OnPulseTick(object? sender, EventArgs e)
    {
        _pulseVisible = !_pulseVisible;
        PulseDot.Opacity = _pulseVisible ? 1.0 : 0.0;
    }

    private async void OnSubmitClicked(object? sender, EventArgs e)
    {
        ClientLog.Write("[VoiceDictationDialog] SUBMIT");

        // Transition label to "Recorded" immediately so the user gets instant
        // visual confirmation that the clip was accepted.
        RecordingLabel.Text = "Recorded";
        RecordingLabel.TextColor = Color.FromArgb("#5FD08A");
        PulseDot.IsVisible = false;
        LevelHintLabel.IsVisible = false;

        UtteranceAudio? audio = null;
        try
        {
            if (_recorder.IsRecording)
                audio = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[VoiceDictationDialog] StopAsync on SUBMIT FAILED: {ex.Message}");
        }
        await ResolveAsync(audio);
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        ClientLog.Write("[VoiceDictationDialog] CANCEL");
        try
        {
            if (_recorder.IsRecording)
                _ = await _recorder.StopAsync();
        }
        catch { /* discarding a half-captured clip */ }
        await ResolveAsync(null);
    }

    protected override bool OnBackButtonPressed()
    {
        // Hardware back / system back gesture is treated as CANCEL so a back press
        // does not leave a hot mic behind.
        if (_resolved) return base.OnBackButtonPressed();
        _ = ResolveAsync(null);
        return true;
    }

    private async Task ResolveAsync(UtteranceAudio? audio)
    {
        if (_resolved) return;
        _resolved = true;
        StopTimers();
        _tcs.TrySetResult(audio);
        try { await Navigation.PopModalAsync(animated: false); }
        catch (Exception ex) { ClientLog.Write($"[VoiceDictationDialog] PopModalAsync FAILED: {ex.Message}"); }
    }

    private void StopTimers()
    {
        if (_timer is not null)
        {
            _timer.Tick -= OnTimerTick;
            _timer.Stop();
            _timer = null;
        }
        if (_pulseTimer is not null)
        {
            _pulseTimer.Tick -= OnPulseTick;
            _pulseTimer.Stop();
            _pulseTimer = null;
        }
    }
}
