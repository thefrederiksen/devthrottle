using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Full-screen FIFO takeover for the desktop: steps through every session that needs
/// the user, one at a time, wingman-led. The wingman briefing is the primary surface -
/// read on the left and spoken aloud the moment you land on a session - while the live
/// terminal is a secondary "verify" pane you can glance at or hide (Hide terminal). The
/// whole point is focus: no sidebar, no tabs; the order is decided, you just deal with
/// what is in front of you and hit Next.
///
/// Voice is the default interaction. The briefing is spoken on arrival via the in-process
/// <see cref="DesktopTtsPlayer"/>; Ask Wingman (the hero button, or Space) records a
/// question and speaks the read-only answer back. Both reuse the same whole-audio batch
/// <see cref="BatchDictationRecorder"/> capture the Voice tab uses (issue #590) and the
/// read-only <see cref="Core.Wingman.WingmanService"/> the rest of the app uses. The question
/// is transcribed ONCE after the user stops, through the shared batch pipeline using the
/// user-selected method (no hardcoded realtime model, no live partials), with dictionary-only
/// correction. Ask Agent versus Ask Wingman is a UI-only routing of the finished transcript and
/// never alters the transcribed text. Advancing is manual (Skip / Hold / Next) - nothing
/// auto-rotates.
/// </summary>
public partial class FifoWindow : Window
{
    private const string Green = "#5FD08A";
    private const string Blue = "#2B6CB0";
    private const string Yellow = "#DCDCAA";
    private const string Red = "#F44747";

    // Read-only briefing prompt for the right pane: the wingman summarizes the session
    // from its terminal, the same read-only path "Ask Wingman" uses. Shared with the
    // Terminal tab's "Explain" button so honing one briefing hones both.
    private const string BriefingQuestion = global::CcDirector.Core.Wingman.WingmanService.BriefingQuestion;

    private readonly SessionManager _sm;
    private readonly AgentOptions _options;
    private readonly DesktopTtsPlayer _tts;

    // Sessions handled this pass, so Next/Skip move forward instead of re-presenting.
    private readonly HashSet<Guid> _pass = new();
    private Session? _current;

    private BatchDictationRecorder? _recorder;
    private bool _recording;
    private bool _busy;
    private bool _wingmanTurn;
    private bool _terminalVisible = true;

    // Cancels the spoken briefing for the session being left when the user advances or
    // starts talking, so the wingman never talks over the next session or the user.
    private CancellationTokenSource? _briefingCts;

