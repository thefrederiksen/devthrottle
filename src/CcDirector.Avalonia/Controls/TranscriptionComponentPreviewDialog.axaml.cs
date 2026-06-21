using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Developer harness for the shared <see cref="TranscriptionComponent"/> (issue #588).
/// Hosts both variants (full and small) and lets a developer or QA flip every state so
/// each can be screenshotted in the running app. It is not a product surface and runs no
/// real capture - a timer and a synthetic waveform animate the recording state for proof.
/// </summary>
public partial class TranscriptionComponentPreviewDialog : Window
{
    private static readonly string SampleText =
        "Set up a research agent to check every place we run transcription, and make sure " +
        "cc-director uses the same flow everywhere. Do not change my wording, only fix the " +
        "dictionary terms.";

    private readonly DispatcherTimer _animTimer;
    private DateTime _recordingStarted;
    private readonly Random _random = new();
    private bool _animating;

    public TranscriptionComponentPreviewDialog()
    {
        FileLog.Write("[TranscriptionComponentPreviewDialog] Constructor: initializing");
        InitializeComponent();

        FullComponent.Variant = TranscriptionVariant.Full;
        FullComponent.SetMicrophones(
            new List<string> { "Default - Microphone Array (Realtek)", "Headset Microphone", "USB Microphone" },
            "Default - Microphone Array (Realtek)");

        SmallComponent.Variant = TranscriptionVariant.Small;

        WireOutputs(FullComponent, "FULL");
        WireOutputs(SmallComponent, "SMALL");

        // Drives the simulated timer + waveform while in the recording state.
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _animTimer.Tick += AnimTimer_Tick;

        ShowState(TranscriptionState.Idle);
    }

    private void WireOutputs(TranscriptionComponent component, string label)
    {
        component.Finished += text => Log($"{label}: OnFinished(\"{Trim(text)}\")");
        component.Held += text => Log($"{label}: OnHeld(\"{Trim(text)}\")");
        component.Cancelled += () => Log($"{label}: OnCancelled()");
        component.Errored += reason => Log($"{label}: OnError(\"{reason}\") [retry]");
        component.MicrophoneChanged += device => Log($"{label}: MicrophoneChanged(\"{device}\")");
    }

    private static string Trim(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= 48 ? text : text[..48] + "...";
    }

    private void Log(string line)
    {
        OutputLog.Text = line;
        FileLog.Write($"[TranscriptionComponentPreviewDialog] {line}");
    }

    private void ShowState(TranscriptionState state)
    {
        StopAnimation();
        switch (state)
        {
            case TranscriptionState.Idle:
                FullComponent.ShowIdle();
                SmallComponent.ShowIdle();
                break;
            case TranscriptionState.Recording:
                FullComponent.ShowRecording();
                SmallComponent.ShowRecording();
                StartAnimation();
                break;
            case TranscriptionState.Transcribing:
                FullComponent.ShowTranscribing();
                SmallComponent.ShowTranscribing();
                break;
            case TranscriptionState.Review:
                FullComponent.ShowReview(SampleText, 2);
                SmallComponent.ShowReview(SampleText, 2);
                break;
            case TranscriptionState.Used:
                FullComponent.ShowUsed(SampleText);
                SmallComponent.ShowUsed(SampleText);
                break;
            case TranscriptionState.Error:
                FullComponent.ShowError("Microphone not available");
                SmallComponent.ShowError("Microphone not available");
                break;
        }
    }

    private void StartAnimation()
    {
        _recordingStarted = DateTime.UtcNow;
        _animating = true;
        _animTimer.Start();
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        _animTimer.Stop();
    }

    private void AnimTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _recordingStarted;
        FullComponent.UpdateTimer(elapsed);
        SmallComponent.UpdateTimer(elapsed);

        var levels = new double[15];
        for (var i = 0; i < levels.Length; i++)
            levels[i] = _random.NextDouble();
        FullComponent.UpdateLevels(levels);
        SmallComponent.UpdateLevels(levels);
    }

    private void StateIdle_Click(object? sender, RoutedEventArgs e) => RunState(TranscriptionState.Idle);
    private void StateRecording_Click(object? sender, RoutedEventArgs e) => RunState(TranscriptionState.Recording);
    private void StateTranscribing_Click(object? sender, RoutedEventArgs e) => RunState(TranscriptionState.Transcribing);
    private void StateReview_Click(object? sender, RoutedEventArgs e) => RunState(TranscriptionState.Review);
    private void StateUsed_Click(object? sender, RoutedEventArgs e) => RunState(TranscriptionState.Used);
    private void StateError_Click(object? sender, RoutedEventArgs e) => RunState(TranscriptionState.Error);

    private void RunState(TranscriptionState state)
    {
        try
        {
            FileLog.Write($"[TranscriptionComponentPreviewDialog] RunState: {state}");
            ShowState(state);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TranscriptionComponentPreviewDialog] RunState FAILED: {ex}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAnimation();
        base.OnClosed(e);
    }
}
