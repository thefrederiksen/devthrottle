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

    // The most recent spoken clip for the current session (briefing or wingman answer), kept so
    // the Replay button can re-play it (issue #148). Holds only the last clip; cleared when the
    // session starts working again so stale audio is never replayed.
    private readonly LastClipCache _clipCache = new();

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
        // Drive the Stop-talking pill off the speaker's playback state, and sync it now in
        // case audio is already playing (e.g. returning from the background mid-read).
        _tts.PlayingChanged += OnPlayingChanged;
        UpdateStopTalkingVisibility(_tts.IsPlaying);
        _ = LoadQueueCountAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        DeviceDisplay.Current.KeepScreenOn = false;
        _tts.PlayingChanged -= OnPlayingChanged;
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

    // ===== stop-talking control (issue #146) ===============================
    // A floating pill, pinned to the top and shown only while audio is playing, lets the
    // user silence a long read at once without scrolling. Tapping it cancels the turn (so a
    // multi-part read does not just roll on to the next clip) and stops playback.

    private void OnPlayingChanged(bool playing)
        => MainThread.BeginInvokeOnMainThread(() => UpdateStopTalkingVisibility(playing));

    private void UpdateStopTalkingVisibility(bool playing) => StopTalkingButton.IsVisible = playing;

    private void OnStopTalkingClicked(object? sender, EventArgs e)
    {
        ClientLog.Write("[FifoPage] Stop talking tapped");
        CancelSpeechAndTurn();
        UpdateStopTalkingVisibility(false);
        SetFifoStatus("Stopped. Ask Agent, Skip, or Hold", StatusGreen);
    }

    // ===== offline gate (issue #147) =======================================
    // Buttons must never sink into the disabled "busy" state waiting on a network call that
    // cannot succeed (the 5-minute HTTP timeout is what makes them "appear dead" with no
    // signal). Any handler that is about to do network work calls EnsureOnline FIRST: when
    // offline it shows an instant message, keeps the buttons live, and the handler bails
    // before awaiting anything. The local effects (stop the briefing, stop the mic) have
    // already run by then, so the press still gives immediate feedback.

    private static bool DeviceOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    private bool EnsureOnline(string action)
    {
        var verdict = OfflineGuard.Check(DeviceOnline, action);
        if (verdict.Allowed) return true;
        ClientLog.Write($"[FifoPage] offline gate blocked action='{action}'");
        SetFifoStatus(verdict.Message, StatusRed);
        SetBusy(false);   // never leave the buttons stuck disabled
        return false;
    }

    // ===== start panel =====================================================

    private async void OnRescanClicked(object? sender, EventArgs e) => await LoadQueueCountAsync();

    private async Task LoadQueueCountAsync()
    {
        SaveCreds();
        // Don't spin "Loading sessions..." behind a doomed fetch when there is no signal;
        // tell the user at once on the start panel's own status line.
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
    /// this pass. When none remain, the user is caught up.
    ///
    /// The briefing audio is PRE-GENERATED before the session card is shown (issue #148): we
    /// fetch the wingman's explanation and synthesize its speech while still on the previous
    /// card (status "Preparing next session..."), and only switch to the new session once the
    /// audio is in hand, then play it immediately. The clip is cached so Replay can re-play it.
    /// The busy lock is held across the fetch + synth so the controls cannot fire mid-prepare.
    /// </summary>
    private async Task PresentNextAsync()
    {
        // The advance needs the roster: gate it so an offline tap fails instantly instead of
        // disabling every button behind a doomed fetch.
        if (!EnsureOnline("load your sessions")) return;
        SetBusy(true);
        // The previous session's clip is stale the moment we move on.
        ClearCachedClip();
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

        // Pre-generate the briefing audio BEFORE switching to the session, tied to a fresh
        // token so Exit can abandon it. We stay on the current card showing "Preparing..." until
        // the audio is ready, so the user never lands on a silent page waiting for speech.
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        var token = _turnCts.Token;
        SetFifoStatus("Preparing next session...", StatusBlue);

        VoiceConversation.PreparedBriefing? prepared = null;
        try
        {
            var convo = new VoiceConversation(new DirectorVoiceClient(TokenEntry.Text ?? ""), _tts);
            prepared = await convo.PrepareExplainAsync(next, token);
        }
        catch (OperationCanceledException)
        {
            return; // the user exited during prepare; nothing to show
        }
        catch (Exception ex)
        {
            // The briefing could not be prepared: still land on the session so the user can act,
            // just without the spoken briefing.
            ClientLog.Write($"[FifoPage] PrepareExplain failed: {ex.Message}");
        }

        // Audio is in hand (or failed): now switch to the session.
        _idleAnnounced = false;
        IdlePanel.IsVisible = false;
        MainScroll.IsVisible = false;
        FifoPanel.IsVisible = true;
        _current = next;
        _lastEndpoint = next.TailnetEndpoint;
        var remaining = _conductor.Queue.Count(s => !_pass.Contains(s.SessionId));
        ShowSessionPanel(next, remaining);
        if (prepared is not null) BriefingLabel.Text = prepared.DisplayText;

        // Mark it as being talked to so the desktop tile / web view / roster agree.
        _ = new DirectorVoiceClient(TokenEntry.Text ?? "")
            .SetVoiceModeAsync(next.TailnetEndpoint, next.SessionId, true);

        // The session is on screen: free the controls so Answer / Skip / Hold respond at once.
        SetBusy(false);

        if (prepared is not null)
        {
            CacheClip(prepared.Audio);                 // make it replayable
            _ = PlayClipAsync(prepared.Audio, token);  // speak it now (cancellable)
        }
        else
        {
            SetFifoStatus("Could not read this session", StatusRed);
        }
    }

    /// <summary>
    /// Play an already-synthesized clip (the just-prepared briefing, or a cached Replay) off the
    /// UI path so the user can interrupt it. A cancelled token (skip / hold / answer / Stop) ends
    /// quietly and must not stomp the status set by whatever they did next.
    /// </summary>
    private async Task PlayClipAsync(byte[] audio, CancellationToken ct)
    {
        try
        {
            SetFifoStatus("Speaking...", StatusBlue);
            await _tts.PlayAsync(audio, ct);
            if (!ct.IsCancellationRequested)
                SetFifoStatus("Ask Agent, Skip, or Hold", StatusGreen);
        }
        catch (OperationCanceledException)
        {
            // The user skipped, held, answered, or stopped playback. Expected.
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                SetFifoStatus("Could not play this briefing", StatusRed);
            ClientLog.Write($"[FifoPage] PlayClip failed: {ex.Message}");
        }
    }

    // ===== last-clip cache + Replay (issue #148) ===========================

    // Remember the most recent spoken clip (briefing or wingman answer) and reveal Replay.
    private void CacheClip(byte[] audio)
    {
        _clipCache.Set(audio);
        MainThread.BeginInvokeOnMainThread(() => ReplayButton.IsVisible = _clipCache.HasClip);
    }

    // Drop the cached clip and hide Replay - used when the session starts working again (a new
    // job starts) and when leaving the session, so stale audio is never replayed.
    private void ClearCachedClip()
    {
        _clipCache.Clear();
        MainThread.BeginInvokeOnMainThread(() => ReplayButton.IsVisible = false);
    }

    private void OnReplayClicked(object? sender, EventArgs e)
    {
        var clip = _clipCache.Clip;
        if (clip is null || _busy) return;
        ClientLog.Write("[FifoPage] Replay tapped");
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        _ = PlayClipAsync(clip, _turnCts.Token);
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
            SetIdleButtons();
            SetFifoStatus("Ask Agent, Skip, or Hold", StatusGreen);
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
                SetRecordingButtons(wingman);
                RecordingHintLabel.Text = wingman
                    ? "Recording - tap Ask Wingman to send"
                    : "Recording - tap Ask Agent to send";
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
            SetIdleButtons();
            await DisplayAlert("Voice error", ex.Message, "OK");
        }
    }

    private async Task RunFifoTurnAsync(SessionInfo session, UtteranceAudio audio)
    {
        // The mic is already stopped (HandleRecordAsync did that the instant the user tapped).
        // The upload/transcribe is the network step: gate it so an offline send surfaces at
        // once and the buttons stay live, rather than dimming for the whole HTTP timeout.
        if (!EnsureOnline(_recordingForWingman ? "ask the wingman" : "send your answer"))
            return;
        SetBusy(true);
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        var convo = new VoiceConversation(new DirectorVoiceClient(TokenEntry.Text ?? ""), _tts);
        try
        {
            var outcome = await convo.DeliverToSessionAsync(
                session, audio, OnTurnUpdate, _turnCts.Token, forceWingman: _recordingForWingman,
                onClip: CacheClip);

            switch (outcome.Kind)
            {
                case VoiceConversation.FifoOutcomeKind.Delivered:
                case VoiceConversation.FifoOutcomeKind.Skip:
                    // The answer went to the agent: it is (re)starting work, so its briefing is
                    // now stale (issue #148). Drop it before advancing so Replay never plays it.
                    ClearCachedClip();
                    await MarkHandledAndAdvanceAsync(session, wasHold: false);
                    break;
                case VoiceConversation.FifoOutcomeKind.Hold:
                    await MarkHandledAndAdvanceAsync(session, wasHold: true);
                    break;
                case VoiceConversation.FifoOutcomeKind.WingmanAnswered:
                    SetFifoStatus("Ask Agent, Skip, or Hold", StatusGreen);
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
        // The cut above is the instant local feedback; advancing needs the roster, so gate it.
        if (!EnsureOnline("move to the next session")) return;
        await MarkHandledAndAdvanceAsync(_current, wasHold: false);
    }

    private async void OnHoldClicked(object? sender, EventArgs e)
    {
        if (_current is null || _busy) return;
        CancelSpeechAndTurn();   // cut the briefing immediately
        // Hold posts to the Director before it can advance: gate it so an offline tap does not
        // dim every button behind the hold call's timeout.
        if (!EnsureOnline("hold this session")) return;
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
        ClearCachedClip();   // leaving FIFO: nothing to replay
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
        if (busy) SetBusyButtons();
        else SetIdleButtons();
    }

    // ===== action-button states (issue #143) ==============================
    // The two primary buttons keep their identity WORDS at all times ("Ask Agent" /
    // "Ask Wingman", set in XAML and never changed here); state is shown by colour and
    // enablement only, so a driver is never confused about which button talks to whom.

    private static readonly Color AgentGreen = Color.FromArgb("#5FD08A");
    private static readonly Color AgentText = Color.FromArgb("#06210F");
    private static readonly Color WingmanTeal = Color.FromArgb("#0E7C6B");
    private static readonly Color RecordingRed = Color.FromArgb("#E5484D");
    private static readonly Color DisabledGrey = Color.FromArgb("#6B7280");

    // Resting: both primaries live, Skip / Hold available.
    private void SetIdleButtons()
    {
        AnswerButton.IsEnabled = true;
        AnswerButton.BackgroundColor = AgentGreen;
        AnswerButton.TextColor = AgentText;

        AskWingmanButton.IsEnabled = true;
        AskWingmanButton.BackgroundColor = WingmanTeal;
        AskWingmanButton.TextColor = Colors.White;

        SkipButton.IsEnabled = true;
        HoldButton.IsEnabled = true;
        ReplayButton.IsEnabled = true;
    }

    // Recording: the button being recorded turns red (tap it again to send); every other
    // control is disabled so the only moves are send (the red button) or Cancel. This also
    // closes a latent bug where Skip mid-recording advanced without stopping the mic.
    private void SetRecordingButtons(bool wingman)
    {
        AnswerButton.IsEnabled = !wingman;
        AnswerButton.BackgroundColor = wingman ? DisabledGrey : RecordingRed;
        AnswerButton.TextColor = Colors.White;

        AskWingmanButton.IsEnabled = wingman;
        AskWingmanButton.BackgroundColor = wingman ? RecordingRed : DisabledGrey;
        AskWingmanButton.TextColor = Colors.White;

        SkipButton.IsEnabled = false;
        HoldButton.IsEnabled = false;
        ReplayButton.IsEnabled = false;
    }

    // Busy (a turn is running): everything disabled and dimmed.
    private void SetBusyButtons()
    {
        AnswerButton.IsEnabled = false;
        AnswerButton.BackgroundColor = DisabledGrey;
        AnswerButton.TextColor = Colors.White;

        AskWingmanButton.IsEnabled = false;
        AskWingmanButton.BackgroundColor = DisabledGrey;
        AskWingmanButton.TextColor = Colors.White;

        SkipButton.IsEnabled = false;
        HoldButton.IsEnabled = false;
        ReplayButton.IsEnabled = false;
    }

    // Top-right burger menu: switch between pages.
    private async void OnNavMenuClicked(object? sender, TappedEventArgs e)
    {
        var choice = await DisplayActionSheet("Go to", "Cancel", null, "Sessions", "FIFO", "FIFO Text", "Notes", "Exes", "Dictionary", "Transcripts");
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;
        CancelSpeechAndTurn();
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
