using CcDirectorClient.Recording;
using CcDirectorClient.Voice;

namespace CcDirectorClient.Controls;

/// <summary>
/// Reusable voice mode UI for one session at a time. Owns every piece of the voice
/// experience: fetching and speaking the wingman's briefing, recording the user's
/// answer (via the <see cref="VoiceDictationDialog"/> popup), running the turn,
/// playing the reply, caching the last spoken clip for Replay, the floating
/// Stop-talking pill, and the action-button state machine.
///
/// Hosts (FifoPage walks a queue; TalkPage's Voice tab pins to one session) drive
/// the control by calling <see cref="LoadSessionAsync"/> and reacting to the
/// outcome events (<see cref="AnswerDelivered"/>, <see cref="HoldRequested"/>,
/// <see cref="SkipRequested"/>, <see cref="WingmanAnswered"/>, <see cref="ExitRequested"/>).
/// The control does NOT navigate, manage a queue, or own a burger menu - those are
/// host concerns. The driving-safety layout (pinned-top buttons, scrolling output)
/// is baked into the XAML root and works identically in any host.
/// </summary>
public partial class VoiceSessionView : ContentView
{
    // Voice status line colors (the big "where we are" label).
    private const string StatusGreen = "#5FD08A";   // ready / done
    private const string StatusYellow = "#E8B339";  // in progress
    private const string StatusRed = "#E5484D";     // recording / error
    private const string StatusBlue = "#2B6CB0";    // speaking / reading

    private static readonly Color AgentGreen = Color.FromArgb("#5FD08A");
    private static readonly Color AgentText = Color.FromArgb("#06210F");
    private static readonly Color WingmanTeal = Color.FromArgb("#0E7C6B");
    private static readonly Color DisabledGrey = Color.FromArgb("#6B7280");

    private IUtteranceRecorder? _recorder;
    private IReplySpeaker? _tts;
    private IVoiceForeground? _foreground;
    private IAudioCue? _audioCue;
    // The gateway token is read fresh from Preferences on every operation that needs
    // it, so the control does not need to be re-configured when the user changes it.
    private Func<string> _tokenProvider = () => "";
    // The Gateway base URL, read fresh the same way. Required by the walkie-talkie
    // agent turn, which submits/polls voice turns on the Gateway (issue #378).
    private Func<string> _gatewayUrlProvider = () => "";

    // The most recent spoken clip for the current session (briefing or wingman answer),
    // kept so Replay can re-play it (issue #148). Cleared whenever the session changes
    // or the agent (re)starts work, so stale audio is never replayed.
    private readonly LastClipCache _clipCache = new();

    // Cancels the in-flight turn / briefing so a host action (load next session, exit,
    // hold, etc.) can abandon it instantly.
    private CancellationTokenSource? _turnCts;

    private SessionInfo? _current;
    private bool _busy;
    private bool _recordingForWingman;
    // True when the host has called LoadSessionAsync and the user is interacting with a
    // session. False before the first load and after ClearSession. Used to gate the
    // Stop-talking pill so a stale playback signal from a previous host does not flash
    // the pill on a fresh control.
    private bool _attached;

    /// <summary>
    /// When true (single-session walkie-talkie host, TalkPage), Ask Agent runs the full
    /// wait-send-follow-summarize-speak loop (<see cref="VoiceConversation.SpeakTurnAsync"/>)
    /// so the agent's reply is read back aloud as plain spoken prose.
    /// When false (FIFO queue host, FifoPage), Ask Agent delivers the answer and moves on
    /// (<see cref="VoiceConversation.DeliverToSessionAsync"/>). Default false so FifoPage
    /// needs no change.
    /// </summary>
    public bool WalkieTalkieMode { get; set; } = false;

    /// <summary>True only when Skip is meaningful (queue host). Single-session hosts set this
    /// to false; Skip is hidden and Hold expands to fill the row.</summary>
    public bool ShowSkip
    {
        get => _showSkip;
        set
        {
            _showSkip = value;
            ApplySkipVisibility();
        }
    }
    private bool _showSkip = true;

