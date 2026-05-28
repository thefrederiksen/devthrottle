using CcDirectorClient.Recording;
using CcDirectorClient.Voice;

namespace CcDirectorClient;

/// <summary>
/// FIFO text mode: the typed/read twin of <see cref="FifoPage"/>. Same conveyor belt
/// through every session that needs the user, same queue/skip/hold semantics - but the
/// briefing is READ on screen instead of spoken, and the answer is TYPED instead of
/// dictated. No microphone, no TTS, no foreground service: it is pure HTTP + text.
///
/// For each session in turn the page auto-switches to it, fetches the wingman's
/// "what's happening" briefing and shows it. The user types an answer and taps Send;
/// the moment it is delivered to the session the page advances to the next one - it never
/// waits for the agent to respond. The user can also Skip (move on this pass), Hold (park
/// it out of the rotation), or Ask the wingman a typed question (answered as text). A
/// "pass" tracks sessions already handled so the belt moves forward; when the pass is
/// empty the user is caught up.
/// </summary>
public partial class FifoTextPage : ContentPage
{
    private const string PrefServer = "gateway_url";
    private const string PrefToken = "gateway_token";

    // Status line colors, matching the other voice screens.
    private const string StatusGreen = "#5FD08A";
    private const string StatusYellow = "#E8B339";
    private const string StatusRed = "#E5484D";
    private const string StatusBlue = "#2B6CB0";

    // We deliberately do NOT wait for the agent's turn - FIFO deposits the answer and moves
    // on. /chat sends the text before it begins polling, so a short timeout returns right
    // after delivery, confirming the send landed without blocking on the whole turn.
    private const int DeliverTimeoutMs = 2_500;

    // FIFO queue: red sessions that are NOT on hold, in stable order.
    private readonly ConductorState _conductor = new(excludeHeld: true);

    // Sessions already handled this pass, so the belt moves forward instead of
    // re-presenting a session we just dealt with.
    private readonly HashSet<string> _pass = new(StringComparer.OrdinalIgnoreCase);

    private SessionInfo? _current;
    private bool _busy;
    private CancellationTokenSource? _turnCts;

    public FifoTextPage()
    {
        InitializeComponent();

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
        // Keep the screen on while reading/typing so a long briefing is not interrupted.
        DeviceDisplay.Current.KeepScreenOn = true;
        _ = LoadQueueCountAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        DeviceDisplay.Current.KeepScreenOn = false;
        _turnCts?.Cancel();
    }

    // ===== start panel =====================================================

    private async void OnRescanClicked(object? sender, EventArgs e) => await LoadQueueCountAsync();

