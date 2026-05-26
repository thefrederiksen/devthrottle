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

    // Voice status line colors (the big "where we are" label).
    private const string StatusGreen = "#5FD08A";   // ready / done
    private const string StatusYellow = "#E8B339";  // in progress
    private const string StatusRed = "#E5484D";     // recording / error
    private const string StatusBlue = "#2B6CB0";    // speaking / reading

    private readonly IUtteranceRecorder _recorder;
    private readonly IReplySpeaker _tts;
    private readonly IVoiceForeground _foreground;

    // Pumps the live mic level meter + elapsed time while recording (100ms,
    // same cadence as the offline recorder's meter).
    private readonly IDispatcherTimer _levelTimer;
    private DateTime _recStart;

    private SessionInfo? _selected;
    private bool _busy;

    // Cancels the in-flight turn (including a wait for a busy session to finish)
    // so Back can abandon it instead of trapping the user behind a long turn.
    private CancellationTokenSource? _turnCts;

    // Conductor mode: rotate only through sessions that need the user, one at a
    // time, waiting after each (push-to-talk reply or Next). Nothing auto-advances.
    private readonly ConductorState _conductor = new();
    private bool _conductorMode;

    // True when the current push-to-talk recording should be routed to the wingman
    // (the user tapped "Ask Wingman" rather than "Talk"). Set when recording starts,
    // read when it stops. Saying "Hey wingman ..." routes server-side regardless.
    private bool _wingmanTurn;

    // Which of the three single-session tabs is showing: "voice" | "wingman" | "terminal".
    private string _activeTab = "voice";

    // Terminal mirror: polls the raw PTY buffer while the Terminal tab is visible.
    // The cursor advances each poll so we only fetch newly written bytes; the
    // accumulated text is the on-screen scrollback. -1 means "dump the whole buffer
    // on the next poll" (first open or session switch).
    private readonly IDispatcherTimer _terminalTimer;
    private long _terminalCursor = -1;
    private readonly System.Text.StringBuilder _terminalText = new();
    private bool _terminalPolling;

    // True while a Wingman tab refresh/send is running, so the tab does not fire
    // overlapping calls.
    private bool _wingmanBusy;

    public TalkPage(IUtteranceRecorder recorder, IReplySpeaker tts, IVoiceForeground foreground)
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

        // Terminal mirror poll: ~1s while the Terminal tab is visible. Read-only;
        // it only fetches newly written PTY bytes and appends them on screen.
        _terminalTimer = Dispatcher.CreateTimer();
        _terminalTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _terminalTimer.Tick += async (_, _) => await PollTerminalAsync();

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
        _levelTimer.Stop();
        _terminalTimer.Stop();
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
        // Show that a session is currently being talked to (voice mode) so the roster
        // agrees with the desktop tile and the web view.
        if (s.VoiceMode) subtitle = "[voice] " + subtitle;
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
        // Mark the session as in walkie-talkie voice mode so the desktop tile, the web
        // view, and the roster all show it is being talked to. Best-effort and
        // fire-and-forget: it must never block the user from talking.
        _ = new DirectorVoiceClient(TokenEntry.Text ?? "")
            .SetVoiceModeAsync(session.TailnetEndpoint, session.SessionId, true);
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
        _levelTimer.Stop();
        RecordingCard.IsVisible = false;
        LevelMeter.Progress = 0;
        SetVoiceStatus("Ready", StatusGreen);
        SetTalkButton(recording: false, busy: false);

        // Reset the three-tab content for the new session: start on Voice, clear the
        // Wingman + Terminal panes so stale content from a previous session is gone.
        _terminalTimer.Stop();
        _terminalCursor = -1;
        _terminalText.Clear();
        TerminalOutputLabel.Text = "Connecting to terminal...";
        TerminalStatusLabel.Text = "";
        WingmanOutputLabel.Text = "Loading clean output...";
        WingmanNoteLabel.Text = "Tap Refresh for a read on this session.";
        WingmanStatusLabel.Text = "";
        if (WingmanInput is not null) WingmanInput.Text = "";
        if (TerminalInput is not null) TerminalInput.Text = "";
        ShowTab("voice");

        ListPanel.IsVisible = false;
        TalkPanel.IsVisible = true;
    }

    // ===== three-tab switcher (Voice / Wingman / Terminal) =================

    private void OnVoiceTabClicked(object? sender, EventArgs e) => ShowTab("voice");
    private void OnWingmanTabClicked(object? sender, EventArgs e) => ShowTab("wingman");
    private void OnTerminalTabClicked(object? sender, EventArgs e) => ShowTab("terminal");

    /// <summary>
    /// Swap the visible single-session section and update the segmented control's
    /// highlight. Starts the Terminal poll when entering Terminal and stops it when
    /// leaving; lazily loads the Wingman clean output the first time the tab is shown
    /// for a session. Voice is the eyes-free default. Immediate visual feedback first,
    /// then any async load runs in the background.
    /// </summary>
    private void ShowTab(string tab)
    {
        _activeTab = tab;

        VoiceSection.IsVisible = tab == "voice";
        WingmanSection.IsVisible = tab == "wingman";
        TerminalSection.IsVisible = tab == "terminal";

        SetTabButton(VoiceTabButton, tab == "voice");
        SetTabButton(WingmanTabButton, tab == "wingman");
        SetTabButton(TerminalTabButton, tab == "terminal");

        // Terminal poll only runs while its tab is visible (read-only mirror).
        if (tab == "terminal")
        {
            if (_selected is not null)
            {
                TerminalStatusLabel.Text = "Live mirror (read-only)";
                _terminalTimer.Start();
            }
        }
        else
        {
            _terminalTimer.Stop();
        }

        // Lazily load the Wingman clean output the first time the tab is opened for
        // this session (when it still shows the placeholder).
        if (tab == "wingman" && _selected is not null
            && WingmanOutputLabel.Text == "Loading clean output...")
        {
            _ = RefreshWingmanOutputAsync();
        }
    }

    private void SetTabButton(Button button, bool selected)
    {
        if (selected)
        {
            button.BackgroundColor = Color.FromArgb("#2B6CB0");
            button.TextColor = Colors.White;
            button.FontAttributes = FontAttributes.Bold;
        }
        else
        {
            button.BackgroundColor = Color.FromArgb("#1A2236");
            button.TextColor = Color.FromArgb("#8A93A6");
            button.FontAttributes = FontAttributes.None;
        }
    }

    private void OnBackClicked(object? sender, EventArgs e)
    {
        // Abandon any in-flight turn (e.g. a wait for a busy session to finish)
        // rather than trapping the user; the turn runners swallow the resulting
        // cancellation quietly.
        _turnCts?.Cancel();
        _levelTimer.Stop();
        _terminalTimer.Stop();
        RecordingCard.IsVisible = false;
        LevelMeter.Progress = 0;
        if (_recorder.IsRecording)
        {
            try { _ = _recorder.StopAsync(); } catch { /* discarding a half-captured clip on leave */ }
        }
        _tts.Stop();
        // Leaving the session: clear its voice-mode flag so other clients stop showing
        // it as being talked to. Best-effort, fire-and-forget.
        if (_selected is not null)
            _ = new DirectorVoiceClient(TokenEntry.Text ?? "")
                .SetVoiceModeAsync(_selected.TailnetEndpoint, _selected.SessionId, false);
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
        SetVoiceStatus("Reading...", StatusBlue);
        TurnStatusLabel.Text = "";

        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        var convo = new VoiceConversation(new DirectorVoiceClient(TokenEntry.Text ?? ""), _tts);
        try
        {
            await convo.SpeakConductorItemAsync(session, OnTurnUpdate, _turnCts.Token);
            SetVoiceStatus("Push Talk to reply, or Next", StatusGreen);
            TurnStatusLabel.Text = "";
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

    // "Talk" -> route to the agent (unless the user says "Hey wingman ...").
    private async void OnTalkClicked(object? sender, EventArgs e) => await HandleTalkButtonAsync(wingman: false);

    // "Ask Wingman" -> force this push-to-talk turn to the read-only wingman, no wake
    // phrase needed. Same record/stop mechanics as Talk.
    private async void OnAskWingmanClicked(object? sender, EventArgs e) => await HandleTalkButtonAsync(wingman: true);

    private async Task HandleTalkButtonAsync(bool wingman)
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
                _wingmanTurn = wingman;
                _foreground.Start();
                await _recorder.StartAsync();
                SetTalkButton(recording: true, busy: false);
                TurnStatusLabel.Text = "";
                _recStart = DateTime.UtcNow;
                RecElapsedLabel.Text = "00:00";
                LevelMeter.Progress = 0;
                RecordingCard.IsVisible = true;
                _levelTimer.Start();
                SetVoiceStatus(wingman ? "Recording for wingman" : "Recording", StatusRed);
                return;
            }

            // Second press (either button): stop capturing and run the round-trip in
            // whichever mode the recording was started in.
            _levelTimer.Stop();
            RecordingCard.IsVisible = false;
            LevelMeter.Progress = 0;
            SetVoiceStatus(_wingmanTurn ? "Asking wingman" : "Sending", StatusYellow);
            var audio = await _recorder.StopAsync();
            await RunTurnAsync(_selected, audio);
        }
        catch (OperationCanceledException)
        {
            // User pressed Back to abandon the turn; nothing to report.
        }
        catch (Exception ex)
        {
            _levelTimer.Stop();
            RecordingCard.IsVisible = false;
            LevelMeter.Progress = 0;
            TurnStatusLabel.Text = "";
            SetVoiceStatus("Something went wrong", StatusRed);
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
            await convo.SpeakTurnAsync(session, audio, OnTurnUpdate, _turnCts.Token, forceWingman: _wingmanTurn);
            TurnStatusLabel.Text = "";
            SetVoiceStatus("Ready", StatusGreen);
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
            case "transcribing": SetVoiceStatus("Transcribing...", StatusYellow); break;
            case "transcript": TranscriptLabel.Text = u.Text; TurnStatusLabel.Text = ""; break;
            case "thinking": SetVoiceStatus("Agent working...", StatusYellow); break;
            case "progress": SetVoiceStatus("Agent working...", StatusYellow); TurnStatusLabel.Text = u.Text; break;
            case "waiting": SetVoiceStatus("Session busy - holding your question", StatusYellow); TurnStatusLabel.Text = u.Text; break;
            case "reply": ReplyLabel.Text = u.Text; TurnStatusLabel.Text = ""; SetVoiceStatus("Speaking...", StatusBlue); break;
            case "wingman": SetVoiceStatus("Asking the wingman...", StatusBlue); TurnStatusLabel.Text = ""; break;
            case "name": SetVoiceStatus($"Reading: {u.Text}", StatusBlue); break;       // conductor
            case "recap": TranscriptLabel.Text = u.Text; SetVoiceStatus("Speaking...", StatusBlue); break;
            case "answer": ReplyLabel.Text = u.Text; TurnStatusLabel.Text = ""; SetVoiceStatus("Speaking...", StatusBlue); break;
            default: TurnStatusLabel.Text = u.Text; break;
        }
    });

    private void SetVoiceStatus(string text, string colorHex)
    {
        VoiceStatusLabel.Text = text;
        VoiceStatusLabel.TextColor = Color.FromArgb(colorHex);
    }

    // ===== WINGMAN tab: clean text output + annotation + Speak/Send -> agent =====

    private async void OnWingmanRefreshClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        await RefreshWingmanNoteAsync();
        await RefreshWingmanOutputAsync();
    }

    /// <summary>
    /// Fetch the wingman's plain-language note (what just happened / what it is
    /// waiting on) for the selected session and show it atop the clean output. Uses
    /// the SAME mode=explain path the Voice tab's "What's happening?" uses.
    /// </summary>
    private async Task RefreshWingmanNoteAsync()
    {
        if (_selected is null || _wingmanBusy) return;
        var session = _selected;
        try
        {
            _wingmanBusy = true;
            WingmanStatusLabel.Text = "Reading the session...";
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            var note = await client.ExplainAsync(session.TailnetEndpoint, session.SessionId);
            WingmanNoteLabel.Text = string.IsNullOrWhiteSpace(note)
                ? "Nothing to report yet." : note;
            WingmanStatusLabel.Text = "";
        }
        catch (Exception ex)
        {
            WingmanStatusLabel.Text = $"Wingman note failed: {ex.Message}";
        }
        finally
        {
            _wingmanBusy = false;
        }
    }

    /// <summary>
    /// Fetch the clean, de-noised conversation (parsed /turns) and render it as
    /// readable text. No TTS on this tab - replies are text only.
    /// </summary>
    private async Task RefreshWingmanOutputAsync()
    {
        if (_selected is null) return;
        var session = _selected;
        try
        {
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            var text = await client.GetTurnsTextAsync(session.TailnetEndpoint, session.SessionId);
            WingmanOutputLabel.Text = string.IsNullOrWhiteSpace(text)
                ? "No clean output yet for this session." : text;
        }
        catch (Exception ex)
        {
            WingmanOutputLabel.Text = $"Could not load clean output: {ex.Message}";
        }
    }

    // Speak = dictate into the textbox. Reuses the same record + transcribe path the
    // Voice tab uses, but instead of sending it puts the transcript in the input so
    // the user can review/edit before tapping Send.
    private async void OnWingmanSpeakClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        try
        {
            if (!_recorder.IsRecording)
            {
                var status = await Permissions.RequestAsync<Permissions.Microphone>();
                if (status != PermissionStatus.Granted)
                {
                    await DisplayAlert("Microphone needed",
                        "CC Director Client needs microphone access to dictate.", "OK");
                    return;
                }
                await _recorder.StartAsync();
                WingmanSpeakButton.Text = "Stop";
                WingmanSpeakButton.BackgroundColor = Color.FromArgb("#E5484D");
                WingmanStatusLabel.Text = "Listening...";
                return;
            }

            // Second press: stop, transcribe, drop the text into the input box.
            WingmanSpeakButton.Text = "Speak";
            WingmanSpeakButton.BackgroundColor = Color.FromArgb("#0E7C6B");
            WingmanStatusLabel.Text = "Transcribing...";
            var audio = await _recorder.StopAsync();
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            var t = await client.TranscribeUtteranceAsync(
                _selected.TailnetEndpoint, _selected.SessionId, audio.Bytes, audio.Mime);
            var existing = WingmanInput.Text ?? "";
            WingmanInput.Text = string.IsNullOrWhiteSpace(existing)
                ? t.Text : (existing.TrimEnd() + " " + t.Text);
            WingmanStatusLabel.Text = "";
        }
        catch (Exception ex)
        {
            WingmanSpeakButton.Text = "Speak";
            WingmanSpeakButton.BackgroundColor = Color.FromArgb("#0E7C6B");
            WingmanStatusLabel.Text = "";
            await DisplayAlert("Dictation error", ex.Message, "OK");
        }
    }

    // Send -> the working agent. The reply renders as TEXT in the clean output (no TTS).
    private async void OnWingmanSendClicked(object? sender, EventArgs e)
    {
        if (_selected is null || _wingmanBusy) return;
        var text = (WingmanInput.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var session = _selected;
        try
        {
            _wingmanBusy = true;
            WingmanSendButton.IsEnabled = false;
            WingmanStatusLabel.Text = "Sending to agent...";
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            var result = await client.SendChatAsync(session.TailnetEndpoint, session.SessionId, text);

            // Follow the turn with cheap polls (no TTS, no progress notes) until the
            // agent finishes, then refresh the clean output so the reply shows as text.
            while (result.ShouldKeepPolling)
            {
                WingmanStatusLabel.Text = "Agent working...";
                await Task.Delay(TimeSpan.FromSeconds(3));
                result = await client.PollChatAsync(session.TailnetEndpoint, session.SessionId, wantProgress: false);
            }

            WingmanInput.Text = "";
            WingmanStatusLabel.Text = string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase)
                ? "" : $"Turn ended: {result.Status}";
            await RefreshWingmanOutputAsync();
            await RefreshWingmanNoteAsync();
        }
        catch (Exception ex)
        {
            WingmanStatusLabel.Text = "";
            await DisplayAlert("Send error", ex.Message, "OK");
        }
        finally
        {
            _wingmanBusy = false;
            WingmanSendButton.IsEnabled = true;
        }
    }

    // ===== TERMINAL tab: read-only raw mirror + control buttons ============

    /// <summary>
    /// One poll of the raw PTY buffer. Appends only the newly written bytes (cursor
    /// advances each call), keeps the on-screen scrollback bounded, and scrolls to
    /// the bottom. Read-only: this never writes to the PTY.
    /// </summary>
    private async Task PollTerminalAsync()
    {
        if (_selected is null || _terminalPolling || _activeTab != "terminal") return;
        var session = _selected;
        try
        {
            _terminalPolling = true;
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            // Read a fresh cleaned snapshot (last N lines) each poll and REPLACE the
            // view. Cleaned text + replace avoids both raw escape-code noise and the
            // ghost-stacking you get from appending a TUI's repeated repaints.
            var slice = await client.GetBufferAsync(session.TailnetEndpoint, session.SessionId, -1);
            _terminalCursor = slice.NewCursor;
            if (!string.IsNullOrEmpty(slice.Text))
            {
                TerminalOutputLabel.Text = slice.Text;
                await TerminalScroll.ScrollToAsync(0, TerminalOutputLabel.Height, false);
            }
            else if (TerminalOutputLabel.Text == "Connecting to terminal...")
            {
                TerminalOutputLabel.Text = "(no terminal output yet)";
            }
        }
        catch (Exception ex)
        {
            TerminalStatusLabel.Text = $"Mirror error: {ex.Message}";
            _terminalTimer.Stop();
        }
        finally
        {
            _terminalPolling = false;
        }
    }

    // Send a typed line to the PTY (AppendEnter so it submits).
    private async void OnTerminalSendClicked(object? sender, EventArgs e)
    {
        var text = (TerminalInput.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        TerminalInput.Text = "";
        await SendTerminalKeysAsync(text, appendEnter: true);
    }

    private async void OnTerminalEnterClicked(object? sender, EventArgs e)
        => await SendTerminalKeysAsync("\r", appendEnter: false);

    // Arrow keys: the standard cursor escape sequences ESC[A / ESC[B / ESC[C / ESC[D.
    // The leading byte is the real Escape control (decimal 27, 0x1b); built from
    // (char)27 so the source stays plain ASCII with no embedded control char.
    private static readonly string EscPrefix = ((char)27).ToString() + "[";
    private async void OnTerminalUpClicked(object? sender, EventArgs e)
        => await SendTerminalKeysAsync(EscPrefix + "A", appendEnter: false);
    private async void OnTerminalDownClicked(object? sender, EventArgs e)
        => await SendTerminalKeysAsync(EscPrefix + "B", appendEnter: false);
    private async void OnTerminalRightClicked(object? sender, EventArgs e)
        => await SendTerminalKeysAsync(EscPrefix + "C", appendEnter: false);
    private async void OnTerminalLeftClicked(object? sender, EventArgs e)
        => await SendTerminalKeysAsync(EscPrefix + "D", appendEnter: false);

    private async void OnTerminalEscClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        var session = _selected;
        try
        {
            TerminalStatusLabel.Text = "Sent Esc";
            await new DirectorVoiceClient(TokenEntry.Text ?? "")
                .SendEscapeAsync(session.TailnetEndpoint, session.SessionId);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Terminal error", ex.Message, "OK");
        }
    }

    private async void OnTerminalStopClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        var session = _selected;
        try
        {
            TerminalStatusLabel.Text = "Sent Stop (Ctrl+C)";
            await new DirectorVoiceClient(TokenEntry.Text ?? "")
                .SendInterruptAsync(session.TailnetEndpoint, session.SessionId);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Terminal error", ex.Message, "OK");
        }
    }

    private async Task SendTerminalKeysAsync(string text, bool appendEnter)
    {
        if (_selected is null) return;
        var session = _selected;
        try
        {
            TerminalStatusLabel.Text = "Sent";
            await new DirectorVoiceClient(TokenEntry.Text ?? "")
                .SendKeysAsync(session.TailnetEndpoint, session.SessionId, text, appendEnter);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Terminal error", ex.Message, "OK");
        }
    }

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
            TalkButton.Text = "Ask Agent";
            TalkButton.BackgroundColor = Color.FromArgb("#5FD08A");
            TalkButton.TextColor = Color.FromArgb("#06210F");
        }
    }

    private sealed record SessionRow(SessionInfo Session, string Name, string Subtitle, Color Dot);
}