    /// <summary>Caption for the back/exit button at the top of the panel. Host-set
    /// (e.g. "&lt; Exit FIFO" or "&lt; Back to sessions").</summary>
    public string ExitButtonText
    {
        get => ExitButton.Text;
        set => ExitButton.Text = value ?? "< Exit";
    }

    /// <summary>The resting "what you can do now" status line. Host-set so the
    /// wording matches its mode (e.g. "Ask Agent, Skip, or Hold" vs "Ask Agent, or Hold").</summary>
    public string ReadyActionsLine { get; set; } = "Ask Agent, Skip, or Hold";

    /// <summary>Fired after the user's answer was delivered to the agent. The queue host
    /// advances; the single-session host stays put and lets the control reset itself.</summary>
    public event EventHandler? AnswerDelivered;

    /// <summary>Fired after a successful Hold. The queue host advances to the next session;
    /// the single-session host pops back to the session list.</summary>
    public event EventHandler? HoldRequested;

    /// <summary>Fired when the user taps Skip. Hidden in single-session mode (ShowSkip=false).</summary>
    public event EventHandler? SkipRequested;

    /// <summary>Fired after the wingman has answered a question; the control stays on the
    /// session in both hosts (Ask Wingman is read-only, never advances).</summary>
    public event EventHandler? WingmanAnswered;

    /// <summary>Fired when the user taps the exit/back button. Hosts navigate accordingly.</summary>
    public event EventHandler? ExitRequested;

