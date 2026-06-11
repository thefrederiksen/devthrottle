using CcDirectorClient.Recording;
using CcDirectorClient.Voice;
using Microsoft.Maui.Controls.Shapes;

namespace CcDirectorClient;

/// <summary>
/// Sessions screen: lists the roster from the Gateway and lets the user pick one to
/// open. Per-session view has three tabs:
///   - Voice: inline shared <see cref="Controls.VoiceSessionView"/> - literally the
///     same control that FIFO uses while it walks the queue. AnswerDelivered is a
///     no-op (stay on the session); HoldRequested or ExitRequested pops back to the
///     roster.
///   - Wingman: inline. Read the clean output / wingman note, dictate-or-type-and-send.
///   - Terminal: inline. Read-only raw xterm.js mirror plus control buttons.
///
/// The screen is kept awake while in front (Waze-style) so a glance check does not
/// require unlocking.
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

    // True while the app is in the background (Waze etc.). Speech that the
    // VoiceSessionView started must keep playing; only a real in-app navigation tears
    // down the control. Set from the Window lifecycle.
    private bool _backgrounded;
    private bool _lifecycleHooked;

    // Which inline tab is showing: "voice" | "wingman" | "terminal". Voice is the
    // default because it is the headlining feature; the user is most likely to want
    // to talk to the session they just picked.
    private string _activeTab = "voice";

    // True while a Wingman tab refresh/send is running, so the tab does not fire
    // overlapping calls.
    private bool _wingmanBusy;

    // The latest brief currently rendered, kept so the vote/close actions know its
    // TurnNumber + suggested action, and so the poll can skip an unchanged re-render.
    private TurnBrief? _lastBrief;

    // Cancels the Wingman brief poll loop when the tab or page is left.
    private CancellationTokenSource? _wingmanPollCts;

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

        // The Voice tab is a self-contained walkie-talkie (see the OnVoice* handlers): it
        // drives the recorder + transcribe + send + TTS directly, so there is no shared
        // control to configure here. _tts.PlayingChanged is hooked in OnAppearing to show
        // the "Stop talking" button only while a reply plays.
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Waze-style: keep the screen on while the talk screen is in front so the
        // user can glance at state without unlocking, and audio keeps flowing.
        DeviceDisplay.Current.KeepScreenOn = true;
        _backgrounded = false;
        HookAppLifecycle();
        // Hook reply playback so the "Stop talking" button tracks it (re-subscribe safely
        // on a background return). Sync the button to the current state.
        _tts.PlayingChanged -= OnTtsPlayingChanged;
        _tts.PlayingChanged += OnTtsPlayingChanged;
        VoiceStopSpeakingButton.IsVisible = _tts.IsPlaying;
        _ = LoadRosterAsync();

        // Returning from background onto an open session's Wingman tab: resume the brief
        // poll that OnDisappearing stopped.
        if (_selected is not null && _activeTab == "wingman") StartWingmanBriefPoll();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        DeviceDisplay.Current.KeepScreenOn = false;

        // The page is no longer in front, so the Wingman brief is not visible: stop polling
        // in both the background and the navigate-away cases. OnAppearing resumes it.
        StopWingmanBriefPoll();

        // Background -> keep reply playback + the foreground service alive so the voice keeps
        // talking (Waze-style). Real navigation away -> tear the voice round-trip down.
        if (_backgrounded) return;

        _tts.PlayingChanged -= OnTtsPlayingChanged;
        StopVoiceActivity();
        UnhookAppLifecycle();
        UnloadTerminalWebView();
        _foreground.Stop();
    }

    // ===== app background vs in-app navigation =============================

    // The MAUI Window fires Deactivated when the app loses focus and does NOT fire on
    // in-app page navigation, distinguishing background from navigation.
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
    // Inline-tab handlers (Wingman, Terminal) check Connectivity directly before any
    // network call so a doomed request never dims their buttons behind the HTTP timeout.

    private static bool DeviceOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    // ===== roster ===========================================================

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadRosterAsync();

    private async Task LoadRosterAsync()
    {
        SaveCreds();
        var gate = OfflineGuard.Check(DeviceOnline, "load sessions");
        if (!gate.Allowed) { ListStatusLabel.Text = gate.Message; SessionsList.ItemsSource = null; return; }
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
        _selected = session;
        TalkSessionName.Text = session.DisplayName;
        TalkSessionState.Text = string.IsNullOrWhiteSpace(session.LastStatusReason)
            ? session.ActivityState : session.LastStatusReason;

        // Reset the three tab panels for the new session; they load lazily on tab entry.
        TerminalStatusLabel.Text = "";
        WingmanStatusLabel.Text = "";
        WingmanBriefContainer.Children.Clear();
        _lastBrief = null;
        if (WingmanInput is not null) WingmanInput.Text = "";
        if (TerminalInput is not null) TerminalInput.Text = "";
        ResetVoiceUi();

        ListPanel.IsVisible = false;
        TalkPanel.IsVisible = true;

        // All three tabs work on every session now: Voice (walkie-talkie), Wingman (the
        // Gateway brief, stamped for every session) and Terminal. So all tab buttons show.
        VoiceTabButton.IsVisible = true;
        WingmanTabButton.IsVisible = true;
        TabSwitcher.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);

        // Open a wingman-on session on Voice (the headline) and a wingman-off / freshly
        // created session on Terminal so the user watches it come alive. Either way the
        // Voice tab is one tap away.
        ShowTab(session.WingmanEnabled ? "voice" : "terminal");
    }

    // ===== three-tab switcher (Voice / Wingman / Terminal) =================

    private void OnVoiceTabClicked(object? sender, EventArgs e) => ShowTab("voice");
    private void OnWingmanTabClicked(object? sender, EventArgs e) => ShowTab("wingman");
    private void OnTerminalTabClicked(object? sender, EventArgs e) => ShowTab("terminal");

    /// <summary>
    /// Swap which inline section is visible and update the segmented control's highlight.
    /// Starts/stops the Terminal stream on entry/exit; polls the Wingman brief only while
    /// that tab is shown; stops any voice recording/playback when the Voice tab is left.
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

        if (tab == "terminal")
        {
            if (_selected is not null) LoadTerminalWebView();
        }
        else
        {
            UnloadTerminalWebView();
        }

        // Leaving the Voice tab stops an in-flight recording and any reply playback so the
        // mic/audio never keep running behind another tab.
        if (tab != "voice") StopVoiceActivity();

        // The Wingman tab polls the Gateway for the latest brief while it is the active tab;
        // leaving it stops the poll so we never fetch a brief the user cannot see.
        if (tab == "wingman" && _selected is not null)
            StartWingmanBriefPoll();
        else
            StopWingmanBriefPoll();
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

    private void OnBackClicked(object? sender, EventArgs e) => GoBackToRoster();

    private void GoBackToRoster()
    {
        StopWingmanBriefPoll();
        UnloadTerminalWebView();
        // Stop any in-flight recording and reply playback before leaving the session.
        StopVoiceActivity();
        _selected = null;
        _foreground.Stop();
        TalkPanel.IsVisible = false;
        ListPanel.IsVisible = true;
        _ = LoadRosterAsync();
    }

    // ===== VOICE tab: walkie-talkie (record -> submit -> send -> speak reply) =====
    // The big button toggles Record (green) <-> Submit (red). On Submit the clip is
    // transcribed, sent to the agent, and the agent's reply is spoken aloud + shown. All
    // self-contained: it drives the recorder, DirectorVoiceClient, and the TTS player.

    // True while the mic is capturing; true while the whole turn (transcribe/send/speak) runs.
    private bool _voiceRecording;
    private bool _voiceTurnBusy;
    // Cancels an in-flight SpeakTurnAsync so tab/page leave stops the round-trip cleanly.
    private CancellationTokenSource? _voiceTurnCts;

    private void ResetVoiceUi()
    {
        _voiceRecording = false;
        SetVoiceRecordingUi(false);
        VoiceStatusLabel.Text = "Tap Record and talk to the agent.";
        VoiceYouCard.IsVisible = false;
        VoiceReplyCard.IsVisible = false;
        VoiceYouLabel.Text = "";
        VoiceReplyLabel.Text = "";
    }

    // The "Stop talking" button mirrors reply playback (fired on the main thread).
    private void OnTtsPlayingChanged(bool playing) => VoiceStopSpeakingButton.IsVisible = playing;

    private void OnVoiceStopSpeakingClicked(object? sender, EventArgs e) => _tts.Stop();

    // Stop an in-flight recording (discarding the clip), cancel any running turn, and cut
    // any reply playback. Safe to call when neither is active. Used on tab/page leave.
    private void StopVoiceActivity()
    {
        _voiceTurnCts?.Cancel();
        _tts.Stop();
        if (_voiceRecording || _recorder.IsRecording)
        {
            try { _ = _recorder.StopAsync(); } catch { /* discarding a half-captured clip on leave */ }
            _voiceRecording = false;
            SetVoiceRecordingUi(false);
        }
    }

    private void SetVoiceRecordingUi(bool recording)
    {
        VoiceRecordButton.Text = recording ? "Submit" : "Record";
        VoiceRecordButton.BackgroundColor = recording ? Color.FromArgb("#E5484D") : Color.FromArgb("#5FD08A");
        VoiceRecordButton.TextColor = recording ? Colors.White : Color.FromArgb("#06210F");
        VoiceCancelButton.IsVisible = recording;
    }

    private async void OnVoiceRecordClicked(object? sender, EventArgs e)
    {
        if (_selected is null || _voiceTurnBusy) return;

        if (!_voiceRecording)
        {
            // Start capturing.
            var status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlert("Microphone needed",
                    "CC Director Client needs microphone access to talk to the agent.", "OK");
                return;
            }
            try
            {
                _foreground.Start();
                await _recorder.StartAsync();
                _voiceRecording = true;
                SetVoiceRecordingUi(true);
                VoiceStatusLabel.Text = "Recording... tap Submit when you're done.";
            }
            catch (Exception ex)
            {
                _foreground.Stop();
                await DisplayAlert("Recording error", ex.Message, "OK");
            }
            return;
        }

        // Submit: stop capturing and run the turn.
        UtteranceAudio audio;
        try
        {
            audio = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            _voiceRecording = false;
            SetVoiceRecordingUi(false);
            _foreground.Stop();
            await DisplayAlert("Recording error", ex.Message, "OK");
            return;
        }
        _voiceRecording = false;
        SetVoiceRecordingUi(false);
        await RunVoiceTurnAsync(_selected, audio);
    }

    private async void OnVoiceCancelClicked(object? sender, EventArgs e)
    {
        if (!_voiceRecording) return;
        _voiceTurnCts?.Cancel();
        _tts.Stop();
        try { await _recorder.StopAsync(); } catch { /* discard */ }
        _voiceRecording = false;
        SetVoiceRecordingUi(false);
        _foreground.Stop();
        VoiceStatusLabel.Text = "Tap Record and talk to the agent.";
    }

    // Transcribe the clip, wait until the session is ready, send the question, follow the
    // turn to completion, summarize via the wingman into plain spoken prose, then speak the
    // summary and show the raw reply on screen. The foreground service stays up for the whole
    // turn so capture + playback survive a screen-off.
    // Status line cycles: Transcribing -> Sending -> Thinking -> Summarizing -> Speaking.
    private async Task RunVoiceTurnAsync(SessionInfo session, UtteranceAudio audio)
    {
        var gate = OfflineGuard.Check(DeviceOnline, "send your voice message");
        if (!gate.Allowed) { VoiceStatusLabel.Text = gate.Message; _foreground.Stop(); return; }

        _voiceTurnBusy = true;
        VoiceRecordButton.IsEnabled = false;

        // Ensure the wingman is active for this session - voice mode requires it for
        // reply summarization, regardless of the gateway default. Best-effort.
        _ = new DirectorVoiceClient(TokenEntry.Text ?? "")
            .SetWingmanEnabledAsync(session.TailnetEndpoint, session.SessionId, true);

        var convo = new VoiceConversation(new DirectorVoiceClient(TokenEntry.Text ?? ""), _tts);
        _voiceTurnCts?.Cancel();
        _voiceTurnCts = new CancellationTokenSource();
        var cts = _voiceTurnCts;
        try
        {
            string rawReply = await convo.SpeakTurnAsync(session, audio,
                onUpdate: u =>
                {
                    // Route each stage to the appropriate UI element on the main thread.
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        switch (u.Stage)
                        {
                            case "transcribing":
                                VoiceStatusLabel.Text = "Transcribing...";
                                break;
                            case "transcript":
                                VoiceYouLabel.Text = u.Text;
                                VoiceYouCard.IsVisible = true;
                                VoiceReplyCard.IsVisible = false;
                                break;
                            case "waiting":
                                VoiceStatusLabel.Text = "Waiting for session to finish...";
                                break;
                            case "thinking":
                                VoiceStatusLabel.Text = "Thinking...";
                                break;
                            case "summarizing":
                                VoiceStatusLabel.Text = "Summarizing...";
                                break;
                            case "reply":
                                VoiceReplyLabel.Text = u.Text;
                                VoiceReplyCard.IsVisible = true;
                                VoiceStatusLabel.Text = "Speaking...";
                                break;
                            default:
                                VoiceStatusLabel.Text = u.Text;
                                break;
                        }
                    });
                },
                ct: cts.Token);

            if (!string.IsNullOrWhiteSpace(rawReply))
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    VoiceReplyLabel.Text = rawReply;
                    VoiceReplyCard.IsVisible = true;
                });
            }
            VoiceStatusLabel.Text = "Tap Record to reply.";
        }
        catch (OperationCanceledException)
        {
            // User cancelled or navigated away.
            VoiceStatusLabel.Text = "Tap Record and talk to the agent.";
        }
        catch (Exception ex)
        {
            VoiceStatusLabel.Text = "";
            await DisplayAlert("Voice error", ex.Message, "OK");
        }
        finally
        {
            _voiceTurnBusy = false;
            VoiceRecordButton.IsEnabled = true;
            _foreground.Stop();
        }
    }

    // ===== FIFO launcher ===================================================

    private async void OnFifoClicked(object? sender, EventArgs e)
    {
        _tts.Stop();
        await Shell.Current.GoToAsync("//FifoPage");
    }

    // ===== New-session flow (issue #245): pick a fleet Director + a recent repo =====

    // The Director the user picked in step 1; its TailnetEndpoint is stamped onto the
    // session we create so the phone can then open its terminal.
    private DirectorInfo? _selectedDirector;
    // Guards against a double-tap firing two POST /sessions while the first is in flight.
    private bool _creatingSession;

    private async void OnNewSessionClicked(object? sender, EventArgs e) => await OpenNewSessionPanelAsync();

    private async Task OpenNewSessionPanelAsync()
    {
        SaveCreds();
        _selectedDirector = null;
        DirectorsList.ItemsSource = null;
        ReposList.ItemsSource = null;
        ReposStatusLabel.Text = "Pick a machine first.";
        NewSessionStatusLabel.Text = "";
        NewSessionPathEntry.Text = "";

        ListPanel.IsVisible = false;
        NewSessionPanel.IsVisible = true;

        var gate = OfflineGuard.Check(DeviceOnline, "list machines");
        if (!gate.Allowed) { DirectorsStatusLabel.Text = gate.Message; return; }

        DirectorsStatusLabel.Text = "Loading machines...";
        try
        {
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            var directors = await gateway.GetDirectorsAsync();
            var rows = directors.Select(ToDirectorRow).ToList();
            DirectorsList.ItemsSource = rows;
            DirectorsStatusLabel.Text = rows.Count == 0
                ? "No machines found."
                : $"{rows.Count} machine(s). Tap one.";

            // Default-select the most-recently-seen machine (rows[0]) so the repos load
            // with one fewer tap; setting SelectedItem fires OnDirectorSelected.
            if (rows.Count > 0) DirectorsList.SelectedItem = rows[0];
        }
        catch (Exception ex)
        {
            DirectorsStatusLabel.Text = $"Could not load machines: {ex.Message}";
        }
    }

    private async void OnDirectorSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not DirectorRow row) return;
        _selectedDirector = row.Director;       // keep the highlight to show the choice
        await LoadReposAsync(row.Director);
    }

    private async Task LoadReposAsync(DirectorInfo director)
    {
        ReposList.ItemsSource = null;
        var gate = OfflineGuard.Check(DeviceOnline, "list repositories");
        if (!gate.Allowed) { ReposStatusLabel.Text = gate.Message; return; }

        ReposStatusLabel.Text = $"Loading repos on {director.DisplayName}...";
        try
        {
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            var repos = await gateway.GetReposAsync(director.DirectorId);
            var rows = repos.Select(ToRepoRow).ToList();
            ReposList.ItemsSource = rows;
            ReposStatusLabel.Text = rows.Count == 0
                ? "No recent repos here. Enter a path below."
                : $"{rows.Count} recent repo(s). Tap one to start.";
        }
        catch (Exception ex)
        {
            ReposStatusLabel.Text = $"Could not load repos: {ex.Message}";
        }
    }

    private async void OnRepoSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not RepoRow row) return;
        if (sender is CollectionView cv) cv.SelectedItem = null;   // allow re-tapping
        await CreateSessionAsync(row.Repo.Path);
    }

    private async void OnCreateSessionClicked(object? sender, EventArgs e)
    {
        var path = (NewSessionPathEntry.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            NewSessionStatusLabel.Text = "Enter a repo path, or tap a recent repo above.";
            return;
        }
        await CreateSessionAsync(path);
    }

    private async Task CreateSessionAsync(string repoPath)
    {
        if (_creatingSession) return;
        if (_selectedDirector is null)
        {
            NewSessionStatusLabel.Text = "Pick a machine first.";
            return;
        }
        var gate = OfflineGuard.Check(DeviceOnline, "start a session");
        if (!gate.Allowed) { NewSessionStatusLabel.Text = gate.Message; return; }

        var director = _selectedDirector;
        _creatingSession = true;
        NewSessionCreateButton.IsEnabled = false;
        NewSessionCreateButton.Text = "Creating...";
        NewSessionStatusLabel.Text = $"Creating session in {repoPath} on {director.DisplayName}...";
        try
        {
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            var session = await gateway.CreateSessionAsync(director, repoPath);
            // Open the new session straight on the Terminal tab so the user watches it
            // come alive. A freshly-created session is Wingman-off, so EnterTalk opens
            // the Terminal tab automatically.
            NewSessionPanel.IsVisible = false;
            EnterTalk(session);
        }
        catch (Exception ex)
        {
            NewSessionStatusLabel.Text = $"Could not create session: {ex.Message}";
        }
        finally
        {
            _creatingSession = false;
            NewSessionCreateButton.IsEnabled = true;
            NewSessionCreateButton.Text = "Create session";
        }
    }

    private void OnNewSessionBackClicked(object? sender, EventArgs e)
    {
        NewSessionPanel.IsVisible = false;
        ListPanel.IsVisible = true;
        _ = LoadRosterAsync();
    }

    private DirectorRow ToDirectorRow(DirectorInfo d)
    {
        var ver = string.IsNullOrWhiteSpace(d.Version) ? "" : $"v{d.Version}";
        var seen = d.LastSeen.HasValue ? $"last seen {d.LastSeen.Value.ToLocalTime():t}" : "not seen recently";
        var subtitle = string.IsNullOrWhiteSpace(ver) ? seen : $"{ver} - {seen}";
        return new DirectorRow(d, d.DisplayName, subtitle);
    }

    private RepoRow ToRepoRow(RepoInfo r) => new(r, r.DisplayName, r.Path);

    // ===== WINGMAN tab: clean text output + annotation + Speak/Send -> agent =====

    private async void OnWingmanRefreshClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        await RefreshWingmanBriefAsync();
    }

    // ----- brief poll ------------------------------------------------------
    // The brief is stamped by the Gateway at each turn's end; the tab polls for the latest
    // while it is in front so a new one appears on its own. A single poll loop, cancelled on
    // tab/page leave, owns the cadence; the busy guard keeps it from overlapping a manual
    // Refresh or a post-send nudge.

    private void StartWingmanBriefPoll()
    {
        StopWingmanBriefPoll();
        var cts = new CancellationTokenSource();
        _wingmanPollCts = cts;
        _ = WingmanBriefPollLoopAsync(cts.Token);
    }

    private void StopWingmanBriefPoll()
    {
        _wingmanPollCts?.Cancel();
        _wingmanPollCts?.Dispose();
        _wingmanPollCts = null;
    }

    private async Task WingmanBriefPollLoopAsync(CancellationToken ct)
    {
        // First fetch is immediate; then every 5s until the tab/page is left.
        while (!ct.IsCancellationRequested)
        {
            await RefreshWingmanBriefAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshWingmanBriefAsync()
    {
        if (_selected is null || _wingmanBusy) return;
        var session = _selected;
        var gate = OfflineGuard.Check(DeviceOnline, "load the wingman brief");
        if (!gate.Allowed) { WingmanStatusLabel.Text = gate.Message; return; }
        try
        {
            _wingmanBusy = true;
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            var latest = await gateway.GetLatestBriefAsync(session.SessionId);

            // Session switched or tab left while the fetch was in flight: drop the result.
            if (_selected is null || _selected.SessionId != session.SessionId || _activeTab != "wingman")
                return;

            if (latest.Brief is null)
            {
                _lastBrief = null;
                WingmanBriefContainer.Children.Clear();
                WingmanStatusLabel.Text = string.Equals(latest.BriefingState, "Briefing", StringComparison.OrdinalIgnoreCase)
                    ? "Wingman is reading this turn..."
                    : "No brief yet - the wingman writes one at each turn's end.";
                return;
            }

            WingmanStatusLabel.Text = BriefStatusLine(latest.Brief);

            // Skip a redundant rebuild when the brief has not changed - avoids the 5s
            // flicker and keeps an evidence expander the user opened.
            if (_lastBrief is not null
                && _lastBrief.TurnNumber == latest.Brief.TurnNumber
                && _lastBrief.GeneratedAtUtc == latest.Brief.GeneratedAtUtc)
                return;

            _lastBrief = latest.Brief;
            RenderBrief(latest.Brief);
        }
        catch (Exception ex)
        {
            WingmanStatusLabel.Text = $"Could not load the brief: {ex.Message}";
        }
        finally
        {
            _wingmanBusy = false;
        }
    }

    private static string BriefStatusLine(TurnBrief b)
    {
        var model = string.IsNullOrWhiteSpace(b.Model) ? "wingman" : b.Model;
        var tag = b.Degraded ? " (degraded)" : "";
        return $"Brief from {model}{tag} - turn {b.TurnNumber}";
    }

    // ----- brief rendering -------------------------------------------------
    // The cards are built in code (content is variable) into WingmanBriefContainer, mirroring
    // the desktop BriefPane: headline / you're doing / you asked / NEEDS YOU (tappable
    // options) / ALL CLEAR / CLAUDE DID / vote / mission-complete.

    private static readonly Color Ink = Color.FromArgb("#E6EAF2");
    private static readonly Color Mut = Color.FromArgb("#8A93A6");
    private static readonly Color CardBg = Color.FromArgb("#0F1626");
    private static readonly Color CardLine = Color.FromArgb("#1E2A44");
    private static readonly Color Teal = Color.FromArgb("#37C2B6");
    private static readonly Color GoodGreen = Color.FromArgb("#5FD08A");
    private static readonly Color WarnAmber = Color.FromArgb("#E8B339");
    private static readonly Color BadRed = Color.FromArgb("#E5484D");

    private void RenderBrief(TurnBrief b)
    {
        var stack = WingmanBriefContainer;
        stack.Children.Clear();

        // Headline (falls back to intent) + optional new-chapter pill.
        var headline = !string.IsNullOrWhiteSpace(b.Headline) ? b.Headline
            : (!string.IsNullOrWhiteSpace(b.Intent) ? b.Intent : "This session");
        var headBox = new VerticalStackLayout { Spacing = 6 };
        headBox.Children.Add(new Label
        { Text = headline, TextColor = Ink, FontSize = 18, FontAttributes = FontAttributes.Bold });
        if (b.NewChapter) headBox.Children.Add(Pill("New chapter", Mut));
        stack.Children.Add(Card(CardBg, CardLine, headBox));

        // You're doing (rolling intent), only when it says more than the headline already did.
        if (!string.IsNullOrWhiteSpace(b.Intent) && !string.Equals(b.Intent, headline, StringComparison.Ordinal))
            stack.Children.Add(Card(CardBg, CardLine, Section("YOU'RE DOING", Teal, b.Intent)));

        // You asked.
        if (!string.IsNullOrWhiteSpace(b.YouAsked))
            stack.Children.Add(Card(CardBg, CardLine, Section("YOU ASKED", Mut, "\"" + b.YouAsked!.Trim() + "\"")));

        // Needs you / all clear.
        if (b.NeedsYou is not null)
            stack.Children.Add(NeedsYouCard(b.NeedsYou));
        else if (!string.IsNullOrWhiteSpace(b.AllClear))
            stack.Children.Add(AllClearCard(b.AllClear!));

        // Claude did.
        if (b.Did is not null && b.Did.Count > 0)
            stack.Children.Add(Card(CardBg, CardLine, DidSection(b.Did)));

        // Vote strip.
        stack.Children.Add(VoteStrip());

        // Mission complete (only the known close_session type is rendered).
        if (b.SuggestedAction is not null
            && string.Equals(b.SuggestedAction.Type, "close_session", StringComparison.OrdinalIgnoreCase))
            stack.Children.Add(MissionCompleteCard(b.SuggestedAction));
    }

    private static Border Card(Color bg, Color border, View content) => new()
    {
        BackgroundColor = bg,
        Stroke = new SolidColorBrush(border),
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = 13 },
        Padding = new Thickness(14, 13),
        Content = content,
    };

    private static Label HeadLabel(string text, Color color) => new()
    { Text = text, TextColor = color, FontSize = 11, FontAttributes = FontAttributes.Bold, CharacterSpacing = 0.6 };

    private static VerticalStackLayout Section(string label, Color labelColor, string body)
    {
        var v = new VerticalStackLayout { Spacing = 5 };
        v.Children.Add(HeadLabel(label, labelColor));
        v.Children.Add(new Label { Text = body, TextColor = Ink, FontSize = 14, LineHeight = 1.35 });
        return v;
    }

    private static Border Pill(string text, Color textColor) => new()
    {
        BackgroundColor = Colors.Transparent,
        Stroke = new SolidColorBrush(CardLine),
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = 20 },
        Padding = new Thickness(9, 2),
        HorizontalOptions = LayoutOptions.Start,
        Content = new Label { Text = text, TextColor = textColor, FontSize = 11 },
    };

    private static VerticalStackLayout DidSection(List<string> did)
    {
        var v = new VerticalStackLayout { Spacing = 5 };
        v.Children.Add(HeadLabel("CLAUDE DID", Mut));
        foreach (var d in did)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var g = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            };
            var dot = new Label { Text = "-", TextColor = Mut, FontSize = 14 };
            var lbl = new Label { Text = d.Trim(), TextColor = Ink, FontSize = 14, LineHeight = 1.4 };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(lbl, 1);
            g.Children.Add(dot);
            g.Children.Add(lbl);
            v.Children.Add(g);
        }
        return v;
    }

    private Border NeedsYouCard(TurnBriefNeedsYou n)
    {
        var fyi = string.Equals(n.Urgency, "fyi", StringComparison.OrdinalIgnoreCase);
        var border = fyi ? WarnAmber : BadRed;
        var v = new VerticalStackLayout { Spacing = 8 };
        v.Children.Add(HeadLabel("NEEDS YOU", fyi ? WarnAmber : Color.FromArgb("#FF8A8E")));
        // v3.4 (the trust fix): Claude's verbatim decisive line comes FIRST and expanded - the
        // brief leads with Claude's own words, not a re-derived summary the user cannot trust.
        if (!string.IsNullOrWhiteSpace(n.Evidence))
        {
            v.Children.Add(new Label
            { Text = "CLAUDE SAID", TextColor = Color.FromArgb("#9FB4D8"), FontSize = 10, CharacterSpacing = 1.2, FontAttributes = FontAttributes.Bold });
            v.Children.Add(new Label
            { Text = n.Evidence.Trim(), TextColor = Ink, FontSize = 14, LineHeight = 1.4 });
        }

        v.Children.Add(new Label
        { Text = n.Statement, TextColor = Ink, FontSize = 15, FontAttributes = FontAttributes.Bold, LineHeight = 1.4 });

        if (string.Equals(n.Confidence, "ambiguous", StringComparison.OrdinalIgnoreCase))
            v.Children.Add(new Label { Text = "Wingman is unsure - double-check.", TextColor = WarnAmber, FontSize = 12 });

        // Options.
        if (n.Options is not null && n.Options.Count > 0)
        {
            var multiple = string.Equals(n.SelectionMode, "multiple", StringComparison.OrdinalIgnoreCase);
            var opts = new VerticalStackLayout { Spacing = 8 };
            foreach (var o in n.Options) opts.Children.Add(OptionButton(o, n, multiple));
            if (multiple && !string.IsNullOrWhiteSpace(n.Submit))
            {
                var submit = new Button
                {
                    Text = "Submit", BackgroundColor = Color.FromArgb("#2B6CB0"), TextColor = Colors.White,
                    HeightRequest = 46, CornerRadius = 11,
                };
                submit.Clicked += async (_, _) => await SendBriefAnswerAsync(n.Submit!, appendEnter: false, label: "Submitted");
                opts.Children.Add(submit);
            }
            v.Children.Add(opts);
        }

        // If you do nothing.
        if (!string.IsNullOrWhiteSpace(n.IfIgnored))
        {
            var g = new Grid
            {
                ColumnSpacing = 6,
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            };
            var head = new Label
            { Text = "If you do nothing:", TextColor = Color.FromArgb("#F0C558"), FontSize = 12.5, FontAttributes = FontAttributes.Bold };
            var body = new Label { Text = n.IfIgnored!.Trim(), TextColor = WarnAmber, FontSize = 12.5, LineHeight = 1.35 };
            Grid.SetColumn(head, 0);
            Grid.SetColumn(body, 1);
            g.Children.Add(head);
            g.Children.Add(body);
            v.Children.Add(g);
        }

        return Card(CardBg, border, v);
    }

    private Border OptionButton(TurnBriefOption o, TurnBriefNeedsYou n, bool multiple)
    {
        var inner = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        var textStack = new VerticalStackLayout { Spacing = 2 };
        textStack.Children.Add(new Label { Text = o.Key, TextColor = Ink, FontSize = 14, FontAttributes = FontAttributes.Bold });
        if (!string.IsNullOrWhiteSpace(o.Note))
            textStack.Children.Add(new Label { Text = o.Note!.Trim(), TextColor = Mut, FontSize = 12, LineHeight = 1.3 });
        Grid.SetColumn(textStack, 0);
        inner.Children.Add(textStack);

        if (o.Recommended)
        {
            var pill = new Border
            {
                BackgroundColor = GoodGreen,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 20 },
                Padding = new Thickness(8, 3),
                VerticalOptions = LayoutOptions.Start,
                Content = new Label { Text = "REC", TextColor = Color.FromArgb("#06210F"), FontSize = 10, FontAttributes = FontAttributes.Bold },
            };
            Grid.SetColumn(pill, 1);
            inner.Children.Add(pill);
        }

        var card = new Border
        {
            BackgroundColor = o.Recommended ? Color.FromArgb("#15291C") : Color.FromArgb("#1A2236"),
            Stroke = new SolidColorBrush(o.Recommended ? GoodGreen : CardLine),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 11 },
            Padding = new Thickness(12, 11),
            Content = inner,
        };

        // single: a tap answers (append Enter when the answer is a typed reply). multiple: a
        // tap toggles the choice (no Enter); the Submit button completes the answer. keys:
        // the option's Send already carries its own key sequence, so never append Enter.
        var appendEnter = !multiple && string.Equals(n.AnswerVia, "reply", StringComparison.OrdinalIgnoreCase);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await SendBriefAnswerAsync(o.Send, appendEnter, o.Key);
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private Border AllClearCard(string text)
    {
        var v = new VerticalStackLayout { Spacing = 5 };
        v.Children.Add(HeadLabel("ALL CLEAR", GoodGreen));
        v.Children.Add(new Label { Text = text.Trim(), TextColor = Ink, FontSize = 15, LineHeight = 1.4 });
        return Card(Color.FromArgb("#0E1B14"), GoodGreen, v);
    }

    private Border MissionCompleteCard(TurnBriefSuggestedAction action)
    {
        var v = new VerticalStackLayout { Spacing = 9 };
        v.Children.Add(HeadLabel("MISSION COMPLETE?", GoodGreen));
        var reason = string.IsNullOrWhiteSpace(action.Reason)
            ? "Wingman thinks the goal is delivered." : action.Reason.Trim();
        v.Children.Add(new Label { Text = reason, TextColor = Ink, FontSize = 14, LineHeight = 1.4 });
        var close = new Button
        {
            Text = "Close this session", BackgroundColor = GoodGreen, TextColor = Color.FromArgb("#06210F"),
            FontAttributes = FontAttributes.Bold, HeightRequest = 48, CornerRadius = 11,
        };
        close.Clicked += async (_, _) => await CloseSessionFromBriefAsync();
        v.Children.Add(close);
        return Card(Color.FromArgb("#0E1B14"), GoodGreen, v);
    }

    private Border VoteStrip()
    {
        var g = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) },
        };
        var prompt = new Label { Text = "Was this read useful?", TextColor = Mut, FontSize = 12, VerticalOptions = LayoutOptions.Center };
        var up = VoteButton("Useful", "up");
        var down = VoteButton("Wrong", "down");
        Grid.SetColumn(prompt, 0);
        Grid.SetColumn(up, 1);
        Grid.SetColumn(down, 2);
        g.Children.Add(prompt);
        g.Children.Add(up);
        g.Children.Add(down);
        return Card(CardBg, CardLine, g);
    }

    private Button VoteButton(string text, string vote)
    {
        var btn = new Button
        {
            Text = text, BackgroundColor = Color.FromArgb("#1A2236"), TextColor = Mut, FontSize = 12,
            HeightRequest = 34, Padding = new Thickness(12, 0), CornerRadius = 8,
        };
        btn.Clicked += async (_, _) => await SendVoteAsync(btn, vote);
        return btn;
    }

    // ----- brief actions ---------------------------------------------------
    // All optimistic: the tap shows its effect immediately, then the next poll reconciles
    // the real state from the Gateway (per the project's optimistic-UI rule).

    private async Task SendBriefAnswerAsync(string send, bool appendEnter, string label)
    {
        if (_selected is null) return;
        var session = _selected;
        var gate = OfflineGuard.Check(DeviceOnline, "answer the wingman");
        if (!gate.Allowed) { WingmanStatusLabel.Text = gate.Message; return; }
        try
        {
            WingmanStatusLabel.Text = $"Sent: {label}";
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            await client.SendKeysAsync(session.TailnetEndpoint, session.SessionId, send, appendEnter);
            // The next brief lands at the turn's end; nudge a refresh shortly after so the
            // change shows up without waiting for the 5s poll tick.
            _ = RefreshSoonAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Send error", ex.Message, "OK");
        }
    }

    private async Task RefreshSoonAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        await RefreshWingmanBriefAsync();
    }

    private async Task SendVoteAsync(Button btn, string vote)
    {
        if (_selected is null || _lastBrief is null) return;
        var session = _selected;
        var brief = _lastBrief;
        var gate = OfflineGuard.Check(DeviceOnline, "send feedback");
        if (!gate.Allowed) { WingmanStatusLabel.Text = gate.Message; return; }
        try
        {
            // Optimistic mark on the tapped button.
            btn.BackgroundColor = string.Equals(vote, "up", StringComparison.Ordinal) ? GoodGreen : BadRed;
            btn.TextColor = Colors.White;
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            await gateway.SendBriefFeedbackAsync(session.SessionId, brief.TurnNumber, vote);
            WingmanStatusLabel.Text = "Thanks - feedback recorded.";
        }
        catch (Exception ex)
        {
            btn.BackgroundColor = Color.FromArgb("#1A2236");
            btn.TextColor = Mut;
            await DisplayAlert("Feedback error", ex.Message, "OK");
        }
    }

    private async Task CloseSessionFromBriefAsync()
    {
        if (_selected is null) return;
        var session = _selected;
        var ok = await DisplayAlert("Close session?",
            $"Close \"{session.DisplayName}\"? This shuts the session down.", "Close", "Cancel");
        if (!ok) return;
        var gate = OfflineGuard.Check(DeviceOnline, "close the session");
        if (!gate.Allowed) { WingmanStatusLabel.Text = gate.Message; return; }
        try
        {
            WingmanStatusLabel.Text = "Closing session...";
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            await gateway.CloseSessionAsync(session.SessionId);
            GoBackToRoster();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Close error", ex.Message, "OK");
        }
    }

    private async void OnWingmanSpeakClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;

        var status = await Permissions.RequestAsync<Permissions.Microphone>();
        if (status != PermissionStatus.Granted)
        {
            await DisplayAlert("Microphone needed",
                "CC Director Client needs microphone access to dictate.", "OK");
            return;
        }

        SpeakDictationResult dictation;
        try
        {
            dictation = await SpeakIntoTextboxDialog.PromptAsync(Navigation, _recorder);
        }
        catch (Exception ex)
        {
            WingmanStatusLabel.Text = "";
            await DisplayAlert("Dictation error", ex.Message, "OK");
            return;
        }

        if (dictation.Action == SpeakAction.Cancel || dictation.Audio is null)
        {
            WingmanStatusLabel.Text = "";
            return;
        }

        var gate = OfflineGuard.Check(DeviceOnline, "transcribe your dictation");
        if (!gate.Allowed) { WingmanStatusLabel.Text = gate.Message; return; }

        try
        {
            WingmanStatusLabel.Text = "Transcribing...";
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            var t = await client.TranscribeUtteranceAsync(
                _selected.TailnetEndpoint, _selected.SessionId,
                dictation.Audio.Bytes, dictation.Audio.Mime);

            var existing = WingmanInput.Text ?? "";
            WingmanInput.Text = string.IsNullOrWhiteSpace(existing)
                ? t.Text : (existing.TrimEnd() + " " + t.Text);
            WingmanStatusLabel.Text = "";

            if (dictation.Action == SpeakAction.Send)
                OnWingmanSendClicked(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            WingmanStatusLabel.Text = "";
            await DisplayAlert("Dictation error", ex.Message, "OK");
        }
    }

    private async void OnWingmanSendClicked(object? sender, EventArgs e)
    {
        if (_selected is null || _wingmanBusy) return;
        var text = (WingmanInput.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var gate = OfflineGuard.Check(DeviceOnline, "send to the agent");
        if (!gate.Allowed) { WingmanStatusLabel.Text = gate.Message; return; }
        var session = _selected;
        try
        {
            _wingmanBusy = true;
            WingmanSendButton.IsEnabled = false;
            WingmanStatusLabel.Text = "Sending to agent...";
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            var result = await client.SendChatAsync(session.TailnetEndpoint, session.SessionId, text);

            while (result.ShouldKeepPolling)
            {
                WingmanStatusLabel.Text = "Agent working...";
                await Task.Delay(TimeSpan.FromSeconds(3));
                result = await client.PollChatAsync(session.TailnetEndpoint, session.SessionId, wantProgress: false);
            }

            WingmanInput.Text = "";
            WingmanStatusLabel.Text = string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase)
                ? "" : $"Turn ended: {result.Status}";
            await RefreshWingmanBriefAsync();
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

    private void LoadTerminalWebView()
    {
        if (_selected is null) return;
        TerminalStatusLabel.Text = "Live terminal (read-only)";
        TerminalWebView.Source = new HtmlWebViewSource
        {
            Html = RawTerminalPage.BuildHtml(_selected.TailnetEndpoint, _selected.SessionId),
        };
    }

    private void UnloadTerminalWebView()
    {
        TerminalWebView.Source = new HtmlWebViewSource
        {
            Html = "<html><body style=\"margin:0;background:#06090F\"></body></html>",
        };
    }

    // Show/hide the keys panel (input + Send + Enter/Esc/Stop + arrows). Hidden by
    // default so the terminal fills the screen; the button label tracks the next action.
    private void OnTerminalKeysToggleClicked(object? sender, EventArgs e)
    {
        TerminalControls.IsVisible = !TerminalControls.IsVisible;
        TerminalKeysToggle.Text = TerminalControls.IsVisible ? "Hide keys" : "Keys";
    }

    private async void OnTerminalSendClicked(object? sender, EventArgs e)
    {
        var text = (TerminalInput.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        TerminalInput.Text = "";
        await SendTerminalKeysAsync(text, appendEnter: true);
    }

    // Dictate into the terminal input (same dialog as the Wingman tab's Speak), then either
    // park the transcript in the box or send it as a typed line. Mirrors OnWingmanSpeakClicked
    // but targets TerminalInput and the terminal's send path.
    private async void OnTerminalSpeakClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;

        var status = await Permissions.RequestAsync<Permissions.Microphone>();
        if (status != PermissionStatus.Granted)
        {
            await DisplayAlert("Microphone needed",
                "CC Director Client needs microphone access to dictate.", "OK");
            return;
        }

        SpeakDictationResult dictation;
        try
        {
            dictation = await SpeakIntoTextboxDialog.PromptAsync(Navigation, _recorder);
        }
        catch (Exception ex)
        {
            TerminalStatusLabel.Text = "";
            await DisplayAlert("Dictation error", ex.Message, "OK");
            return;
        }

        if (dictation.Action == SpeakAction.Cancel || dictation.Audio is null)
        {
            TerminalStatusLabel.Text = "";
            return;
        }

        var gate = OfflineGuard.Check(DeviceOnline, "transcribe your dictation");
        if (!gate.Allowed) { TerminalStatusLabel.Text = gate.Message; return; }

        try
        {
            TerminalStatusLabel.Text = "Transcribing...";
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            var t = await client.TranscribeUtteranceAsync(
                _selected.TailnetEndpoint, _selected.SessionId,
                dictation.Audio.Bytes, dictation.Audio.Mime);

            var existing = TerminalInput.Text ?? "";
            TerminalInput.Text = string.IsNullOrWhiteSpace(existing)
                ? t.Text : (existing.TrimEnd() + " " + t.Text);
            TerminalStatusLabel.Text = "";

            if (dictation.Action == SpeakAction.Send)
                OnTerminalSendClicked(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            TerminalStatusLabel.Text = "";
            await DisplayAlert("Dictation error", ex.Message, "OK");
        }
    }

    private async void OnTerminalEnterClicked(object? sender, EventArgs e)
        => await SendTerminalKeysAsync("\r", appendEnter: false);

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
        var gate = OfflineGuard.Check(DeviceOnline, "send Esc to the terminal");
        if (!gate.Allowed) { TerminalStatusLabel.Text = gate.Message; return; }
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
        var gate = OfflineGuard.Check(DeviceOnline, "send Stop to the terminal");
        if (!gate.Allowed) { TerminalStatusLabel.Text = gate.Message; return; }
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
        var gate = OfflineGuard.Check(DeviceOnline, "send to the terminal");
        if (!gate.Allowed) { TerminalStatusLabel.Text = gate.Message; return; }
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

    // Top-right burger menu: switch between pages.
    private async void OnNavMenuClicked(object? sender, TappedEventArgs e)
    {
        var choice = await DisplayActionSheet("Go to", "Cancel", null, "Sessions", "FIFO", "FIFO Text", "Notes", "Exes", "Dictionary", "Transcripts");
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;
        _tts.Stop();
        if (choice == "Notes")
            await Shell.Current.GoToAsync("//MainPage");
        else if (choice == "Sessions")
            await Shell.Current.GoToAsync("//TalkPage");
        else if (choice == "FIFO")
            await Shell.Current.GoToAsync("//FifoPage");
        else if (choice == "FIFO Text")
            await Shell.Current.GoToAsync("//FifoTextPage");
        else if (choice == "Exes")
            await Shell.Current.GoToAsync("//ExesPage");
        else if (choice == "Dictionary")
            await Shell.Current.GoToAsync("//DictionaryPage");
        else if (choice == "Transcripts")
            await Shell.Current.GoToAsync("//TranscriptsPage");
    }

    private sealed record SessionRow(SessionInfo Session, string Name, string Subtitle, Color Dot);

    // Picker rows for the new-session flow (issue #245).
    private sealed record DirectorRow(DirectorInfo Director, string Name, string Subtitle);
    private sealed record RepoRow(RepoInfo Repo, string Name, string Subtitle);
}
