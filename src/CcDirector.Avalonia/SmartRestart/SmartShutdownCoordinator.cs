using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// THE TWO DOORS, AND EVERYTHING BEHIND THEM (mission 5.3 items 1 to 9). File, Smart Restart and the
/// close of the main window both come here; this class opens the one dialog, starts what the owner chose
/// on the engine, puts the progress screen in place of the session view, and acts on how the run ended.
///
/// The main window builds one of these and calls it from its two doors. It does nothing else new, so
/// that the very large file other missions edit every day carries a few lines of this feature and the
/// whole flow is tested here with fakes and without the main window.
///
/// WHO DECIDES WHAT. "Are there sessions" and the number the dialog shows come from ONE list, the one
/// <see cref="SmartShutdownSessionReader"/> builds, so the two can never disagree. The engine never
/// closes the application and never shows a window; this class never touches a session.
///
/// It lives on the interface thread: both doors, the dialog and the run's finished event all arrive
/// there, so its state needs no lock.
/// </summary>
public sealed class SmartShutdownCoordinator
{
    /// <summary>How long the record for an operating system shutdown may take before it is given up on.</summary>
    public static readonly TimeSpan OperatingSystemShutdownLimit = TimeSpan.FromSeconds(5);

    private enum Stage
    {
        /// <summary>Nothing is open and nothing is under way.</summary>
        Idle,

        /// <summary>The dialog is open.</summary>
        Asking,

        /// <summary>A smart shutdown is under way and the progress screen is shown.</summary>
        Running,

        /// <summary>"Shut down and ignore all sessions" is under way and the ending state is shown.</summary>
        Ending,
    }

    private readonly Func<ISmartShutdown?> _engine;
    private readonly Func<IEnumerable<Session>> _sessions;
    private readonly Func<SmartShutdownViewModel, Task<SmartShutdownChoice>> _showDialog;
    private readonly Action<Control> _showInPlaceOfSessionView;
    private readonly Action _restoreSessionView;
    private readonly Action _closeApplication;
    private readonly Action<string> _showMessage;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _operatingSystemShutdownLimit;

    private Stage _stage = Stage.Idle;
    private bool _closeAllowed;

