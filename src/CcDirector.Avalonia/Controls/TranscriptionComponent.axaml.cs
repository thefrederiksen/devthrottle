using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The size variant of <see cref="TranscriptionComponent"/>.
/// Full is the desktop dialog size; Small is an inline strip for embedding.
/// </summary>
public enum TranscriptionVariant
{
    /// <summary>The full desktop dialog layout (timer, large waveform, transcript area, microphone selector).</summary>
    Full,

    /// <summary>The compact inline strip layout (pulse dot, timer, condensed waveform, action buttons).</summary>
    Small
}

/// <summary>
/// The lifecycle state of a transcription turn, driven by the host.
/// </summary>
public enum TranscriptionState
{
    /// <summary>Nothing in progress; the component is at rest.</summary>
    Idle,

    /// <summary>Capturing audio. Shows a waveform and timer but never any transcript text (no live partial preview).</summary>
    Recording,

    /// <summary>The whole audio has been sent; waiting for the final transcript. No text is shown yet.</summary>
    Transcribing,

    /// <summary>The Hold path: the final transcript is shown for review (Send / Edit / Discard) with the dictionary-correction count.</summary>
    Review,

    /// <summary>The Finish path: the text was used automatically. Terminal state.</summary>
    Used,

    /// <summary>Transcription failed; shows a named reason and offers Retry.</summary>
    Error
}

/// <summary>
/// Shared transcription UI component (issue #588). One control rendering two size
/// variants - <see cref="TranscriptionVariant.Full"/> and
/// <see cref="TranscriptionVariant.Small"/> - across the states
/// idle -&gt; recording -&gt; transcribing -&gt; (review | used | error), with the same
/// Finish / Hold / Cancel controls.
///
/// This control is PRESENTATION AND STATE ONLY. It captures no audio and runs no
/// transcription. The host drives it through the public <c>Show*</c> /
/// <c>UpdateTimer</c> / <c>UpdateLevels</c> methods and reacts to the output
/// contract events <see cref="Finished"/>, <see cref="Held"/>,
/// <see cref="Cancelled"/>, and <see cref="Errored"/>. There is deliberately no
/// method to push partial transcript text in - the no-live-preview rule is
/// enforced by the absence of that path; text can only arrive via
/// <see cref="ShowReview"/> or <see cref="ShowUsed"/> after transcribing.
/// </summary>
public partial class TranscriptionComponent : UserControl
{
    private const int FullWaveBars = 15;
    private const int SmallWaveBars = 14;
    private const double FullWaveMaxHeight = 60.0;
    private const double SmallWaveMaxHeight = 22.0;
    private const double WaveMinHeight = 6.0;

    private TranscriptionVariant _variant = TranscriptionVariant.Full;
    private TranscriptionState _state = TranscriptionState.Idle;

    private readonly List<Border> _fullBars = new();
    private readonly List<Border> _smallBars = new();

    /// <summary>Raised when the user chooses Finish (auto-use). Carries the final text.</summary>
    public event Action<string>? Finished;

    /// <summary>Raised when the user chooses Hold/Send-after-review. Carries the final text.</summary>
    public event Action<string>? Held;

    /// <summary>Raised when the user cancels or discards the turn.</summary>
    public event Action? Cancelled;

    /// <summary>Raised when the user asks to retry after an error. Carries the error reason that was shown.</summary>
    public event Action<string>? Errored;

    /// <summary>Raised when the user picks a different microphone (full variant only). Carries the device name.</summary>
    public event Action<string>? MicrophoneChanged;

    private string _currentText = string.Empty;
    private string _currentErrorReason = string.Empty;

    public TranscriptionComponent()
    {
        InitializeComponent();
        BuildWaveBars();
        ApplyVariant();
        ShowIdle();
    }

    /// <summary>The size variant. Switching it re-applies the current state to the visible layout.</summary>
    public TranscriptionVariant Variant
    {
        get => _variant;
        set
        {
            if (_variant == value) return;
            _variant = value;
            ApplyVariant();
            ApplyState();
        }
    }

    /// <summary>The current lifecycle state (read-only; set it via the Show* methods).</summary>
    public TranscriptionState State => _state;

    /// <summary>
    /// Populate the microphone list (full variant only) and select the active device.
    /// The small variant inherits the default device and exposes no selector.
    /// </summary>
    public void SetMicrophones(IReadOnlyList<string> devices, string? selected)
    {
        if (devices is null) throw new ArgumentNullException(nameof(devices));
        FileLog.Write($"[TranscriptionComponent] SetMicrophones: count={devices.Count}, selected={selected ?? "(none)"}");

        FullMicSelector.ItemsSource = new List<string>(devices);
        if (!string.IsNullOrWhiteSpace(selected))
            FullMicSelector.SelectedItem = selected;
        else if (devices.Count > 0)
            FullMicSelector.SelectedIndex = 0;
    }