    public VoiceSessionView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Hand the control the services it needs (recorder, TTS, voice foreground service,
    /// a way to read the current gateway token, optional audio cues, and a way to read
    /// the Gateway base URL for the walkie-talkie agent turn). The MAUI XAML loader
    /// builds the control with a parameterless constructor, so the host calls this
    /// once after instantiation; everything else flows through it.
    /// </summary>
    public void Configure(IUtteranceRecorder recorder, IReplySpeaker tts,
                          IVoiceForeground foreground, Func<string> tokenProvider,
                          IAudioCue? audioCue = null, Func<string>? gatewayUrlProvider = null)
    {
        _recorder = recorder;
        _tts = tts;
        _foreground = foreground;
        _tokenProvider = tokenProvider ?? (() => "");
        _audioCue = audioCue;
        _gatewayUrlProvider = gatewayUrlProvider ?? (() => "");
    }

    /// <summary>
    /// Host lifecycle hook: call from the host page's OnAppearing. Subscribes the
    /// Stop-talking pill to the speaker's playback state and seeds it if audio is
    /// already playing (e.g. returning from the background mid-read).
    /// </summary>
    public void OnHostAppearing()
    {
        if (_tts is null) return;
        _tts.PlayingChanged += OnPlayingChanged;
        UpdateStopTalkingVisibility(_attached && _tts.IsPlaying);
    }

    /// <summary>
    /// Host lifecycle hook: call from the host page's OnDisappearing with
    /// <paramref name="isBackgrounded"/>=true when the app merely went to the background
    /// (speech keeps playing), false when the user navigated to another page (full
    /// teardown). The host is responsible for tracking which case it is via the MAUI
    /// Window.Deactivated/Activated lifecycle.
    /// </summary>
    public void OnHostDisappearing(bool isBackgrounded)
    {
        if (_tts is not null) _tts.PlayingChanged -= OnPlayingChanged;
        if (isBackgrounded) return;
        // Real navigation away: stop everything voice-related so the in-flight reply
        // cannot arrive late and talk over the next page.
        _turnCts?.Cancel();
        if (_recorder is not null && _recorder.IsRecording)
        {
            try { _ = _recorder.StopAsync(); } catch { /* discarding a half-captured clip on leave */ }
        }
        _tts?.Stop();
        ClearVoiceMode(_current);
        _foreground?.Stop();
        _attached = false;
    }

    /// <summary>
    /// Switch the control to the given session: fetch its wingman briefing, mark the
    /// session as in voice mode (so other clients agree), display the briefing as text,
    /// then speak it. The action buttons are released the moment the briefing is on
    /// screen so the user can interrupt with Ask Agent / Skip / Hold without waiting
    /// for playback. If the briefing fetch fails the session is still shown so the user
    /// can still act on it.
    ///
    /// Safe to call repeatedly: each call cancels any in-flight turn / playback and
    /// starts a fresh briefing for the new session.
    /// </summary>
    public async Task LoadSessionAsync(SessionInfo session)
    {
        if (_recorder is null || _tts is null || _foreground is null)
            throw new InvalidOperationException("Configure must be called before LoadSessionAsync");

        _attached = true;
        ClearCachedClip();
        _current = session;
        SeedPanelForSession(session);
        SetBusy(true);

        if (!EnsureOnline("read this session")) return;

        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        var token = _turnCts.Token;
        SetStatus("Reading what's happening...", StatusBlue);

        VoiceConversation.PreparedBriefing? prepared = null;
        try
        {
            var convo = new VoiceConversation(new DirectorVoiceClient(_tokenProvider()), _tts);
            prepared = await convo.PrepareExplainAsync(session, token);
        }
        catch (OperationCanceledException)
        {
            return; // host cancelled or moved on
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[VoiceSessionView] PrepareExplain failed: {ex.Message}");
        }

        if (token.IsCancellationRequested) return;

        if (prepared is not null) BriefingLabel.Text = prepared.DisplayText;

        // Flag voice mode so the desktop tile / web view / roster show that this session
        // is being talked to. Best-effort, fire-and-forget.
        var voiceClient = new DirectorVoiceClient(_tokenProvider());
        _ = voiceClient.SetVoiceModeAsync(session.TailnetEndpoint, session.SessionId, true);


        SetBusy(false);

        if (prepared is not null)
        {
            CacheClip(prepared.Audio);
            _ = PlayClipAsync(prepared.Audio, token);
        }
        else
        {
            SetStatus(ReadyActionsLine, StatusGreen);
        }
    }

    /// <summary>
    /// Drop the current session: cancel the turn, stop playback, clear the voice-mode
    /// flag on the gateway, and reset the panel. Hosts call this before navigating away
    /// or when moving past the last queue entry, so the control is in a clean state.
    /// </summary>
    public void ClearSession()
    {
        _turnCts?.Cancel();
        _tts?.Stop();
        ClearCachedClip();
        ClearVoiceMode(_current);
        _current = null;
        BriefingLabel.Text = "-";
        TranscriptLabel.Text = "-";
        ReplyLabel.Text = "-";
        TurnStatusLabel.Text = "";
        SessionNameLabel.Text = "";
        SessionStateLabel.Text = "";
        SetStatus("Ready", StatusGreen);
        SetIdleButtons();
        _attached = false;
    }

    /// <summary>
    /// Fill the header / body labels with what we know about the session BEFORE any
    /// async fetch completes, so the first frame the user sees already reads as their
    /// session and not an empty / last-session shell.
    /// </summary>
    private void SeedPanelForSession(SessionInfo session)
    {
        SessionNameLabel.Text = session.DisplayName;
        SessionStateLabel.Text = string.IsNullOrWhiteSpace(session.LastStatusReason)
            ? session.ActivityState
            : session.LastStatusReason;
        BriefingLabel.Text = "-";
        TranscriptLabel.Text = "-";
        ReplyLabel.Text = "-";
        TurnStatusLabel.Text = "";
    }

    // ===== answer / ask wingman ============================================

    // async void event handlers are special: an exception they throw goes straight to
    // the platform's unhandled-exception path, which on Android means the app process
    // is terminated. Every handler here is wrapped so the worst case is a status-line
    // error message + a logged exception, never a crash.
    private async void OnAnswerClicked(object? sender, EventArgs e)
    {
        try { await HandleRecordAsync(wingman: false); }
        catch (Exception ex) { ReportHandlerCrash("OnAnswerClicked", ex); }
    }

    private async void OnAskWingmanClicked(object? sender, EventArgs e)
    {
        try { await HandleRecordAsync(wingman: true); }
        catch (Exception ex) { ReportHandlerCrash("OnAskWingmanClicked", ex); }
    }

    private async Task HandleRecordAsync(bool wingman)
    {
        if (_current is null || _busy || _recorder is null || _foreground is null) return;

        // One try around the whole flow: previously the mic-permission request, the
        // foreground service start, and the resolution of Navigation for the modal
        // dialog all sat OUTSIDE the try block, so any of them throwing produced an
        // Android-level crash dialog. With the whole flow guarded, every failure
        // surfaces as a status-line message + a logged exception.
        try
        {
            // Cut the briefing (or last reply) immediately so it does not get captured
            // by the mic or talk over the user as they start answering.
            CancelSpeechAndTurn();

            var status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                await ShowAlert("Microphone needed",
                    "CC Director Client needs microphone access to talk.");
                return;
            }

            _recordingForWingman = wingman;
            _foreground.Start();
            // Cue: mic is now open - fire before prompting so the user hears it the moment
            // the dialog appears and the foreground service is live.
            _audioCue?.PlayStart();
            SetStatus(wingman ? "Recording for wingman" : "Recording your answer", StatusRed);

            // Navigation on a ContentView only resolves once the control is attached
            // to a Page in the visual tree; fall back to the hosting page directly so
            // a stale / unset proxy can never throw at PushModalAsync.
            var nav = Navigation ?? FindAncestorPage()?.Navigation;
            if (nav is null)
            {
                SetStatus("Cannot open dictation dialog (no navigation)", StatusRed);
                SetIdleButtons();
                ClientLog.Write("[VoiceSessionView] HandleRecord: Navigation is null");
                return;
            }

            UtteranceAudio? audio = await VoiceDictationDialog.PromptAsync(nav, _recorder);

            if (audio is null)
            {
                // User hit Cancel (or the mic could not start). Return to rest.
                SetIdleButtons();
                SetStatus(ReadyActionsLine, StatusGreen);
                return;
            }

            // Cue: user hit SUBMIT and the clip was captured successfully.
            _audioCue?.PlayStop();
            SetStatus(_recordingForWingman ? "Asking wingman" : "Sending", StatusYellow);
            await RunTurnAsync(_current, audio);
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[VoiceSessionView] HandleRecord({(wingman ? "wingman" : "agent")}) FAILED: {ex.GetType().FullName}: {ex.Message}");
            ClientLog.Write($"[VoiceSessionView] stack: {ex.StackTrace}");
            SetStatus("Something went wrong", StatusRed);
            SetIdleButtons();
            await SafeShowAlert("Voice error", ex.Message);
        }
    }

