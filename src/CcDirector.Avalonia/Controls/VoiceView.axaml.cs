using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Eyes-free voice tab for the active session. Two push-to-talk buttons:
/// Ask Agent (talk TO the working agent) and Ask Wingman (ask the read-only
/// observer). Click a button to start capturing; click the same button again
/// ("Stop &amp; Send") to stop, which transcribes in-process via
/// <see cref="SpeakService"/> (the same dictation engine the Speak button uses)
/// and raises the matching event with the cleaned transcript. The host
/// (MainWindow) decides what to do with it - send to the session or ask the
/// wingman - and calls back <see cref="ShowReply"/> with the answer.
///
/// This view owns only audio capture + its own status UI. It holds no session
/// reference and never writes to the PTY directly; the host does that.
/// </summary>
public partial class VoiceView : UserControl
{
    private AgentOptions? _options;
    private SpeakService? _service;
    private bool _recording;
    private bool _busy;
    private bool _wingmanTurn;

    /// <summary>Raised with the cleaned transcript when the user finishes an Ask-Agent turn.</summary>
    public event Action<string>? AskAgentRequested;

    /// <summary>Raised with the cleaned transcript when the user finishes an Ask-Wingman turn.</summary>
    public event Action<string>? AskWingmanRequested;

    public VoiceView()
    {
        InitializeComponent();
    }

    /// <summary>Provide the options needed to run in-process dictation. Call once before use.</summary>
    public void Initialize(AgentOptions options)
    {
        _options = options;
    }

    /// <summary>Set the session this tab is talking to (null clears it).</summary>
    public void SetSession(string? displayName)
    {
        SessionNameText.Text = string.IsNullOrWhiteSpace(displayName) ? "No session selected" : displayName;
        TranscriptText.Text = "-";
        ReplyText.Text = "-";
        SetStatus("Ready", "#5FD08A");
    }

    /// <summary>Show the answer text (from the agent or the wingman). Thread-safe.</summary>
    public void ShowReply(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ReplyText.Text = string.IsNullOrWhiteSpace(text) ? "-" : text;
            SetStatus("Ready", "#5FD08A");
        });
    }

    /// <summary>Set the status line text + color. Thread-safe.</summary>
    public void SetStatus(string text, string colorHex)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = text;
            StatusText.Foreground = new SolidColorBrush(Color.Parse(colorHex));
        });
    }

    private async void AskAgentButton_Click(object? sender, RoutedEventArgs e)
        => await HandleTalkAsync(wingman: false);

    private async void AskWingmanButton_Click(object? sender, RoutedEventArgs e)
        => await HandleTalkAsync(wingman: true);

    private async Task HandleTalkAsync(bool wingman)
    {
        if (_busy || _options is null) return;

        // ---- START a new capture ----
        if (!_recording)
        {
            if (string.IsNullOrWhiteSpace(_options.ResolveOpenAiKey()))
            {
                SetStatus("Voice needs an OPENAI_API_KEY env var or Voice.OpenAiKey in appsettings.", "#F44747");
                return;
            }
            try
            {
                _wingmanTurn = wingman;
                _service = new SpeakService(_options);
                await _service.StartAsync("default");
                _recording = true;
                SetRecordingUi(true, wingman);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[VoiceView] start FAILED: {ex.Message}");
                SetStatus("Could not start: " + ex.Message, "#F44747");
                await SafeDisposeServiceAsync();
                _recording = false;
                SetRecordingUi(false, false);
            }
            return;
        }

        // ---- STOP the active capture, transcribe, raise the event ----
        _recording = false;
        _busy = true;
        AskAgentButton.IsEnabled = false;
        AskWingmanButton.IsEnabled = false;
        SetStatus("Transcribing...", "#DCDCAA");
        try
        {
            var svc = _service;
            _service = null;
            var result = svc is null ? null : await svc.StopAsync();
            if (svc is not null) await svc.DisposeAsync();

            var transcript = (result?.CleanedTranscript ?? "").Trim();
            TranscriptText.Text = string.IsNullOrWhiteSpace(transcript) ? "(nothing heard)" : transcript;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                SetStatus("Ready", "#5FD08A");
                return;
            }

            SetStatus(_wingmanTurn ? "Asking the wingman..." : "Sending to agent...", "#DCDCAA");
            if (_wingmanTurn) AskWingmanRequested?.Invoke(transcript);
            else AskAgentRequested?.Invoke(transcript);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceView] stop FAILED: {ex.Message}");
            SetStatus("Something went wrong: " + ex.Message, "#F44747");
        }
        finally
        {
            _busy = false;
            SetRecordingUi(false, false);
        }
    }

    private void SetRecordingUi(bool recording, bool wingman)
    {
        if (recording)
        {
            SetStatus(wingman ? "Listening for the wingman... click to send" : "Listening... click to send", "#F44747");
            // The active turn's button becomes "Stop & Send"; the other is disabled
            // so a turn cannot be started on top of an in-progress capture.
            AskAgentButton.Content = wingman ? "Ask Agent" : "Stop & Send";
            AskWingmanButton.Content = wingman ? "Stop & Send" : "Ask Wingman";
            AskAgentButton.IsEnabled = !wingman;
            AskWingmanButton.IsEnabled = wingman;
        }
        else
        {
            AskAgentButton.Content = "Ask Agent";
            AskWingmanButton.Content = "Ask Wingman";
            AskAgentButton.IsEnabled = true;
            AskWingmanButton.IsEnabled = true;
        }
    }

    private async Task SafeDisposeServiceAsync()
    {
        var svc = _service;
        _service = null;
        if (svc is null) return;
        try { await svc.DisposeAsync(); }
        catch (Exception ex) { FileLog.Write($"[VoiceView] dispose error: {ex.Message}"); }
    }
}