    /// <summary>Update the elapsed-time display. The host owns the clock; the component only renders it.</summary>
    public void UpdateTimer(TimeSpan elapsed)
    {
        FullTimer.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 100}";
        SmallTimer.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }

    /// <summary>
    /// Update the live waveform from normalized levels (0..1). Only meaningful in the
    /// recording state; ignored otherwise. The number of values need not match the bar
    /// count - they are sampled across the available bars.
    /// </summary>
    public void UpdateLevels(IReadOnlyList<double> levels)
    {
        if (_state != TranscriptionState.Recording) return;
        if (levels is null || levels.Count == 0) return;

        ApplyLevelsTo(_fullBars, levels, FullWaveMaxHeight);
        ApplyLevelsTo(_smallBars, levels, SmallWaveMaxHeight);
    }

    /// <summary>Reset to the idle state - no capture, no text.</summary>
    public void ShowIdle()
    {
        _state = TranscriptionState.Idle;
        _currentText = string.Empty;
        _currentErrorReason = string.Empty;
        FullTranscript.Text = string.Empty;
        ApplyState();
    }

    /// <summary>Enter the recording state - waveform + timer, no transcript text.</summary>
    public void ShowRecording()
    {
        _state = TranscriptionState.Recording;
        _currentText = string.Empty;
        FullTranscript.Text = string.Empty;
        ApplyState();
    }

    /// <summary>Enter the transcribing state - whole audio sent, waiting for the final text. No text shown.</summary>
    public void ShowTranscribing()
    {
        _state = TranscriptionState.Transcribing;
        FullTranscript.Text = string.Empty;
        ApplyState();
    }

    /// <summary>
    /// Enter the Hold/review state - shows the final text with the dictionary-correction
    /// count and offers Send / Edit / Discard.
    /// </summary>
    /// <param name="finalText">The final transcript (after dictionary correction).</param>
    /// <param name="correctedWordCount">How many known dictionary words were corrected.</param>
    public void ShowReview(string finalText, int correctedWordCount)
    {
        if (correctedWordCount < 0) throw new ArgumentOutOfRangeException(nameof(correctedWordCount));
        FileLog.Write($"[TranscriptionComponent] ShowReview: textLen={finalText?.Length ?? 0}, corrected={correctedWordCount}");

        _state = TranscriptionState.Review;
        _currentText = finalText ?? string.Empty;
        FullTranscript.Text = _currentText;
        FullCorrectionNote.Text = correctedWordCount == 1
            ? "1 dictionary word corrected. Raw wording preserved."
            : $"{correctedWordCount} dictionary words corrected. Raw wording preserved.";
        ApplyState();
    }

    /// <summary>
    /// Enter the Used state - the Finish path used the text automatically (no review).
    /// Terminal; the component shows the used text dimmed.
    /// </summary>
    public void ShowUsed(string finalText)
    {
        _state = TranscriptionState.Used;
        _currentText = finalText ?? string.Empty;
        FullTranscript.Text = _currentText;
        ApplyState();
    }

    /// <summary>Enter the error state - shows a named reason and offers Retry.</summary>
    public void ShowError(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An error reason is required.", nameof(reason));
        FileLog.Write($"[TranscriptionComponent] ShowError: reason={reason}");

        _state = TranscriptionState.Error;
        _currentErrorReason = reason;
        FullTranscript.Text = reason;
        ApplyState();
    }

    // ---- Layout helpers ------------------------------------------------------

    private void BuildWaveBars()
    {
        _fullBars.Clear();
        FullWave.Children.Clear();
        for (var i = 0; i < FullWaveBars; i++)
        {
            var bar = new Border
            {
                Width = 7,
                Height = WaveMinHeight,
                CornerRadius = new global::Avalonia.CornerRadius(3),
                Background = new SolidColorBrush(Color.Parse("#5B8CFF")),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            _fullBars.Add(bar);
            FullWave.Children.Add(bar);
        }

        _smallBars.Clear();
        SmallWave.Children.Clear();
        for (var i = 0; i < SmallWaveBars; i++)
        {
            var bar = new Border
            {
                Width = 4,
                Height = WaveMinHeight,
                CornerRadius = new global::Avalonia.CornerRadius(2),
                Background = new SolidColorBrush(Color.Parse("#6F9BFF")),
                VerticalAlignment = VerticalAlignment.Center
            };
            _smallBars.Add(bar);
            SmallWave.Children.Add(bar);
        }
    }

    private static void ApplyLevelsTo(List<Border> bars, IReadOnlyList<double> levels, double maxHeight)
    {
        for (var i = 0; i < bars.Count; i++)
        {
            // Sample the level list across the bar count so any source length works.
            var sourceIndex = levels.Count == bars.Count ? i : i * levels.Count / bars.Count;
            var level = Math.Clamp(levels[sourceIndex], 0.0, 1.0);
            bars[i].Height = WaveMinHeight + level * (maxHeight - WaveMinHeight);
        }
    }

    private void ApplyVariant()
    {
        FullRoot.IsVisible = _variant == TranscriptionVariant.Full;
        SmallRoot.IsVisible = _variant == TranscriptionVariant.Small;
    }

    private void ApplyState()
    {
        switch (_state)
        {
            case TranscriptionState.Idle:
                ApplyIdle();
                break;
            case TranscriptionState.Recording:
                ApplyRecording();
                break;
            case TranscriptionState.Transcribing:
                ApplyTranscribing();
                break;
            case TranscriptionState.Review:
                ApplyReview();
                break;
            case TranscriptionState.Used:
                ApplyUsed();
                break;
            case TranscriptionState.Error:
                ApplyError();
                break;
        }
    }

    private void ApplyIdle()
    {
        SetFullPill("IDLE", "#232C47", "#AEB9D8");
        FullMicRow.IsVisible = true;
        FullTimer.IsVisible = false;
        FullWave.IsVisible = false;
        FullHint.IsVisible = false;
        FullCorrectionNote.IsVisible = false;
        FullDictNote.IsVisible = true;
        SetFullButtons("Finish", true, "Hold", true, "Cancel");

        SetSmallDot("#6C79A0");
        SmallTimer.Text = "Ready";
        SmallTimer.Foreground = new SolidColorBrush(Color.Parse("#AEB9D8"));
        SmallWave.IsVisible = false;
        SetSmallButtons("Finish", true, "Hold", true, "X", true);
    }

    private void ApplyRecording()
    {
        SetFullPill("RECORDING", "#3A1414", "#FF7A7A");
        FullMicRow.IsVisible = true;
        FullTimer.IsVisible = true;
        FullTimer.FontSize = 40;
        FullTimer.Foreground = new SolidColorBrush(Color.Parse("#EAF0FF"));
        FullWave.IsVisible = true;
        FullHint.IsVisible = true;
        FullCorrectionNote.IsVisible = false;
        FullDictNote.IsVisible = true;
        SetFullButtons("Finish", true, "Hold", true, "Cancel");

        SetSmallDot("#FF5D5D");
        SmallTimer.Foreground = new SolidColorBrush(Color.Parse("#EAF0FF"));
        SmallWave.IsVisible = true;
        SmallWave.Opacity = 1.0;
        SetSmallButtons("Finish", true, "Hold", true, "X", true);
    }

    private void ApplyTranscribing()
    {
        SetFullPill("TRANSCRIBING", "#3A2A10", "#FFC06B");
        FullMicRow.IsVisible = false;
        FullTimer.IsVisible = false;
        FullWave.IsVisible = false;
        FullHint.IsVisible = false;
        FullCorrectionNote.IsVisible = false;
        FullDictNote.IsVisible = true;
        // Cancel remains available; the two commit actions are not yet meaningful.
        SetFullButtons("Finish", false, "Hold", false, "Cancel");

        SetSmallDot("#FFC06B");
        SmallTimer.Text = "Transcribing";
        SmallTimer.Foreground = new SolidColorBrush(Color.Parse("#FFC06B"));
        SmallWave.IsVisible = true;
        SmallWave.Opacity = 0.4;
        // Small transcribing strip collapses to a single Cancel.
        SetSmallButtons(null, false, null, false, "Cancel", true);
    }

    private void ApplyReview()
    {
        SetFullPill("HELD - REVIEW", "#232C47", "#AEB9D8");
        FullMicRow.IsVisible = false;
        FullTimer.IsVisible = false;
        FullWave.IsVisible = false;
        FullHint.IsVisible = false;
        FullCorrectionNote.IsVisible = true;
        FullDictNote.IsVisible = false;
        SetFullButtons("Send", true, "Edit", true, "Discard");

        SetSmallDot("#AEB9D8");
        SmallTimer.Text = "Review";
        SmallTimer.Foreground = new SolidColorBrush(Color.Parse("#AEB9D8"));
        SmallWave.IsVisible = false;
        SetSmallButtons("Send", true, "Edit", true, "X", true);
    }

    private void ApplyUsed()
    {
        SetFullPill("USED", "#10331F", "#2ECC8F");
        FullMicRow.IsVisible = false;
        FullTimer.IsVisible = false;
        FullWave.IsVisible = false;
        FullHint.IsVisible = false;
        FullCorrectionNote.IsVisible = false;
        FullDictNote.IsVisible = false;
        SetFullButtons("Finish", false, "Hold", false, "Close");

        SetSmallDot("#2ECC8F");
        SmallTimer.Text = "Used";
        SmallTimer.Foreground = new SolidColorBrush(Color.Parse("#2ECC8F"));
        SmallWave.IsVisible = false;
        SetSmallButtons(null, false, null, false, "X", true);
    }

    private void ApplyError()
    {
        SetFullPill("ERROR", "#3A1414", "#FF6B6B");
        FullMicRow.IsVisible = false;
        FullTimer.IsVisible = false;
        FullWave.IsVisible = false;
        FullHint.IsVisible = false;
        FullCorrectionNote.IsVisible = false;
        FullDictNote.IsVisible = false;
        SetFullButtons("Retry", true, null, false, "Cancel");

        SetSmallDot("#FF6B6B");
        SmallTimer.Text = "Error";
        SmallTimer.Foreground = new SolidColorBrush(Color.Parse("#FF6B6B"));
        SmallWave.IsVisible = false;
        SetSmallButtons("Retry", true, null, false, "X", true);
    }

    private void SetFullPill(string text, string backHex, string foreHex)
    {
        FullStateText.Text = text;
        FullStateText.Foreground = new SolidColorBrush(Color.Parse(foreHex));
        FullStatePill.Background = new SolidColorBrush(Color.Parse(backHex));
    }

    private void SetSmallDot(string hex)
    {
        SmallDot.Background = new SolidColorBrush(Color.Parse(hex));
    }

    private void SetFullButtons(string primary, bool primaryEnabled, string? secondary, bool secondaryEnabled, string cancel)
    {
        FullPrimaryButton.Content = primary;
        FullPrimaryButton.IsEnabled = primaryEnabled;
        FullSecondaryButton.Content = secondary;
        FullSecondaryButton.IsEnabled = secondaryEnabled;
        FullSecondaryButton.IsVisible = secondary is not null;
        FullCancelButton.Content = cancel;
    }

    private void SetSmallButtons(string? primary, bool primaryEnabled, string? secondary, bool secondaryEnabled, string cancel, bool cancelEnabled)
    {
        SmallPrimaryButton.Content = primary;
        SmallPrimaryButton.IsEnabled = primaryEnabled;
        SmallPrimaryButton.IsVisible = primary is not null;
        SmallSecondaryButton.Content = secondary;
        SmallSecondaryButton.IsEnabled = secondaryEnabled;
        SmallSecondaryButton.IsVisible = secondary is not null;
        SmallCancelButton.Content = cancel;
        SmallCancelButton.IsEnabled = cancelEnabled;
    }

    // ---- Event handlers (entry points) --------------------------------------

    private void FullMicSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (FullMicSelector.SelectedItem is string device && !string.IsNullOrWhiteSpace(device))
                MicrophoneChanged?.Invoke(device);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TranscriptionComponent] mic selection FAILED: {ex.Message}");
        }
    }

    private void FullPrimaryButton_Click(object? sender, RoutedEventArgs e) => RaisePrimary();
    private void SmallPrimaryButton_Click(object? sender, RoutedEventArgs e) => RaisePrimary();

    private void FullSecondaryButton_Click(object? sender, RoutedEventArgs e) => RaiseSecondary();
    private void SmallSecondaryButton_Click(object? sender, RoutedEventArgs e) => RaiseSecondary();

    private void FullCancelButton_Click(object? sender, RoutedEventArgs e) => RaiseCancel();
    private void SmallCancelButton_Click(object? sender, RoutedEventArgs e) => RaiseCancel();

    private void RaisePrimary()
    {
        try
        {
            switch (_state)
            {
                case TranscriptionState.Recording:
                case TranscriptionState.Idle:
                    // Finish -> auto-use without review.
                    Finished?.Invoke(_currentText);
                    break;
                case TranscriptionState.Review:
                    // Send the reviewed text.
                    Held?.Invoke(_currentText);
                    break;
                case TranscriptionState.Error:
                    // Retry.
                    Errored?.Invoke(_currentErrorReason);
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TranscriptionComponent] primary action FAILED: {ex.Message}");
        }
    }

    private void RaiseSecondary()
    {
        try
        {
            switch (_state)
            {
                case TranscriptionState.Recording:
                case TranscriptionState.Idle:
                    // Hold -> capture then review.
                    Held?.Invoke(_currentText);
                    break;
                case TranscriptionState.Review:
                    // Edit makes the review text editable in place.
                    FullTranscript.IsReadOnly = false;
                    FullTranscript.Focus();
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TranscriptionComponent] secondary action FAILED: {ex.Message}");
        }
    }

    private void RaiseCancel()
    {
        try
        {
            // The review-state transcript may have been edited; keep the host's
            // copy in sync before discarding so an "edit then discard" still
            // reports the user's intent cleanly.
            Cancelled?.Invoke();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TranscriptionComponent] cancel FAILED: {ex.Message}");
        }
    }
}