    private async Task RunTurnAsync(SessionInfo session, UtteranceAudio audio)
    {
        if (_tts is null) return;
        // The upload/transcribe is the network step: gate it so an offline send surfaces
        // at once and the buttons stay live, instead of dimming for the HTTP timeout.
        if (!EnsureOnline(_recordingForWingman ? "ask the wingman" : "send your answer"))
            return;
        SetBusy(true);
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        var convo = new VoiceConversation(
            new DirectorVoiceClient(_tokenProvider()), _tts, _gatewayUrlProvider());
        try
        {
            // Walkie-talkie mode (single-session host): submit the turn to the Gateway's
            // async voice-turn pipeline and poll until the reply audio is back (issue
            // #378), so the agent's reply is read back aloud as plain prose.
            // FIFO mode (queue host): deliver and move on without waiting for the reply.
            if (WalkieTalkieMode && !_recordingForWingman)
            {
                var rawReply = await convo.SpeakTurnAsync(
                    session, audio, OnTurnUpdate, _turnCts.Token);
                SetStatus(ReadyActionsLine, StatusGreen);
                SetBusy(false);
                // Reply was spoken; AnswerDelivered tells the single-session host to reset
                // rather than advance (TalkPage reacts by staying on the session, ready for
                // another question).
                AnswerDelivered?.Invoke(this, EventArgs.Empty);
                return;
            }

            var outcome = await convo.DeliverToSessionAsync(
                session, audio, OnTurnUpdate, _turnCts.Token, forceWingman: _recordingForWingman,
                onClip: CacheClip);

            switch (outcome.Kind)
            {
                case VoiceConversation.FifoOutcomeKind.Delivered:
                    // The answer went to the agent: it is (re)starting work, so its briefing
                    // is now stale. Drop it before the host advances/resets.
                    ClearCachedClip();
                    SetBusy(false);
                    AnswerDelivered?.Invoke(this, EventArgs.Empty);
                    break;
                case VoiceConversation.FifoOutcomeKind.WingmanAnswered:
                    SetStatus(ReadyActionsLine, StatusGreen);
                    SetBusy(false);
                    WingmanAnswered?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
        }
        catch (Exception ex)
        {
            // Cue: the turn failed (transcription, network, or send error).
            _audioCue?.PlayError();
            SetStatus("Something went wrong", StatusRed);
            SetBusy(false);
            await ShowAlert("Voice error", ex.Message);
        }
    }

    private void OnTurnUpdate(VoiceConversation.TurnUpdate u) => MainThread.BeginInvokeOnMainThread(() =>
    {
        switch (u.Stage)
        {
            case "explaining": SetStatus("Reading what's happening...", StatusBlue); break;
            case "briefing": BriefingLabel.Text = u.Text; SetStatus("Speaking...", StatusBlue); break;
            case "transcribing": SetStatus("Transcribing...", StatusYellow); break;
            case "transcript": TranscriptLabel.Text = u.Text; TurnStatusLabel.Text = ""; break;
            case "delivering": SetStatus("Sending your answer...", StatusYellow); break;
            case "delivered": ReplyLabel.Text = u.Text; SetStatus("Sent - next session", StatusGreen); break;
            case "wingman": SetStatus("Asking the wingman...", StatusBlue); break;
            case "answer": ReplyLabel.Text = u.Text; SetStatus("Speaking...", StatusBlue); break;
            // Walkie-talkie mode stages (issue #348):
            case "thinking": SetStatus("Thinking...", StatusYellow); break;
            case "summarizing": SetStatus("Summarizing...", StatusBlue); break;
            case "reply": ReplyLabel.Text = u.Text; SetStatus("Speaking...", StatusBlue); break;
            default: TurnStatusLabel.Text = u.Text; break;
        }
    });

    // ===== skip / hold / exit (host-routed) ================================

    private void OnSkipClicked(object? sender, EventArgs e)
    {
        if (_current is null || _busy) return;
        CancelSpeechAndTurn();
        SkipRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnHoldClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_current is null || _busy) return;
            CancelSpeechAndTurn();
            if (!EnsureOnline("hold this session")) return;
            SetBusy(true);
            SetStatus("Holding this session...", StatusYellow);
            try
            {
                await new DirectorVoiceClient(_tokenProvider())
                    .SetHoldAsync(_current.TailnetEndpoint, _current.SessionId, true);
            }
            catch (Exception ex)
            {
                SetStatus("Could not hold this session", StatusRed);
                SetBusy(false);
                await SafeShowAlert("Hold error", ex.Message);
                return;
            }
            ClearCachedClip();
            SetBusy(false);
            HoldRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { ReportHandlerCrash("OnHoldClicked", ex); }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        CancelSpeechAndTurn();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    // ===== last-clip cache + Replay (issue #148) ===========================

    private void CacheClip(byte[] audio)
    {
        _clipCache.Set(audio);
        MainThread.BeginInvokeOnMainThread(() => ReplayButton.IsVisible = _clipCache.HasClip);
    }

    private void ClearCachedClip()
    {
        _clipCache.Clear();
        MainThread.BeginInvokeOnMainThread(() => ReplayButton.IsVisible = false);
    }

    private void OnReplayClicked(object? sender, EventArgs e)
    {
        var clip = _clipCache.Clip;
        if (clip is null || _busy) return;
        ClientLog.Write("[VoiceSessionView] Replay tapped");
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        _ = PlayClipAsync(clip, _turnCts.Token);
    }

    /// <summary>
    /// Play an already-synthesized clip (the just-prepared briefing, or a cached Replay)
    /// off the UI path so the user can interrupt it. A cancelled token (skip / hold /
    /// answer / Stop) ends quietly and must not stomp the status set by whatever they
    /// did next.
    /// </summary>
    private async Task PlayClipAsync(byte[] audio, CancellationToken ct)
    {
        if (_tts is null) return;
        try
        {
            SetStatus("Speaking...", StatusBlue);
            await _tts.PlayAsync(audio, ct);
            if (!ct.IsCancellationRequested)
                SetStatus(ReadyActionsLine, StatusGreen);
        }
        catch (OperationCanceledException)
        {
            // expected: user skipped / held / answered / stopped
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                SetStatus("Could not play this briefing", StatusRed);
            ClientLog.Write($"[VoiceSessionView] PlayClip failed: {ex.Message}");
        }
    }

    // ===== stop-talking pill (issue #146) ==================================

    private void OnPlayingChanged(bool playing)
        => MainThread.BeginInvokeOnMainThread(() => UpdateStopTalkingVisibility(playing));

    private void UpdateStopTalkingVisibility(bool playing)
        => StopTalkingButton.IsVisible = _attached && playing;

    private void OnStopTalkingClicked(object? sender, EventArgs e)
    {
        ClientLog.Write("[VoiceSessionView] Stop talking tapped");
        CancelSpeechAndTurn();
        UpdateStopTalkingVisibility(false);
        SetStatus("Stopped. " + ReadyActionsLine, StatusGreen);
    }

    // ===== offline gate (issue #147) =======================================

    private static bool DeviceOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    private bool EnsureOnline(string action)
    {
        var verdict = OfflineGuard.Check(DeviceOnline, action);
        if (verdict.Allowed) return true;
        ClientLog.Write($"[VoiceSessionView] offline gate blocked action='{action}'");
        SetStatus(verdict.Message, StatusRed);
        SetBusy(false);
        return false;
    }

    // ===== helpers =========================================================

    private void CancelSpeechAndTurn()
    {
        _turnCts?.Cancel();
        _tts?.Stop();
    }

    private void ClearVoiceMode(SessionInfo? session)
    {
        if (session is null) return;
        _ = new DirectorVoiceClient(_tokenProvider())
            .SetVoiceModeAsync(session.TailnetEndpoint, session.SessionId, false);
    }

    private void SetStatus(string text, string colorHex)
    {
        StatusLabel.Text = text;
        StatusLabel.TextColor = Color.FromArgb(colorHex);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy) SetBusyButtons();
        else SetIdleButtons();
    }

