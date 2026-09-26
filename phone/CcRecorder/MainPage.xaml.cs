using CcRecorder.Account;
using CcRecorder.Recording;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Networking;

namespace CcRecorder;

public partial class MainPage : ContentPage
{
    // Status-badge colors for the per-row upload/transcription checkmarks.
    private static readonly Brush CheckDone = new SolidColorBrush(Color.FromArgb("#5FD08A"));   // green
    private static readonly Brush CheckPending = new SolidColorBrush(Color.FromArgb("#3A4358")); // faint gray
    private static readonly Brush CheckFailed = new SolidColorBrush(Color.FromArgb("#E5484D"));  // red

    private readonly IAudioRecorder _recorder;
    private readonly IDispatcherTimer _uiTimer;
    private readonly IDispatcherTimer _queueRefreshTimer;
    // Uploads need a sign-in; without one the periodic kick below would only log "not signed in".
    private bool _signedIn;

    public MainPage(IAudioRecorder recorder)
    {
        InitializeComponent();
        _recorder = recorder;
        _recorder.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            RefreshUi();
            RefreshLibrary();
        });

        _uiTimer = Dispatcher.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(100); // smooth level meter
        _uiTimer.Tick += (_, _) => { RefreshTimer(); LevelMeter.Progress = _recorder.ReadLevel(); };

        // Re-read the queue from disk periodically so uploads done by the
        // background WorkManager worker (a separate instance) show up live.
        _queueRefreshTimer = Dispatcher.CreateTimer();
        _queueRefreshTimer.Interval = TimeSpan.FromSeconds(2);
        _queueRefreshTimer.Tick += (_, _) =>
        {
            RefreshLibrary();
            // Guarantee a freshly-queued recording starts uploading promptly
            // instead of waiting on the OS background scheduler. Only fires for
            // brand-new "Queued" work with nothing already in flight; failed
            // ("Retry") items are left to WorkManager's backoff so we never
            // hammer the server. ProcessUploadQueueAsync is gated, so a repeat
            // call while one is running is a harmless no-op.
            var recs = _recorder.ListRecordings();
            if (_signedIn && recs.Any(r => r.State == "Queued") && recs.All(r => r.State != "Uploading"))
                _ = ProcessQueueAsync();
        };


        // Drain the queue whenever the network comes back.
        Connectivity.Current.ConnectivityChanged += (_, _) =>
            MainThread.BeginInvokeOnMainThread(() => _ = ProcessQueueAsync());
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = RefreshAccountSafelyAsync();
        RefreshUi();
        RefreshLibrary();
        _queueRefreshTimer.Start();
        // Foreground pass for instant feedback, plus the WorkManager path which
        // runs under a wakelock so anything still pending drains even if the OS
        // freezes this app a few seconds after it loses focus.
        _ = ProcessQueueAsync();
        _recorder.EnqueueBackgroundUpload();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _queueRefreshTimer.Stop();
    }

    private async void OnRecordClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_recorder.IsRecording)
            {
                // Capture the title as edited up to this moment, then stop. The
                // field is editable throughout recording; the value at stop wins.
                _recorder.SetTitle(TitleEntry.Text ?? "");
                await _recorder.StopAsync();
                TitleEntry.Text = ""; // reset for the next recording
                _uiTimer.Stop();
                RefreshUi();
                RefreshLibrary();
                // The new recording drops into the list below and shows its own
                // upload progress there; the recording area stays focused on
                // capture, not uploads.
                _ = ProcessQueueAsync();
                return;
            }

            var status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlert("Microphone needed",
                    "CC Recorder needs microphone access to record.", "OK");
                return;
            }

            await _recorder.StartAsync(TitleEntry.Text ?? "");
            _uiTimer.Start();
            RefreshUi();
