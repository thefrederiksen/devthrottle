using CcDirectorClient.Recording;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Networking;

namespace CcDirectorClient;

public partial class MainPage : ContentPage
{
    private const string PrefServer = "gateway_url";
    private const string PrefToken = "gateway_token";

    // Status-badge colors for the per-row upload/transcription checkmarks.
    private static readonly Brush CheckDone = new SolidColorBrush(Color.FromArgb("#5FD08A"));   // green
    private static readonly Brush CheckPending = new SolidColorBrush(Color.FromArgb("#3A4358")); // faint gray
    private static readonly Brush CheckFailed = new SolidColorBrush(Color.FromArgb("#E5484D"));  // red

    private readonly IAudioRecorder _recorder;
    private readonly IDispatcherTimer _uiTimer;
    private readonly IDispatcherTimer _queueRefreshTimer;

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
            if (recs.Any(r => r.State == "Queued") && recs.All(r => r.State != "Uploading"))
                _ = ProcessQueueAsync();
        };

        // Seed the gateway URL on first run (or after a reinstall that wiped
        // preferences) so recordings upload without manual setup. Editable.
        var savedServer = Preferences.Get(PrefServer, "");
        if (string.IsNullOrWhiteSpace(savedServer))
        {
            savedServer = RecorderDefaults.GatewayUrl;
            Preferences.Set(PrefServer, savedServer);
        }
        ServerEntry.Text = savedServer;
        TokenEntry.Text = Preferences.Get(PrefToken, "");

        // Drain the queue whenever the network comes back.
        Connectivity.Current.ConnectivityChanged += (_, _) =>
            MainThread.BeginInvokeOnMainThread(() => _ = ProcessQueueAsync());
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
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
                    "DevThrottle Client needs microphone access to record.", "OK");
                return;
            }

            await _recorder.StartAsync(TitleEntry.Text ?? "");
            _uiTimer.Start();
            RefreshUi();
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

    private void OnCredsChanged(object? sender, FocusEventArgs e)
    {
        SaveCreds();
        _ = ProcessQueueAsync();
    }

    // Tapping a recording shows its transcript (if uploaded) or explains state.
    private async void OnRecordingSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not LibraryRow row) return;
        if (sender is CollectionView cv) cv.SelectedItem = null;

        var isPlaying = _recorder.PlayingRecordingId == row.RecordingId;
        var options = new List<string> { isPlaying ? "Stop" : "Play" };
        if (!string.IsNullOrWhiteSpace(row.Transcript)) options.Add("Read");
        if (row.State is "Queued" or "Retry") options.Add("Sync now");
        if (!string.IsNullOrWhiteSpace(row.UploadError)) options.Add("Why did sync fail?");
        if (!string.IsNullOrWhiteSpace(row.TranscriptError)) options.Add("Why did processing fail?");

        var choice = await DisplayActionSheet(row.Title, "Cancel", null, options.ToArray());
        switch (choice)
        {
            case "Play": _recorder.Play(row.RecordingId); break;
            case "Stop": _recorder.StopPlayback(); break;
            case "Read": await DisplayAlert(row.Title, row.Transcript, "Close"); break;
            case "Sync now": await ProcessQueueAsync(); break;
            case "Why did sync fail?": await DisplayAlert("Sync error", row.UploadError ?? "", "OK"); break;
            case "Why did processing fail?": await DisplayAlert("Processing error", row.TranscriptError ?? "", "OK"); break;
        }
    }

    // ===== background upload queue ==========================================

    private async Task ProcessQueueAsync()
    {
        // The recorder owns the queue logic (shared with the background
        // WorkManager worker) and raises Changed per item, which refreshes the
        // per-row progress in the list. We just kick it.
        SaveCreds();
        await _recorder.ProcessUploadQueueAsync();
    }

    private static bool IsOnline()
        => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    private void SaveCreds()
    {
        Preferences.Set(PrefServer, (ServerEntry.Text ?? "").Trim());
        Preferences.Set(PrefToken, (TokenEntry.Text ?? "").Trim());
    }

    // ===== UI refresh =======================================================

    private void RefreshUi()
    {
        var rec = _recorder.IsRecording;
        var paused = _recorder.IsPaused;

        RecordButton.Text = rec ? "Stop" : "Start";
        RecordButton.BackgroundColor = rec ? Color.FromArgb("#1F4E79") : Color.FromArgb("#2B6CB0");

        PauseButton.IsVisible = rec;
        PauseButton.Text = paused ? "Resume" : "Pause";
        PauseButton.BackgroundColor = paused ? Color.FromArgb("#3FA66A") : Color.FromArgb("#2B6CB0");

        // Discreet running cue only: a small neutral dot while active. No red and no
        // "recording" wording, so a glance does not reveal audio capture. The hidden
        // StateLabel is left blank.
        StatusDot.IsVisible = rec;

        var segs = _recorder.Current?.Chunks.Count ?? 0;
        SegmentLabel.Text = $"{segs} segment(s) captured";
        if (!rec) LevelMeter.Progress = 0;
        RefreshTimer();
        RefreshNotes();
    }

    private void RefreshTimer()
        => TimerLabel.Text = _recorder.IsRecording
            ? _recorder.Elapsed.ToString(@"hh\:mm\:ss")
            : "";

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
                    uploadStroke, transStroke, !transFailed, transFailed);
            })
            .ToList();
    }

    private static string BuildSubtitle(RecordingSummary r, bool playing)
    {
        var dur = TimeSpan.FromMilliseconds(r.DurationMs).ToString(@"hh\:mm\:ss");
        // Upload status first (the bytes-on-server fact). Once uploaded, the
        // transcription sub-status is shown separately so a transcription
        // problem never reads as an upload failure.
        var stateText = r.State switch
        {
            "Recording" => "In progress...",
            "Queued" => "Pending sync",
            "Uploading" => string.IsNullOrWhiteSpace(r.UploadProgress) ? "Syncing..." : r.UploadProgress,
            "Retry" => "Sync failed - tap to retry",
            "Uploaded" => r.TranscriptionState switch
            {
                "Transcribing" => string.IsNullOrWhiteSpace(r.UploadProgress)
                    ? "Synced - processing..."
                    : "Synced - " + r.UploadProgress,
                "Transcribed" => "Synced",
                "Failed" => "Synced - processing failed (tap for details)",
                _ => "Synced",
            },
            _ => r.State,
        };
        var prefix = playing ? "Open  -  " : "";
        return $"{prefix}{dur}  -  {stateText}  -  tap to open";
    }

    // Top-right burger menu: switch between the Talk, Recorder, Exes, Dictionary and Transcripts pages.
    private async void OnNavMenuClicked(object? sender, TappedEventArgs e)
    {
        var choice = await DisplayActionSheet("Go to", "Cancel", null, "Sessions", "Notes", "Exes", "Dictionary", "Transcripts");
        if (choice == "Sessions")
            await Shell.Current.GoToAsync("//TalkPage");
        else if (choice == "Notes")
            await Shell.Current.GoToAsync("//MainPage");
        else if (choice == "Exes")
            await Shell.Current.GoToAsync("//ExesPage");
        else if (choice == "Dictionary")
            await Shell.Current.GoToAsync("//DictionaryPage");
        else if (choice == "Transcripts")
            await Shell.Current.GoToAsync("//TranscriptsPage");
    }

    private sealed record LibraryRow(
        string RecordingId, string Title, string Subtitle, string? Transcript,
        string State, string? UploadError, string? TranscriptError, double Progress, bool IsUploading,
        Brush UploadStroke, Brush TransCheckStroke, bool TransShowCheck, bool TransShowX);
}
