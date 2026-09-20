# Proof - phase 2 - the swap: the two doors open the new screens

Developer seat on branch `smart-restart-p2-swap`, 20 September 2026. Issues: mission #3167; this closes
defect #3168 (the old Drain window threw on opening - that window no longer exists).

## What was built

- **`SmartShutdownCoordinator`** (`src/CcDirector.Avalonia/SmartRestart/`), the whole flow in one class.
  It is handed everything through its constructor: the engine (as a function that may return nothing),
  a way to read the current `Session` objects, a way to show the dialog, a way to put a control in
  place of the session view and a way to take it away, a way to close the application, a way to show
  one sentence (the notification bar), and a clock. Two entry points: `OpenFromFileMenuAsync()` and
  `HandleWindowClosing(reason)`, which returns true when the close must be cancelled.
- **`SmartShutdownSurface`** (same folder), what stands in place of the session view: it holds either
  the progress screen or the plain "Ending your sessions..." state, and, under a run that ended badly,
  a bar with one button, "Back to your sessions".
- **The dialog's three states.** `SmartShutdownViewModel` gained `BeginChecking`, `ApplyAvailability`
  and `ApplyCheckFailure`; the dialog gained one row that shows "Checking whether a smart shutdown can
  be done..." or an amber panel with the engine's reason exactly as given. The confirm button is bound
  to `CanConfirm` and has the readable disabled look of `docs/VisualStyle.md` section 13.
- **The main window** builds the coordinator lazily and calls it from the two doors. Nothing else new.
  `MainWindow.axaml` gained a name on the session view's grid (`SessionViewGrid`) and one
  `ContentControl` (`SmartShutdownHost`) beside it.
- **Removed:** `DrainDirectorDialog.axaml`, `DrainDirectorDialog.axaml.cs`, `CloseDialog.axaml`,
  `CloseDialog.axaml.cs`. Nothing else was used only by them. A search of `src` and `tools` for
  `DrainDirectorDialog`, `CloseDialog` and "Drain this Director" leaves two lines: a test comment that
  says the old window was "since removed", and the summary "Drain this Director to a record on the
  Gateway" in `src/CcDirector.ControlApi/Restart/DirectorRestartCycle.cs`, which is about the Drain
  ENGINE, claims no window, and is in a project this mandate forbids touching.

## The size of the change to the main window

`MainWindow.axaml.cs`: 21 lines added, 38 removed (`git diff --numstat`). `MainWindow.axaml`: 5 added,
1 removed. The field `_closeConfirmed` went with the old hook; `OnClosing` is no longer `async`.

## The counts (each with a full build, in the foreground)

| Command | Before | After |
|---|---|---|
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 46 | **87 passed, 0 failed, 0 skipped** |
| `dotnet test src/CcDirector.Avalonia.Tests` | 645 | **686 passed, 0 failed, 0 skipped** |
| `dotnet build src/CcDirector.Avalonia --no-incremental` | | **0 warnings, 0 errors** |
| `dotnet build cc-director.sln` | | **0 warnings, 0 errors** |

41 new tests, all in `SmartShutdownCoordinatorTests.cs`. Every one of the 46 earlier tests is unchanged
and green (one comment in `SmartShutdownDialogTests.cs` was reworded, no code).

## What each new test proves, in plain words

Every test uses a real owner window, the REAL dialog shown over it and pressed with real clicks and
keys, the real progress screen, and a fake engine that answers only when the test says so. No test
builds a choice or a result and hands it to the coordinator.

- **No sessions, window close:** the close is not cancelled, no dialog opens, the engine is never asked.
  The same with only an exited session.
- **One idle session:** the close IS cancelled and the dialog says "1 session is running" and "0 working,
  1 waiting". This is the review's narrower-rule warning: the decision and the count are one list.
- **No sessions, File menu:** no dialog, the engine is never asked, one sentence says there are no
  sessions to shut down.
- **Each door:** the window close opens "Smart shutdown" and asks the engine with purpose Close; the File
  menu opens "Smart Restart" and asks with purpose Restart.
- **Cancel, Escape, the dialog's own close:** Start, ignore-all and the record are never called, nothing
  is closed, no message, the session view is still shown. And afterwards the next close opens a second
  dialog, so nothing was left half open.
