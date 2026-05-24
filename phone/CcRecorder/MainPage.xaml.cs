using CcRecorder.Recording;
using Microsoft.Maui.Networking;

namespace CcRecorder;

public partial class MainPage : ContentPage
{
    private const string PrefServer = "gateway_url";
    private const string PrefToken = "gateway_token";

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
            UpdateQueueBanner();
        });

        _uiTimer = Dispatcher.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(100); // smooth level meter
        _uiTimer.Tick += (_, _) => { RefreshTimer(); LevelMeter.Progress = _recorder.ReadLevel(); };

        // Re-read the queue from disk periodically so uploads done by the
        // background WorkManager worker (a separate instance) show up live.
        _queueRefreshTimer = Dispatcher.CreateTimer();
        _queueRefreshTimer.Interval = TimeSpan.FromSeconds(2);
        _queueRefreshTimer.Tick += (_, _) => { RefreshLibrary(); UpdateQueueBanner(); };

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
        UpdateQueueBanner();
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
                // Explicit confirmation that it's saved + queued, then the
                // upload runs in the background and the banner tracks it.
                SetBanner("Saved - added to upload queue", "Uploading now in the background...", "#16324A");
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

    // Tapping the status banner forces a sync now.
    private void OnBannerTapped(object? sender, EventArgs e) => _ = ProcessQueueAsync();

    // Tapping a recording shows its transcript (if uploaded) or explains state.
    private async void OnRecordingSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not LibraryRow row) return;
        if (sender is CollectionView cv) cv.SelectedItem = null;

        var isPlaying = _recorder.PlayingRecordingId == row.RecordingId;
        var options = new List<string> { isPlaying ? "Stop playing" : "Play recording" };
        if (!string.IsNullOrWhiteSpace(row.Transcript)) options.Add("Read transcript");
        if (row.State is "Queued" or "Retry") options.Add("Upload now");
        if (!string.IsNullOrWhiteSpace(row.UploadError)) options.Add("Why did upload fail?");

        var choice = await DisplayActionSheet(row.Title, "Cancel", null, options.ToArray());
        switch (choice)
        {
            case "Play recording": _recorder.Play(row.RecordingId); break;
            case "Stop playing": _recorder.StopPlayback(); break;
            case "Read transcript": await DisplayAlert(row.Title, row.Transcript, "Close"); break;
            case "Upload now": await ProcessQueueAsync(); break;
            case "Why did upload fail?": await DisplayAlert("Upload error", row.UploadError ?? "", "OK"); break;
        }
    }

    // ===== background upload queue ==========================================

    private async Task ProcessQueueAsync()
    {
        // The recorder owns the queue logic (shared with the background
        // WorkManager worker) and raises Changed per item, which refreshes the
        // UI. We just kick it and update the banner.
        SaveCreds();
        await _recorder.ProcessUploadQueueAsync();
        UpdateQueueBanner();
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
            .Select(r => new LibraryRow(
                r.RecordingId, r.Title, BuildSubtitle(r, r.RecordingId == playingId),
                r.Transcript, r.State, r.UploadError))
            .ToList();
    }

    // The only status that matters to the user: is it on the server yet.
    private void UpdateQueueBanner()
    {
        var all = _recorder.ListRecordings().Where(r => r.State != "Recording").ToList();
        if (all.Count == 0) { StatusBanner.IsVisible = false; return; }

        int pending = all.Count(r => r.State is "Queued" or "Uploading" or "Retry");

        if (pending == 0)
        {
            SetBanner("All recordings uploaded", "Everything is safe on your server.", "#1C4A2E");
            return;
        }
        if (Connectivity.Current.NetworkAccess == NetworkAccess.None)
        {
            SetBanner($"{pending} waiting to upload", "No network yet. Kept safe on your phone; will upload automatically.", "#4A3A16");
            return;
        }
        SetBanner($"Uploading in the background ({pending} left)", "You can keep recording.", "#16324A");
    }

    private static string BuildSubtitle(RecordingSummary r, bool playing)
    {
        var dur = TimeSpan.FromMilliseconds(r.DurationMs).ToString(@"hh\:mm\:ss");
        var stateText = r.State switch
        {
            "Uploaded" => "Uploaded to server",
            "Uploading" => "Uploading...",
            "Retry" => "Upload failed - tap for details / retry",
            "Recording" => "Recording...",
            _ => "Queued for upload",
        };
        var prefix = playing ? "Playing now  -  " : "";
        return $"{prefix}{dur}  -  {stateText}  -  tap to play";
    }

    private void SetBanner(string title, string detail, string bgColor)
    {
        StatusBanner.IsVisible = true;
        StatusBanner.BackgroundColor = Color.FromArgb(bgColor);
        BannerTitle.Text = title;
        BannerDetail.Text = detail;
    }

    private sealed record LibraryRow(string RecordingId, string Title, string Subtitle, string? Transcript, string State, string? UploadError);
}
