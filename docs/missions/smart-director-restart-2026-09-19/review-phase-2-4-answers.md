# Answers - review of phase 2, task 3: the swap of the two doors

Answers to `review-phase-2-4.md`. Written by the Developer seat the phase 2 Tech Lead (session c6c50eeb)
opened for the findings; the Developer who built the swap was gone. **Both decisions below are the Tech
Lead's.** They were given to this seat in its mandate, and this seat carried them out; it did not rule
on either.

Branch `smart-restart-p2-swap`, 20 September 2026.

## First, the branch was brought up to main

`git merge origin/main` at `f20bbd33b` (merge commit `abb232669`). The engine had landed on main as a
squash (pull request 3193). One file conflicted:
`docs/missions/smart-director-restart-2026-09-19/proof-phase-1-task-3.md`, the engine's own proof, which
this seat does not own; main's version was taken whole. No file under `src` conflicted. Afterwards
`git diff origin/main --stat` shows only the swap's own files: the coordinator, the surface, the dialog
and its view model, the two main window files, the four removed window files, the swap's tests and
fakes, one reworded comment in the dialog tests, the proof and its four pictures. No engine file.

## Finding 1 - the window can close with live sessions and no question when the Director has no engine

**ACCEPTED. The ruling is the Tech Lead's.**

The reason: the owner said that if any sessions are running, the number is shown and he is asked. A
Director whose control service did not start is a real state, and in it the close ended working
sessions without a word. The first Developer's worry, that a cancelled close with nothing to finish it
traps the owner in the window, is answered by the ruling rather than overruled: the dialog's ignore-all
choice lets the close carry on.

What was done (commit `da62648b0`):

- `SmartShutdownCoordinator.HandleWindowClosing`: with sessions running and no engine the close is
  cancelled and the SAME dialog opens.
- `SmartShutdownViewModel.ApplyNoEngine`: the confirm is dead from the start, nothing says "checking"
  because nothing is being asked, and the refusal panel says: "This Director's control service did not
  start, so a smart shutdown cannot run. The log has the reason." The heading above it already says
  that the other two choices still work.
- "Shut down and ignore all sessions" there calls no engine, because there is none. It lets the close
  carry on through the main window's existing close path, which ends the sessions. The owner is asked
  exactly once: the close that comes back is not asked about again.
- Cancel, Escape and the dialog's own close do nothing; the next close asks again.
- With zero sessions the close still carries on with no dialog. The File menu keeps its sentence.
- The operating system shutting down is unchanged: no dialog, and with no engine nothing is recorded,
  which is logged.

Tests, all driving the real dialog with real clicks and keys: no engine and one idle session opens the
dialog with the smart choice dead and the reason shown; ignore-all there closes the application exactly
once and no call is written down on the engine; cancel there closes nothing; Escape and the dialog's own
close close nothing; Enter takes nothing; no engine and no sessions opens nothing; the File menu says
why. The old test that asserted the silent carry-on was removed, because it asserted the defect.

Proved it can fail: committed first, then the old silent carry-on put back, full build, the whole
`SmartRestart` filter: **6 failed, 89 passed**, the first of them
`HandleWindowClosing_NoEngineAndOneIdleSession_OpensTheDialogWithTheSmartChoiceDeadAndTheReasonShown`.
Restored with `git checkout`, `git status` clean, full build: **95 passed, 0 failed**.

Decided by this seat the smaller way, because nobody could be asked:

1. **No "Ending your sessions..." state on the no-engine path.** With an engine that state covers the
   wait for the engine's answer. Here there is no answer to wait for; the close follows at once on the
   path the application has always closed by.
2. **No record is written on that path, and the dialog does not say so.** The record is the engine's.
   The dialog's own words for ignore-all already say the sessions are ended at once and no handovers
   are written; the log line says there was no engine.
3. **The words live once, in the view model** (`NoEngineReason`), beside the dialog's other words, and
   the test compares against the sentence written out, not against the constant.

## Finding 2 - this branch must not merge ahead of the finished engine

**ACCEPTED as a hold on the merge, with no change to the code. The ruling is the Tech Lead's.**

The reason: the finding is true and is not a defect inside this diff. Read again in main's copy of
`DirectorSmartShutdown.cs` at `f20bbd33b`: `Start` still throws for the purpose `Restart`,
`ShutDownIgnoringAllAsync` still throws, `RecordAndLetEndAsync` still throws. The swap's tests are green
because they fake the engine, so "headless window tests green" would let this branch merge with File,
Smart Restart a dead end, ignore-all ending nothing from either door, and an operating system shutdown
recording nothing.

**This branch does not merge until the engine on main has all three: the restart purpose, ignore-all,
and the operating system record.** No pull request was opened and nothing was merged by this seat.
Nothing else was done about this finding.

## The counts, each with a full build, in the foreground

- `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"`:
  **95 passed, 0 failed, 0 skipped** (87 before: one test came in with main, one removed, eight added).
- `dotnet test src/CcDirector.Avalonia.Tests`: **694 passed, 0 failed, 0 skipped** (686 before).
- `dotnet build cc-director.sln`: **0 warnings, 0 errors**.

## What this does NOT cover

The same gaps the proof names still stand. No test opens the real main window, so that "the main
window's existing close path ends the sessions" is read from the application, not run: in these tests
the close is a counter. The real `ControlApiHost` failing to start was not produced; the test hands the
coordinator a factory that returns nothing. By hand on the isolated rig in phase 5, add: start a
Director whose control service cannot start, open a session, press the X, read the dialog, press
"Shut down and ignore all sessions" and confirm the window closes once and the session's process ends.
