using CcDirectorClient.Recording;
using CcDirectorClient.Voice;

namespace CcDirectorClient;

/// <summary>
/// FIFO voice mode: a conveyor belt through every session that needs the user. FifoPage
/// owns ONLY the queue: the Start panel, the Idle "all caught up" panel with its
/// 5-minute recheck, and the conductor state. The voice experience itself - briefing,
/// Ask Agent, Ask Wingman, Replay, Hold, Skip, status, Stop-talking pill - lives in
/// the shared <see cref="Controls.VoiceSessionView"/> that this page hosts and drives.
///
/// For each session in turn the page calls <c>Voice.LoadSessionAsync(session)</c>; when
/// the control fires <see cref="Controls.VoiceSessionView.AnswerDelivered"/> /
/// <see cref="Controls.VoiceSessionView.SkipRequested"/> /
/// <see cref="Controls.VoiceSessionView.HoldRequested"/> the page advances to the next
/// session not yet handled this pass. When the pass is empty the user is caught up.
///
/// The same VoiceSessionView is also embedded inline in TalkPage's Voice tab; the
/// single-session and queue experiences are literally the same control with a
/// different parent telling it what to do next.
/// </summary>
public partial class FifoPage : ContentPage
{
    private const string PrefServer = "gateway_url";
    private const string PrefToken = "gateway_token";

    private readonly IUtteranceRecorder _recorder;
    private readonly IReplySpeaker _tts;
    private readonly IVoiceForeground _foreground;
    private readonly IAudioCue _audioCue;

    // When the conveyor runs dry the page parks on the idle panel and re-checks the
    // roster on this cadence, resuming the moment a session needs the user again. A 1s
    // tick keeps the visible countdown honest; the actual re-scan fires at zero.
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(5);
    private readonly IDispatcherTimer _recheckTimer;
    private DateTime _nextCheckAt;
    // Speak the "all caught up" cue once per dry spell, not on every silent re-check.
    private bool _idleAnnounced;

    // FIFO queue: red sessions that are NOT on hold, in stable order.
    private readonly ConductorState _conductor = new(excludeHeld: true);

    // Sessions already handled this pass (answered / skipped / held), so the belt moves
    // forward instead of immediately re-presenting a session we just dealt with.
    private readonly HashSet<string> _pass = new(StringComparer.OrdinalIgnoreCase);

    private SessionInfo? _current;
    // A Director base URL we can still reach for a standalone "all caught up" cue after
    // the last session has been handled (its endpoint is otherwise gone from _current).
    private string _lastEndpoint = "";

    private bool _busy;

    // True while the app is in the background (e.g. the user switched to Waze). Speech
    // and the recheck loop must keep running then; teardown is only correct on a real
    // in-app navigation away. Set from the Window lifecycle.
    private bool _backgrounded;
    private bool _lifecycleHooked;