#if ANDROID
            // Doze ignores wake locks from apps that are not on the battery
            // optimization exemption list, so all-day capture needs BOTH the
            // service's wake lock and this exemption. Shows the system consent
            // dialog; does nothing once granted. Fired after StartAsync so the
            // recording is already running regardless of what the user does
            // with the dialog, and it handles its own errors so a refusal to
            // show the dialog can never read as a recording-start failure.
            await RequestIgnoreBatteryOptimizationsIfNeededAsync();
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlert("Recording error", ex.Message, "OK");
        }
    }

    private void OnPauseClicked(object? sender, EventArgs e)
    {
        if (_recorder.IsPaused) _recorder.Resume();
        else _recorder.Pause();
        RefreshUi();
    }

    private void OnAddNoteClicked(object? sender, EventArgs e)
    {
        var text = NoteEntry.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _recorder.AddNote(text);
        NoteEntry.Text = "";
        RefreshNotes();
    }

    private async void OnSignInClicked(object? sender, EventArgs e)
    {
        RecorderLog.Write("[MainPage] OnSignInClicked");
        SignInButton.IsEnabled = false;
        AccountLabel.Text = "Signing in - finish in the browser, then come back here.";
        try
        {
            await DeviceAccount.SignInAsync(CancellationToken.None);
            await RefreshAccountAsync();
            await ProcessQueueAsync();
        }
        catch (SignInFailedException ex)
        {
            RecorderLog.Write($"[MainPage] OnSignInClicked FAILED: {ex.Message}");
            await RefreshAccountSafelyAsync();
            await DisplayAlert("Sign in", ex.Message, "OK");
        }
        catch (Exception ex)
        {
            RecorderLog.Write($"[MainPage] OnSignInClicked FAILED: {ex.GetType().Name}: {ex.Message}");
            await RefreshAccountSafelyAsync();
            await DisplayAlert("Sign in", "Sign-in failed: " + ex.Message, "OK");
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private async void OnSignOutClicked(object? sender, EventArgs e)
    {
        try
        {
            var sure = await DisplayAlert("Sign out",
                "Recordings stay on this phone and upload after you sign in again.", "Sign out", "Cancel");
            if (!sure) return;
            await DeviceAccount.SignOutAsync();
            await RefreshAccountAsync();
        }
        catch (Exception ex)
        {
            RecorderLog.Write($"[MainPage] OnSignOutClicked FAILED: {ex.Message}");
            await DisplayAlert("Sign out", ex.Message, "OK");
        }
    }

    // Lifecycle entry point: owns its try/catch so a storage failure shows up instead of vanishing.
    private async Task RefreshAccountSafelyAsync()
    {
        try
        {
            await RefreshAccountAsync();
        }
        catch (Exception ex)
        {
            RecorderLog.Write($"[MainPage] RefreshAccountAsync FAILED: {ex.Message}");
            AccountLabel.Text = "Could not read this phone's sign-in: " + ex.Message;
        }
    }

    private async Task RefreshAccountAsync()
    {
        var signedIn = await DeviceAccount.DeviceKeyAsync() is not null;
        _signedIn = signedIn;
        var host = Uri.TryCreate(DeviceAccount.GatewayUrl(), UriKind.Absolute, out var u) ? u.Host : DeviceAccount.GatewayUrl();
        AccountLabel.Text = signedIn
            ? $"Signed in. Recordings upload to {host}."
            : "Not signed in. Recordings are kept on this phone and upload after you sign in.";
        SignInButton.IsVisible = !signedIn;
        SignOutButton.IsVisible = signedIn;
    }

    // Tapping a recording shows its transcript (if uploaded) or explains state.
    private async void OnRecordingSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not LibraryRow row) return;
        if (sender is CollectionView cv) cv.SelectedItem = null;

        var isPlaying = _recorder.PlayingRecordingId == row.RecordingId;
        var options = new List<string> { isPlaying ? "Stop playing" : "Play recording" };
        if (!string.IsNullOrWhiteSpace(row.Transcript)) options.Add("Read transcript");
        if (row.State is "Queued" or "Retry") options.Add("Upload now");
        if (row.State == RecordingUploadGate.NoAudio) options.Add("Why is it empty?");
        else if (row.Interrupted) options.Add("Why was it interrupted?");
        if (!string.IsNullOrWhiteSpace(row.UploadError)) options.Add("Why did upload fail?");
        if (!string.IsNullOrWhiteSpace(row.TranscriptError)) options.Add("Why did transcription fail?");

        var choice = await DisplayActionSheet(row.Title, "Cancel", null, options.ToArray());
        switch (choice)
        {
            case "Play recording": _recorder.Play(row.RecordingId); break;
            case "Stop playing": _recorder.StopPlayback(); break;
            case "Read transcript": await DisplayAlert(row.Title, row.Transcript, "Close"); break;
            case "Upload now": await ProcessQueueAsync(); break;
            case "Why was it interrupted?":
                await DisplayAlert("Recording interrupted",
                    (row.CaptureError ?? "The recording was cut off before it was stopped.")
                    + " Everything that could be saved was kept and queued for upload;"
                    + " the tail of the audio may be missing.", "OK");
                break;
            case "Why is it empty?":
                await DisplayAlert("Nothing recorded", row.CaptureError ?? RecordingUploadGate.NoAudioReason, "OK");
                break;
            case "Why did upload fail?": await DisplayAlert("Upload error", row.UploadError ?? "", "OK"); break;
            case "Why did transcription fail?": await DisplayAlert("Transcription error", row.TranscriptError ?? "", "OK"); break;
        }
    }

    // ===== background upload queue ==========================================

    private async Task ProcessQueueAsync()
    {
        // The recorder owns the queue logic (shared with the background
        // WorkManager worker) and raises Changed per item, which refreshes the
        // per-row progress in the list. We just kick it.
        await _recorder.ProcessUploadQueueAsync();
    }

    private static bool IsOnline()
        => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    // ===== UI refresh =======================================================

    private void RefreshUi()
    {
        var rec = _recorder.IsRecording;
        var paused = _recorder.IsPaused;

        RecordButton.Text = rec ? "Stop" : "Record";
        RecordButton.BackgroundColor = rec ? Color.FromArgb("#6B7280") : Color.FromArgb("#E5484D");

        PauseButton.IsVisible = rec;
        PauseButton.Text = paused ? "Resume" : "Pause";
        PauseButton.BackgroundColor = paused ? Color.FromArgb("#3FA66A") : Color.FromArgb("#2B6CB0");

        StateLabel.Text = !rec ? "Idle" : paused ? "Paused" : "Recording";
        StateLabel.TextColor = !rec ? Color.FromArgb("#5FD08A")
            : paused ? Color.FromArgb("#E8B339") : Color.FromArgb("#E5484D");

        var segs = _recorder.Current?.Chunks.Count ?? 0;
        SegmentLabel.Text = $"{segs} segment(s) captured";
        if (!rec) LevelMeter.Progress = 0;
        RefreshTimer();
        RefreshNotes();
    }

    private void RefreshTimer()
        => TimerLabel.Text = _recorder.Elapsed.ToString(@"hh\:mm\:ss");

    private void RefreshNotes()
    {
        var notes = _recorder.Current?.Notes
            .OrderBy(n => n.TMs)
            .Select(n => $"[{TimeSpan.FromMilliseconds(n.TMs):mm\\:ss}] {n.Text}")
            .ToList() ?? new List<string>();
        NotesList.ItemsSource = notes;
    }

    private void RefreshLibrary()
    {
        var playingId = _recorder.PlayingRecordingId;
        LibraryList.ItemsSource = _recorder.ListRecordings()
            .Select(r =>
            {
                // A determinate bar appears on the row while work is in flight:
                // sending segments (the upload) or, afterwards, server-side
                // transcription. UploadPhase is set during both and cleared when
                // each finishes; the fraction is that phase's count over total.
                var working = !string.IsNullOrEmpty(r.UploadPhase);
                var total = r.UploadTotal > 0 ? r.UploadTotal : r.SegmentCount;
                var progress = working && total > 0
                    ? Math.Clamp((double)r.UploadCurrent / total, 0, 1)
                    : 0;

                // Two status checkmarks per row: upload (bytes on server) and
                // transcription (text produced). Green check = done, faint =
                // pending, red X = failed. They are independent on purpose.
                var uploadStroke = r.State == "Uploaded" ? CheckDone : CheckPending;
                var transFailed = r.TranscriptionState == "Failed";
                var transStroke = r.TranscriptionState == "Transcribed" ? CheckDone : CheckPending;

                return new LibraryRow(
                    r.RecordingId, r.Title, BuildSubtitle(r, r.RecordingId == playingId),
                    r.Transcript, r.State, r.UploadError, r.TranscriptError, progress, working,
                    uploadStroke, transStroke, !transFailed, transFailed,
                    r.Interrupted, r.CaptureError);
            })
            .ToList();
    }