- **The smart choice:** 30 minutes picked in the real dropdown; `Start` gets Close and 30 minutes; the
  progress screen is what is shown and the session view is hidden. From the File menu `Start` gets
  Restart and the default 10 minutes.
- **`Start` throws "already under way":** the engine's message is shown as given, the session view is
  untouched, nothing is closed, and the next close asks again.
- **The six outcomes:** Emptied closes the application exactly once, and the close that comes back
  through the window's closing carries on with no second dialog. RestartAccepted does nothing. Cancelled
  puts the session view back at once. Refused, RestartRefused and Failed (one test, three cases): the
  engine's reason is drawn as given, the session view stays hidden, and it comes back only when the real
  "Back to your sessions" button is pressed. The application is never closed on those five.
- **While a run is under way:** a second window close is cancelled and opens nothing; a second menu
  press opens nothing; `Start` was called once. The same for a close while the dialog is still open.
- **The three states of the check, in the real dialog:** before the engine answers the dialog is already
  open, says it is checking, the confirm is dead and Enter takes nothing. When it may: the confirm is
  live and holds the focus, nothing is refused. When it may not: the refusal is shown exactly as given,
  the confirm is dead, Cancel is live, and pressing "Shut down and ignore all sessions" reaches the
  engine once. From the File menu with `CanRestart` false: `RestartRefusal` as given, confirm dead. From
  the window close with `CanRestart` false: the confirm is LIVE, because a close asks for no restart.
  When the check itself throws: confirm dead, the failure shown.
- **Ignore all, window close:** the plain ending state is on the screen BEFORE the engine answers (no
  progress screen, no back bar), the engine was called once with purpose Close, a close pressed
  meanwhile is cancelled, and after the answer the application closes once. With `RecordWritten` false
  the application still closes.
- **Ignore all, File menu:** the engine is called with purpose Restart; afterwards the session view is
  back, nothing is closed, and one sentence says the Director was not restarted.
- **Ignore all and the engine throws** (what the engine as built does today): its words are shown, the
  session view comes back, nothing is closed.
- **The operating system shutting down:** no dialog, no check, `RecordAndLetEndAsync` called once, the
  close carries on. The same when the engine throws. When the engine never answers, the wait is given up
  at the limit (200 milliseconds in the test, five seconds in the product) and the token it was handed
  can be cancelled.
- **No engine:** the close carries on; the menu says the control service did not start.
- **The main window's source** names none of `DrainDirectorDialog`, `CloseDialog`, "Drain this
  Director", and does name both calls into the coordinator and the "Smart Restart" menu item. A second
  test (three cases) proves the finder finds each old name when it is there.

## The revert proofs

Committed first (`af79c4d51`), then one mutation at a time, a FULL build each time (never `--no-build`),
the whole `SmartRestart` filter each time so nothing unnamed could hide, restored with
`git checkout -- <file>`, `git status` clean after each.

| Mutation | Result | Tests that went red |
|---|---|---|
| A. `HandleWindowClosing`: the no-sessions guard made unreachable (`Count < 0`) | **2 failed, 85 passed** | `HandleWindowClosing_NoSessionsRunning_...`, `HandleWindowClosing_OnlyAnExitedSession_...` |
| B. `HandleWindowClosing`: the old narrower rule put back (only Working or WaitingForInput reach the reader) | **1 failed, 86 passed** | `HandleWindowClosing_OneIdleSession_CancelsTheCloseAndTheDialogCountsThatSession` |
| C. A comment naming `CloseDialog` added to `MainWindow.axaml.cs` | **1 failed, 86 passed** | `MainWindowSource_NamesNeitherOldWindow_AndCallsTheCoordinatorFromBothDoors` |
| Restored, full build | **87 passed, 0 failed** | none |

## The pictures (each one looked at)

In this folder, written by `CaptureRenderedFrame_TheFourPicturesOfTheSwap_AreDrawnNotBlank` when
`SMART_RESTART_SCREENSHOT_DIR` names a folder:

- `swap-dialog-while-checking.png` - "Checking whether a smart shutdown can be done..." above the
  buttons; the confirm is the dimmed blue of a disabled primary button and still readable.
- `swap-dialog-smart-choice-refused.png` - the amber panel: the heading says the other two choices still
  work, then the engine's reason word for word (it begins with a small letter because the engine's does).
