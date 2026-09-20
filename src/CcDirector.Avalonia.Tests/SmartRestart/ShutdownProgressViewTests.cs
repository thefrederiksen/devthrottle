using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Avalonia.SmartRestart;
using Xunit;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// The shutdown progress screen, opened for real inside a window and driven the way the owner and the
/// engine drive it: the fake shutdown is stepped by hand, the buttons are clicked with the mouse, and
/// what is asserted is what the screen DREW - the text of the controls in the window - not a view model
/// a test filled in.
///
/// A mouse click is used instead of raising the click event, because a raised event reaches a disabled
/// button and a real click does not. "A second click does nothing" is only true of the real thing.
///
/// What these do NOT cover: the one-second timer firing by itself (no test sleeps; the tests call the
/// same method the timer calls), and the screen sitting inside the main window, which a later task does.
/// </summary>
public class ShutdownProgressViewTests
{
    internal static readonly DateTimeOffset Start = new(2026, 9, 19, 22, 0, 0, TimeSpan.Zero);

    internal static readonly string[] NineSessions =
    {
        "Billing - Tech Lead - invoices",
        "Billing - Developer - the export",
        "Billing - Developer - the totals",
        "Docs - Developer - the install page",
        "Docs - Reviewer - the install page",
        "Voice - Developer - the wake word",
        "Voice - Developer - the ready cue",
        "Fleet - Delivery Lead - the restart",
        "Fleet - Developer - the history list",
    };

    internal sealed record Screen(
        Window Window,
        ShutdownProgressView View,
        FakeShutdownProgressSource Source,
        FakeShutdownClock Clock);

    internal static Screen Open(ShutdownProgressKind kind, params string[] sessionNames)
    {
        var clock = new FakeShutdownClock(Start);
        var source = new FakeShutdownProgressSource(kind, TimeSpan.FromMinutes(10), Start, sessionNames);
        var view = new ShutdownProgressView(source, clock.Now);
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Screen(window, view, source, clock);
    }

    internal static void Click(Screen screen, Button button)
    {
        var centre = button.TranslatePoint(
            new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), screen.Window);
        Assert.NotNull(centre);
        screen.Window.MouseDown(centre.Value, MouseButton.Left);
        screen.Window.MouseUp(centre.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The visible text of each row as drawn: name, state, and the reason when there is one.</summary>
    private static List<string[]> DrawnRows(ShutdownProgressView view) =>
        view.RowList.GetRealizedContainers()
            .Select(container => container.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text => text.IsVisible)
                .Select(text => text.Text ?? "")
                .ToArray())
            .ToList();

    /// <summary>
    /// The law of this phase: the view is opened. Every control the markup names must be connected,
    /// which is exactly what a hand-written InitializeComponent breaks - it loads the markup and leaves
    /// each named field null.
    /// </summary>
    [AvaloniaFact]
    public void Show_InsideAWindow_ConnectsEveryNamedControl()
    {
        var screen = Open(ShutdownProgressKind.Smart, NineSessions);

        Assert.NotNull(screen.View.CountText);
        Assert.NotNull(screen.View.TimeLeftText);
        Assert.NotNull(screen.View.StatusText);
        Assert.NotNull(screen.View.RowList);
        Assert.NotNull(screen.View.ButtonPanel);
        Assert.NotNull(screen.View.BtnShutDownNow);
        Assert.NotNull(screen.View.BtnCancelAndKeepWorking);
        Assert.True(screen.View.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Rows_EveryState_DrawnInTheMissionsExactWords()
    {
        var screen = Open(ShutdownProgressKind.Smart,
            "one", "two", "three", "four", "five", "six", "seven");

        screen.Source.MoveTo("two", ShutdownProgressState.Writing);
        screen.Source.MoveTo("three", ShutdownProgressState.HandedOver);
        screen.Source.MoveTo("four", ShutdownProgressState.ShutDown);
        screen.Source.MoveTo("five", ShutdownProgressState.Interrupted);
        screen.Source.MoveTo("six", ShutdownProgressState.EndedAtLimit);
        screen.Source.MoveTo("seven", ShutdownProgressState.CouldNotBeAsked,
            "The session has a question box open and cannot take a prompt.");
        Dispatcher.UIThread.RunJobs();

        var rows = DrawnRows(screen.View);
        Assert.Equal(7, rows.Count);
        Assert.Equal(new[] { "one", "asked" }, rows[0]);
        Assert.Equal(new[] { "two", "writing" }, rows[1]);
        Assert.Equal(new[] { "three", "handed over" }, rows[2]);
        Assert.Equal(new[] { "four", "shut down" }, rows[3]);
        Assert.Equal(new[] { "five", "interrupted" }, rows[4]);
        Assert.Equal(new[] { "six", "ended at the limit" }, rows[5]);
        Assert.Equal(
            new[] { "seven", "could not be asked", "The session has a question box open and cannot take a prompt." },
            rows[6]);
    }

    /// <summary>
    /// The wedged session of mission section 7. It must never be left looking "asked", and when the
    /// engine sends no reason the row says so instead of showing an empty line.
    /// </summary>
    [AvaloniaFact]
    public void Rows_CouldNotBeAskedWithNoReason_SaysNoReasonWasGiven()
    {
        var screen = Open(ShutdownProgressKind.Smart, "wedged");

        screen.Source.MoveTo("wedged", ShutdownProgressState.CouldNotBeAsked);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "wedged", "could not be asked", "No reason was given." }, DrawnRows(screen.View)[0]);
    }

