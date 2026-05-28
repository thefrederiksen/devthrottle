using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Modal dialog that plays a spoken briefing through the shared
/// <see cref="DesktopTtsPlayer"/> and gives the user a single, obvious way to
/// stop it. It exists to fix the old fire-and-forget "Speak it" button, which
/// could be clicked repeatedly with no visible playback state and no stop
/// control. As a modal, it blocks its launching button while audio plays, so
/// the user cannot stack overlapping requests.
///
/// Lifecycle:
///   Opened  - start playback (<see cref="DesktopTtsPlayer.SpeakAsync"/>).
///   natural end - the SpeakAsync task completes; the dialog closes itself.
///   Stop / Escape / window close - cancel the in-flight audio and close.
///   generation failure - show an error and turn Stop into "Close" rather than
///                         closing silently as though it had spoken.
/// </summary>
public partial class SpeakPlaybackDialog : Window
{
    private readonly DesktopTtsPlayer _player;
    private readonly string _text;
    private readonly CancellationTokenSource _cts = new();

    // True once the user (or the window-close path) has asked to stop. Guards the
    // SpeakAsync continuation from closing a window the user already closed and
    // from reporting an error for an intentional stop.
    private bool _stopped;

    public SpeakPlaybackDialog(DesktopTtsPlayer player, string text)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _text = text ?? "";
        InitializeComponent();
        BodyText.Text = _text;

        Opened += async (_, _) => await PlayAsync();
        Closed += (_, _) => StopPlayback();
        KeyDown += SpeakPlaybackDialog_KeyDown;
    }

    // Parameterless constructor for the XAML designer.
    public SpeakPlaybackDialog() : this(null!, "") { }

    private void SpeakPlaybackDialog_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            FileLog.Write("[SpeakPlaybackDialog] Escape -> Stop");
            e.Handled = true;
            StopButton_Click(this, new RoutedEventArgs());
        }
    }

    private async Task PlayAsync()
    {
        try
        {
            FileLog.Write($"[SpeakPlaybackDialog] PlayAsync: {_text.Length} chars");
            var played = await _player.SpeakAsync(_text, _cts.Token);

            // User already stopped/closed: their handler owns the close.
            if (_stopped) return;

            if (played)
                Close();
            else
                ShowError("Could not generate the audio. Check the OpenAI key and network connection.");
        }
        catch (OperationCanceledException)
        {
            // Stop pressed mid-generation; the Stop handler closes the window.
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakPlaybackDialog] PlayAsync FAILED: {ex.Message}");
            if (!_stopped)
                ShowError("Playback failed: " + ex.Message);
        }
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        _stopped = true;
        StopPlayback();
        Close();
    }

    private void StopPlayback()
    {
        _stopped = true;
        try { _cts.Cancel(); } catch { /* already disposed */ }
        _player.Stop();
    }

    private void ShowError(string message)
    {
        StatusText.Text = "Failed";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        BodyText.Text = message;
        StopButton.Content = "Close";
        StopButton.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x50));
    }
}