- `swap-ending-your-sessions.png` - one sentence in the middle of the space, nothing else.
- `swap-progress-after-a-failed-run.png` - the progress screen with the engine's reason in bold, and the
  bar beneath with "Back to your sessions".

Drawn by Skia under the headless platform in a bare window, not by the running application.

## Decisions made because nobody could be asked

1. **The File menu with no sessions shows a sentence and does nothing.** The engine as built cannot
   restart with nothing to shut down: `Start` with purpose Restart throws "not built yet", and a restart
   exists only as the end of a run.
2. **Refused, RestartRefused, Failed: the progress screen stays, with a bar and one button beneath it.**
   The progress screen already draws the engine's reason in bold; the smallest true thing was to leave
   it there and add the way back, in the primary button colour of the visual style guide. The merged
   progress view was not changed; the bar belongs to the new surface around it.
3. **Ignore all from the File menu does not restart and says so.** `ShutDownIgnoringAllAsync` writes a
   record and ends sessions; neither its contract nor its result mentions a launcher. Closing the
   application instead would have been a different thing from what was asked. The sentence shown is
   "N session(s) were ended. The Director was not restarted: ...".
4. **The operating system shutdown blocks the interface thread for at most five seconds.** The close
   that follows ends the process (`OnShutdown` calls `Environment.Exit`), so a record that is not waited
   for is never written, and cancelling that close would hold up the machine's shutdown. The engine's
   call runs on a pool thread so the wait cannot deadlock. This is the one place the flow blocks.
5. **With no sessions, the operating system shutdown records nothing** - same list, same rule.
6. **No engine (the control service did not start): the close carries on, logged; the menu says why.**
   Cancelling a close with no way to finish it would trap the owner in the window.
7. **An unexpected failure inside `HandleWindowClosing` cancels the close and shows the message**, rather
   than closing over sessions nobody was asked about.
8. **After RestartAccepted the doors work again.** If the launcher never stops the process, the owner can
   still close the (now empty) window.
9. **A view model nobody asked to check starts in "may".** Every earlier dialog test builds the view
   model directly and expects a live confirm. The coordinator calls `BeginChecking` before the window is
   shown, and the engine asks itself the same question again when a run starts, so a confirm that got
   through is still refused there.
10. **A refusal that arrives without a reason throws.** The engine promises one; words made up here
    would hide its defect.
11. **While the progress screen is shown the session rail at the left stays live.** Only the session view
    is replaced, as the mandate says.

## What the engine could not give me

Read in `DirectorSmartShutdown.cs` on this branch (engine merged at `55870f183`):

- `Start` with purpose **Restart** throws `NotSupportedException` ("not built yet"). So File, Smart
  Restart, confirmed, today shows that sentence in the notification bar and changes nothing.
- `ShutDownIgnoringAllAsync` throws `NotSupportedException`. So "Shut down and ignore all sessions",
  from either door, today shows the ending state for an instant, then that sentence, and the session
  view comes back. Nothing is ended and nothing is closed.
- `RecordAndLetEndAsync` throws `NotSupportedException`. So on an operating system shutdown nothing is
  recorded today; it is logged and the close carries on.
- `CancelAndKeepWorking` is ignored and every snapshot says `CanCancel` false, so that button is dead.

What works end to end against the engine as built is the window close with the smart choice. I did not
reach into the engine for any of this. Each of the three throws is covered by a test that feeds the
coordinator the same exception.

## What this proof does NOT cover

- **The real main window is not opened by any test.** That its menu item and `OnClosing` call the
  coordinator is read from its source, not run. The five functions the main window hands the coordinator
  (hide `SessionViewGrid`, show `SmartShutdownHost`, `Close`, `ShowNotification`, the session list) are
  not exercised by any test.
- The real engine is never called by these tests; the fake answers in its place.
- `WindowCloseReason.OSShutdown` is handed in by the test; no operating system shut anything down.
- The native File menu is not pressed.

**By hand on the isolated rig in phase 5:** press File, Smart Restart with sessions and with none; press
the X with one idle session, with none, and twice in a row; confirm the terminal and the prompt bar are
really hidden behind the progress screen and come back after "Back to your sessions"; confirm that after
Emptied the window closes without a second dialog and the process exits; unplug the Gateway and read the
refusal in the real dialog; sign out of Windows with sessions running and read the log line.
