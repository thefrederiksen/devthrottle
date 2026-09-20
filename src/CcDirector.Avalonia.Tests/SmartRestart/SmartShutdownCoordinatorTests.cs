using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CcDirector.Avalonia.SmartRestart;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// The two doors and everything behind them, driven end to end WITHOUT the main window: a real owner
/// window, the real Smart shutdown dialog shown over it and pressed with real clicks and keys, the real
/// progress screen, and a fake engine stepped by hand. No test builds a choice or a result and hands it
/// to the coordinator; every choice comes out of the real dialog and every outcome out of the run.
///
/// WHAT THESE DO NOT COVER. The real main window is never opened: that its File menu item and its
/// OnClosing call this coordinator is read from its source by one test here, not run. The real engine
/// is never called. The operating system shutting down is a close reason handed in by the test.
/// </summary>
public class SmartShutdownCoordinatorTests
{
    // ===== No sessions running: no dialog at all =====

    /// <summary>With nothing running the close is not cancelled, no dialog opens, the engine is never asked.</summary>
    [AvaloniaFact]
    public void HandleWindowClosing_NoSessionsRunning_OpensNoDialogAndTheCloseCarriesOn()
    {
        var rig = new CoordinatorRig();

        var cancelled = rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        Assert.False(cancelled);
        Assert.Equal(0, rig.DialogsOpened);
        Assert.Empty(rig.Engine.Checks);
        Assert.True(rig.SessionViewIsShown);
    }

