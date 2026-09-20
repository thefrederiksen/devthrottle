using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// What stands in place of the session view while the Director is on its way down: the progress screen
/// of a smart shutdown, or the plain ending state of "shut down and ignore all sessions". It decides
/// nothing; <see cref="SmartShutdownCoordinator"/> tells it what to hold.
///
/// This class deliberately does NOT define InitializeComponent. The generated one connects the named
/// controls; a hand-written one that only loads the markup leaves every one of them null.
/// </summary>
public partial class SmartShutdownSurface : UserControl
{
    public SmartShutdownSurface()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the owner has read the reason and asks for the session view back.</summary>
    public event Action? BackRequested;

    /// <summary>The progress screen this surface holds, or null when it holds the ending state.</summary>
    public ShutdownProgressView? Progress => ProgressHost.Content as ShutdownProgressView;

    /// <summary>Hold the progress screen of a run.</summary>
    public void ShowProgress(ShutdownProgressView progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        FileLog.Write("[SmartShutdownSurface] ShowProgress");
        ProgressHost.Content = progress;
        ProgressHost.IsVisible = true;
        TxtEnding.IsVisible = false;
    }

    /// <summary>Hold the plain ending state: one sentence, no rows, no buttons.</summary>
    public void ShowEnding()
    {
        FileLog.Write("[SmartShutdownSurface] ShowEnding");
        ProgressHost.IsVisible = false;
        TxtEnding.IsVisible = true;
    }

    /// <summary>
    /// The run ended badly and its reason is on the progress screen. Offer the way back, and leave
    /// everything where it is until the owner takes it.
    /// </summary>
    public void OfferBack()
    {
        FileLog.Write("[SmartShutdownSurface] OfferBack");
        BackPanel.IsVisible = true;
        BtnBackToSessions.Focus();
    }

    private void BtnBackToSessions_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SmartShutdownSurface] Back to your sessions clicked");
        try
        {
            BackRequested?.Invoke();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownSurface] BtnBackToSessions_Click FAILED: {ex}");
        }
    }
}
