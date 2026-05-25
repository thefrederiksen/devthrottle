using CcDirectorClient.Recording;
using CcDirectorClient.Voice;

namespace CcDirectorClient;

/// <summary>
/// The voice client's main screen. Lists the sessions from the Gateway and lets
/// the user pick one to talk to: push Talk to start capturing, push Send to stop
/// and run the round-trip (transcribe -> chat -> native TTS). The screen is kept
/// awake while it is in front (Waze-style) so a glance check does not require
/// unlocking.
/// </summary>
public partial class TalkPage : ContentPage
{
    private const string PrefServer = "gateway_url";
    private const string PrefToken = "gateway_token";

    private static readonly Color DotGreen = Color.FromArgb("#5FD08A");
    private static readonly Color DotBlue = Color.FromArgb("#2B6CB0");
    private static readonly Color DotYellow = Color.FromArgb("#E8B339");
    private static readonly Color DotRed = Color.FromArgb("#E5484D");
    private static readonly Color DotGray = Color.FromArgb("#5A6378");

    private readonly IUtteranceRecorder _recorder;
    private readonly IReplySpeaker _tts;
    private readonly IVoiceForeground _foreground;

    private SessionInfo? _selected;
    private bool _busy;

    // Cancels the in-flight turn (including a wait for a busy session to finish)
    // so Back can abandon it instead of trapping the user behind a long turn.
    private CancellationTokenSource? _turnCts;

    // Conductor mode: rotate only through sessions that need the user, one at a
    // time, waiting after each (push-to-talk reply or Next). Nothing auto-advances.
    private readonly ConductorState _conductor = new();
    private bool _conductorMode;

    public TalkPage(IUtteranceRecorder recorder, IReplySpeaker tts, IVoiceForeground foreground)
    {
        InitializeComponent();
        _recorder = recorder;
        _tts = tts;
        _foreground = foreground;

        var savedServer = Preferences.Get(PrefServer, "");
        if (string.IsNullOrWhiteSpace(savedServer))
        {
            savedServer = RecorderDefaults.GatewayUrl;
            Preferences.Set(PrefServer, savedServer);
        }
        ServerEntry.Text = savedServer;
        TokenEntry.Text = Preferences.Get(PrefToken, "");
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Waze-style: keep the screen on while the talk screen is in front so the
        // user can glance at state without unlocking, and audio keeps flowing.
        DeviceDisplay.Current.KeepScreenOn = true;
        _ = _tts.InitAsync();
        _ = LoadRosterAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        DeviceDisplay.Current.KeepScreenOn = false;
        _tts.Stop();
        _foreground.Stop();
    }

    // ===== roster ===========================================================

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadRosterAsync();

    private async Task LoadRosterAsync()
    {
        SaveCreds();
        ListStatusLabel.Text = "Loading sessions...";
        try
        {
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            var roster = await gateway.GetRosterAsync();
            var rows = roster.Select(ToRow).ToList();
            SessionsList.ItemsSource = rows;
            ListStatusLabel.Text = rows.Count == 0
                ? "No sessions found."
                : $"{rows.Count} session(s). Tap one to talk.";
        }
        catch (Exception ex)
        {
            ListStatusLabel.Text = $"Could not load sessions: {ex.Message}";
            SessionsList.ItemsSource = null;
        }
    }

    private SessionRow ToRow(SessionInfo s)
    {
        var subtitle = string.IsNullOrWhiteSpace(s.LastStatusReason)
            ? (string.IsNullOrWhiteSpace(s.MachineName) ? s.ActivityState : $"{s.MachineName} - {s.ActivityState}")
            : s.LastStatusReason;
        return new SessionRow(s, s.DisplayName, subtitle, DotFor(s.StatusColor));
    }

    private static Color DotFor(string statusColor) => statusColor?.ToLowerInvariant() switch
    {
        "green" => DotGreen,
        "blue" => DotBlue,
        "yellow" => DotYellow,
        "red" => DotRed,
        _ => DotGray,
    };