#if ANDROID
    // One-tap system dialog asking to exempt the app from battery optimization.
    // Required for unlimited recording: Doze ignores wake locks from non-exempt
    // apps, so without this the capture CPU is suspended about half an hour
    // after the screen turns off. No-op once granted; asked again on every
    // record start until it is. Deliberately never blocks recording - a denied
    // exemption still records, and a recording that later gets cut off is
    // marked INTERRUPTED rather than hidden.
    private async Task RequestIgnoreBatteryOptimizationsIfNeededAsync()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var pkg = ctx.PackageName;
            if (string.IsNullOrEmpty(pkg)) return;
            var pm = (global::Android.OS.PowerManager?)ctx.GetSystemService(
                global::Android.Content.Context.PowerService);
            if (pm is null || pm.IsIgnoringBatteryOptimizations(pkg)) return;
            var intent = new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                global::Android.Net.Uri.Parse("package:" + pkg));
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            ctx.StartActivity(intent);
        }
        catch (Exception ex)
        {
            // The recording is already running; tell the user exactly how to
            // grant the exemption by hand instead of failing the start.
            await DisplayAlert("Battery optimization",
                "Android refused to show the exemption dialog (" + ex.Message + "). "
                + "Recording continues, but for all-day capture please set it manually: "
                + "Settings > Apps > CC Recorder > Battery > Unrestricted.", "OK");
        }
    }