    /// <param name="engine">The smart shutdown engine, or null when this Director has none to give (its
    /// control service did not start).</param>
    /// <param name="sessions">The Director's sessions as they are now.</param>
    /// <param name="showDialog">Shows the dialog over the main window and gives back the choice.</param>
    /// <param name="showInPlaceOfSessionView">Puts a control in place of the session view.</param>
    /// <param name="restoreSessionView">Takes that control away and puts the session view back.</param>
    /// <param name="closeApplication">The main window's own close path.</param>
    /// <param name="showMessage">Shows the owner one plain sentence (the notification bar).</param>
    /// <param name="clock">What time it is now, for the progress screen.</param>
    /// <param name="operatingSystemShutdownLimit">Test seam; the product uses
    /// <see cref="OperatingSystemShutdownLimit"/>.</param>
    public SmartShutdownCoordinator(
        Func<ISmartShutdown?> engine,
        Func<IEnumerable<Session>> sessions,
        Func<SmartShutdownViewModel, Task<SmartShutdownChoice>> showDialog,
        Action<Control> showInPlaceOfSessionView,
        Action restoreSessionView,
        Action closeApplication,
        Action<string> showMessage,
        TimeProvider clock,
        TimeSpan? operatingSystemShutdownLimit = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _showDialog = showDialog ?? throw new ArgumentNullException(nameof(showDialog));
        _showInPlaceOfSessionView = showInPlaceOfSessionView ?? throw new ArgumentNullException(nameof(showInPlaceOfSessionView));
        _restoreSessionView = restoreSessionView ?? throw new ArgumentNullException(nameof(restoreSessionView));
        _closeApplication = closeApplication ?? throw new ArgumentNullException(nameof(closeApplication));
        _showMessage = showMessage ?? throw new ArgumentNullException(nameof(showMessage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _operatingSystemShutdownLimit = operatingSystemShutdownLimit ?? OperatingSystemShutdownLimit;
    }

    /// <summary>
    /// The door flow last started: from the dialog opening to the moment the choice has been acted on.
    /// A run it started goes on after it completes. Completed when no door has been used.
    /// </summary>
    public Task Flow { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// File, Smart Restart. An entry point: nothing thrown here reaches the menu.
    /// </summary>
    public Task OpenFromFileMenuAsync()
    {
        FileLog.Write($"[SmartShutdownCoordinator] OpenFromFileMenuAsync: stage={_stage}");
        try
        {
            if (_stage != Stage.Idle)
            {
                FileLog.Write($"[SmartShutdownCoordinator] OpenFromFileMenuAsync: ignored, stage={_stage} - what is already on the screen is the answer");
                return Task.CompletedTask;
            }

            var engine = _engine();
            if (engine is null)
            {
                _showMessage("Smart Restart is not available: this Director's control service did not start. The log says why.");
                return Task.CompletedTask;
            }

            var sessions = SmartShutdownSessionReader.Read(_sessions());
            if (sessions.Count == 0)
            {
                // The engine as built restarts only as the end of a run, and a run is a shutdown of
                // sessions; with none there is nothing it can be asked to do.
                _showMessage("Smart Restart: there are no sessions to shut down, so nothing was done.");
                return Task.CompletedTask;
            }

            Flow = RunDoorAsync(engine, SmartShutdownDoor.FileMenu, sessions);
            return Flow;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownCoordinator] OpenFromFileMenuAsync FAILED: {ex}");
            _showMessage($"Smart Restart could not start: {ex.Message}");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The close of the main window, on every platform. An entry point: nothing thrown here reaches the
    /// window. Returns true when the close must be CANCELLED; false lets it carry on as it always has.
    /// </summary>
    /// <param name="reason">Why the window is closing.</param>
    public bool HandleWindowClosing(WindowCloseReason reason)
    {
        FileLog.Write($"[SmartShutdownCoordinator] HandleWindowClosing: reason={reason}, stage={_stage}, closeAllowed={_closeAllowed}");
        try
        {
            if (_closeAllowed)
            {
                FileLog.Write("[SmartShutdownCoordinator] HandleWindowClosing: carry on, this close is the one the flow asked for");
                return false;
            }

            if (reason == WindowCloseReason.OSShutdown)
            {
                RecordForOperatingSystemShutdown();
                return false;
            }

            if (_stage != Stage.Idle)
            {
                FileLog.Write($"[SmartShutdownCoordinator] HandleWindowClosing: close cancelled, stage={_stage} - what is already on the screen is the answer");
                return true;
            }

            var sessions = SmartShutdownSessionReader.Read(_sessions());
            if (sessions.Count == 0)
            {
                FileLog.Write("[SmartShutdownCoordinator] HandleWindowClosing: no sessions running, carry on without asking");
                return false;
            }

            // NO ENGINE IS NOT A REASON TO CLOSE UNASKED (mission 4.4: if any sessions are running, show
            // the number and ask). The same dialog opens with the smart choice dead and the reason said;
            // "Shut down and ignore all sessions" lets this close carry on, and Cancel keeps the window.
            var engine = _engine();
            if (engine is null)
                FileLog.Write($"[SmartShutdownCoordinator] HandleWindowClosing: no engine (the control service did not start), asking all the same; sessions={sessions.Count}");

            Flow = RunDoorAsync(engine, SmartShutdownDoor.WindowClose, sessions);
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownCoordinator] HandleWindowClosing FAILED: {ex}");
            _showMessage($"The window was not closed, because the shutdown could not be started: {ex.Message}");
            return true;
        }
    }

    // One door, from the dialog opening to the choice acted on. It runs on the interface thread up to
    // its first await, so the stage is Asking before the caller gets control back.
    // The engine is null only from the window close of a Director whose control service did not start.
    private async Task RunDoorAsync(ISmartShutdown? engine, SmartShutdownDoor door, IReadOnlyList<SmartShutdownSession> sessions)
    {
        var purpose = door == SmartShutdownDoor.FileMenu ? SmartShutdownPurpose.Restart : SmartShutdownPurpose.Close;
        var surfaceShown = false;
        _stage = Stage.Asking;
        try
        {
            FileLog.Write($"[SmartShutdownCoordinator] RunDoorAsync: door={door}, purpose={purpose}, sessions={sessions.Count}");

            // THE DIALOG OPENS AT ONCE AND THE ENGINE IS ASKED AFTER. The check may take a second or
            // two; until it answers the confirm is dead and the dialog says it is checking.
            var viewModel = new SmartShutdownViewModel(sessions, door);
            // Cancelled when the dialog closes and never disposed: the check may still be holding its
            // token then, and a source with no timer and no wait handle holds nothing to let go of.
            var stopChecking = new CancellationTokenSource();
            Task<SmartShutdownChoice> choosing;
            if (engine is null)
            {
                // Nothing to ask: the smart choice is dead from the start, and the dialog says why.
                viewModel.ApplyNoEngine();
                choosing = _showDialog(viewModel);
            }
            else
            {
                viewModel.BeginChecking();
                choosing = _showDialog(viewModel);
                _ = CheckIntoAsync(engine, purpose, viewModel, stopChecking.Token);
            }

            var choice = await choosing;
            stopChecking.Cancel();
            FileLog.Write($"[SmartShutdownCoordinator] RunDoorAsync: choice={choice.Choice}, timeAllowed={choice.TimeAllowed}");

            switch (choice.Choice)
            {
                case SmartShutdownChoiceKind.Cancelled:
                    _stage = Stage.Idle;
                    return;

                case SmartShutdownChoiceKind.SmartShutdown:
                {
                    if (engine is null)
                        throw new InvalidOperationException("The dialog chose a smart shutdown on a Director with no engine to run one.");

                    var timeAllowed = choice.TimeAllowed
                        ?? throw new InvalidOperationException("The dialog chose a smart shutdown without a time allowed.");

                    // Start BEFORE anything is put on the screen: when it throws (a run is already
                    // under way) its message is shown and nothing has changed.
                    var run = engine.Start(new SmartShutdownRequest(purpose, timeAllowed, Reason: null));

                    var surface = new SmartShutdownSurface();
                    var progress = new ShutdownProgressView(run, _clock);
                    progress.ViewModel.Finished += result => OnRunFinished(surface, result);
                    surface.ShowProgress(progress);
                    _showInPlaceOfSessionView(surface);
                    surfaceShown = true;
                    _stage = Stage.Running;
                    return;
                }

                case SmartShutdownChoiceKind.IgnoreAllSessions:
                {
                    if (engine is null)
                    {
                        // No engine to call and so no record to write: the close the owner asked for
                        // carries on through the main window's own path, which ends the sessions.
                        FileLog.Write($"[SmartShutdownCoordinator] RunDoorAsync: ignore all with no engine, the close carries on; sessions={sessions.Count}");
                        _stage = Stage.Idle;
                        CloseTheApplication();
                        return;
                    }

                    var surface = new SmartShutdownSurface();
                    surface.ShowEnding();
                    _showInPlaceOfSessionView(surface);
                    surfaceShown = true;
                    _stage = Stage.Ending;

                    // The time allowed is carried for the record only; nobody was asked for one.
                    var result = await engine.ShutDownIgnoringAllAsync(
                        new SmartShutdownRequest(purpose, SmartShutdownTimes.Default, Reason: null),
                        CancellationToken.None);
                    FileLog.Write($"[SmartShutdownCoordinator] RunDoorAsync: ignore all done, sessionsEnded={result.SessionsEnded}, " +
                                  $"recordWritten={result.RecordWritten}, workspace={result.WorkspaceId ?? "-"}");
                    if (!result.RecordWritten)
                        FileLog.Write($"[SmartShutdownCoordinator] RunDoorAsync: the record was NOT written: {result.RecordRefusal}");

                    _stage = Stage.Idle;
                    if (door == SmartShutdownDoor.WindowClose)
                    {
                        CloseTheApplication();
                        return;
                    }

                    // The File menu asked for a restart, and this path of the engine has none: it writes
                    // the record and ends the sessions, and its result says nothing about a launcher. So
                    // the Director stays up, empty, and says so.
                    _restoreSessionView();
                    _showMessage($"{result.SessionsEnded} session(s) were ended. The Director was not restarted: " +
                                 "\"Shut down and ignore all sessions\" ends the sessions and does not restart.");
                    return;
                }

                default:
                    throw new InvalidOperationException($"The dialog gave back a choice this flow does not know: {choice.Choice}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownCoordinator] RunDoorAsync FAILED: {ex}");
            _stage = Stage.Idle;
            if (surfaceShown)
                _restoreSessionView();
            _showMessage(ex.Message);
        }
    }

    // The engine's answer into the open dialog. It stands alone beside the dialog, so it ends its own
    // failures here: a check that fails leaves the confirm dead with the failure shown.
    private static async Task CheckIntoAsync(
        ISmartShutdown engine, SmartShutdownPurpose purpose, SmartShutdownViewModel viewModel, CancellationToken stopChecking)
    {
        try
        {
            var availability = await engine.CheckAsync(purpose, stopChecking);
            if (stopChecking.IsCancellationRequested)
            {
                FileLog.Write("[SmartShutdownCoordinator] CheckIntoAsync: the answer arrived after the dialog closed, dropped");
                return;
            }

            viewModel.ApplyAvailability(availability);
        }
        catch (OperationCanceledException) when (stopChecking.IsCancellationRequested)
        {
            FileLog.Write("[SmartShutdownCoordinator] CheckIntoAsync: stopped, the dialog closed first");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownCoordinator] CheckIntoAsync FAILED: {ex}");
            viewModel.ApplyCheckFailure($"The check did not finish: {ex.Message}");
        }
    }

    // The run is over. Raised once by the progress screen, on the interface thread.
    private void OnRunFinished(SmartShutdownSurface surface, SmartShutdownResult result)
    {
        FileLog.Write($"[SmartShutdownCoordinator] OnRunFinished: outcome={result.Outcome}, detail={result.Detail}");
        try
        {
            _stage = Stage.Idle;
            switch (result.Outcome)
            {
                case SmartShutdownOutcome.Emptied:
                    CloseTheApplication();
                    break;

                case SmartShutdownOutcome.RestartAccepted:
                    FileLog.Write("[SmartShutdownCoordinator] OnRunFinished: the launcher stops this process; nothing to do");
                    break;

                case SmartShutdownOutcome.Cancelled:
                    _restoreSessionView();
                    break;

                case SmartShutdownOutcome.Refused:
                case SmartShutdownOutcome.RestartRefused:
                case SmartShutdownOutcome.Failed:
                    // The reason is on the progress screen, in the engine's words. It stays there until
                    // the owner has read it and asks for the sessions back.
                    surface.BackRequested += _restoreSessionView;
                    surface.OfferBack();
                    break;

                default:
                    throw new InvalidOperationException($"The flow does not know shutdown outcome {result.Outcome}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownCoordinator] OnRunFinished FAILED: {ex}");
            _showMessage(ex.Message);
        }
    }

    private void CloseTheApplication()
    {
        FileLog.Write("[SmartShutdownCoordinator] CloseTheApplication: closing through the main window's own path, without asking again");
        _closeAllowed = true;
        _closeApplication();
    }

    // THE OPERATING SYSTEM IS SHUTTING DOWN (mission 10.5). No dialog and no progress screen: there is
    // no ten minutes. The close that follows ends the process, so the record is waited for HERE, on the
    // interface thread, for at most the limit - the one place this class blocks, and nobody is looking.
    // The engine's call goes to a pool thread so that waiting on it cannot deadlock the thread it would
    // otherwise come back to. Whatever happens, the close carries on.
    private void RecordForOperatingSystemShutdown()
    {
        try
        {
            var sessions = SmartShutdownSessionReader.Read(_sessions());
            if (sessions.Count == 0)
            {
                FileLog.Write("[SmartShutdownCoordinator] RecordForOperatingSystemShutdown: no sessions running, nothing to record");
                return;
            }

            var engine = _engine();
            if (engine is null)
            {
                FileLog.Write($"[SmartShutdownCoordinator] RecordForOperatingSystemShutdown: no engine, nothing recorded; sessions={sessions.Count}");
                return;
            }

            using var giveUp = new CancellationTokenSource(_operatingSystemShutdownLimit);
            var recording = Task.Run(() => engine.RecordAndLetEndAsync(giveUp.Token));
            if (!recording.Wait(_operatingSystemShutdownLimit))
            {
                FileLog.Write($"[SmartShutdownCoordinator] RecordForOperatingSystemShutdown: gave up after {_operatingSystemShutdownLimit.TotalSeconds:0.#} seconds; the close carries on");
                return;
            }

            var result = recording.Result;
            FileLog.Write($"[SmartShutdownCoordinator] RecordForOperatingSystemShutdown: recordWritten={result.RecordWritten}, " +
                          $"workspace={result.WorkspaceId ?? "-"}, refusal={result.RecordRefusal ?? "-"}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SmartShutdownCoordinator] RecordForOperatingSystemShutdown FAILED: {ex.GetBaseException().Message}; the close carries on");
        }
    }
}
