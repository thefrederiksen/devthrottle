using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// The shutdown progress screen: shown in place of the session view once the owner has chosen to shut
/// down, in both the smart and the ignore-all case. A view, not a dialog, so it can sit inside the main
/// window or inside a bare window of its own.
///
/// This class deliberately does NOT define InitializeComponent. The generated one connects the named
/// controls; a hand-written one that only loads the markup leaves every one of them null.
/// </summary>
public partial class ShutdownProgressView : UserControl
{
    private readonly DispatcherTimer _clockTimer;

    /// <param name="source">The shutdown that is under way.</param>
    /// <param name="clock">What time it is now. Injected so a test moves it instead of sleeping.</param>
    public ShutdownProgressView(IShutdownProgressSource source, Func<DateTimeOffset> clock)
    {
        InitializeComponent();
        ViewModel = new ShutdownProgressViewModel(source, clock);
        DataContext = ViewModel;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += ClockTimer_Tick;
    }

    /// <summary>Everything this screen shows.</summary>
    public ShutdownProgressViewModel ViewModel { get; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clockTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _clockTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            ViewModel.RefreshTimeLeft();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ShutdownProgressView] ClockTimer_Tick FAILED: {ex}");
        }
    }

    private void BtnShutDownNow_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ShutdownProgressView] Shut down now clicked");
        try
        {
            ViewModel.RequestShutDownNow();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ShutdownProgressView] BtnShutDownNow_Click FAILED: {ex}");
            ViewModel.ReportFailure("Shut down now could not be started. The reason is in the Director log.");
        }
    }

    private void BtnCancelAndKeepWorking_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ShutdownProgressView] Cancel and keep working clicked");
        try
        {
            ViewModel.RequestCancelAndKeepWorking();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ShutdownProgressView] BtnCancelAndKeepWorking_Click FAILED: {ex}");
            ViewModel.ReportFailure("Cancelling could not be started. The reason is in the Director log.");
        }
    }
}