    [AvaloniaFact]
    public void Count_AsSessionsGo_FollowsTheShutdown()
    {
        var screen = Open(ShutdownProgressKind.Smart, NineSessions);
        Assert.Equal("0 of 9 shut down", screen.View.CountText.Text);

        // Handed over is not gone: the session is still open.
        screen.Source.MoveTo(NineSessions[8], ShutdownProgressState.HandedOver);
        screen.Source.MoveTo(NineSessions[7], ShutdownProgressState.Interrupted);
        screen.Source.MoveTo(NineSessions[6], ShutdownProgressState.CouldNotBeAsked, "wedged");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("0 of 9 shut down", screen.View.CountText.Text);

        screen.Source.MoveTo(NineSessions[0], ShutdownProgressState.ShutDown);
        screen.Source.MoveTo(NineSessions[1], ShutdownProgressState.ShutDown);
        screen.Source.MoveTo(NineSessions[2], ShutdownProgressState.ShutDown);
        screen.Source.MoveTo(NineSessions[3], ShutdownProgressState.EndedAtLimit);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("4 of 9 shut down", screen.View.CountText.Text);
        Assert.True(screen.View.ButtonPanel.IsVisible);

        foreach (var name in NineSessions)
            screen.Source.MoveTo(name, ShutdownProgressState.ShutDown);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("9 of 9 shut down", screen.View.CountText.Text);
    }

    [AvaloniaFact]
    public void Finished_EverySessionGone_CountCompleteAndButtonsGone()
    {
        var screen = Open(ShutdownProgressKind.Smart, "one", "two");

        screen.Source.MoveTo("one", ShutdownProgressState.ShutDown);
        screen.Source.MoveTo("two", ShutdownProgressState.EndedAtLimit);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("2 of 2 shut down", screen.View.CountText.Text);
        Assert.Equal("Every session is shut down.", screen.View.StatusText.Text);
        Assert.False(screen.View.ButtonPanel.IsVisible);
        Assert.False(screen.View.BtnShutDownNow.IsEffectivelyVisible);
        Assert.False(screen.View.BtnCancelAndKeepWorking.IsEffectivelyVisible);
        Assert.False(screen.View.TimeLeftText.IsVisible);
    }

    [AvaloniaFact]
    public void TimeLeft_AsTheClockMoves_CountsDownAndNeverGoesBelowZero()
    {
        var screen = Open(ShutdownProgressKind.Smart, NineSessions);
        Assert.True(screen.View.TimeLeftText.IsVisible);
        Assert.Equal("Time left: 10:00", screen.View.TimeLeftText.Text);

        screen.Clock.Advance(TimeSpan.FromSeconds(200));
        screen.View.ViewModel.RefreshTimeLeft();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Time left: 6:40", screen.View.TimeLeftText.Text);

        screen.Clock.Advance(TimeSpan.FromSeconds(395.5));
        screen.View.ViewModel.RefreshTimeLeft();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Time left: 0:05", screen.View.TimeLeftText.Text);

        screen.Clock.Advance(TimeSpan.FromHours(3));
        screen.View.ViewModel.RefreshTimeLeft();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Time left: 0:00", screen.View.TimeLeftText.Text);
    }

    /// <summary>A change in the shutdown re-reads the clock too, so the time never waits for the next tick.</summary>
    [AvaloniaFact]
    public void TimeLeft_WhenTheShutdownReportsAChange_IsReadAgain()
    {
        var screen = Open(ShutdownProgressKind.Smart, NineSessions);

        screen.Clock.Advance(TimeSpan.FromMinutes(7));
        screen.Source.MoveTo(NineSessions[0], ShutdownProgressState.Interrupted);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Time left: 3:00", screen.View.TimeLeftText.Text);
    }

    [AvaloniaFact]
    public void ShutDownNow_Clicked_AsksOnceDisablesBothAndSaysSo()
    {
        var screen = Open(ShutdownProgressKind.Smart, NineSessions);
        Assert.True(screen.View.BtnShutDownNow.IsEnabled);
        Assert.True(screen.View.BtnCancelAndKeepWorking.IsEnabled);

        Click(screen, screen.View.BtnShutDownNow);

        Assert.Equal(1, screen.Source.ShutDownNowRequests);
        Assert.False(screen.View.BtnShutDownNow.IsEnabled);
        Assert.False(screen.View.BtnCancelAndKeepWorking.IsEnabled);
        Assert.Equal("Shutting down now...", screen.View.StatusText.Text);

        // A second click on either button does nothing.
        Click(screen, screen.View.BtnShutDownNow);
        Click(screen, screen.View.BtnCancelAndKeepWorking);
        Assert.Equal(1, screen.Source.ShutDownNowRequests);
        Assert.Equal(0, screen.Source.CancelRequests);
    }