    private void OnSessionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not SessionRow row) return;
        if (sender is CollectionView cv) cv.SelectedItem = null;
        EnterTalk(row.Session);
    }

    private void OnCredsChanged(object? sender, FocusEventArgs e)
    {
        SaveCreds();
        _ = LoadRosterAsync();
    }

    private void SaveCreds()
    {
        Preferences.Set(PrefServer, (ServerEntry.Text ?? "").Trim());
        Preferences.Set(PrefToken, (TokenEntry.Text ?? "").Trim());
    }

    // ===== single-session talk =============================================

    private void EnterTalk(SessionInfo session)
    {
        _conductorMode = false;
        NextButton.IsVisible = false;
        ShowTalkPanelFor(session);
    }

    private void ShowTalkPanelFor(SessionInfo session)
    {
        _selected = session;
        TalkSessionName.Text = session.DisplayName;
        TalkSessionState.Text = string.IsNullOrWhiteSpace(session.LastStatusReason)
            ? session.ActivityState : session.LastStatusReason;
        TranscriptLabel.Text = "-";
        ReplyLabel.Text = "-";
        TurnStatusLabel.Text = "";
        SetTalkButton(recording: false, busy: false);

        ListPanel.IsVisible = false;
        TalkPanel.IsVisible = true;
    }

    private void OnBackClicked(object? sender, EventArgs e)
    {
        // Abandon any in-flight turn (e.g. a wait for a busy session to finish)
        // rather than trapping the user; the turn runners swallow the resulting
        // cancellation quietly.
        _turnCts?.Cancel();
        if (_recorder.IsRecording)
        {
            try { _ = _recorder.StopAsync(); } catch { /* discarding a half-captured clip on leave */ }
        }
        _tts.Stop();
        _selected = null;
        _conductorMode = false;
        NextButton.IsVisible = false;
        _foreground.Stop(); // leaving the conversation; release the background hold
        TalkPanel.IsVisible = false;
        ListPanel.IsVisible = true;
        _ = LoadRosterAsync();
    }

    // ===== all-sessions conductor ==========================================

    private async void OnConductorClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        try
        {
            // Replies use the mic, and we want the foreground hold up before the
            // first spoken item so backgrounding mid-recap does not cut it off.
            // Required order on Android 14+: permission before starting the
            // microphone-typed foreground service.
            var status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlert("Microphone needed",
                    "CC Director Client needs microphone access to reply to sessions.", "OK");
                return;
            }

            ListStatusLabel.Text = "Finding sessions that need you...";
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            var roster = await gateway.GetRosterAsync();
            _conductor.Update(roster);

            if (!_conductor.HasWork)
            {
                ListStatusLabel.Text = "No sessions need you right now.";
                await DisplayAlert("All caught up", "No sessions need you right now.", "OK");
                return;
            }

            _conductorMode = true;
            _foreground.Start();
            NextButton.IsVisible = true;
            await SpeakCurrentConductorItemAsync();
        }
        catch (Exception ex)
        {
            ListStatusLabel.Text = $"Could not start conductor: {ex.Message}";
            await DisplayAlert("Conductor error", ex.Message, "OK");
        }
    }

    private async void OnNextClicked(object? sender, EventArgs e)
    {
        if (_busy || !_conductorMode) return;
        try
        {
            // Refresh first so a session that has been resolved drops out, then
            // move to the next one. Only the explicit Next advances - nothing auto-rotates.
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            var roster = await gateway.GetRosterAsync();
            _conductor.Update(roster);
            _conductor.Advance();

            if (!_conductor.HasWork)
            {
                await DisplayAlert("All caught up", "No more sessions need you.", "OK");
                OnBackClicked(this, EventArgs.Empty);
                return;
            }
            await SpeakCurrentConductorItemAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Conductor error", ex.Message, "OK");
        }
    }

    private async Task SpeakCurrentConductorItemAsync()
    {
        var session = _conductor.Current;
        if (session is null) { OnBackClicked(this, EventArgs.Empty); return; }

        ShowTalkPanelFor(session);
        TalkSessionState.Text = $"Needs you - {_conductor.Count} in queue";
        SetTalkButton(recording: false, busy: true);
        TurnStatusLabel.Text = "Reading...";

        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        var convo = new VoiceConversation(new DirectorVoiceClient(TokenEntry.Text ?? ""), _tts);
        try
        {
            await convo.SpeakConductorItemAsync(session, OnTurnUpdate, _turnCts.Token);
            TurnStatusLabel.Text = "Push Talk to reply, or Next.";
        }
        catch (OperationCanceledException)
        {
            // User left the conductor; nothing to report.
        }
        catch (Exception ex)
        {
            TurnStatusLabel.Text = "";
            await DisplayAlert("Voice error", ex.Message, "OK");
        }
        finally
        {
            SetTalkButton(recording: false, busy: false);
        }
    }

    private async void OnTalkClicked(object? sender, EventArgs e)
    {
        if (_selected is null || _busy) return;
        try
        {
            if (!_recorder.IsRecording)
            {
                var status = await Permissions.RequestAsync<Permissions.Microphone>();
                if (status != PermissionStatus.Granted)
                {
                    await DisplayAlert("Microphone needed",
                        "CC Director Client needs microphone access to talk.", "OK");
                    return;
                }
                // Start the foreground service now that the mic permission is
                // granted, so the round-trip and the spoken reply survive the app
                // being backgrounded or the screen going off (fixes "problem
                // fetching"). Required order on Android 14+: permission first.
                _foreground.Start();
                await _recorder.StartAsync();
                SetTalkButton(recording: true, busy: false);
                TurnStatusLabel.Text = "Listening...";
                return;
            }

            // Second press: stop capturing and run the round-trip.
            var audio = await _recorder.StopAsync();
            await RunTurnAsync(_selected, audio);
        }
        catch (OperationCanceledException)
        {
            // User pressed Back to abandon the turn; nothing to report.
        }
        catch (Exception ex)
        {
            TurnStatusLabel.Text = "";
            SetTalkButton(recording: false, busy: false);
            await DisplayAlert("Voice error", ex.Message, "OK");
        }
    }

    private async Task RunTurnAsync(SessionInfo session, UtteranceAudio audio)
    {
        SetTalkButton(recording: false, busy: true);
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        var convo = new VoiceConversation(
            new DirectorVoiceClient(TokenEntry.Text ?? ""), _tts);
        try
        {
            await convo.SpeakTurnAsync(session, audio, OnTurnUpdate, _turnCts.Token);
        }
        finally
        {
            SetTalkButton(recording: false, busy: false);
        }
    }

    private void OnTurnUpdate(VoiceConversation.TurnUpdate u) => MainThread.BeginInvokeOnMainThread(() =>
    {
        switch (u.Stage)
        {
            case "transcript": TranscriptLabel.Text = u.Text; TurnStatusLabel.Text = ""; break;
            case "reply": ReplyLabel.Text = u.Text; TurnStatusLabel.Text = ""; break;
            case "answer": ReplyLabel.Text = u.Text; TurnStatusLabel.Text = ""; break;
            case "recap": TranscriptLabel.Text = u.Text; break;       // conductor context
            case "name": TurnStatusLabel.Text = $"Reading: {u.Text}"; break;
            case "waiting": TurnStatusLabel.Text = u.Text; break;     // session busy; holding the question
            case "progress": TurnStatusLabel.Text = u.Text; break;
            default: TurnStatusLabel.Text = u.Text; break;            // transcribing / thinking
        }
    });

    private void SetTalkButton(bool recording, bool busy)
    {
        _busy = busy;
        TalkButton.IsEnabled = !busy;
        if (busy)
        {
            TalkButton.Text = "Working...";
            TalkButton.BackgroundColor = Color.FromArgb("#6B7280");
            TalkButton.TextColor = Colors.White;
        }
        else if (recording)
        {
            TalkButton.Text = "Send";
            TalkButton.BackgroundColor = Color.FromArgb("#E5484D");
            TalkButton.TextColor = Colors.White;
        }
        else
        {
            TalkButton.Text = "Talk";
            TalkButton.BackgroundColor = Color.FromArgb("#5FD08A");
            TalkButton.TextColor = Color.FromArgb("#06210F");
        }
    }

    private sealed record SessionRow(SessionInfo Session, string Name, string Subtitle, Color Dot);
}
