using CcDirectorClient.Recording;
using CcDirectorClient.Voice;

namespace CcDirectorClient;

/// <summary>
/// FIFO voice mode: a conveyor belt through every session that needs the user.
///
/// For each session in turn the page auto-switches to it, has the wingman speak what is
/// happening and what it wants, then waits. The user answers by voice (Answer), and the
/// moment the answer is transcribed and delivered to the session the page advances to the
/// next one - it never waits for the agent to actually respond. The user can also Skip a
/// session (come back to it later in the pass) or Hold it (park it out of the rotation
/// until they bring it back), either with the buttons or by telling the wingman.
///
/// The queue is built from the same authoritative "needs you" (red) status the Talk
/// screen's conductor uses, minus any session the user has put on hold. A "pass" tracks
/// the sessions already handled this round so the belt moves forward and does not loop
/// back onto a session that was just answered; when the pass is empty the user is caught
/// up. The screen is kept awake while in front (Waze-style) for eyes-free use.
/// </summary>
public partial class FifoPage : ContentPage
{
    private const string PrefServer = "gateway_url";
    private const string PrefToken = "gateway_token";

    // Voice status line colors (the big "where we are" label), matching the Talk screen.
    private const string StatusGreen = "#5FD08A";   // ready / done
    private const string StatusYellow = "#E8B339";  // in progress
    private const string StatusRed = "#E5484D";     // recording / error
    private const string StatusBlue = "#2B6CB0";    // speaking / reading

    private readonly IUtteranceRecorder _recorder;
    private readonly IReplySpeaker _tts;
    private readonly IVoiceForeground _foreground;

    // Pumps the live mic level meter + elapsed time while recording (100ms).
    private readonly IDispatcherTimer _levelTimer;
    private DateTime _recStart;

    // When the conveyor runs dry the page parks on the idle panel and re-checks the roster
    // on this cadence, resuming the moment a session needs the user again. A 1s tick keeps
    // the visible countdown honest; the actual re-scan fires when it reaches zero.
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
    // A Director base URL we can still reach for a standalone "all caught up" cue after the
    // last session has been handled (its endpoint is otherwise gone from _current).
    private string _lastEndpoint = "";

    private bool _busy;
    private bool _recordingForWingman;

    // Cancels the in-flight turn / briefing so Exit can abandon it.
    private CancellationTokenSource? _turnCts;

    // True while the app is in the background (the user switched to another app such as
    // Waze). Speech and the recheck loop must keep running then; they are only torn down
    // on a real in-app navigation away. Set from the Window lifecycle (see HookAppLifecycle).
    private bool _backgrounded;
    private bool _lifecycleHooked;

    public FifoPage(IUtteranceRecorder recorder, IReplySpeaker tts, IVoiceForeground foreground)
    {
        InitializeComponent();
        _recorder = recorder;
        _tts = tts;
        _foreground = foreground;

        _levelTimer = Dispatcher.CreateTimer();
        _levelTimer.Interval = TimeSpan.FromMilliseconds(100);
        _levelTimer.Tick += (_, _) =>
        {
            LevelMeter.Progress = _recorder.ReadLevel();
            var secs = (int)(DateTime.UtcNow - _recStart).TotalSeconds;
            RecElapsedLabel.Text = TimeSpan.FromSeconds(secs).ToString(@"mm\:ss");
        };

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
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        DeviceDisplay.Current.KeepScreenOn = true;
        _backgrounded = false;
        HookAppLifecycle();
        _ = LoadQueueCountAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        DeviceDisplay.Current.KeepScreenOn = false;
        _levelTimer.Stop();

        // The app went to the background (e.g. the user switched to Waze in the car):
        // keep the in-flight turn, the spoken reply, the 5-minute recheck loop, and the
        // foreground service ALL alive so speech keeps playing and FIFO resumes on return.
        // Tearing down here is only correct for a real in-app navigation away.
        if (_backgrounded) return;

        // Real navigation away from the FIFO screen: cancel the turn so a late reply can
        // never come back and overlap a prompt on the next screen, stop the recheck loop,
        // stop speech, and release the voice / foreground resources.
        UnhookAppLifecycle();
        _turnCts?.Cancel();
        _recheckTimer.Stop();
        if (_recorder.IsRecording)
        {
            try { _ = _recorder.StopAsync(); } catch { /* discard a half-captured clip on leave */ }
        }
        _tts.Stop();
        ClearVoiceMode(_current);
        _foreground.Stop();
    }

