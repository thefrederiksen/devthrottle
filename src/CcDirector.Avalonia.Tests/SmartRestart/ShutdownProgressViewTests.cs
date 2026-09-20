using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Avalonia.SmartRestart;
using CcDirector.ControlApi.SmartRestart;
using Xunit;
using static CcDirector.ControlApi.SmartRestart.SmartShutdownPhase;
using static CcDirector.ControlApi.SmartRestart.SmartShutdownSessionState;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// The smart shutdown progress screen, opened for real inside a window and driven the way the owner and
/// the engine drive it: a fake of the REAL run interface is stepped by hand, the buttons are clicked with
/// the mouse, and what is asserted is what the screen DREW - the text and state of the controls in the
/// window - not a view model a test filled in.
///
/// The words in every snapshot here are deliberately NOT the words a screen would choose ("the engine
/// says ..."), so a label that appears on screen can only have come from the snapshot.
///
/// A mouse click is used instead of raising the click event, because a raised event reaches a disabled
/// button and a real click does not.
///
/// What these do NOT cover: the one-second clock firing by itself (no test sleeps; the tests call the
/// same method the clock calls), the real engine, and the screen sitting inside the main window.
/// </summary>
public class ShutdownProgressViewTests
{
    internal sealed record Screen(
        Window Window,
        ShutdownProgressView View,
        FakeSmartShutdownRun Run,
        FakeShutdownClock Clock);

    internal static Screen Open(SmartShutdownSnapshot first)
    {
        var clock = new FakeShutdownClock(new DateTimeOffset(Shutdown.StartedUtc));
        var run = new FakeSmartShutdownRun(first);
        var view = new ShutdownProgressView(run, clock);
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Screen(window, view, run, clock);
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

    /// <summary>The visible text of each row as drawn: name, state, and the detail when there is one.</summary>
    private static List<string[]> DrawnRows(ShutdownProgressView view) =>
        view.RowList.GetRealizedContainers()
            .Select(container => container.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text => text.IsVisible)
                .Select(text => text.Text ?? "")
                .ToArray())
            .ToList();

    private static SmartShutdownSnapshot Collecting(params SmartShutdownSessionProgress[] rows) =>
        Shutdown.Snapshot(SmartShutdownPhase.Collecting, "the engine says: collecting", "the engine says: some of them", rows);

    /// <summary>
    /// The law of this phase: the view is opened. Every control the markup names must be connected,
    /// which is exactly what a hand-written InitializeComponent breaks - it loads the markup and leaves
    /// each named field null.
    /// </summary>
    [AvaloniaFact]
    public void Show_InsideAWindow_ConnectsEveryNamedControl()
    {
        var screen = Open(Collecting(Shutdown.Row("one", Asked, "asked")));

        Assert.NotNull(screen.View.CountText);
        Assert.NotNull(screen.View.TimeLeftText);
        Assert.NotNull(screen.View.PhaseText);
        Assert.NotNull(screen.View.NoteText);
        Assert.NotNull(screen.View.ResultText);
        Assert.NotNull(screen.View.RowList);
        Assert.NotNull(screen.View.ButtonPanel);
        Assert.NotNull(screen.View.BtnShutDownNow);
        Assert.NotNull(screen.View.BtnCancelAndKeepWorking);
        Assert.True(screen.View.IsEffectivelyVisible);
    }

    /// <summary>
    /// The count label here DISAGREES with the rows on purpose (one row, "7 of 3"): a screen that counted
    /// for itself would draw something else.
    /// </summary>
    [AvaloniaFact]
    public void Snapshot_TheEnginesLabels_AppearWordForWord()
    {
        var screen = Open(Shutdown.Snapshot(
            SmartShutdownPhase.Collecting,
            "the engine says: waiting on the handovers",
            "the engine says: 7 of 3 are away",
            new[] { Shutdown.Row("one", Writing, "the engine says: scribbling", "the engine says: page two") },
            note: "the engine says: one just finished"));

        Assert.Equal("the engine says: 7 of 3 are away", screen.View.CountText.Text);
        Assert.Equal("the engine says: waiting on the handovers", screen.View.PhaseText.Text);
        Assert.True(screen.View.NoteText.IsVisible);
        Assert.Equal("the engine says: one just finished", screen.View.NoteText.Text);
        Assert.Equal(
            new[] { "one", "the engine says: scribbling", "the engine says: page two" },
            DrawnRows(screen.View)[0]);
        Assert.False(screen.View.ResultText.IsVisible);
    }