    /// <summary>A session whose process has exited is not running: the same list that would count it says none.</summary>
    [AvaloniaFact]
    public void HandleWindowClosing_OnlyAnExitedSession_OpensNoDialogAndTheCloseCarriesOn()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Exited, "gone");

        var cancelled = rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        Assert.False(cancelled);
        Assert.Equal(0, rig.DialogsOpened);
    }

    /// <summary>
    /// THE NARROWER-RULE WARNING FROM THE REVIEW. The old close hook asked only about sessions working
    /// or waiting for input, so a Director whose one session was idle closed without a word. One idle
    /// session is enough: the close is cancelled and the dialog shows that one session.
    /// </summary>
    [AvaloniaFact]
    public void HandleWindowClosing_OneIdleSession_CancelsTheCloseAndTheDialogCountsThatSession()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Idle, "idle one");

        var cancelled = rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        Assert.True(cancelled);
        Assert.Equal(1, rig.DialogsOpened);
        Assert.True(rig.Dialog!.IsVisible);
        Assert.Equal("1 session is running", rig.Dialog.TxtSessionCount.Text);
        Assert.Equal("0 working, 1 waiting", rig.Dialog.TxtWorkingWaiting.Text);
    }

    /// <summary>The File menu with nothing running says so in one sentence and asks the engine nothing.</summary>
    [AvaloniaFact]
    public void OpenFromFileMenuAsync_NoSessionsRunning_OpensNoDialogAndSaysThereIsNothingToShutDown()
    {
        var rig = new CoordinatorRig();

        _ = rig.Coordinator.OpenFromFileMenuAsync();
        CoordinatorRig.Settle();

        Assert.Equal(0, rig.DialogsOpened);
        Assert.Empty(rig.Engine.Checks);
        Assert.Empty(rig.Engine.Starts);
        var message = Assert.Single(rig.Messages);
        Assert.Contains("there are no sessions to shut down", message);
    }

    // ===== Each door: its own words, its own purpose =====

    [AvaloniaFact]
    public void HandleWindowClosing_SessionsRunning_OpensTheDialogAsSmartShutdownAndAsksTheEngineForClose()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");
        rig.AddSession(ActivityState.WaitingForInput, "reviewer");

        rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        Assert.Equal("Smart shutdown", rig.Dialog!.Title);
        Assert.Equal("Smart shutdown", rig.Dialog.BtnSmart.Content);
        Assert.Equal("2 sessions are running", rig.Dialog.TxtSessionCount.Text);
        Assert.Equal(new[] { SmartShutdownPurpose.Close }, rig.Engine.Checks);
    }

    [AvaloniaFact]
    public void OpenFromFileMenuAsync_SessionsRunning_OpensTheDialogAsSmartRestartAndAsksTheEngineForRestart()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");

        _ = rig.Coordinator.OpenFromFileMenuAsync();
        CoordinatorRig.Settle();

        Assert.Equal("Smart Restart", rig.Dialog!.Title);
        Assert.Equal("Smart Restart", rig.Dialog.BtnSmart.Content);
        Assert.Equal(new[] { SmartShutdownPurpose.Restart }, rig.Engine.Checks);
    }

    // ===== Cancel, Escape, the dialog's own close: nothing happens =====

    [AvaloniaFact]
    public void Dialog_CancelClicked_TheEngineIsNeverStartedAndNothingIsClosed()
    {
        var rig = OpenFromTheWindowClose();

        CoordinatorRig.Click(rig.Dialog!.BtnCancel);

        AssertNothingHappened(rig);
    }

    [AvaloniaFact]
    public void Dialog_EscapePressed_TheEngineIsNeverStartedAndNothingIsClosed()
    {
        var rig = OpenFromTheWindowClose();

        rig.Dialog!.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        CoordinatorRig.Settle();

        AssertNothingHappened(rig);
    }

    /// <summary>There is no window frame to press under the headless platform; Close() is what its X does.</summary>
    [AvaloniaFact]
    public void Dialog_ClosedByItsOwnClose_TheEngineIsNeverStartedAndNothingIsClosed()
    {
        var rig = OpenFromTheWindowClose();

        rig.Dialog!.Close();
        CoordinatorRig.Settle();

        AssertNothingHappened(rig);
    }

    /// <summary>After a cancel the doors work again: the next close opens a second dialog.</summary>
    [AvaloniaFact]
    public void HandleWindowClosing_AfterACancel_OpensTheDialogAgain()
    {
        var rig = OpenFromTheWindowClose();
        CoordinatorRig.Click(rig.Dialog!.BtnCancel);

        var cancelled = rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        Assert.True(cancelled);
        Assert.Equal(2, rig.DialogsOpened);
    }

    // ===== The smart choice =====

    /// <summary>Thirty minutes picked in the real dropdown reaches Start, with the door's purpose, and
    /// the progress screen is what stands in place of the session view.</summary>
    [AvaloniaFact]
    public void Dialog_SmartShutdownClickedWithThirtyMinutes_StartGetsCloseAndThirtyMinutesAndTheProgressScreenIsShown()
    {
        var rig = OpenFromTheWindowClose();
        rig.Dialog!.CmbTimeAllowed.SelectedIndex = 3;
        CoordinatorRig.Settle();

        CoordinatorRig.Click(rig.Dialog.BtnSmart);

        var request = Assert.Single(rig.Engine.Starts);
        Assert.Equal(SmartShutdownPurpose.Close, request.Purpose);
        Assert.Equal(TimeSpan.FromMinutes(30), request.TimeAllowed);
        Assert.True(rig.Coordinator.Flow.IsCompletedSuccessfully);
        Assert.False(rig.SessionView.IsVisible);
        var progress = rig.Surface!.Progress;
        Assert.NotNull(progress);
        Assert.True(progress!.IsEffectivelyVisible);
        Assert.Equal("0 of 1 shut down", progress.CountText.Text);
        Assert.False(rig.Surface.BackPanel.IsVisible);
        Assert.Equal(0, rig.ApplicationCloses);
    }

    [AvaloniaFact]
    public void Dialog_SmartRestartClickedFromTheFileMenu_StartGetsRestartAndTheDefaultTenMinutes()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");
        _ = rig.Coordinator.OpenFromFileMenuAsync();
        CoordinatorRig.Settle();
        rig.EngineSaysItMay();

        CoordinatorRig.Click(rig.Dialog!.BtnSmart);

        var request = Assert.Single(rig.Engine.Starts);
        Assert.Equal(SmartShutdownPurpose.Restart, request.Purpose);
        Assert.Equal(TimeSpan.FromMinutes(10), request.TimeAllowed);
        Assert.NotNull(rig.Surface!.Progress);
    }

    /// <summary>A run already under way: Start throws, its message is shown, and nothing has changed.</summary>
    [AvaloniaFact]
    public void Dialog_SmartShutdownClickedWhileTheEngineAlreadyHasARun_ShowsTheEnginesMessageAndChangesNothing()
    {
        var rig = OpenFromTheWindowClose();
        rig.Engine.StartThrows = new InvalidOperationException("A smart shutdown is already under way on this Director.");

        CoordinatorRig.Click(rig.Dialog!.BtnSmart);

        Assert.Equal("A smart shutdown is already under way on this Director.", Assert.Single(rig.Messages));
        Assert.True(rig.SessionViewIsShown);
        Assert.Equal(0, rig.ApplicationCloses);
        // Nothing is left half open: the next close asks again.
        Assert.True(rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing));
        CoordinatorRig.Settle();
        Assert.Equal(2, rig.DialogsOpened);
    }

    // ===== The six outcomes =====

    [AvaloniaFact]
    public void RunFinished_Emptied_ClosesTheApplicationExactlyOnceAndThatCloseIsNotAskedAbout()
    {
        var rig = RunUnderWay();

        rig.Engine.Run.Complete(SmartShutdownOutcome.Emptied, "Every session is shut down.");
        CoordinatorRig.Settle();

        Assert.Equal(1, rig.ApplicationCloses);
        // The close the flow asked for comes back through the window's closing: it carries on, no dialog.
        Assert.False(rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing));
        CoordinatorRig.Settle();
        Assert.Equal(1, rig.DialogsOpened);
        Assert.Equal(1, rig.ApplicationCloses);
    }

    [AvaloniaFact]
    public void RunFinished_RestartAccepted_DoesNothing()
    {
        var rig = RunUnderWay();

        rig.Engine.Run.Complete(SmartShutdownOutcome.RestartAccepted, "The launcher accepted.");
        CoordinatorRig.Settle();

        Assert.Equal(0, rig.ApplicationCloses);
        Assert.NotNull(rig.Surface);
        Assert.False(rig.Surface!.BackPanel.IsVisible);
        Assert.Empty(rig.Messages);
    }

    [AvaloniaFact]
    public void RunFinished_Cancelled_PutsTheSessionViewBackAtOnce()
    {
        var rig = RunUnderWay();

        rig.Engine.Run.Complete(SmartShutdownOutcome.Cancelled, "The Director holds the same missions.");
        CoordinatorRig.Settle();

        Assert.True(rig.SessionViewIsShown);
        Assert.Equal(0, rig.ApplicationCloses);
    }

    /// <summary>
    /// The three bad ends. The engine's reason is drawn, as given, on the progress screen, and the
    /// session view does NOT come back until the owner presses the real "Back to your sessions".
    /// </summary>
    [AvaloniaTheory]
    [InlineData(SmartShutdownOutcome.Refused, "the Gateway could not be reached (timeout). Nothing has been touched.")]
    [InlineData(SmartShutdownOutcome.RestartRefused, "this Director is a development slot; its launcher would not restart it.")]
    [InlineData(SmartShutdownOutcome.Failed, "The smart shutdown stopped on an error: disk full.")]
    public void RunFinished_EndedBadly_TheReasonStaysReadableUntilTheOwnerAsksForTheSessionsBack(
        SmartShutdownOutcome outcome, string reason)
    {
        var rig = RunUnderWay();

        rig.Engine.Run.Complete(outcome, reason);
        CoordinatorRig.Settle();

        Assert.Equal(0, rig.ApplicationCloses);
        Assert.False(rig.SessionView.IsVisible);
        var surface = rig.Surface!;
        Assert.Equal(reason, surface.Progress!.ResultText.Text);
        Assert.True(surface.Progress.ResultText.IsEffectivelyVisible);
        Assert.True(surface.BackPanel.IsEffectivelyVisible);

        CoordinatorRig.Click(surface.BtnBackToSessions);

        Assert.True(rig.SessionViewIsShown);
        Assert.Equal(0, rig.ApplicationCloses);
    }

    // ===== While a run is under way =====

    [AvaloniaFact]
    public void HandleWindowClosing_WhileARunIsUnderWay_CancelsTheCloseAndOpensNothing()
    {
        var rig = RunUnderWay();

        var cancelled = rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        Assert.True(cancelled);
        Assert.Equal(1, rig.DialogsOpened);
        Assert.Single(rig.Engine.Starts);
        Assert.Equal(0, rig.ApplicationCloses);
        Assert.NotNull(rig.Surface!.Progress);
    }

    [AvaloniaFact]
    public void OpenFromFileMenuAsync_WhileARunIsUnderWay_OpensNothingAndClosesNothing()
    {
        var rig = RunUnderWay();

        _ = rig.Coordinator.OpenFromFileMenuAsync();
        CoordinatorRig.Settle();

        Assert.Equal(1, rig.DialogsOpened);
        Assert.Single(rig.Engine.Starts);
        Assert.Equal(0, rig.ApplicationCloses);
        Assert.NotNull(rig.Surface!.Progress);
    }

    [AvaloniaFact]
    public void HandleWindowClosing_WhileTheDialogIsOpen_CancelsTheCloseAndOpensNoSecondDialog()
    {
        var rig = OpenFromTheWindowClose();

        var cancelled = rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        Assert.True(cancelled);
        Assert.Equal(1, rig.DialogsOpened);
    }

    // ===== The engine's check, in the real dialog =====

    /// <summary>The dialog is open BEFORE the engine answers: it says it is checking, the confirm is
    /// dead, and Enter - which takes the default button - takes nothing.</summary>
    [AvaloniaFact]
    public void Dialog_WhileTheEngineHasNotAnswered_SaysItIsCheckingAndTheConfirmIsDead()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");
        rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        var dialog = rig.Dialog!;
        Assert.True(dialog.IsVisible);
        Assert.True(dialog.TxtChecking.IsEffectivelyVisible);
        Assert.Equal("Checking whether a smart shutdown can be done...", dialog.TxtChecking.Text);
        Assert.False(dialog.BtnSmart.IsEffectivelyEnabled);
        Assert.False(dialog.RefusalPanel.IsVisible);

        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        CoordinatorRig.Settle();

        Assert.True(dialog.IsVisible);
        Assert.Empty(rig.Engine.Starts);
    }

    [AvaloniaFact]
    public void Dialog_TheEngineSaysItMay_TheConfirmComesAliveAndNothingIsRefused()
    {
        var rig = OpenFromTheWindowClose();

        var dialog = rig.Dialog!;
        Assert.False(dialog.TxtChecking.IsVisible);
        Assert.False(dialog.RefusalPanel.IsVisible);
        Assert.True(dialog.BtnSmart.IsEffectivelyEnabled);
        Assert.True(dialog.BtnSmart.IsFocused);
    }

    /// <summary>The Gateway cannot be reached (mission section 7): the reason is shown as given, the
    /// smart choice stays dead, and "Shut down and ignore all sessions" still works.</summary>
    [AvaloniaFact]
    public void Dialog_TheEngineRefusesTheSmartShutdown_ShowsTheReasonAsGivenAndIgnoreAllStillWorks()
    {
        const string refusal = "the Gateway could not be reached (No such host is known). Nothing has been touched.";
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");
        rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        rig.Engine.CheckAnswer.SetResult(new SmartShutdownAvailability(false, refusal, true, null));
        CoordinatorRig.Settle();

        var dialog = rig.Dialog!;
        Assert.False(dialog.TxtChecking.IsVisible);
        Assert.True(dialog.RefusalPanel.IsEffectivelyVisible);
        Assert.Equal(refusal, dialog.TxtSmartShutdownRefusal.Text);
        Assert.True(dialog.TxtSmartShutdownRefusal.IsEffectivelyVisible);
        Assert.False(dialog.TxtRestartRefusal.IsVisible);
        Assert.False(dialog.BtnSmart.IsEffectivelyEnabled);
        Assert.True(dialog.BtnCancel.IsEffectivelyEnabled);

        CoordinatorRig.Click(dialog.BtnIgnore);

        Assert.Single(rig.Engine.IgnoreAlls);
        Assert.Empty(rig.Engine.Starts);
    }

    [AvaloniaFact]
    public void Dialog_FromTheFileMenuAndTheEngineCannotRestart_ShowsTheRestartReasonAsGivenAndTheConfirmIsDead()
    {
        const string refusal = "this Director is a development slot; its launcher would restart another one.";
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");
        _ = rig.Coordinator.OpenFromFileMenuAsync();
        CoordinatorRig.Settle();

        rig.Engine.CheckAnswer.SetResult(new SmartShutdownAvailability(true, null, false, refusal));
        CoordinatorRig.Settle();

        var dialog = rig.Dialog!;
        Assert.Equal(refusal, dialog.TxtRestartRefusal.Text);
        Assert.True(dialog.TxtRestartRefusal.IsEffectivelyVisible);
        Assert.False(dialog.TxtSmartShutdownRefusal.IsVisible);
        Assert.False(dialog.BtnSmart.IsEffectivelyEnabled);
    }

    /// <summary>The window close asks for no restart, so a Director that cannot restart can still be shut down.</summary>
    [AvaloniaFact]
    public void Dialog_FromTheWindowCloseAndTheEngineCannotRestart_TheConfirmIsStillLive()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");
        rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        rig.Engine.CheckAnswer.SetResult(new SmartShutdownAvailability(true, null, false, "a development slot"));
        CoordinatorRig.Settle();

        Assert.True(rig.Dialog!.BtnSmart.IsEffectivelyEnabled);
        Assert.False(rig.Dialog.RefusalPanel.IsVisible);
    }

    [AvaloniaFact]
    public void Dialog_TheCheckItselfFails_TheConfirmStaysDeadAndTheFailureIsShown()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");
        rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        rig.Engine.CheckAnswer.SetException(new InvalidOperationException("the socket closed"));
        CoordinatorRig.Settle();

        Assert.False(rig.Dialog!.BtnSmart.IsEffectivelyEnabled);
        Assert.Equal("The check did not finish: the socket closed", rig.Dialog.TxtSmartShutdownRefusal.Text);
    }

    // ===== Shut down and ignore all sessions =====

    /// <summary>The plain ending state is on the screen BEFORE the engine answers; the engine is called
    /// once with the door's purpose; the application closes once, after the answer.</summary>
    [AvaloniaFact]
    public void Dialog_IgnoreAllClickedFromTheWindowClose_ShowsTheEndingStateAtOnceThenClosesTheApplication()
    {
        var rig = OpenFromTheWindowClose();

        CoordinatorRig.Click(rig.Dialog!.BtnIgnore);

        var request = Assert.Single(rig.Engine.IgnoreAlls);
        Assert.Equal(SmartShutdownPurpose.Close, request.Purpose);
        var surface = rig.Surface!;
        Assert.True(surface.TxtEnding.IsEffectivelyVisible);
        Assert.Equal("Ending your sessions...", surface.TxtEnding.Text);
        Assert.Null(surface.Progress);
        Assert.False(surface.BackPanel.IsVisible);
        Assert.False(rig.SessionView.IsVisible);
        Assert.Equal(0, rig.ApplicationCloses);
        // Still ending: a second close opens nothing and closes nothing.
        Assert.True(rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing));

        rig.Engine.IgnoreAllAnswer.SetResult(new IgnoreAllResult(true, "workspace-1", null, 1));
        CoordinatorRig.Settle();

        Assert.Equal(1, rig.ApplicationCloses);
        Assert.Single(rig.Engine.IgnoreAlls);
        Assert.Equal(1, rig.DialogsOpened);
    }

    /// <summary>The record could not be written: the owner chose to discard the sessions, so the close still follows.</summary>
    [AvaloniaFact]
    public void Dialog_IgnoreAllAndTheRecordWasNotWritten_TheApplicationStillCloses()
    {
        var rig = OpenFromTheWindowClose();
        CoordinatorRig.Click(rig.Dialog!.BtnIgnore);

        rig.Engine.IgnoreAllAnswer.SetResult(new IgnoreAllResult(false, null, "the Gateway could not be reached", 1));
        CoordinatorRig.Settle();

        Assert.Equal(1, rig.ApplicationCloses);
    }

    /// <summary>This path of the engine restarts nothing, so from the File menu the Director stays up and says so.</summary>
    [AvaloniaFact]
    public void Dialog_IgnoreAllClickedFromTheFileMenu_EndsTheSessionsPutsTheSessionViewBackAndSaysItDidNotRestart()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");
        _ = rig.Coordinator.OpenFromFileMenuAsync();
        CoordinatorRig.Settle();
        rig.EngineSaysItMay();

        CoordinatorRig.Click(rig.Dialog!.BtnIgnore);
        Assert.True(rig.Surface!.TxtEnding.IsEffectivelyVisible);
        rig.Engine.IgnoreAllAnswer.SetResult(new IgnoreAllResult(true, "workspace-1", null, 1));
        CoordinatorRig.Settle();

        Assert.Equal(SmartShutdownPurpose.Restart, Assert.Single(rig.Engine.IgnoreAlls).Purpose);
        Assert.Equal(0, rig.ApplicationCloses);
        Assert.True(rig.SessionViewIsShown);
        Assert.Contains("The Director was not restarted", Assert.Single(rig.Messages));
    }

    /// <summary>What the engine as built does today: it refuses. Its words are shown, the session view
    /// comes back, nothing is closed.</summary>
    [AvaloniaFact]
    public void Dialog_IgnoreAllAndTheEngineThrows_ShowsItsMessagePutsTheSessionViewBackAndClosesNothing()
    {
        var rig = OpenFromTheWindowClose();
        CoordinatorRig.Click(rig.Dialog!.BtnIgnore);

        rig.Engine.IgnoreAllAnswer.SetException(
            new NotSupportedException("Shut down and ignore all sessions is not built yet. Nothing has been touched."));
        CoordinatorRig.Settle();

        Assert.Equal("Shut down and ignore all sessions is not built yet. Nothing has been touched.", Assert.Single(rig.Messages));
        Assert.True(rig.SessionViewIsShown);
        Assert.Equal(0, rig.ApplicationCloses);
    }

    // ===== The operating system is shutting down =====

    [AvaloniaFact]
    public void HandleWindowClosing_TheOperatingSystemIsShuttingDown_OpensNoDialogRecordsOnceAndTheCloseCarriesOn()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");

        var cancelled = rig.Coordinator.HandleWindowClosing(WindowCloseReason.OSShutdown);
        CoordinatorRig.Settle();

        Assert.False(cancelled);
        Assert.Equal(1, rig.Engine.Records);
        Assert.Equal(0, rig.DialogsOpened);
        Assert.Empty(rig.Engine.Checks);
        Assert.True(rig.SessionViewIsShown);
        Assert.Equal(0, rig.ApplicationCloses);
    }

    /// <summary>What the engine as built does today: it refuses. The close carries on all the same.</summary>
    [AvaloniaFact]
    public void HandleWindowClosing_TheOperatingSystemIsShuttingDownAndTheEngineThrows_TheCloseStillCarriesOn()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");
        rig.Engine.RecordAnswer = _ => throw new NotSupportedException("not built yet");

        var cancelled = rig.Coordinator.HandleWindowClosing(WindowCloseReason.OSShutdown);

        Assert.False(cancelled);
        Assert.Equal(1, rig.Engine.Records);
        Assert.Empty(rig.Messages);
    }

    /// <summary>An engine that never answers is given up on at the limit, and the token it was handed is cancelled.</summary>
    [AvaloniaFact]
    public void HandleWindowClosing_TheOperatingSystemIsShuttingDownAndTheEngineNeverAnswers_GivesUpAtTheLimit()
    {
        var rig = new CoordinatorRig(operatingSystemShutdownLimit: TimeSpan.FromMilliseconds(200));
        rig.AddSession(ActivityState.Working, "builder");
        var handed = new TaskCompletionSource<CancellationToken>();
        rig.Engine.RecordAnswer = ct =>
        {
            handed.SetResult(ct);
            return new TaskCompletionSource<IgnoreAllResult>().Task;
        };

        var cancelled = rig.Coordinator.HandleWindowClosing(WindowCloseReason.OSShutdown);

        Assert.False(cancelled);
        Assert.Equal(1, rig.Engine.Records);
        Assert.True(handed.Task.IsCompleted);
        Assert.True(handed.Task.Result.CanBeCanceled);
    }

    // ===== A Director with no engine to give =====

    // The control service did not start, so the factory hands the coordinator nothing. The owner said:
    // if any sessions are running, show the number and ask. So the window close still asks.

    /// <summary>
    /// No engine and one idle session: the close is cancelled and the SAME dialog opens, with the count,
    /// the smart choice dead from the start (nothing is being checked), and the reason in plain words.
    /// The other two choices are live.
    /// </summary>
    [AvaloniaFact]
    public void HandleWindowClosing_NoEngineAndOneIdleSession_OpensTheDialogWithTheSmartChoiceDeadAndTheReasonShown()
    {
        var rig = new CoordinatorRig(withEngine: false);
        rig.AddSession(ActivityState.Idle, "idle one");

        var cancelled = rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        Assert.True(cancelled);
        Assert.Equal(1, rig.DialogsOpened);
        var dialog = rig.Dialog!;
        Assert.True(dialog.IsVisible);
        Assert.Equal("Smart shutdown", dialog.Title);
        Assert.Equal("1 session is running", dialog.TxtSessionCount.Text);
        Assert.False(dialog.TxtChecking.IsVisible);
        Assert.False(dialog.BtnSmart.IsEffectivelyEnabled);
        Assert.True(dialog.RefusalPanel.IsEffectivelyVisible);
        Assert.True(dialog.TxtSmartShutdownRefusal.IsEffectivelyVisible);
        Assert.Equal(
            "This Director's control service did not start, so a smart shutdown cannot run. The log has the reason.",
            dialog.TxtSmartShutdownRefusal.Text);
        Assert.True(dialog.BtnIgnore.IsEffectivelyEnabled);
        Assert.True(dialog.BtnCancel.IsEffectivelyEnabled);
        Assert.Equal(0, rig.ApplicationCloses);
        Assert.Empty(rig.Messages);
    }

    /// <summary>
    /// Ignore all with no engine: there is nothing to call, so the close the owner asked for carries on
    /// through the main window's own path, once, and the close that comes back is not asked about again.
    /// </summary>
    [AvaloniaFact]
    public void Dialog_IgnoreAllClickedWithNoEngine_ClosesTheApplicationExactlyOnceAndCallsNoEngine()
    {
        var rig = new CoordinatorRig(withEngine: false);
        rig.AddSession(ActivityState.Idle, "idle one");
        rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        var dialog = rig.Dialog!;
        CoordinatorRig.Click(dialog.BtnIgnore);

        Assert.Equal(1, rig.ApplicationCloses);
        Assert.False(dialog.IsVisible);
        // The close the flow asked for comes back through the window's closing: it carries on, unasked.
        Assert.False(rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing));
        CoordinatorRig.Settle();
        Assert.Equal(1, rig.ApplicationCloses);
        Assert.Equal(1, rig.DialogsOpened);
        AssertTheEngineWasNeverCalled(rig);
        Assert.Empty(rig.Messages);
    }

    /// <summary>Cancel with no engine: nothing is closed, the session view stays, and the next close asks again.</summary>
    [AvaloniaFact]
    public void Dialog_CancelClickedWithNoEngine_ClosesNothingAndTheNextCloseAsksAgain()
    {
        var rig = new CoordinatorRig(withEngine: false);
        rig.AddSession(ActivityState.Idle, "idle one");
        rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        CoordinatorRig.Click(rig.Dialog!.BtnCancel);

        Assert.Equal(0, rig.ApplicationCloses);
        Assert.True(rig.SessionViewIsShown);
        Assert.Empty(rig.Messages);
        AssertTheEngineWasNeverCalled(rig);
        Assert.True(rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing));
        CoordinatorRig.Settle();
        Assert.Equal(2, rig.DialogsOpened);
        Assert.Equal(0, rig.ApplicationCloses);
    }

    /// <summary>Escape and the dialog's own close with no engine: the same nothing.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dialog_EscapeOrItsOwnCloseWithNoEngine_ClosesNothing(bool escape)
    {
        var rig = new CoordinatorRig(withEngine: false);
        rig.AddSession(ActivityState.Working, "builder");
        rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        if (escape)
            rig.Dialog!.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        else
            rig.Dialog!.Close();
        CoordinatorRig.Settle();

        Assert.Equal(0, rig.ApplicationCloses);
        Assert.True(rig.SessionViewIsShown);
        AssertTheEngineWasNeverCalled(rig);
    }

    /// <summary>The smart choice is dead with no engine: Enter, the confirm's key, takes nothing.</summary>
    [AvaloniaFact]
    public void Dialog_EnterPressedWithNoEngine_TakesNothing()
    {
        var rig = new CoordinatorRig(withEngine: false);
        rig.AddSession(ActivityState.Working, "builder");
        rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        var dialog = rig.Dialog!;
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        CoordinatorRig.Settle();

        Assert.True(dialog.IsVisible);
        Assert.Equal(0, rig.ApplicationCloses);
        Assert.True(rig.SessionViewIsShown);
    }

    /// <summary>No engine and nothing running: the close still carries on with no dialog.</summary>
    [AvaloniaFact]
    public void HandleWindowClosing_NoEngineAndNoSessions_OpensNoDialogAndTheCloseCarriesOn()
    {
        var rig = new CoordinatorRig(withEngine: false);

        var cancelled = rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();

        Assert.False(cancelled);
        Assert.Equal(0, rig.DialogsOpened);
        Assert.Equal(0, rig.ApplicationCloses);
    }

    /// <summary>The File menu with no engine keeps its one sentence and opens nothing.</summary>
    [AvaloniaFact]
    public void OpenFromFileMenuAsync_NoEngine_OpensNoDialogAndSaysWhy()
    {
        var rig = new CoordinatorRig(withEngine: false);
        rig.AddSession(ActivityState.Working, "builder");

        _ = rig.Coordinator.OpenFromFileMenuAsync();
        CoordinatorRig.Settle();

        Assert.Equal(0, rig.DialogsOpened);
        Assert.Contains("control service did not start", Assert.Single(rig.Messages));
    }

    // The rig always holds a fake engine; with no engine the coordinator is never handed it, so any
    // call written down on it is a call that should not have been possible.
    private static void AssertTheEngineWasNeverCalled(CoordinatorRig rig)
    {
        Assert.Empty(rig.Engine.Checks);
        Assert.Empty(rig.Engine.Starts);
        Assert.Empty(rig.Engine.IgnoreAlls);
        Assert.Equal(0, rig.Engine.Records);
    }

    // ===== The main window's source =====

    /// <summary>
    /// The two old windows are gone and the main window must not name them again. Read from the source,
    /// because no test opens the real main window. It also proves the swap is wired: both doors name the
    /// coordinator.
    /// </summary>
    [Fact]
    public void MainWindowSource_NamesNeitherOldWindow_AndCallsTheCoordinatorFromBothDoors()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Avalonia", "MainWindow.axaml.cs"));

        Assert.True(source.Length > 100_000, "this is not the main window's source");
        Assert.Empty(OldNamesIn(source));
        Assert.Contains("SmartShutdown.OpenFromFileMenuAsync()", source);
        Assert.Contains("SmartShutdown.HandleWindowClosing(e.CloseReason)", source);
        Assert.Contains("Item(\"Smart Restart\"", source);
    }

    /// <summary>The check above can fail: each of the three old names is found when it is there.</summary>
    [Theory]
    [InlineData("var dialog = new DrainDirectorDialog(host, name);", "DrainDirectorDialog")]
    [InlineData("var dialog = new CloseDialog(_sessionManager, names);", "CloseDialog")]
    [InlineData("Item(\"Drain this Director for restart...\", async () =>", "Drain this Director")]
    public void OldNamesIn_SourceThatNamesAnOldWindow_FindsIt(string line, string expected)
    {
        Assert.Equal(new[] { expected }, OldNamesIn("// before\n" + line + "\n// after"));
    }

    private static string[] OldNamesIn(string source) =>
        new[] { "DrainDirectorDialog", "CloseDialog", "Drain this Director" }
            .Where(name => source.Contains(name, StringComparison.Ordinal))
            .ToArray();

    // ===== The pictures =====

    /// <summary>
    /// The four pictures the mandate names. Each frame is captured from the real window; when
    /// SMART_RESTART_SCREENSHOT_DIR names a folder they are written there, to be LOOKED at. This test
    /// does not judge what a picture shows; the tests above read the same controls for that.
    /// </summary>
    [AvaloniaFact]
    public void CaptureRenderedFrame_TheFourPicturesOfTheSwap_AreDrawnNotBlank()
    {
        var folder = Environment.GetEnvironmentVariable("SMART_RESTART_SCREENSHOT_DIR");

        var checking = new CoordinatorRig();
        checking.AddSession(ActivityState.Working, "builder");
        checking.AddSession(ActivityState.Idle, "reviewer");
        checking.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();
        Capture(checking.Dialog!, folder, "swap-dialog-while-checking.png");

        var refused = new CoordinatorRig();
        refused.AddSession(ActivityState.Working, "builder");
        refused.AddSession(ActivityState.Idle, "reviewer");
        refused.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing);
        CoordinatorRig.Settle();
        refused.Engine.CheckAnswer.SetResult(new SmartShutdownAvailability(
            false,
            "the Gateway could not be reached (No such host is known). A smart shutdown keeps its record on " +
            "the Gateway so that the sessions can be brought back after this machine has been down, so it " +
            "does not start without one. Nothing has been touched. Try again when the Gateway answers, or " +
            "choose to shut down and ignore all sessions.",
            true, null));
        CoordinatorRig.Settle();
        Capture(refused.Dialog!, folder, "swap-dialog-smart-choice-refused.png");

        var ending = OpenFromTheWindowClose();
        CoordinatorRig.Click(ending.Dialog!.BtnIgnore);
        Capture(ending.Main, folder, "swap-ending-your-sessions.png");

        var failed = RunUnderWay();
        failed.Engine.Run.Complete(SmartShutdownOutcome.Failed,
            "The smart shutdown stopped on an error: the Gateway stopped answering. The record on the Gateway says how far it got.");
        CoordinatorRig.Settle();
        Capture(failed.Main, folder, "swap-progress-after-a-failed-run.png");
    }

    private static void Capture(Window window, string? folder, string fileName)
    {
        CoordinatorRig.Settle();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 100 && frame.PixelSize.Height > 100, $"{fileName}: the frame is {frame.PixelSize}");

        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        frame.Save(Path.Combine(folder, fileName));
    }

    // ===== Shared steps =====

    /// <summary>One working session, the window close pressed, the engine has said it may.</summary>
    private static CoordinatorRig OpenFromTheWindowClose()
    {
        var rig = new CoordinatorRig();
        rig.AddSession(ActivityState.Working, "builder");
        Assert.True(rig.Coordinator.HandleWindowClosing(WindowCloseReason.WindowClosing));
        CoordinatorRig.Settle();
        rig.EngineSaysItMay();
        return rig;
    }

    /// <summary>The smart choice pressed in the real dialog: a run is under way and the progress screen is shown.</summary>
    private static CoordinatorRig RunUnderWay()
    {
        var rig = OpenFromTheWindowClose();
        CoordinatorRig.Click(rig.Dialog!.BtnSmart);
        Assert.NotNull(rig.Surface?.Progress);
        return rig;
    }

    private static void AssertNothingHappened(CoordinatorRig rig)
    {
        Assert.False(rig.Dialog!.IsVisible);
        Assert.True(rig.Coordinator.Flow.IsCompletedSuccessfully);
        Assert.Empty(rig.Engine.Starts);
        Assert.Empty(rig.Engine.IgnoreAlls);
        Assert.Equal(0, rig.Engine.Records);
        Assert.Equal(0, rig.ApplicationCloses);
        Assert.Empty(rig.Messages);
        Assert.True(rig.SessionViewIsShown);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "packages")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repository root (no 'packages' directory above the test binary).");
    }
}
