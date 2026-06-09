using CcDirectorClient.Recording;
using CcDirectorClient.Voice;

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

        // Configure the shared voice control for single-session use. Skip makes no
        // sense without a queue; ExitButton pops back to the roster; the resting status
        // line drops "Skip" so the wording matches the visible buttons. The control
        // tells us what happened via these events:
        //   - AnswerDelivered: stay on this session (no-op for us)
        //   - HoldRequested: the session was just parked; pop back to the roster
        //   - SkipRequested: unreachable here (Skip is hidden) - but wire defensively
        //   - WingmanAnswered: stay on session (no-op)
        //   - ExitRequested: user tapped the back button; pop back to the roster
        Voice.Configure(_recorder, _tts, _foreground, () => TokenEntry.Text ?? "");
        Voice.ShowSkip = false;
        Voice.ExitButtonText = "< Back to sessions";
        Voice.ReadyActionsLine = "Ask Agent, or Hold";
        Voice.AnswerDelivered += OnVoiceAnswerDelivered;
        Voice.HoldRequested += OnVoiceHoldRequested;
        Voice.SkipRequested += OnVoiceHoldRequested;       // defensive
        Voice.WingmanAnswered += OnVoiceWingmanAnswered;
        Voice.ExitRequested += OnVoiceExitRequested;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Waze-style: keep the screen on while the talk screen is in front so the
        // user can glance at state without unlocking, and audio keeps flowing.
        DeviceDisplay.Current.KeepScreenOn = true;
        _backgrounded = false;
        HookAppLifecycle();
        Voice.OnHostAppearing();
        _ = LoadRosterAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        DeviceDisplay.Current.KeepScreenOn = false;

        // Background -> keep voice + foreground service alive so speech continues. Real
        // navigation away -> tear down the voice control, the terminal stream, and the
        // foreground service hold.
        Voice.OnHostDisappearing(_backgrounded);
        if (_backgrounded) return;

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

        // Reset Wingman + Terminal panels for the new session; they load lazily on tab entry.
        TerminalStatusLabel.Text = "";
        WingmanOutputLabel.Text = "Loading clean output...";
        WingmanNoteLabel.Text = "Tap Refresh for a read on this session.";
        WingmanStatusLabel.Text = "";
        if (WingmanInput is not null) WingmanInput.Text = "";
        if (TerminalInput is not null) TerminalInput.Text = "";

        ListPanel.IsVisible = false;
        TalkPanel.IsVisible = true;

        // Wingman-off sessions live as plain terminal cards: hide the Voice + Wingman tab
        // buttons and open straight on the Terminal tab. There is no auto-explain briefing
        // to speak and no voice / wingman content to show.
        VoiceTabButton.IsVisible = session.WingmanEnabled;
        WingmanTabButton.IsVisible = session.WingmanEnabled;

        if (!session.WingmanEnabled)
        {
            ShowTab("terminal");
            return;
        }

        // Default tab is Voice: hand the session to the shared control which immediately
        // fetches and speaks the wingman's briefing. The control's LoadSessionAsync also
        // sets the gateway voice-mode flag so the desktop / web client agree.
        ShowTab("voice");
        _ = Voice.LoadSessionAsync(session);
    }

    // ===== three-tab switcher (Voice / Wingman / Terminal) =================

    private void OnVoiceTabClicked(object? sender, EventArgs e) => ShowTab("voice");
    private void OnWingmanTabClicked(object? sender, EventArgs e) => ShowTab("wingman");
    private void OnTerminalTabClicked(object? sender, EventArgs e) => ShowTab("terminal");

    /// <summary>
    /// Swap which inline section is visible and update the segmented control's highlight.
    /// Starts/stops the Terminal stream on entry/exit; lazily loads the Wingman clean
    /// output the first time the tab is shown for a session. Voice is the default and is
    /// the inline shared VoiceSessionView; switching tabs does NOT tear it down, so the
    /// briefing keeps playing if the user peeks at Wingman or Terminal.
    /// </summary>
    private void ShowTab(string tab)
    {
        _activeTab = tab;

        Voice.IsVisible = tab == "voice";
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

    private void OnBackClicked(object? sender, EventArgs e) => GoBackToRoster();

    private void GoBackToRoster()
    {
        UnloadTerminalWebView();
        if (_recorder.IsRecording)
        {
            try { _ = _recorder.StopAsync(); } catch { /* discarding a half-captured clip on leave */ }
        }
        // The Voice control owns TTS, voice-mode flag, and turn cancellation; ClearSession
        // tears it all down.
        Voice.ClearSession();
        _selected = null;
        _foreground.Stop();
        TalkPanel.IsVisible = false;
        ListPanel.IsVisible = true;
        _ = LoadRosterAsync();
    }

    // ===== shared-voice events =============================================
    // Single-session mode: AnswerDelivered + WingmanAnswered are no-ops (stay on the
    // session, the control already reset itself for the next turn). HoldRequested and
    // ExitRequested pop back to the roster.

    private void OnVoiceAnswerDelivered(object? sender, EventArgs e) { /* stay on session */ }
    private void OnVoiceWingmanAnswered(object? sender, EventArgs e) { /* stay on session */ }
    private void OnVoiceHoldRequested(object? sender, EventArgs e) => GoBackToRoster();
    private void OnVoiceExitRequested(object? sender, EventArgs e) => GoBackToRoster();

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
        await RefreshWingmanNoteAsync();
        await RefreshWingmanOutputAsync();
    }

    private async Task RefreshWingmanNoteAsync()
    {
        if (_selected is null || _wingmanBusy) return;
        var session = _selected;
        var gate = OfflineGuard.Check(DeviceOnline, "read the session");
        if (!gate.Allowed) { WingmanStatusLabel.Text = gate.Message; return; }
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

    private async Task RefreshWingmanOutputAsync()
    {
        if (_selected is null) return;
        var session = _selected;
        var gate = OfflineGuard.Check(DeviceOnline, "load the clean output");
        if (!gate.Allowed) { WingmanOutputLabel.Text = gate.Message; return; }
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
