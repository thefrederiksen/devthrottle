namespace CcDirectorClient.Voice;

/// <summary>
/// Voice-mode dictation dialog (Mode A). Owns the recording lifecycle for one
/// utterance and returns the captured audio on SUBMIT or null on CANCEL.
///
/// Pushed as a modal page with a semi-transparent background so the page behind
/// dims into a backdrop and the centred card looks like a dialog rather than a
/// new screen. The bouncing-bars equaliser runs while the mic is recording so
/// the user has a constant visual that the mic is hearing them.
/// </summary>
public partial class VoiceDictationDialog : ContentPage
{
    private readonly IUtteranceRecorder _recorder;
    private readonly TaskCompletionSource<UtteranceAudio?> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        Bars.Stop();
        base.OnDisappearing();
    }

    private async void OnSubmitClicked(object? sender, EventArgs e)
    {
        ClientLog.Write("[VoiceDictationDialog] SUBMIT");
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
        _tcs.TrySetResult(audio);
        try { await Navigation.PopModalAsync(animated: false); }
        catch (Exception ex) { ClientLog.Write($"[VoiceDictationDialog] PopModalAsync FAILED: {ex.Message}"); }
    }
}
