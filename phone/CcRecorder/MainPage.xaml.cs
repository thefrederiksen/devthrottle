using CcRecorder.Recording;
using Microsoft.Maui.Networking;

namespace CcRecorder;

public partial class MainPage : ContentPage
{
    private const string PrefServer = "gateway_url";
    private const string PrefToken = "gateway_token";

    private readonly IAudioRecorder _recorder;
    private readonly IDispatcherTimer _uiTimer;

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
        _uiTimer.Interval = TimeSpan.FromMilliseconds(500);
        _uiTimer.Tick += (_, _) => RefreshTimer();

        ServerEntry.Text = Preferences.Get(PrefServer, "");
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
        _ = ProcessQueueAsync();
    }

    private async void OnRecordClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_recorder.IsRecording)
            {
                await _recorder.StopAsync();
                _uiTimer.Stop();
                RefreshUi();
                RefreshLibrary();
                UpdateQueueBanner();
                // It's saved and queued. Upload happens in the background;
                // the user can immediately start another recording.
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

        if (row.State == "Uploaded")
        {
            if (!string.IsNullOrWhiteSpace(row.Transcript))
                await DisplayAlert(row.Title, row.Transcript, "Close");
            else
                await DisplayAlert(row.Title, "Uploaded to your server.", "OK");
        }
        else if (row.State == "Retry")
        {
            await DisplayAlert(row.Title,
                "Saved on your phone. It will upload automatically; tap the status bar to try now.", "OK");
        }
        else
        {
            await DisplayAlert(row.Title, "Queued. It will upload automatically in the background.", "OK");
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
        RecordButton.Text = _recorder.IsRecording ? "Stop" : "Record";
        RecordButton.BackgroundColor = _recorder.IsRecording
            ? Color.FromArgb("#6B7280") : Color.FromArgb("#E5484D");
        StateLabel.Text = _recorder.IsRecording ? "Recording" : "Idle";
        StateLabel.TextColor = _recorder.IsRecording
            ? Color.FromArgb("#E5484D") : Color.FromArgb("#5FD08A");
        var segs = _recorder.Current?.Chunks.Count ?? 0;
        SegmentLabel.Text = $"{segs} segment(s) captured";
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
        LibraryList.ItemsSource = _recorder.ListRecordings()
            .Select(r => new LibraryRow(r.RecordingId, r.Title, BuildSubtitle(r), r.Transcript, r.State))
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
        if (!IsOnline())
        {
            SetBanner($"{pending} waiting to upload", "No internet. Kept safe on your phone; will upload automatically.", "#4A3A16");
            return;
        }
        SetBanner($"Uploading in the background ({pending} left)", "You can keep recording.", "#16324A");
    }

    private static string BuildSubtitle(RecordingSummary r)
    {
        var dur = TimeSpan.FromMilliseconds(r.DurationMs).ToString(@"hh\:mm\:ss");
        var stateText = r.State switch
        {
            "Uploaded" => string.IsNullOrWhiteSpace(r.Transcript) ? "Uploaded to server" : "Uploaded to server  -  tap to read",
            "Uploading" => "Uploading...",
            "Retry" => "Will retry - kept on phone",
            "Recording" => "Recording...",
            _ => "Queued for upload",
        };
        return $"{dur}  -  {stateText}";
    }

    private void SetBanner(string title, string detail, string bgColor)
    {
        StatusBanner.IsVisible = true;
        StatusBanner.BackgroundColor = Color.FromArgb(bgColor);
        BannerTitle.Text = title;
        BannerDetail.Text = detail;
    }

    private sealed record LibraryRow(string RecordingId, string Title, string Subtitle, string? Transcript, string State);
}