    private void ApplySkipVisibility()
    {
        SkipButton.IsVisible = _showSkip;
        if (_showSkip)
        {
            Grid.SetColumn(HoldButton, 1);
            Grid.SetColumnSpan(HoldButton, 1);
        }
        else
        {
            Grid.SetColumn(HoldButton, 0);
            Grid.SetColumnSpan(HoldButton, 2);
        }
    }

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

    // Surface alerts on the nearest hosting Page. Used sparingly - most errors land on
    // the status label - but kept for permission denials and unexpected failures.
    private Task ShowAlert(string title, string message) => SafeShowAlert(title, message);

    // Alert that swallows its own failures. Used from catch blocks so a failure to
    // SHOW an error never compounds the original error into a crash.
    private async Task SafeShowAlert(string title, string message)
    {
        try
        {
            var page = FindAncestorPage();
            if (page is null) return;
            await page.DisplayAlert(title, message, "OK");
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[VoiceSessionView] SafeShowAlert FAILED: {ex.Message}");
        }
    }

    // Last-resort handler for async void event handlers. Logs the full exception and
    // tries to keep the UI in a sane state, so a thrown exception never becomes an
    // Android "this app has a bug" dialog.
    private void ReportHandlerCrash(string handler, Exception ex)
    {
        ClientLog.Write($"[VoiceSessionView] {handler} CRASH: {ex.GetType().FullName}: {ex.Message}");
        ClientLog.Write($"[VoiceSessionView] stack: {ex.StackTrace}");
        try
        {
            SetStatus("Something went wrong", StatusRed);
            SetIdleButtons();
        }
        catch { /* never let the recovery itself crash */ }
        _ = SafeShowAlert("Voice error", ex.Message);
    }

    private Page? FindAncestorPage()
    {
        Element? e = this;
        while (e is not null)
        {
            if (e is Page p) return p;
            e = e.Parent;
        }
        return null;
    }
}
