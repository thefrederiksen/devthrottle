namespace CcDirectorClient.Voice;

/// <summary>What the user picked on the speak-into-textbox dialog.</summary>
public enum SpeakAction
{
    /// <summary>Throw the recording away; leave the text box untouched.</summary>
    Cancel,
    /// <summary>Drop the cleaned transcript into the text box, no auto-send.</summary>
    Insert,
    /// <summary>Drop the cleaned transcript into the text box AND auto-send.</summary>
    Send,
}

/// <summary>Outcome of one speak-into-textbox dialog turn.</summary>
public sealed record SpeakDictationResult(SpeakAction Action, UtteranceAudio? Audio);

/// <summary>
/// Speak-into-textbox dictation dialog (Mode B). Same shared bouncing-bars equaliser
/// as the voice-mode dialog, plus a running mm:ss timer and three explicit actions
/// (Cancel / Insert / Send) so the user can choose to review the dictated text
/// before sending or commit straight through.
///
/// Always returns a <see cref="SpeakDictationResult"/>: <see cref="SpeakAction.Cancel"/>
/// carries a null Audio; the other two carry the captured bytes. Transcription itself
/// happens server-side after the dialog closes - the dialog's job is to record one
/// utterance and report what the user wanted done with it.
/// </summary>
public partial class SpeakIntoTextboxDialog : ContentPage
{
    private static readonly TimeSpan TimerInterval = TimeSpan.FromMilliseconds(100);

    private readonly IUtteranceRecorder _recorder;
    private readonly TaskCompletionSource<SpeakDictationResult> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IDispatcherTimer? _timer;
    private DateTime _startedUtc;
    private bool _resolved;

    public SpeakIntoTextboxDialog(IUtteranceRecorder recorder)
    {
        InitializeComponent();
        _recorder = recorder;
    }

    /// <summary>Completes with the user's chosen action plus the captured audio.</summary>
    public Task<SpeakDictationResult> Result => _tcs.Task;

    /// <summary>
    /// Convenience: push the dialog modally on <paramref name="navigation"/> and
    /// await the result. The dialog pops itself before the task completes.
    /// </summary>
    public static async Task<SpeakDictationResult> PromptAsync(
        INavigation navigation, IUtteranceRecorder recorder)
    {
        var dialog = new SpeakIntoTextboxDialog(recorder);
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
            _startedUtc = DateTime.UtcNow;
            TimerLabel.Text = "0:00";
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimerInterval;
            _timer.Tick += OnTimerTick;
            _timer.Start();
            ClientLog.Write("[SpeakIntoTextboxDialog] mic on");
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[SpeakIntoTextboxDialog] StartAsync FAILED: {ex.Message}");
            await ResolveAsync(SpeakAction.Cancel, audio: null);
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
        var elapsed = DateTime.UtcNow - _startedUtc;
        // Capped at 99:59 visually; recordings that long are misuse and the cap
        // just keeps the label from overflowing the row.
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes >= 100) elapsed = TimeSpan.FromMinutes(99) + TimeSpan.FromSeconds(59);
        TimerLabel.Text = elapsed.ToString(@"m\:ss");
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        ClientLog.Write("[SpeakIntoTextboxDialog] SEND");
        await StopAndResolveAsync(SpeakAction.Send);
    }

    private async void OnInsertClicked(object? sender, EventArgs e)
    {
        ClientLog.Write("[SpeakIntoTextboxDialog] INSERT");
        await StopAndResolveAsync(SpeakAction.Insert);
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
        await ResolveAsync(SpeakAction.Cancel, audio: null);
    }

    protected override bool OnBackButtonPressed()
    {
        // Hardware back / system back gesture is a Cancel so a back press never
        // leaves a hot mic.
        if (_resolved) return base.OnBackButtonPressed();
        _ = ResolveAsync(SpeakAction.Cancel, audio: null);
        return true;
    }

    private async Task StopAndResolveAsync(SpeakAction action)
    {
        UtteranceAudio? audio = null;
        try
        {
            if (_recorder.IsRecording)
                audio = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[SpeakIntoTextboxDialog] StopAsync on {action} FAILED: {ex.Message}");
        }
        // If the stop failed there is nothing to send; surface that as a Cancel
        // so the caller does not try to transcribe an empty clip.
        if (audio is null)
            await ResolveAsync(SpeakAction.Cancel, audio: null);
        else
            await ResolveAsync(action, audio);
    }

    private async Task ResolveAsync(SpeakAction action, UtteranceAudio? audio)
    {
        if (_resolved) return;
        _resolved = true;
        StopTimer();
        _tcs.TrySetResult(new SpeakDictationResult(action, audio));
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