    // ===== app background vs in-app navigation =============================

    // Speech must keep playing while the app is merely backgrounded (the user glances at
    // Waze), but must be cut the moment they navigate to another screen so prompts never
    // overlap. The MAUI Window fires Deactivated when the app loses focus and does NOT
    // fire on in-app page navigation, so it tells an OnDisappearing caused by backgrounding
    // apart from one caused by navigation.
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

    // Cancel the current voice turn / briefing and silence playback immediately. Used when the
    // user acts on a session (Skip, Hold, Answer) or leaves the screen, so a message they do
    // not want to hear stops the instant they tap.
    private void CancelSpeechAndTurn()
    {
        _turnCts?.Cancel();
        _tts.Stop();
    }

    // ===== start panel =====================================================

    private async void OnRescanClicked(object? sender, EventArgs e) => await LoadQueueCountAsync();

    private async Task LoadQueueCountAsync()
    {
        SaveCreds();
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
            // Replies use the mic; on Android 14+ the permission must be granted before the
            // microphone-typed foreground service starts.
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
            FifoPanel.IsVisible = true;
            await PresentNextAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("FIFO error", ex.Message, "OK");
        }
    }

    // ===== the conveyor belt ===============================================

    /// <summary>
    /// Refresh the roster, rebuild the queue, and present the next session not yet handled
    /// this pass. When none remain, the user is caught up. The busy lock is held only while
    /// the roster is fetched and the session is shown; the briefing is then spoken in the
    /// background (see <see cref="SpeakBriefingAsync"/>) so the user can Answer / Skip / Hold
    /// the instant the session appears, cutting off a message they do not want to hear.
    /// </summary>
    private async Task PresentNextAsync()
    {
        SetBusy(true);
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
            SetFifoStatus("Could not read this session", StatusRed);
            SetBusy(false);
            await DisplayAlert("FIFO error", ex.Message, "OK");
            return;
        }

        if (next is null)
        {
            await EnterIdleAsync();
            SetBusy(false);
            return;
        }

        // A live session to handle: leave any idle wait and reset the cue so the next
        // dry spell is announced again.
        _idleAnnounced = false;
        IdlePanel.IsVisible = false;
        MainScroll.IsVisible = false;
        FifoPanel.IsVisible = true;
        _current = next;
        _lastEndpoint = next.TailnetEndpoint;
        var remaining = _conductor.Queue.Count(s => !_pass.Contains(s.SessionId));
        ShowSessionPanel(next, remaining);

        // Mark it as being talked to so the desktop tile / web view / roster agree.
        _ = new DirectorVoiceClient(TokenEntry.Text ?? "")
            .SetVoiceModeAsync(next.TailnetEndpoint, next.SessionId, true);

        // The session is on screen: free the controls so Answer / Skip / Hold respond at once.
        SetBusy(false);

        // Speak the briefing in the background, tied to a fresh token, so Skip (or any action)
        // can cancel it mid-sentence instead of forcing the user to sit through it.
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        _ = SpeakBriefingAsync(next, _turnCts.Token);
    }

    /// <summary>
    /// Speak the wingman's briefing for <paramref name="session"/> off the UI path so the user
    /// can interrupt it. A cancelled token (the user skipped / held / answered) ends quietly and
    /// must not stomp the status set by whatever they did next.
    /// </summary>
    private async Task SpeakBriefingAsync(SessionInfo session, CancellationToken ct)
    {
        try
        {
            var convo = new VoiceConversation(new DirectorVoiceClient(TokenEntry.Text ?? ""), _tts);
            SetFifoStatus("Reading what's happening...", StatusBlue);
            await convo.SpeakExplainAsync(session, OnTurnUpdate, ct);
            if (!ct.IsCancellationRequested)
                SetFifoStatus("Answer, Skip, or Hold", StatusGreen);
        }
        catch (OperationCanceledException)
        {
            // The user skipped, held, or answered before the briefing finished. Expected.
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                SetFifoStatus("Could not read this session", StatusRed);
                await DisplayAlert("FIFO error", ex.Message, "OK");
            }
        }
    }

    private void ShowSessionPanel(SessionInfo session, int remaining)
    {
        FifoSessionName.Text = session.DisplayName;
        FifoSessionState.Text = remaining == 1
            ? "Needs you - last one"
            : $"Needs you - {remaining} left";
        BriefingLabel.Text = "-";
        TranscriptLabel.Text = "-";
        ReplyLabel.Text = "-";
        TurnStatusLabel.Text = "";
        RecordingCard.IsVisible = false;
        LevelMeter.Progress = 0;
        _levelTimer.Stop();
    }

    /// <summary>
    /// The conveyor ran dry: park on the idle panel, keep the foreground service alive so the
    /// loop survives the screen going off, and re-check the roster every <see cref="RecheckInterval"/>.
    /// The moment a session needs the user again, <see cref="PresentNextAsync"/> resumes the belt.
    /// The spoken "all caught up" cue fires once per dry spell, not on every silent re-check.
    /// </summary>
    private async Task EnterIdleAsync()
    {
        ClearVoiceMode(_current);
        _current = null;
        _pass.Clear();
        FifoPanel.IsVisible = false;
        StartPanel.IsVisible = false;
        IdlePanel.IsVisible = true;
        MainScroll.IsVisible = true;

        // Keep the foreground service running so the dispatcher keeps ticking in the background.
        _nextCheckAt = DateTime.UtcNow + RecheckInterval;
        UpdateIdleCountdown();
        _recheckTimer.Start();

        if (_idleAnnounced) return;
        _idleAnnounced = true;

        // Audible cue for eyes-free use, spoken via the last Director we talked to.
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

    // "Check now" button, and the automatic recheck when the countdown reaches zero.
    private async void OnCheckNowClicked(object? sender, EventArgs e) => await CheckNowAsync();

    private async Task CheckNowAsync()
    {
        if (_busy) return;
        _recheckTimer.Stop();
        await PresentNextAsync();
    }

    // ===== answer / ask wingman (push-to-talk) =============================

    private async void OnAnswerClicked(object? sender, EventArgs e) => await HandleRecordAsync(wingman: false);
    private async void OnAskWingmanClicked(object? sender, EventArgs e) => await HandleRecordAsync(wingman: true);

    // Abandon the current recording (e.g. the user tapped Answer/Ask Wingman by mistake):
    // stop the mic, discard the clip without transcribing or sending, and return to the
    // session's resting state. Never advances the queue.
    private async void OnCancelRecordingClicked(object? sender, EventArgs e)
    {
        if (!_recorder.IsRecording) return;
        try
        {
            _levelTimer.Stop();
            RecordingCard.IsVisible = false;
            LevelMeter.Progress = 0;
            try { await _recorder.StopAsync(); } catch { /* discard the half-captured clip */ }
            SetAnswerButton(recording: false, busy: false);
            SetFifoStatus("Answer, Skip, or Hold", StatusGreen);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Voice error", ex.Message, "OK");
        }
    }

    private async Task HandleRecordAsync(bool wingman)
    {
        if (_current is null || _busy) return;
        try
        {
            if (!_recorder.IsRecording)
            {
                // Cut any briefing still playing so it is not captured by the mic or left
                // talking over the user as they start their answer.
                CancelSpeechAndTurn();
                var status = await Permissions.RequestAsync<Permissions.Microphone>();
                if (status != PermissionStatus.Granted)
                {
                    await DisplayAlert("Microphone needed",
                        "CC Director Client needs microphone access to talk.", "OK");
                    return;
                }
                _recordingForWingman = wingman;
                _foreground.Start();
                await _recorder.StartAsync();
                SetAnswerButton(recording: true, busy: false);
                _recStart = DateTime.UtcNow;
                RecElapsedLabel.Text = "00:00";
                LevelMeter.Progress = 0;
                RecordingCard.IsVisible = true;
                _levelTimer.Start();
                SetFifoStatus(wingman ? "Recording for wingman" : "Recording your answer", StatusRed);
                return;
            }

            // Second press (either button): stop and run the turn in the recorded mode.
            _levelTimer.Stop();
            RecordingCard.IsVisible = false;
            LevelMeter.Progress = 0;
            SetFifoStatus(_recordingForWingman ? "Asking wingman" : "Sending", StatusYellow);
            var audio = await _recorder.StopAsync();
            await RunFifoTurnAsync(_current, audio);
        }
        catch (OperationCanceledException)
        {
            // User exited mid-turn; nothing to report.
        }
        catch (Exception ex)
        {
            _levelTimer.Stop();
            RecordingCard.IsVisible = false;
            LevelMeter.Progress = 0;
            SetFifoStatus("Something went wrong", StatusRed);
            SetAnswerButton(recording: false, busy: false);
            await DisplayAlert("Voice error", ex.Message, "OK");
        }
    }

    private async Task RunFifoTurnAsync(SessionInfo session, UtteranceAudio audio)
    {
        SetBusy(true);
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        var convo = new VoiceConversation(new DirectorVoiceClient(TokenEntry.Text ?? ""), _tts);
        try
        {
            var outcome = await convo.DeliverToSessionAsync(
                session, audio, OnTurnUpdate, _turnCts.Token, forceWingman: _recordingForWingman);

            switch (outcome.Kind)
            {
                case VoiceConversation.FifoOutcomeKind.Delivered:
                case VoiceConversation.FifoOutcomeKind.Skip:
                    await MarkHandledAndAdvanceAsync(session, wasHold: false);
                    break;
                case VoiceConversation.FifoOutcomeKind.Hold:
                    await MarkHandledAndAdvanceAsync(session, wasHold: true);
                    break;
                case VoiceConversation.FifoOutcomeKind.WingmanAnswered:
                    SetFifoStatus("Answer, Skip, or Hold", StatusGreen);
                    SetBusy(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
        }
        catch (Exception ex)
        {
            SetFifoStatus("Something went wrong", StatusRed);
            SetBusy(false);
            await DisplayAlert("Voice error", ex.Message, "OK");
        }
    }

    // ===== skip / hold buttons =============================================

    private async void OnSkipClicked(object? sender, EventArgs e)
    {
        if (_current is null || _busy) return;
        CancelSpeechAndTurn();   // cut the briefing immediately; don't make the user wait it out
        await MarkHandledAndAdvanceAsync(_current, wasHold: false);
    }

    private async void OnHoldClicked(object? sender, EventArgs e)
    {
        if (_current is null || _busy) return;
        CancelSpeechAndTurn();   // cut the briefing immediately
        await MarkHandledAndAdvanceAsync(_current, wasHold: true);
    }

    /// <summary>
    /// Finish with the current session and move on: park it first when
    /// <paramref name="wasHold"/> is set (so it leaves the rotation), mark it handled for
    /// this pass, drop its voice-mode flag, then present the next session. A hold that
    /// fails to take is surfaced and does NOT advance.
    /// </summary>
    private async Task MarkHandledAndAdvanceAsync(SessionInfo session, bool wasHold)
    {
        if (wasHold)
        {
            SetBusy(true);
            SetFifoStatus("Holding this session...", StatusYellow);
            try
            {
                await new DirectorVoiceClient(TokenEntry.Text ?? "")
                    .SetHoldAsync(session.TailnetEndpoint, session.SessionId, true);
            }
            catch (Exception ex)
            {
                SetFifoStatus("Could not hold this session", StatusRed);
                SetBusy(false);
                await DisplayAlert("Hold error", ex.Message, "OK");
                return;
            }
        }

        _pass.Add(session.SessionId);
        ClearVoiceMode(session);
        await PresentNextAsync();
    }

    // ===== exit + helpers ==================================================

    private void OnExitClicked(object? sender, EventArgs e)
    {
        _turnCts?.Cancel();
        _levelTimer.Stop();
        _recheckTimer.Stop();
        RecordingCard.IsVisible = false;
        LevelMeter.Progress = 0;
        if (_recorder.IsRecording)
        {
            try { _ = _recorder.StopAsync(); } catch { /* discarding a half-captured clip on leave */ }
        }
        _tts.Stop();
        ClearVoiceMode(_current);
        _current = null;
        _pass.Clear();
        _foreground.Stop();
        FifoPanel.IsVisible = false;
        IdlePanel.IsVisible = false;
        StartPanel.IsVisible = true;
        MainScroll.IsVisible = true;
        _ = LoadQueueCountAsync();
    }

    // Drop a session's voice-mode flag so other clients stop showing it as talked-to.
    // Best-effort and fire-and-forget; it must never block the flow.
    private void ClearVoiceMode(SessionInfo? session)
    {
        if (session is null) return;
        _ = new DirectorVoiceClient(TokenEntry.Text ?? "")
            .SetVoiceModeAsync(session.TailnetEndpoint, session.SessionId, false);
    }

    private void OnTurnUpdate(VoiceConversation.TurnUpdate u) => MainThread.BeginInvokeOnMainThread(() =>
    {
        switch (u.Stage)
        {
            case "explaining": SetFifoStatus("Reading what's happening...", StatusBlue); break;
            case "briefing": BriefingLabel.Text = u.Text; SetFifoStatus("Speaking...", StatusBlue); break;
            case "transcribing": SetFifoStatus("Transcribing...", StatusYellow); break;
            case "transcript": TranscriptLabel.Text = u.Text; TurnStatusLabel.Text = ""; break;
            case "delivering": SetFifoStatus("Sending your answer...", StatusYellow); break;
            case "delivered": ReplyLabel.Text = u.Text; SetFifoStatus("Sent - next session", StatusGreen); break;
            case "wingman": SetFifoStatus("Asking the wingman...", StatusBlue); break;
            case "answer": ReplyLabel.Text = u.Text; SetFifoStatus("Speaking...", StatusBlue); break;
            default: TurnStatusLabel.Text = u.Text; break;
        }
    });

    private void SetFifoStatus(string text, string colorHex)
    {
        FifoStatusLabel.Text = text;
        FifoStatusLabel.TextColor = Color.FromArgb(colorHex);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SkipButton.IsEnabled = !busy;
        HoldButton.IsEnabled = !busy;
        AskWingmanButton.IsEnabled = !busy;
        SetAnswerButton(recording: false, busy: busy);
    }

    private void SetAnswerButton(bool recording, bool busy)
    {
        // The button's identity never changes (always "Ask Agent") so that while driving
        // the user is never confused about which button talks to the agent. State is shown
        // by colour only: green = ready, red = recording (tap again to send), grey = busy.
        AnswerButton.IsEnabled = !busy;
        AnswerButton.Text = "Ask Agent";
        if (busy)
        {
            AnswerButton.BackgroundColor = Color.FromArgb("#6B7280");
            AnswerButton.TextColor = Colors.White;
        }
        else if (recording)
        {
            AnswerButton.BackgroundColor = Color.FromArgb("#E5484D");
            AnswerButton.TextColor = Colors.White;
        }
        else
        {
            AnswerButton.BackgroundColor = Color.FromArgb("#5FD08A");
            AnswerButton.TextColor = Color.FromArgb("#06210F");
        }
    }

    // Top-right burger menu: switch between pages.
    private async void OnNavMenuClicked(object? sender, TappedEventArgs e)
    {
        var choice = await DisplayActionSheet("Go to", "Cancel", null, "Talk", "FIFO", "FIFO Text", "Notes", "Exes", "Dictionary", "Transcripts");
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;
        CancelSpeechAndTurn();
        if (choice == "Talk")
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