#endif

    private static string BuildSubtitle(RecordingSummary r, bool playing)
    {
        var dur = TimeSpan.FromMilliseconds(r.DurationMs).ToString(@"hh\:mm\:ss");
        // Upload status first (the bytes-on-server fact). Once uploaded, the
        // transcription sub-status is shown separately so a transcription
        // problem never reads as an upload failure.
        var stateText = r.State switch
        {
            "Recording" => "Recording...",
            "Queued" => "Queued for upload",
            RecordingUploadGate.NoAudio => "Nothing recorded (tap for details)",
            "Uploading" => string.IsNullOrWhiteSpace(r.UploadProgress) ? "Uploading..." : r.UploadProgress,
            "Retry" => "Upload failed - tap to retry",
            "Uploaded" => r.TranscriptionState switch
            {
                "Transcribing" => string.IsNullOrWhiteSpace(r.UploadProgress)
                    ? "Uploaded - transcribing..."
                    : "Uploaded - " + r.UploadProgress,
                "Transcribed" => "Uploaded - transcribed",
                "Failed" => "Uploaded - transcription failed (tap for details)",
                _ => "Uploaded to server",
            },
            _ => r.State,
        };
        var prefix = playing ? "Playing now  -  " : "";
        // A cut-off recording must never read as a complete one: say so before
        // anything else in the row.
        var interrupted = r.Interrupted ? "INTERRUPTED  -  " : "";
        return $"{prefix}{interrupted}{dur}  -  {stateText}  -  tap to play";
    }

    private sealed record LibraryRow(
        string RecordingId, string Title, string Subtitle, string? Transcript,
        string State, string? UploadError, string? TranscriptError, double Progress, bool IsUploading,
        Brush UploadStroke, Brush TransCheckStroke, bool TransShowCheck, bool TransShowX,
        bool Interrupted, string? CaptureError);
}