    private async Task LoadQueueCountAsync()
    {
        SaveCreds();
        // Don't spin "Loading sessions..." behind a doomed fetch when there is no signal.
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

    // ===== offline gate (issue #147) =======================================
    // No button may sink into the disabled "Working..." state waiting on a network call that
    // cannot succeed (the HTTP timeout is what makes them "appear dead" with no signal). Any
    // handler about to do network work checks connectivity FIRST: offline it shows an instant
    // message, frees the buttons, and bails before awaiting anything. Mirrors FifoPage.

    private static bool DeviceOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    private bool EnsureOnline(string action)
    {
        var verdict = OfflineGuard.Check(DeviceOnline, action);
        if (verdict.Allowed) return true;
        SetFifoStatus(verdict.Message, StatusRed);
        SetBusy(false);   // never leave the buttons stuck disabled
        return false;
    }

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        try
        {
            _pass.Clear();
            StartPanel.IsVisible = false;
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
    /// this pass by reading its briefing on screen. When none remain, the user is caught up.
    /// </summary>
    private async Task PresentNextAsync()
    {
        // The advance needs the roster: gate it so an offline tap fails instantly instead of
        // disabling every button behind a doomed fetch.
        if (!EnsureOnline("load your sessions")) return;
        SetBusy(true);
        try
        {
            var gateway = new GatewayClient(ServerEntry.Text ?? "", TokenEntry.Text ?? "");
            var roster = await gateway.GetRosterAsync();
            _conductor.Update(roster);

            var next = _conductor.Queue.FirstOrDefault(s => !_pass.Contains(s.SessionId));
            if (next is null)
            {
                CaughtUp();
                return;
            }

            _current = next;
            var remaining = _conductor.Queue.Count(s => !_pass.Contains(s.SessionId));
            ShowSessionPanel(next, remaining);

            _turnCts?.Cancel();
            _turnCts = new CancellationTokenSource();
            SetFifoStatus("Reading what's happening...", StatusBlue);
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            var briefing = await client.ExplainAsync(next.TailnetEndpoint, next.SessionId, _turnCts.Token);
            BriefingLabel.Text = string.IsNullOrWhiteSpace(briefing) ? "Nothing to report on this one yet." : briefing;
            SetFifoStatus("Type an answer, or Skip / Hold", StatusGreen);
        }
        catch (OperationCanceledException)
        {
            // User exited; nothing to report.
        }
        catch (Exception ex)
        {
            SetFifoStatus("Could not read this session", StatusRed);
            await DisplayAlert("FIFO error", ex.Message, "OK");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowSessionPanel(SessionInfo session, int remaining)
    {
        FifoSessionName.Text = session.DisplayName;
        FifoSessionState.Text = remaining == 1 ? "Needs you - last one" : $"Needs you - {remaining} left";
        BriefingLabel.Text = "-";
        ReplyLabel.Text = "-";
        TurnStatusLabel.Text = "";
        InputEditor.Text = "";
    }

    private void CaughtUp()
    {
        _current = null;
        _pass.Clear();
        FifoPanel.IsVisible = false;
        StartPanel.IsVisible = true;
        StartStatusLabel.Text = "All caught up. No sessions need you right now.";
    }

    // ===== send to session / ask wingman ===================================

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        if (_current is null || _busy) return;
        var text = (InputEditor.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            SetFifoStatus("Type your answer first", StatusYellow);
            return;
        }

        var session = _current;
        if (!EnsureOnline("send your answer")) return;
        SetBusy(true);
        SetFifoStatus("Sending...", StatusYellow);
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        try
        {
            // Deliver and move on - do NOT follow the turn (FIFO is fire-and-forget).
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            var result = await client.SendChatAsync(
                session.TailnetEndpoint, session.SessionId, text, DeliverTimeoutMs, _turnCts.Token);
            if (result.IsGone)
                throw new InvalidOperationException("that session has exited");
            if (string.Equals(result.Status, "send_failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result.Status, "no_session_configured", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"could not deliver your answer (status '{result.Status}'): {result.Error}");

            InputEditor.Text = "";
            ReplyLabel.Text = $"Sent to {session.DisplayName}.";
            await MarkHandledAndAdvanceAsync(session, wasHold: false);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
        }
        catch (Exception ex)
        {
            SetFifoStatus("Something went wrong", StatusRed);
            SetBusy(false);
            await DisplayAlert("Send error", ex.Message, "OK");
        }
    }

    private async void OnAskWingmanClicked(object? sender, EventArgs e)
    {
        if (_current is null || _busy) return;
        var text = (InputEditor.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            SetFifoStatus("Type a question or command for the wingman first", StatusYellow);
            return;
        }

        var session = _current;
        if (!EnsureOnline("ask the wingman")) return;
        SetBusy(true);
        SetFifoStatus("Asking the wingman...", StatusBlue);
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        try
        {
            var client = new DirectorVoiceClient(TokenEntry.Text ?? "");
            // Same classify-first path as the spoken FIFO mode: "skip"/"hold" steer the
            // queue; anything else is a question, answered as text below.
            var cmd = await client.InterpretCommandAsync(session.TailnetEndpoint, session.SessionId, text, _turnCts.Token);
            if (string.Equals(cmd.Action, "skip", StringComparison.OrdinalIgnoreCase))
            {
                InputEditor.Text = "";
                await MarkHandledAndAdvanceAsync(session, wasHold: false);
                return;
            }
            if (string.Equals(cmd.Action, "hold", StringComparison.OrdinalIgnoreCase))
            {
                InputEditor.Text = "";
                await MarkHandledAndAdvanceAsync(session, wasHold: true);
                return;
            }

            var answer = await client.AskWingmanAsync(session.TailnetEndpoint, session.SessionId, text, _turnCts.Token);
            ReplyLabel.Text = string.IsNullOrWhiteSpace(answer) ? "The wingman had nothing to report." : answer;
            InputEditor.Text = "";
            SetFifoStatus("Type an answer, or Skip / Hold", StatusGreen);
            SetBusy(false);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
        }
        catch (Exception ex)
        {
            SetFifoStatus("Something went wrong", StatusRed);
            SetBusy(false);
            await DisplayAlert("Wingman error", ex.Message, "OK");
        }
    }

    // ===== skip / hold buttons =============================================

    private async void OnSkipClicked(object? sender, EventArgs e)
    {
        if (_current is null || _busy) return;
        await MarkHandledAndAdvanceAsync(_current, wasHold: false);
    }

    private async void OnHoldClicked(object? sender, EventArgs e)
    {
        if (_current is null || _busy) return;
        // Hold posts to the Director before it can advance: gate so an offline tap does not
        // dim every button behind the hold call's timeout.
        if (!EnsureOnline("hold this session")) return;
        await MarkHandledAndAdvanceAsync(_current, wasHold: true);
    }

    /// <summary>
    /// Finish with the current session and move on: park it first when
    /// <paramref name="wasHold"/> is set, mark it handled for this pass, then present the
    /// next session. A hold that fails to take is surfaced and does NOT advance.
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
        await PresentNextAsync();
    }

    // ===== exit + helpers ==================================================

    private void OnExitClicked(object? sender, EventArgs e)
    {
        _turnCts?.Cancel();
        _current = null;
        _pass.Clear();
        FifoPanel.IsVisible = false;
        StartPanel.IsVisible = true;
        _ = LoadQueueCountAsync();
    }

    private void SetFifoStatus(string text, string colorHex)
    {
        FifoStatusLabel.Text = text;
        FifoStatusLabel.TextColor = Color.FromArgb(colorHex);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendButton.IsEnabled = !busy;
        AskWingmanButton.IsEnabled = !busy;
        SkipButton.IsEnabled = !busy;
        HoldButton.IsEnabled = !busy;
        SendButton.Text = busy ? "Working..." : "Send to session";
    }

    // Top-right burger menu: switch between pages.
    private async void OnNavMenuClicked(object? sender, TappedEventArgs e)
    {
        var choice = await DisplayActionSheet("Go to", "Cancel", null, "Sessions", "FIFO", "FIFO Text", "Notes", "Exes", "Dictionary", "Transcripts");
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
