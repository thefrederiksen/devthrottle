using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// The smart shutdown progress screen: shown in place of the session view while a smart shutdown is
/// under way, whichever door started it (the window close or the File menu). A view, not a dialog, so it
/// can sit inside the main window or inside a bare window of its own.
///
/// It is shown ONCE. Leaving its window lets go of the run for good; a caller that wants the screen
/// again builds a new one.
///
/// This class deliberately does NOT define InitializeComponent. The generated one connects the named
/// controls; a hand-written one that only loads the markup leaves every one of them null.
/// </summary>
public partial class ShutdownProgressView : UserControl
{
    private bool _wasDetached;

    /// <param name="run">The smart shutdown that is under way.</param>
    /// <param name="clock">What time it is now. Injected so a test sets the time instead of sleeping.</param>
    public ShutdownProgressView(ISmartShutdownRun run, TimeProvider clock)
    {
        InitializeComponent();
        ViewModel = new ShutdownProgressViewModel(run, clock);
        DataContext = ViewModel;
    }

    /// <summary>Everything this screen shows, and the Finished event the caller listens to.</summary>
    public ShutdownProgressViewModel ViewModel { get; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_wasDetached)
        {
            FileLog.Write("[ShutdownProgressView] OnAttachedToVisualTree FAILED: shown a second time after it let go of the run");
            throw new InvalidOperationException(
                "This shutdown progress screen already left its window and let go of the run. Build a new ShutdownProgressView for the run instead of showing this one again.");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        FileLog.Write("[ShutdownProgressView] OnDetachedFromVisualTree: letting go of the run");
        _wasDetached = true;
        ViewModel.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private void BtnShutDownNow_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ShutdownProgressView] Shut down now clicked");
        try
        {
            ViewModel.PressShutDownNow();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ShutdownProgressView] BtnShutDownNow_Click FAILED: {ex}");
        }
    }

    private void BtnCancelAndKeepWorking_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ShutdownProgressView] Cancel and keep working clicked");
        try
        {
            ViewModel.PressCancelAndKeepWorking();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ShutdownProgressView] BtnCancelAndKeepWorking_Click FAILED: {ex}");
        }
    }
}