    public FifoPage(IUtteranceRecorder recorder, IReplySpeaker tts, IVoiceForeground foreground, IAudioCue audioCue)
    {
        InitializeComponent();
        _recorder = recorder;
        _tts = tts;
        _foreground = foreground;
        _audioCue = audioCue;

        _recheckTimer = Dispatcher.CreateTimer();
        _recheckTimer.Interval = TimeSpan.FromSeconds(1);
        _recheckTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= _nextCheckAt)
            {
                _recheckTimer.Stop();
                _ = CheckNowAsync();
            }
            else
            {
                UpdateIdleCountdown();
            }
        };

        var savedServer = Preferences.Get(PrefServer, "");
        if (string.IsNullOrWhiteSpace(savedServer))
        {
            savedServer = RecorderDefaults.GatewayUrl;
            Preferences.Set(PrefServer, savedServer);
        }
        ServerEntry.Text = savedServer;
        TokenEntry.Text = Preferences.Get(PrefToken, "");

        // Configure the shared voice control to run in queue mode.
        Voice.Configure(_recorder, _tts, _foreground, () => TokenEntry.Text ?? "", _audioCue,
            gatewayUrlProvider: () => (ServerEntry.Text ?? "").Trim());
        Voice.ShowSkip = true;
        Voice.ExitButtonText = "< Exit FIFO";
        Voice.ReadyActionsLine = "Ask Agent, Skip, or Hold";
        Voice.AnswerDelivered += OnVoiceAnswerDelivered;
        Voice.SkipRequested += OnVoiceSkipRequested;
        Voice.HoldRequested += OnVoiceHoldRequested;
        Voice.WingmanAnswered += OnVoiceWingmanAnswered;
        Voice.ExitRequested += OnVoiceExitRequested;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        DeviceDisplay.Current.KeepScreenOn = true;
        _backgrounded = false;
        HookAppLifecycle();
        Voice.OnHostAppearing();
        _ = LoadQueueCountAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        DeviceDisplay.Current.KeepScreenOn = false;

        // The app went to the background (Waze etc.): keep speech, the recheck loop, and
        // the foreground service alive so FIFO resumes on return. Teardown only fires on
        // a real in-app navigation away.
        Voice.OnHostDisappearing(_backgrounded);
        if (_backgrounded) return;

        UnhookAppLifecycle();
        _recheckTimer.Stop();
    }

    // ===== app background vs in-app navigation =============================

    // The MAUI Window fires Deactivated when the app loses focus and does NOT fire on
    // in-app page navigation, so it distinguishes an OnDisappearing caused by
    // backgrounding from one caused by navigation.
    private void HookAppLifecycle()
    {
        if (_lifecycleHooked || Window is null) return;
        Window.Deactivated += OnWindowDeactivated;
        Window.Activated += OnWindowActivated;
        _lifecycleHooked = true;
    }

    private void UnhookAppLifecycle()
    {
        if (!_lifecycleHooked) return;
        if (Window is not null)
        {
            Window.Deactivated -= OnWindowDeactivated;
            Window.Activated -= OnWindowActivated;
        }
        _lifecycleHooked = false;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => _backgrounded = true;
    private void OnWindowActivated(object? sender, EventArgs e) => _backgrounded = false;

    // ===== offline gate (issue #147) =======================================

    private static bool DeviceOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    // ===== start panel =====================================================

    private async void OnRescanClicked(object? sender, EventArgs e) => await LoadQueueCountAsync();

    private async Task LoadQueueCountAsync()
    {
        SaveCreds();
        var gate = OfflineGuard.Check(DeviceOnline, "load sessions");
        if (!gate.Allowed) { StartStatusLabel.Text = gate.Message; return; }
        StartStatusLabel.Text = "Loading sessions...";
        try
        {
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            var roster = await gateway.GetRosterAsync();
            var queue = SessionFilter.AttentionQueue(roster, excludeHeld: true);
            StartStatusLabel.Text = queue.Count == 0
                ? "No sessions need you right now."
                : $"{queue.Count} session(s) need you. Tap Start to go through them one by one.";
        }
        catch (Exception ex)
        {
            StartStatusLabel.Text = $"Could not load sessions: {ex.Message}";
        }
    }

    private void OnCredsChanged(object? sender, FocusEventArgs e)
    {
        SaveCreds();
        _ = LoadQueueCountAsync();
    }

    private void SaveCreds()
    {
        Preferences.Set(PrefServer, (ServerEntry.Text ?? "").Trim());
        Preferences.Set(PrefToken, (TokenEntry.Text ?? "").Trim());
    }

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        try
        {
            // Replies use the mic; Android 14+ rule: permission before mic-typed
            // foreground service.
            var status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlert("Microphone needed",
                    "CC Director Client needs microphone access to answer sessions.", "OK");
                return;
            }

            _pass.Clear();
            _idleAnnounced = false;
            _foreground.Start();
            StartPanel.IsVisible = false;
            IdlePanel.IsVisible = false;
            MainScroll.IsVisible = false;
            Voice.IsVisible = true;
            await PresentNextAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("FIFO error", ex.Message, "OK");
        }
    }

    // ===== the conveyor belt ===============================================

    /// <summary>
    /// Refresh the roster, rebuild the queue, and hand the next session not yet handled
    /// this pass to the shared <see cref="Controls.VoiceSessionView"/>. When none remain,
    /// the user is caught up and the page parks on the idle panel.
    /// </summary>
    private async Task PresentNextAsync()
    {
        var gate = OfflineGuard.Check(DeviceOnline, "load your sessions");
        if (!gate.Allowed)
        {
            await DisplayAlert("Offline", gate.Message, "OK");
            return;
        }
        _busy = true;
        SessionInfo? next;
        try
        {
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            var roster = await gateway.GetRosterAsync();
            _conductor.Update(roster);
            next = _conductor.Queue.FirstOrDefault(s => !_pass.Contains(s.SessionId));
        }
        catch (Exception ex)
        {
            _busy = false;
            await DisplayAlert("FIFO error", ex.Message, "OK");
            return;
        }

        if (next is null)
        {
            _busy = false;
            await EnterIdleAsync();
            return;
        }

        _idleAnnounced = false;
        IdlePanel.IsVisible = false;
        MainScroll.IsVisible = false;
        Voice.IsVisible = true;
        _current = next;
        _lastEndpoint = next.TailnetEndpoint;
        _busy = false;
        await Voice.LoadSessionAsync(next);
    }

    // ===== shared-voice events =============================================

    private async void OnVoiceAnswerDelivered(object? sender, EventArgs e)
    {
        if (_current is null) return;
        _pass.Add(_current.SessionId);
        await PresentNextAsync();
    }

    private async void OnVoiceSkipRequested(object? sender, EventArgs e)
    {
        if (_current is null) return;
        _pass.Add(_current.SessionId);
        await PresentNextAsync();
    }

    private async void OnVoiceHoldRequested(object? sender, EventArgs e)
    {
        if (_current is null) return;
        // The control already POSTed the hold to the Director before raising this event,
        // so we just advance.
        _pass.Add(_current.SessionId);
        await PresentNextAsync();
    }

    private void OnVoiceWingmanAnswered(object? sender, EventArgs e)
    {
        // Ask Wingman is read-only and never advances; the control already reset to
        // ready. Nothing to do here.
    }

    private void OnVoiceExitRequested(object? sender, EventArgs e)
    {
        _recheckTimer.Stop();
        Voice.ClearSession();
        _current = null;
        _pass.Clear();
        _foreground.Stop();

        Voice.IsVisible = false;
        IdlePanel.IsVisible = false;
        StartPanel.IsVisible = true;
        MainScroll.IsVisible = true;
        _ = LoadQueueCountAsync();
    }

    // The Idle panel has its own back button (laid out in the scroll, not on the active
    // panel) so it routes through here.
    private void OnExitFromIdleClicked(object? sender, EventArgs e) => OnVoiceExitRequested(sender, e);

    /// <summary>
    /// Conveyor ran dry: park on the idle panel, keep the foreground service alive so
    /// the loop survives the screen going off, and re-check the roster every
    /// <see cref="RecheckInterval"/>. The moment a session needs the user again,
    /// <see cref="PresentNextAsync"/> resumes the belt. The spoken "all caught up" cue
    /// fires once per dry spell, not on every silent re-check.
    /// </summary>
    private async Task EnterIdleAsync()
    {
        Voice.ClearSession();
        _current = null;
        _pass.Clear();
        Voice.IsVisible = false;
        StartPanel.IsVisible = false;
        IdlePanel.IsVisible = true;
        MainScroll.IsVisible = true;

        _nextCheckAt = DateTime.UtcNow + RecheckInterval;
        UpdateIdleCountdown();
        _recheckTimer.Start();

        if (_idleAnnounced) return;
        _idleAnnounced = true;

        if (!string.IsNullOrWhiteSpace(_lastEndpoint))
        {
            try
            {
                var convo = new VoiceConversation(new DirectorVoiceClient(TokenEntry.Text ?? ""), _tts);
                await convo.SpeakLineAsync(_lastEndpoint, "All caught up. Nothing to do right now. I'll check again in five minutes.");
            }
            catch { /* the visible state already says it; a spoken cue is a bonus */ }
        }
    }

    private void UpdateIdleCountdown()
    {
        var remaining = _nextCheckAt - DateTime.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        IdleCountdownLabel.Text = $"Checking again in {remaining:mm\\:ss}";
    }

    private async void OnCheckNowClicked(object? sender, EventArgs e) => await CheckNowAsync();

    private async Task CheckNowAsync()
    {
        if (_busy) return;
        _recheckTimer.Stop();
        await PresentNextAsync();
    }

    // Top-right burger menu: switch between pages.
    private async void OnNavMenuClicked(object? sender, TappedEventArgs e)
    {
        var choice = await DisplayActionSheet("Go to", "Cancel", null, "Sessions", "FIFO", "FIFO Text", "Notes", "Exes", "Dictionary", "Transcripts");
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;
        // The control owns the speech; tell it to stop on navigation away so a late
        // reply does not bleed into the next page.
        _tts.Stop();
        if (choice == "Sessions")
            await Shell.Current.GoToAsync("//TalkPage");
        else if (choice == "FIFO")
            await Shell.Current.GoToAsync("//FifoPage");
        else if (choice == "FIFO Text")
            await Shell.Current.GoToAsync("//FifoTextPage");
        else if (choice == "Notes")
            await Shell.Current.GoToAsync("//MainPage");
        else if (choice == "Exes")
            await Shell.Current.GoToAsync("//ExesPage");
        else if (choice == "Dictionary")
            await Shell.Current.GoToAsync("//DictionaryPage");
        else if (choice == "Transcripts")
            await Shell.Current.GoToAsync("//TranscriptsPage");
    }
}