    public FifoWindow(SessionManager sm)
    {
        _sm = sm ?? throw new ArgumentNullException(nameof(sm));
        _options = sm.Options;
        InitializeComponent();
        _tts = new DesktopTtsPlayer(_options);
        ApplyTerminalVisibility();
        Opened += (_, _) => PresentNext();
        Closed += OnClosedCleanup;
        KeyDown += OnKeyDown;
        FileLog.Write("[FifoWindow] opened");
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc cancels an in-progress recording first; otherwise it exits FIFO.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_recording) _ = CancelRecordingAsync();
            else Close();
            return;
        }

        // Space is push-to-talk to the wingman: the default interaction. It only reaches
        // here when the terminal pane is not focused, so verifying in the terminal still
        // types a normal space.
        if (e.Key == Key.Space && !_busy && _current is not null)
        {
            e.Handled = true;
            _ = HandleTalkAsync(wingman: true);
        }
    }

    // ===== the conveyor =====================================================

    private void PresentNext()
    {
        var queue = BuildQueue();
        var next = queue.FirstOrDefault(s => !_pass.Contains(s.Id));
        if (next is null)
        {
            ShowCaughtUp();
            return;
        }
        SetCurrent(next, queue.Count(s => !_pass.Contains(s.Id)));
    }

    private List<Session> BuildQueue() =>
        _sm.ListSessions()
            .Where(s => s.StatusColor == "red"
                        && !s.OnHold
                        && s.Status is not (SessionStatus.Exited or SessionStatus.Failed))
            .OrderBy(s => s.RepoPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id)
            .ToList();

    private void SetCurrent(Session session, int remaining)
    {
        // Cut any briefing still being spoken for the session we are leaving.
        _briefingCts?.Cancel();
        _briefingCts = new CancellationTokenSource();
        var briefingCt = _briefingCts.Token;

        _current = session;
        CaughtUpOverlay.IsVisible = false;

        HeaderTitle.Text = DisplayName(session);
        var more = remaining - 1;
        HeaderSub.Text = more <= 0
            ? $"NEEDS YOU - {session.LastStatusReason}   (last one)"
            : $"NEEDS YOU - {session.LastStatusReason}   ({more} more after this)";

        SetText(BriefingText, "-");
        SetText(TranscriptText, "-");
        SetText(ReplyText, "-");

        try { TerminalHost.Detach(); } catch { }
        TerminalHost.Attach(session);
        // Focus the window, not the terminal: the FIFO is wingman-led and keyboard
        // shortcuts (Space to talk, Esc to exit) must reach the window. The terminal
        // only takes focus if the user clicks into it to verify.
        RootGrid.Focus();

        SetRestingButtons();
        SetStatus("Ask Wingman (Space), Ask Agent, Skip, Hold, or Next", Green);
        _ = LoadBriefingAsync(session, briefingCt);
    }

    private async Task LoadBriefingAsync(Session session, CancellationToken ct)
    {
        SetText(BriefingText, "Reading what's happening...");
        try
        {
            var briefing = await RunWingmanAsync(session, BriefingQuestion);
            if (_current != session || ct.IsCancellationRequested) return;
            SetText(BriefingText, briefing);

            // Speak the briefing aloud: the FIFO is wingman-led, you should hear what's
            // happening, not have to read it. A failed/cancelled voice never breaks the turn.
            try { await _tts.SpeakAsync(briefing, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { FileLog.Write($"[FifoWindow] briefing tts FAILED: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FifoWindow] LoadBriefing FAILED: {ex.Message}");
            if (_current == session) SetText(BriefingText, "Could not read this session: " + ex.Message);
        }
    }

    private void ShowCaughtUp()
    {
        _current = null;
        _briefingCts?.Cancel();
        _tts.Stop();
        try { TerminalHost.Detach(); } catch { }
        CaughtUpOverlay.IsVisible = true;
        SetStatus("All caught up", Green);
        SetRecordingUi(false, false);
        AskAgentButton.IsEnabled = false;
        AskWingmanButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        HoldButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        FileLog.Write("[FifoWindow] caught up - nothing needs the user");
    }

    // Mark the current session handled this pass and move on.
    private void Advance()
    {
        if (_current is not null) _pass.Add(_current.Id);
        PresentNext();
    }

    // ===== queue-steering buttons ===========================================

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _recording || _current is null) return;
        Advance();
    }

    private void OnSkipClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _recording || _current is null) return;
        Advance();
    }

    private void OnHoldClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _recording || _current is null) return;
        SetStatus("Holding this session...", Yellow);
        _current.OnHold = true; // parks it out of the rotation until taken off hold
        FileLog.Write($"[FifoWindow] hold session={_current.Id}");
        Advance();
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        // A fresh pass: anything still red (or newly red) is back on the belt.
        _pass.Clear();
        CaughtUpOverlay.IsVisible = false;
        PresentNext();
    }

    // ===== verify pane (terminal) toggle ====================================

    private void OnVerifyToggleClick(object? sender, RoutedEventArgs e)
    {
        _terminalVisible = !_terminalVisible;
        ApplyTerminalVisibility();
        RootGrid.Focus(); // keep Space/Esc on the window after clicking the toggle
    }

    // Show or hide the secondary terminal pane. Hidden, the wingman briefing takes the
    // whole width for distraction-free listening; shown, you get a verify column.
    private void ApplyTerminalVisibility()
    {
        // Columns: [0] briefing, [1] splitter, [2] terminal. Named ColumnDefinitions
        // don't generate code-behind fields in Avalonia, so index into the grid.
        var splitterCol = CenterGrid.ColumnDefinitions[1];
        var terminalCol = CenterGrid.ColumnDefinitions[2];
        if (_terminalVisible)
        {
            terminalCol.Width = new GridLength(1, GridUnitType.Star);
            splitterCol.Width = GridLength.Auto;
            TerminalPane.IsVisible = true;
            VerifySplitter.IsVisible = true;
            VerifyToggle.Content = "Hide terminal";
        }
        else
        {
            terminalCol.Width = new GridLength(0);
            splitterCol.Width = new GridLength(0);
            TerminalPane.IsVisible = false;
            VerifySplitter.IsVisible = false;
            VerifyToggle.Content = "Verify (show terminal)";
        }
    }

    // ===== voice (click-to-talk) ============================================

    private async void OnAskAgentClick(object? sender, RoutedEventArgs e) => await HandleTalkAsync(wingman: false);
    private async void OnAskWingmanClick(object? sender, RoutedEventArgs e) => await HandleTalkAsync(wingman: true);

    private async Task HandleTalkAsync(bool wingman)
    {
        if (_busy || _current is null) return;

        // ---- START capture ----
        if (!_recording)
        {
            if (string.IsNullOrWhiteSpace(_options.ResolveOpenAiKey()))
            {
                SetStatus("Voice needs an OPENAI_API_KEY env var or Voice.OpenAiKey in appsettings.", Red);
                return;
            }
            try
            {
                // Silence any briefing still being spoken so it does not bleed into capture.
                _briefingCts?.Cancel();
                _tts.Stop();

                _wingmanTurn = wingman;
                // BatchDictationRecorder buffers the whole turn locally; nothing is transcribed
                // until TranscribeAsync runs on stop. No realtime socket, no live partials.
                _recorder = new BatchDictationRecorder(_options);
                await _recorder.StartAsync("default");
                _recording = true;
                SetRecordingUi(true, wingman);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[FifoWindow] start capture FAILED: {ex.Message}");
                SetStatus("Could not start: " + ex.Message, Red);
                await SafeDisposeRecorderAsync();
                _recording = false;
                SetRestingButtons();
            }
            return;
        }

        // ---- STOP capture, transcribe ONCE, act ----
        _recording = false;
        _busy = true;
        AskAgentButton.IsEnabled = false;
        AskWingmanButton.IsEnabled = false;
        SetStatus("Transcribing...", Yellow);
        try
        {
            var rec = _recorder;
            _recorder = null;

            string transcript;
            try
            {
                // One whole-audio batch transcription via the user-selected method, dictionary-only
                // correction. CleanedTranscript equals the raw transcript when no dictionary term hit.
                var result = rec is null ? null : await rec.TranscribeAsync();
                transcript = (result?.CleanedTranscript ?? "").Trim();
            }
            catch (NoAudioCapturedException)
            {
                // Completeness gate (issue #586): an interrupted turn captured no audio, so there is
                // no transcript to use. Treat it as "nothing heard" rather than transcribing partial input.
                FileLog.Write("[FifoWindow] no audio captured; nothing to transcribe");
                transcript = "";
            }
            finally
            {
                if (rec is not null) await rec.DisposeAsync();
            }

            SetText(TranscriptText, string.IsNullOrWhiteSpace(transcript) ? "(nothing heard)" : transcript);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                SetStatus("Ask Agent, Ask Wingman, Skip, Hold, or Next", Green);
                return;
            }

            // Routing (agent vs wingman) is a UI-only decision on the FINISHED transcript; it never
            // alters the transcribed text passed below.
            if (_wingmanTurn) await AskWingmanAsync(transcript);
            else await SendToAgentAsync(transcript);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FifoWindow] stop/act FAILED: {ex.Message}");
            SetStatus("Something went wrong: " + ex.Message, Red);
        }
        finally
        {
            _busy = false;
            SetRestingButtons();
        }
    }

    // Ask Agent: type the spoken answer straight into the session's terminal and submit,
    // then let the user watch the live terminal and hit Next when ready (manual).
    private async Task SendToAgentAsync(string transcript)
    {
        if (_current is null) return;
        // The agent's reply lands in the terminal, so make sure it's visible to watch.
        if (!_terminalVisible) { _terminalVisible = true; ApplyTerminalVisibility(); }
        SetStatus("Sending to agent...", Yellow);
        await _current.SendTextAsync(transcript + "\n");
        SetText(ReplyText, $"Sent to {DisplayName(_current)}. Watch the terminal, then Next.");
        SetStatus("Sent - watch the terminal, then Next", Green);
    }

    // Ask Wingman: read-only answer over the full cleaned terminal, shown and spoken.
    private async Task AskWingmanAsync(string transcript)
    {
        if (_current is null) return;
        SetStatus("Asking the wingman...", Blue);
        var answer = await RunWingmanAsync(_current, transcript);
        SetText(ReplyText, answer);
        try
        {
            SetStatus("Speaking...", Blue);
            await _tts.SpeakAsync(answer);
        }
        catch (Exception ex) { FileLog.Write($"[FifoWindow] tts FAILED: {ex.Message}"); }
        SetStatus("Ask Agent, Ask Wingman, Skip, Hold, or Next", Green);
    }

    private async Task<string> RunWingmanAsync(Session session, string question)
    {
        var bytes = session.Buffer?.DumpAll() ?? Array.Empty<byte>();
        var fullTerminal = global::CcDirector.ControlApi.AnsiCleaner.Clean(bytes);
        var result = await global::CcDirector.Core.Wingman.WingmanService.AnswerViaSessionAsync(
            question, fullTerminal, session.AgentKind.ToString(), session.RepoPath, _options.ClaudePath);
        return string.IsNullOrWhiteSpace(result.Answer)
            ? "The wingman had nothing to report."
            : result.Answer;
    }

    private async Task CancelRecordingAsync()
    {
        _recording = false;
        SetStatus("Cancelled.", Yellow);
        await SafeDisposeRecorderAsync();
        SetRestingButtons();
        SetStatus("Ask Agent, Ask Wingman, Skip, Hold, or Next", Green);
    }

    // ===== button / status helpers ==========================================

    // Resting: both primaries live; Skip/Hold/Next enabled only when there is a session.
    private void SetRestingButtons()
    {
        var hasSession = _current is not null;
        AskAgentButton.Content = "Ask Agent";
        AskWingmanButton.Content = "Ask Wingman  (Space)";
        AskAgentButton.IsEnabled = hasSession;
        AskWingmanButton.IsEnabled = hasSession;
        SkipButton.IsEnabled = hasSession;
        HoldButton.IsEnabled = hasSession;
        NextButton.IsEnabled = hasSession;
    }

    // Recording: the active button becomes "Stop & Send"; everything else is disabled so a
    // turn cannot start on top of a capture and the queue cannot move mid-recording.
    private void SetRecordingUi(bool recording, bool wingman)
    {
        if (recording)
        {
            SetStatus(wingman
                ? "Listening for the wingman... click Stop & Send (Esc cancels)"
                : "Listening... click Stop & Send (Esc cancels)", Red);
            AskAgentButton.Content = wingman ? "Ask Agent" : "Stop & Send";
            AskWingmanButton.Content = wingman ? "Stop & Send" : "Ask Wingman  (Space)";
            AskAgentButton.IsEnabled = !wingman;
            AskWingmanButton.IsEnabled = wingman;
            SkipButton.IsEnabled = false;
            HoldButton.IsEnabled = false;
            NextButton.IsEnabled = false;
        }
        else
        {
            SetRestingButtons();
        }
    }

    private void SetStatus(string text, string colorHex) => Dispatcher.UIThread.Post(() =>
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(colorHex));
    });

    private static void SetText(TextBlock block, string text) => Dispatcher.UIThread.Post(() =>
    {
        block.Text = string.IsNullOrWhiteSpace(text) ? "-" : text;
    });

    private static string DisplayName(Session session)
    {
        var folder = Path.GetFileName(session.RepoPath.TrimEnd('\\', '/'));
        if (!string.IsNullOrWhiteSpace(folder)) return folder;
        var id = session.Id.ToString();
        return id.Length >= 8 ? id[..8] : id;
    }

    private async Task SafeDisposeRecorderAsync()
    {
        var rec = _recorder;
        _recorder = null;
        if (rec is null) return;
        try { await rec.DisposeAsync(); }
        catch (Exception ex) { FileLog.Write($"[FifoWindow] dispose error: {ex.Message}"); }
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        _recording = false;
        _briefingCts?.Cancel();
        _ = SafeDisposeRecorderAsync();
        try { TerminalHost.Detach(); } catch { }
        // Stop any reply still being spoken and release the TTS audio device.
        try { _tts.Dispose(); } catch (Exception ex) { FileLog.Write($"[FifoWindow] tts dispose error: {ex.Message}"); }
        FileLog.Write("[FifoWindow] closed");
    }
}