    /// <summary>
    /// Replace, never merge. The second snapshot drops a row, reorders the rest, renames one, clears a
    /// detail and clears the note; nothing of the first may still be on screen.
    /// </summary>
    [AvaloniaFact]
    public void Snapshot_ASecondOne_ReplacesEverythingTheFirstShowed()
    {
        var screen = Open(Shutdown.Snapshot(
            SmartShutdownPhase.Asking, "first phase", "first count",
            new[]
            {
                Shutdown.Row("one", Asked, "first-one"),
                Shutdown.Row("two", NotDelivered, "first-two", "first reason"),
                Shutdown.Row("three", Writing, "first-three"),
            },
            note: "first note"));

        var renamed = Shutdown.Row("three", ShutDown, "second-three") with { Name = "three, renamed" };
        screen.Run.Push(Shutdown.Snapshot(
            SmartShutdownPhase.Collecting, "second phase", "second count",
            new[] { renamed, Shutdown.Row("two", Asked, "second-two") }));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("second count", screen.View.CountText.Text);
        Assert.Equal("second phase", screen.View.PhaseText.Text);
        Assert.False(screen.View.NoteText.IsVisible);
        var rows = DrawnRows(screen.View);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "three, renamed", "second-three" }, rows[0]);
        Assert.Equal(new[] { "two", "second-two" }, rows[1]);

        var everythingDrawn = screen.Window.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? "").ToList();
        Assert.DoesNotContain(everythingDrawn, text => text.Contains("first", StringComparison.Ordinal));
        Assert.Contains("second count", everythingDrawn);
    }

    [AvaloniaFact]
    public void Rows_AllTenStates_DrawTheEnginesWordsEachWithAColour()
    {
        var states = Enum.GetValues<SmartShutdownSessionState>();
        Assert.Equal(10, states.Length);
        var screen = Open(Collecting(states
            .Select(state => Shutdown.Row("session " + state, state, "label for " + state))
            .ToArray()));

        var containers = screen.View.RowList.GetRealizedContainers().ToList();
        Assert.Equal(10, containers.Count);
        for (var i = 0; i < states.Length; i++)
        {
            var texts = containers[i].GetVisualDescendants().OfType<TextBlock>().ToList();
            var stateText = Assert.Single(texts, text => text.Text == "label for " + states[i]);
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(stateText.Foreground);
            Assert.Equal(255, brush.Color.A);
        }

        // NotDelivered came with no detail, which the screen says in words that are true.
        Assert.Equal(
            new[] { "session NotDelivered", "label for NotDelivered", "No reason was given." },
            DrawnRows(screen.View)[Array.IndexOf(states, NotDelivered)]);
    }

    /// <summary>A value the screen was never taught is refused, never drawn as something plausible.</summary>
    [AvaloniaFact]
    public void UnknownValues_AStateOrAPhase_Throw()
    {
        var unknownState = Shutdown.Row("one", (SmartShutdownSessionState)99, "a state from the future");
        var state = Assert.Throws<InvalidOperationException>(() => new ShutdownProgressRowViewModel(unknownState));
        Assert.Contains("99", state.Message);

        var unknownPhase = Shutdown.Snapshot(
            (SmartShutdownPhase)99, "a phase from the future", "0 of 0", Array.Empty<SmartShutdownSessionProgress>());
        var clock = new FakeShutdownClock(new DateTimeOffset(Shutdown.StartedUtc));
        var phase = Assert.Throws<InvalidOperationException>(
            () => new ShutdownProgressViewModel(new FakeSmartShutdownRun(unknownPhase), clock));
        Assert.Contains("99", phase.Message);
    }

    [AvaloniaFact]
    public void Rows_ASessionUnderALead_IsDrawnBeneathItAndIndented()
    {
        var screen = Open(Collecting(
            Shutdown.Row("lead", Asked, "asked"),
            Shutdown.Row("under the lead", Pending, "waiting for its lead", under: "lead"),
            Shutdown.Row("standalone", Asked, "asked")));

        Assert.Equal(
            new[] { "lead", "under the lead", "standalone" },
            DrawnRows(screen.View).Select(row => row[0]).ToArray());

        double LeftEdgeOf(string name)
        {
            var text = screen.View.RowList.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == name);
            var origin = text.TranslatePoint(new Point(0, 0), screen.Window);
            Assert.NotNull(origin);
            return origin.Value.X;
        }

        Assert.Equal(LeftEdgeOf("lead"), LeftEdgeOf("standalone"));
        Assert.True(LeftEdgeOf("under the lead") >= LeftEdgeOf("lead") + 20,
            "the session under a lead must start well to the right of its lead");
    }

    /// <summary>
    /// Review finding 2a. At the limit a cancel means nothing, the engine says so, and the button is dead:
    /// a real click sends nothing and the screen says nothing of its own.
    /// </summary>
    [AvaloniaFact]
    public void Buttons_TheEngineSaysCancelMayNotBeUsed_CancelIsDeadAndAClickCallsNothing()
    {
        var screen = Open(Shutdown.Snapshot(
            EndingAtLimit, "the engine says: ending what is left", "2 of 3",
            new[] { Shutdown.Row("one", Interrupted, "interrupted") },
            canShutDownNow: false, canCancel: false));

        Assert.True(screen.View.BtnCancelAndKeepWorking.IsEffectivelyVisible);
        Assert.False(screen.View.BtnCancelAndKeepWorking.IsEnabled);
        Assert.False(screen.View.BtnShutDownNow.IsEnabled);

        Click(screen, screen.View.BtnCancelAndKeepWorking);
        Click(screen, screen.View.BtnShutDownNow);

        Assert.Equal(0, screen.Run.CancelCalls);
        Assert.Equal(0, screen.Run.ShutDownNowCalls);
        Assert.Equal("the engine says: ending what is left", screen.View.PhaseText.Text);
    }

    [AvaloniaFact]
    public void Buttons_EachFollowsItsOwnPermission()
    {
        var screen = Open(Shutdown.Snapshot(
            SmartShutdownPhase.Collecting, "collecting", "0 of 1", new[] { Shutdown.Row("one", Asked, "asked") },
            canShutDownNow: true, canCancel: false));
        Assert.True(screen.View.BtnShutDownNow.IsEnabled);
        Assert.False(screen.View.BtnCancelAndKeepWorking.IsEnabled);

        screen.Run.Push(screen.Run.Current with { CanShutDownNow = false, CanCancel = true });
        Dispatcher.UIThread.RunJobs();
        Assert.False(screen.View.BtnShutDownNow.IsEnabled);
        Assert.True(screen.View.BtnCancelAndKeepWorking.IsEnabled);
    }

    /// <summary>
    /// One press, one call, and the button is dead BEFORE the engine answers: the fake stays silent, so
    /// nothing but the screen itself can have killed the button. The words on screen do not move,
    /// because "shutting down now" is the engine's to say. A later snapshot cannot bring a used button back.
    /// </summary>
    [AvaloniaFact]
    public void ShutDownNow_Pressed_CallsOnceAndIsDeadBeforeTheNextSnapshot()
    {
        var screen = Open(Collecting(Shutdown.Row("one", Asked, "asked")));
        screen.Run.StaysSilentWhenPressed = true;
        Assert.True(screen.View.BtnShutDownNow.IsEnabled);

        Click(screen, screen.View.BtnShutDownNow);

        Assert.Equal(1, screen.Run.ShutDownNowCalls);
        Assert.False(screen.View.BtnShutDownNow.IsEnabled);
        Assert.Equal("the engine says: collecting", screen.View.PhaseText.Text);

        Click(screen, screen.View.BtnShutDownNow);
        Assert.Equal(1, screen.Run.ShutDownNowCalls);

        screen.Run.Push(screen.Run.Current with { CanShutDownNow = true });
        Dispatcher.UIThread.RunJobs();
        Assert.False(screen.View.BtnShutDownNow.IsEnabled);
        Click(screen, screen.View.BtnShutDownNow);
        Assert.Equal(1, screen.Run.ShutDownNowCalls);
        Assert.Equal(0, screen.Run.CancelCalls);
    }

    [AvaloniaFact]
    public void CancelAndKeepWorking_Pressed_CallsOnceAndIsDeadBeforeTheNextSnapshot()
    {
        var screen = Open(Collecting(Shutdown.Row("one", Asked, "asked")));
        screen.Run.StaysSilentWhenPressed = true;

        Click(screen, screen.View.BtnCancelAndKeepWorking);

        Assert.Equal(1, screen.Run.CancelCalls);
        Assert.False(screen.View.BtnCancelAndKeepWorking.IsEnabled);
        Assert.Equal("the engine says: collecting", screen.View.PhaseText.Text);

        Click(screen, screen.View.BtnCancelAndKeepWorking);
        screen.Run.Push(screen.Run.Current with { CanCancel = true });
        Dispatcher.UIThread.RunJobs();
        Click(screen, screen.View.BtnCancelAndKeepWorking);

        Assert.Equal(1, screen.Run.CancelCalls);
        Assert.Equal(0, screen.Run.ShutDownNowCalls);
    }

    /// <summary>The fake answers a press the way the engine does: both permissions go, and both buttons follow.</summary>
    [AvaloniaFact]
    public void ShutDownNow_TheEngineAnswers_BothButtonsGoDeadAndTheEnginesWordsShow()
    {
        var screen = Open(Collecting(Shutdown.Row("one", Asked, "asked")));

        Click(screen, screen.View.BtnShutDownNow);
        screen.Run.Push(screen.Run.Current with { Phase = EndingAtLimit, PhaseLabel = "the engine says: ending everything now" });
        Dispatcher.UIThread.RunJobs();

        Assert.False(screen.View.BtnShutDownNow.IsEnabled);
        Assert.False(screen.View.BtnCancelAndKeepWorking.IsEnabled);
        Assert.Equal("the engine says: ending everything now", screen.View.PhaseText.Text);
        Click(screen, screen.View.BtnCancelAndKeepWorking);
        Assert.Equal(0, screen.Run.CancelCalls);
    }

    [AvaloniaFact]
    public void TimeLeft_AgainstTheEnginesLimit_CountsDownAndNeverGoesBelowZero()
    {
        var screen = Open(Collecting(Shutdown.Row("one", Asked, "asked")));
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

    /// <summary>The limit is the ENGINE's: a snapshot that moves it moves the time left, read again at once.</summary>
    [AvaloniaFact]
    public void TimeLeft_ASnapshotWithAnotherLimit_IsReadAgainstTheNewLimit()
    {
        var screen = Open(Collecting(Shutdown.Row("one", Asked, "asked")));

        screen.Clock.Advance(TimeSpan.FromMinutes(7));
        screen.Run.Push(screen.Run.Current with { LimitUtc = Shutdown.StartedUtc.AddMinutes(15) });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Time left: 8:00", screen.View.TimeLeftText.Text);
    }

    [AvaloniaFact]
    public void TimeLeft_ByPhase_ShownOnlyWhileSessionsAreBeingGivenTime()
    {
        var phases = Enum.GetValues<SmartShutdownPhase>();
        Assert.Equal(8, phases.Length);
        var shown = new List<SmartShutdownPhase>();
        foreach (var phase in phases)
        {
            var screen = Open(Shutdown.Snapshot(phase, "phase " + phase, "0 of 1", new[] { Shutdown.Row("one", Asked, "asked") }));
            if (screen.View.TimeLeftText.IsVisible)
                shown.Add(phase);
            screen.Window.Close();
        }

        Assert.Equal(new[] { Asking, SmartShutdownPhase.Collecting, Interrupting }, shown);
    }

    [AvaloniaFact]
    public void Completion_EachOfTheSixOutcomes_ShowsItsDetailAndRaisesFinishedOnce()
    {
        var outcomes = Enum.GetValues<SmartShutdownOutcome>();
        Assert.Equal(6, outcomes.Length);
        foreach (var outcome in outcomes)
        {
            var screen = Open(Collecting(Shutdown.Row("one", Asked, "asked")));
            var raised = new List<SmartShutdownResult>();
            screen.View.ViewModel.Finished += raised.Add;
            Assert.True(screen.View.ButtonPanel.IsVisible);

            var result = screen.Run.Complete(outcome, "the engine says: it ended as " + outcome);
            Dispatcher.UIThread.RunJobs();

            Assert.True(screen.View.ResultText.IsVisible);
            Assert.Equal("the engine says: it ended as " + outcome, screen.View.ResultText.Text);
            Assert.Same(result, Assert.Single(raised));
            Assert.False(screen.View.ButtonPanel.IsVisible);
            Assert.False(screen.View.TimeLeftText.IsVisible);
            Assert.True(screen.View.IsEffectivelyVisible, "the screen itself closes nothing");
            Assert.True(screen.Window.IsVisible, "the screen itself closes nothing");

            Dispatcher.UIThread.RunJobs();
            Assert.Single(raised);
            screen.Window.Close();
        }
    }

    /// <summary>
    /// Review finding 2d. A failed run stops with sessions still open. The owner is told why in the
    /// engine's words, no button is left live to press into a dead run, the screen lets go of the run,
    /// and a straggling snapshot cannot put anything back.
    /// </summary>
    [AvaloniaFact]
    public void Completion_AFailedRun_LeavesNoLiveButtonAndLetsGoOfTheRun()
    {
        var screen = Open(Collecting(Shutdown.Row("one", Writing, "writing")));
        Assert.Equal(1, screen.Run.Subscribers);

        screen.Run.Complete(SmartShutdownOutcome.Failed, "the engine says: the Gateway went away");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("the engine says: the Gateway went away", screen.View.ResultText.Text);
        Assert.False(screen.View.BtnShutDownNow.IsEffectivelyVisible);
        Assert.False(screen.View.BtnCancelAndKeepWorking.IsEffectivelyVisible);
        Assert.False(screen.View.BtnShutDownNow.IsEnabled);
        Assert.False(screen.View.BtnCancelAndKeepWorking.IsEnabled);
        Assert.Equal(0, screen.Run.Subscribers);
        Assert.False(screen.View.ViewModel.IsClockRunning);

        screen.View.ViewModel.PressShutDownNow();
        screen.View.ViewModel.PressCancelAndKeepWorking();
        Assert.Equal(0, screen.Run.ShutDownNowCalls);
        Assert.Equal(0, screen.Run.CancelCalls);
    }

    /// <summary>
    /// The result carries the last snapshot. It is given here ONLY through the result, never pushed, so
    /// the screen can only be showing it because it read the result.
    /// </summary>
    [AvaloniaFact]
    public void Completion_TheResultsFinalSnapshot_IsWhatTheScreenEndsOn()
    {
        var screen = Open(Collecting(Shutdown.Row("one", Writing, "writing")));

        screen.Run.Complete(SmartShutdownOutcome.Emptied, "the engine says: empty", Shutdown.Snapshot(
            Finished, "the engine says: over", "1 of 1 shut down", new[] { Shutdown.Row("one", ShutDown, "shut down") },
            canShutDownNow: false, canCancel: false));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("1 of 1 shut down", screen.View.CountText.Text);
        Assert.Equal("the engine says: over", screen.View.PhaseText.Text);
        Assert.Equal("the engine says: empty", screen.View.ResultText.Text);
        Assert.Equal(new[] { "one", "shut down" }, DrawnRows(screen.View)[0]);
    }

    /// <summary>
    /// A run that never started (Refused) is over before the screen is built. A caller that subscribes
    /// in the same turn that built the screen still hears of it, once.
    /// </summary>
    [AvaloniaFact]
    public void Completion_ARunAlreadyOver_StillReachesACallerThatSubscribesAtOnce()
    {
        var run = new FakeSmartShutdownRun(Shutdown.Snapshot(
            Finished, "the engine says: never started", "0 of 0", Array.Empty<SmartShutdownSessionProgress>(),
            canShutDownNow: false, canCancel: false));
        run.Complete(SmartShutdownOutcome.Refused, "the engine says: the Gateway cannot be reached");

        var view = new ShutdownProgressView(run, new FakeShutdownClock(new DateTimeOffset(Shutdown.StartedUtc)));
        var raised = new List<SmartShutdownResult>();
        view.ViewModel.Finished += raised.Add;
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SmartShutdownOutcome.Refused, Assert.Single(raised).Outcome);
        Assert.Equal("the engine says: the Gateway cannot be reached", view.ResultText.Text);
        Assert.False(view.ButtonPanel.IsVisible);
    }

    /// <summary>
    /// The engine raises from its own threads. Avalonia does NOT throw when a bound collection changes
    /// on the wrong thread - it races the layout pass instead - so "it did not throw" proves nothing.
    /// What is asserted is a presence: every change to the bound rows and to the bound count was made ON
    /// the interface thread, and there was at least one of each.
    /// </summary>
    [AvaloniaFact]
    public async Task Changed_RaisedFromABackgroundThread_ChangesEverythingBoundOnTheInterfaceThread()
    {
        var screen = Open(Collecting(Shutdown.Row("one", Asked, "asked"), Shutdown.Row("two", Asked, "asked")));
        var rowChangesOnInterfaceThread = new List<bool>();
        var propertyChangesOnInterfaceThread = new Dictionary<string, List<bool>>();
        screen.View.ViewModel.Rows.CollectionChanged += (_, _) =>
        {
            lock (rowChangesOnInterfaceThread)
                rowChangesOnInterfaceThread.Add(Dispatcher.UIThread.CheckAccess());
        };
        screen.View.ViewModel.PropertyChanged += (_, e) =>
        {
            lock (propertyChangesOnInterfaceThread)
            {
                if (!propertyChangesOnInterfaceThread.TryGetValue(e.PropertyName ?? "", out var list))
                    propertyChangesOnInterfaceThread[e.PropertyName ?? ""] = list = new List<bool>();
                list.Add(Dispatcher.UIThread.CheckAccess());
            }
        };
        var finishedOnInterfaceThread = new List<bool>();
        screen.View.ViewModel.Finished += _ => finishedOnInterfaceThread.Add(Dispatcher.UIThread.CheckAccess());

        await Task.Run(() =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess(), "the change must really come from another thread");
            screen.Run.Push(Shutdown.Snapshot(
                Interrupting, "interrupting", "1 of 3 shut down",
                new[]
                {
                    Shutdown.Row("one", ShutDown, "shut down"),
                    Shutdown.Row("two", Asked, "asked"),
                    Shutdown.Row("three", Writing, "writing"),
                },
                canShutDownNow: false, note: "a note"));
            screen.Run.Complete(SmartShutdownOutcome.Emptied, "emptied");
        });
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(rowChangesOnInterfaceThread);
        Assert.All(rowChangesOnInterfaceThread, Assert.True);
        foreach (var name in new[]
                 {
                     nameof(ShutdownProgressViewModel.CountText), nameof(ShutdownProgressViewModel.PhaseText),
                     nameof(ShutdownProgressViewModel.NoteText), nameof(ShutdownProgressViewModel.CanShutDownNow),
                     nameof(ShutdownProgressViewModel.ResultText), nameof(ShutdownProgressViewModel.AreButtonsVisible),
                 })
        {
            Assert.True(propertyChangesOnInterfaceThread.ContainsKey(name), name + " never changed");
        }

        Assert.All(propertyChangesOnInterfaceThread.Values.SelectMany(list => list), Assert.True);
        Assert.Equal(new[] { true }, finishedOnInterfaceThread);

        var rows = DrawnRows(screen.View);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "one", "shut down" }, rows[0]);
        Assert.Equal(new[] { "three", "writing" }, rows[2]);
        Assert.Equal("1 of 3 shut down", screen.View.CountText.Text);
    }

    /// <summary>
    /// Review finding 1. Once the view is taken out of its window the screen has let go: the run has no
    /// subscriber, its clock is stopped, and a snapshot raised afterwards never reaches the screen, so
    /// nothing is posted to the interface thread and nothing shown changes. Each "nothing" is pinned to a
    /// presence first: one subscriber, a running clock, and a snapshot that DID arrive while attached.
    /// </summary>
    [AvaloniaFact]
    public async Task Detached_TakenOutOfItsWindow_LetsGoOfTheRunAndStopsItsClock()
    {
        var screen = Open(Collecting(Shutdown.Row("one", Asked, "asked")));
        var model = screen.View.ViewModel;
        Assert.Equal(1, screen.Run.Subscribers);
        Assert.True(model.IsClockRunning);
        await Task.Run(() => screen.Run.Push(screen.Run.Current with { CountLabel = "while attached" }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, model.SnapshotsReceived);
        Assert.Equal("while attached", model.CountText);

        screen.Window.Content = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, screen.Run.Subscribers);
        Assert.False(model.IsClockRunning);

        var finished = 0;
        model.Finished += _ => finished++;
        await Task.Run(() =>
        {
            screen.Run.Push(Shutdown.Snapshot(
                Cancelling, "after it was let go", "after it was let go",
                new[] { Shutdown.Row("someone else", KeptRunning, "kept running") }));
            screen.Run.Complete(SmartShutdownOutcome.Cancelled, "after it was let go");
        });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, model.SnapshotsReceived);
        Assert.Equal("while attached", model.CountText);
        Assert.Equal("the engine says: collecting", model.PhaseText);
        Assert.Equal("one", Assert.Single(model.Rows).Name);
        Assert.Equal("", model.ResultText);
        Assert.Equal(0, finished);
    }
}