    [AvaloniaFact]
    public void CancelAndKeepWorking_Clicked_AsksOnceDisablesBothAndSaysSo()
    {
        var screen = Open(ShutdownProgressKind.Smart, NineSessions);

        Click(screen, screen.View.BtnCancelAndKeepWorking);

        Assert.Equal(1, screen.Source.CancelRequests);
        Assert.False(screen.View.BtnShutDownNow.IsEnabled);
        Assert.False(screen.View.BtnCancelAndKeepWorking.IsEnabled);
        Assert.Equal("Cancelling - bringing your sessions back...", screen.View.StatusText.Text);

        Click(screen, screen.View.BtnCancelAndKeepWorking);
        Click(screen, screen.View.BtnShutDownNow);
        Assert.Equal(1, screen.Source.CancelRequests);
        Assert.Equal(0, screen.Source.ShutDownNowRequests);
    }

    /// <summary>
    /// Cancelled with every session already gone: the sessions are on their way back, so the screen must
    /// not claim the shutdown finished.
    /// </summary>
    [AvaloniaFact]
    public void CancelAndKeepWorking_ThenEverySessionGone_DoesNotClaimFinished()
    {
        var screen = Open(ShutdownProgressKind.Smart, "one");

        Click(screen, screen.View.BtnCancelAndKeepWorking);
        screen.Source.MoveTo("one", ShutdownProgressState.ShutDown);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Cancelling - bringing your sessions back...", screen.View.StatusText.Text);
        Assert.False(screen.View.ViewModel.IsFinished);
    }

    /// <summary>
    /// The failure case: the shutdown refuses the request. The click must not look lost and must not
    /// claim a shutdown that never started; the buttons stay live so the owner can try again.
    /// </summary>
    [AvaloniaFact]
    public void ShutDownNow_TheShutdownThrows_SaysItFailedAndLeavesTheButtonsLive()
    {
        var screen = Open(ShutdownProgressKind.Smart, NineSessions);
        screen.Source.RequestFailure = new InvalidOperationException("the engine is gone");

        Click(screen, screen.View.BtnShutDownNow);

        Assert.Equal(0, screen.Source.ShutDownNowRequests);
        Assert.Equal(
            "Shut down now could not be started. The reason is in the Director log.",
            screen.View.StatusText.Text);
        Assert.True(screen.View.BtnShutDownNow.IsEnabled);
        Assert.True(screen.View.BtnCancelAndKeepWorking.IsEnabled);

        screen.Source.RequestFailure = null;
        Click(screen, screen.View.BtnShutDownNow);
        Assert.Equal(1, screen.Source.ShutDownNowRequests);
        Assert.Equal("Shutting down now...", screen.View.StatusText.Text);
    }

    [AvaloniaFact]
    public void IgnoreAllKind_Opened_ShowsNoTimeLeftAndNoCancelButton()
    {
        var screen = Open(ShutdownProgressKind.IgnoreAll, NineSessions);

        screen.Source.MoveTo(NineSessions[0], ShutdownProgressState.ShutDown);
        Dispatcher.UIThread.RunJobs();

        Assert.False(screen.View.TimeLeftText.IsVisible);
        Assert.False(screen.View.BtnCancelAndKeepWorking.IsVisible);
        Assert.True(screen.View.BtnShutDownNow.IsEffectivelyVisible);
        Assert.Equal("1 of 9 shut down", screen.View.CountText.Text);
        Assert.Equal(9, DrawnRows(screen.View).Count);
        Assert.Equal(
            "Every session is being shut down at once. No handovers are written.",
            screen.View.StatusText.Text);
    }

    /// <summary>
    /// The engine reports from its own threads. Both a state change and a NEW row are raised from a
    /// background thread here: the new row changes the bound collection, which throws on the wrong
    /// thread, and that throw would surface through the awaited task.
    /// </summary>
    [AvaloniaFact]
    public async Task Changed_RaisedFromABackgroundThread_LandsInTheRowsWithoutThrowing()
    {
        var screen = Open(ShutdownProgressKind.Smart, "one", "two");

        await Task.Run(() =>
        {
            screen.Source.MoveTo("one", ShutdownProgressState.ShutDown);
            screen.Source.Add("three", ShutdownProgressState.Writing);
        });
        Dispatcher.UIThread.RunJobs();

        var rows = DrawnRows(screen.View);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "one", "shut down" }, rows[0]);
        Assert.Equal(new[] { "three", "writing" }, rows[2]);
        Assert.Equal("1 of 3 shut down", screen.View.CountText.Text);
    }
}
