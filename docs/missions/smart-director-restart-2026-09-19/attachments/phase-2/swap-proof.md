# Proof - phase 2 - the swap: the two doors open the new screens

Developer seat on branch `smart-restart-p2-swap`, 20 September 2026. Issues: mission #3167; this closes
defect #3168 (the old Drain window threw on opening - that window no longer exists).

**Updated after the review (`review-phase-2-4.md`), by a second Developer seat, 20 September 2026.** The
branch now holds `origin/main` at `f20bbd33b` (merge commit `abb232669`; the engine landed on main as a
squash, pull request 3193, so the engine files on this branch are main's). `git diff origin/main --stat`
shows only the swap's own files. Review finding 1 is fixed in `da62648b0`: decision 6 below is
REPLACED, and the counts, the tests and the revert proofs are brought up to date. The answers to both
findings are in `docs/missions/smart-director-restart-2026-09-19/review-phase-2-4-answers.md`.

**Landed 20 September 2026, by the Developer seat that merged it.** The branch was held for one reason
only - review finding 2, that the engine on main still answered "not built yet" for three of this
flow's paths. Phase 1 has landed all three (pull requests 3193, 3212, 3213). `git fetch origin` and
`git merge origin/main` (a merge, no rebase, no force push) brought `origin/main` at `c2bbf7d36` onto
the branch with NO CONFLICT of any kind - not in the File menu, not in `OnClosing`, not anywhere - so
nothing had to be resolved and no other mission's work was changed by the resolution. The reason there
was none: between `f20bbd33b` (the main already on the branch) and `c2bbf7d36`, main touched no file
under `src/CcDirector.Avalonia` or `src/CcDirector.Avalonia.Tests` at all
(`git diff --stat abb232669 origin/main -- src/CcDirector.Avalonia src/CcDirector.Avalonia.Tests`
shows only this branch's own files, in reverse). `git diff origin/main --stat` after the merge shows
the swap's own 21 files and nothing else. The section "What the engine could not give me" is REWRITTEN
below, because it is no longer true, and the counts of that merge are in their own table.

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

After the review, on main at `f20bbd33b` plus finding 1 (each with a full build, in the foreground):

| Command | Before the review | After |
|---|---|---|
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 87 | **95 passed, 0 failed, 0 skipped** |
| `dotnet test src/CcDirector.Avalonia.Tests` | 686 | **694 passed, 0 failed, 0 skipped** |
| `dotnet build cc-director.sln` | | **0 warnings, 0 errors** |

On the merged result, main at `c2bbf7d36` (the landing run, each with a full build, in the foreground):

| Command | Count |
|---|---|
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | **95 passed, 0 failed, 0 skipped** |
| `dotnet test src/CcDirector.Avalonia.Tests` | **694 passed, 0 failed, 0 skipped** |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"` (the engine) | **606 passed, 0 failed, 0 skipped** |
| `dotnet build cc-director.sln` | **0 warnings, 0 errors** |

95 and 694 are the same counts the Tech Lead and the fifth Reviewer read before the hold, which is what
a merge that touched no Avalonia file should give; 606 is the same count the Delivery Lead read on
`origin/main` at `c2bbf7d36`. The counts were READ, not the colour: the `SmartRestart` filter matching
nothing would also exit green, and 95 is not nothing.

How 87 became 95: one test came in with main (a session reader test, pull request 3195); the one old
no-engine test was taken out, because it asserted the silent carry-on the review found; eight no-engine
tests were added (seven methods, one of them with two cases).

41 new tests in the first round, all in `SmartShutdownCoordinatorTests.cs`. Every one of the 46 earlier tests is unchanged
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
- **No engine (the control service did not start), replaced after the review.** With one idle session
  the window close is cancelled and the SAME real dialog opens: "Smart shutdown", "1 session is running",
  no "checking" line (nothing is being asked), the confirm dead, and the amber panel with the sentence
  "This Director's control service did not start, so a smart shutdown cannot run. The log has the
  reason." "Shut down and ignore all sessions" and Cancel are live. Pressing ignore-all closes the
  application exactly once, the close that comes back through the window's closing carries on with no
  second dialog, and no call of any kind was written down on the engine. Cancel, Escape and the dialog's
  own close close nothing, and the next close asks again. Enter takes nothing. With no sessions the close
  carries on with no dialog. The File menu keeps its one sentence and opens nothing.
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

After the review, committed first (`da62648b0`), the same way:

| Mutation | Result | Tests that went red |
|---|---|---|
| D. `HandleWindowClosing`: the old silent carry-on put back (no engine: `return false`) | **6 failed, 89 passed** | `HandleWindowClosing_NoEngineAndOneIdleSession_OpensTheDialogWithTheSmartChoiceDeadAndTheReasonShown`, `Dialog_IgnoreAllClickedWithNoEngine_...`, `Dialog_CancelClickedWithNoEngine_...`, `Dialog_EscapeOrItsOwnCloseWithNoEngine_...` (both cases), `Dialog_EnterPressedWithNoEngine_TakesNothing` |
| Restored with `git checkout`, `git status` clean, full build | **95 passed, 0 failed** | none |

The first of those is the one the mandate asked to see fail. The other five went red with it because
each needs the dialog to have opened.

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
6. **REPLACED after the review; the ruling is the Tech Lead's, not mine.** It used to read: no engine,
   the close carries on, logged. Now: with sessions running and no engine the window close opens the same
   dialog. The smart choice is dead and the dialog says why. "Shut down and ignore all sessions" has no
   engine to call, so it lets the close carry on through the main window's own close path, which ends the
   sessions; the owner was asked exactly once. Cancel, Escape and the dialog's own close do nothing. So
   the owner is asked AND is never trapped in the window, which was the worry behind the old decision.
   No record is written on that path, because the record is the engine's and there is no engine; the log
   says so. No "Ending your sessions..." state is shown there: the close follows at once, on the same
   path the application has always closed by. The File menu keeps its sentence.
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

## What the engine could give me, once phase 1 landed

This section USED to list three refusals. Read again in `DirectorSmartShutdown.cs` as it stands on this
merged branch (main at `c2bbf7d36`), all three are gone and each reaches real, built code. Read, not
assumed; the line numbers are this file's.

- **`Start` with purpose `Restart`** (lines 167 to 180) no longer throws `NotSupportedException`. It asks
  the restart eligibility first and throws `InvalidOperationException` with the launcher's own reason only
  when the launcher would not restart this Director - refused before anything is touched. Otherwise it
  builds and begins a real run, and the run asks the launcher at the end
  (`RunAsync` line 592, `AskLauncherAsync`). So File, Smart Restart, confirmed, now runs end to end.
- **`ShutDownIgnoringAllAsync`** (lines 217 to 309) is built: it refuses while a run or a drain holds the
  Director, writes the record first, ends every live session, makes one further pass for a session that
  appeared while the record was being written, and returns `RecordWritten`, the count ended and a detail
  in plain words. When the record cannot be written the sessions are still ended and `RecordRefusal` says
  why - which is what this flow already showed the owner.
- **`RecordAndLetEndAsync`** (lines 313 to 341) is built: it returns the existing workspace when a run has
  already written the record, and otherwise writes an operating-system-shutdown record and ends nothing.
- **`CancelAndKeepWorking`** (line 484) is honoured, and the snapshots the run publishes are the drain's
  own (`OnSnapshot = Publish`), which say `CanCancel: _recordExists` (`DirectorDrain.cs` line 2176). So
  the progress screen's "Cancel and keep working" button is live once the record exists, where it used
  to be dead in every state.

The coordinator was NOT changed for any of this and did not need to be: it always called the real
interface and always showed the engine's own words. The tests that fed it `NotSupportedException` still
stand, because an engine that refuses is a state the flow must still handle - they no longer describe
what the engine does today, and each one says so in its own name (it is an engine that throws, not an
engine that is unbuilt). I reached into no engine file.

**One thing this landing does NOT change, and the Delivery Lead should know it.** Decision 1 below (the
File menu with no sessions shows a sentence and does nothing) was decided on the OLD engine, whose
`Start` with purpose `Restart` refused outright. That reason has gone: a run with nothing to shut down
would now find the Director empty and ask the launcher. The behaviour was left exactly as it is, because
changing it is a new decision about what the File menu should do with an empty Director, not part of
landing this swap, and the mission (section 4.4) says with no sessions running there is no dialog at all.
It is written here so it is a choice somebody made, not something nobody noticed.

## What this proof does NOT cover

- **The real main window is not opened by any test.** That its menu item and `OnClosing` call the
  coordinator is read from its source, not run. The five functions the main window hands the coordinator
  (hide `SessionViewGrid`, show `SmartShutdownHost`, `Close`, `ShowNotification`, the session list) are
  not exercised by any test.
- The real engine is never called by these tests; the fake answers in its place. That is still true
  after phase 1 landed: the engine is built, and no test here runs it.
- `WindowCloseReason.OSShutdown` is handed in by the test; no operating system shut anything down.
- The native File menu is not pressed.

**By hand on the isolated rig in phase 5:** press File, Smart Restart with sessions and with none; press
the X with one idle session, with none, and twice in a row; confirm the terminal and the prompt bar are
really hidden behind the progress screen and come back after "Back to your sessions"; confirm that after
Emptied the window closes without a second dialog and the process exits; unplug the Gateway and read the
refusal in the real dialog; sign out of Windows with sessions running and read the log line.
