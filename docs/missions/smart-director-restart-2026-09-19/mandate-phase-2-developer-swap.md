# Mandate - Smart Director Restart - Developer - the swap: the two doors open the new screens

You are a Developer on phase 2 of the Smart Director Restart mission. You were opened by the phase 2
Tech Lead (session c6c50eeb) and you report to it, never to the owner and never to the fleet. You have
no transcript; this file and the files it names are your history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p2-swap`, branch `smart-restart-p2-swap`. Work
only there. It was cut from `origin/main` at `8b296fe48` and the engine branch
`origin/smart-restart/p1-engine` is MERGED INTO IT (commit `55870f183`), because the engine is pushed
and reviewed but not yet on main. Do not rebase this branch and do not merge anything else into it; the
Tech Lead moves it onto main when the engine lands. The mission record lives in ANOTHER worktree,
`D:/ReposFred/devthrottle-smart-restart-p2`; read it there, never write to it.

## ONE TURN ONLY - read twice

Nobody can wake you once your turn ends. No message reaches a stopped session. Finish EVERYTHING below,
push it, and write your proof before your turn ends. There is no second round. Run everything in the
foreground; nothing in the background; no sub-agents. Never run `rm` (or any delete) on a path built
from a variable: a safety hook stops the command and asks the owner, which hangs your seat for good.
Literal paths only; remove tracked files with `git rm <literal path>`.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. In `D:/ReposFred/devthrottle-smart-restart-p2/docs/missions/smart-director-restart-2026-09-19/`:
   `mission.md` (sections 4.4, 5.3 items 1 to 9, 5.5, 7, 8, 10.1, 10.4, 10.5; it wins over this file),
   and the last section of `review-phase-2-1.md` about the close hook's narrower rule.
3. In your worktree: `docs/missions/smart-director-restart-2026-09-19/phase-1-interface.md`, the real
   engine under `src/CcDirector.ControlApi/SmartRestart/` (read `DirectorSmartShutdown.cs` for what it
   really does, and `ControlApiHost.CreateSmartShutdown()`), and the two screens already merged under
   `src/CcDirector.Avalonia/SmartRestart/` with their tests under
   `src/CcDirector.Avalonia.Tests/SmartRestart/`.
4. `docs/CodingStyle.md`, `docs/VisualStyle.md` and the repository's `CLAUDE.md` (rules 1, 3, 4, 7).

Issues: mission #3167, and defect #3168, which this task closes.

## Your one task

Make the two doors open the new screens, and remove the old windows.

`MainWindow.axaml.cs` is a very large file other missions edit every day. Keep the change to it and to
`MainWindow.axaml` as SMALL as you can: put the flow in ONE new class under
`src/CcDirector.Avalonia/SmartRestart/` (a coordinator of your naming, with `SmartRestart` or
`SmartShutdown` in it), given everything it needs through its constructor - the engine
(`ISmartShutdown`), a way to read the current `Session` objects, a way to show the dialog, a way to put
the progress screen in place of the session view and to take it away again, and a way to close the
application - so that the whole flow is tested with fakes and without the main window. The main window
builds it, calls it from the two doors, and does nothing else new.

What the flow does:

- **The File menu.** "Smart Restart" replaces "Drain this Director for restart..." (mission 10.1: no
  "Director" in the words). It opens the dialog with the door for the File menu; the purpose is
  `SmartShutdownPurpose.Restart`.
- **The window close** (`OnClosing`), on every platform, opens the SAME dialog with the door for the
  window close; the purpose is `SmartShutdownPurpose.Close`. It replaces `CloseDialog`.
- **No sessions running: no dialog at all.** The close carries on as it does today; the File menu item
  asks the engine to restart with nothing to shut down, if and only if the engine as built supports
  that with zero sessions - read it; if it does not, the menu item says plainly in a small message that
  there are no sessions to shut down and does nothing else, and you write down which in the proof. The
  decision "are there sessions" and the count the dialog shows MUST come from the same list, the one
  `SmartShutdownSessionReader` builds. Today's close hook uses a narrower rule (working or waiting for
  input only); that rule goes.
- **The dialog opens at once and asks the engine after** (`CheckAsync` may take a second or two; never
  block the interface thread on it). While the answer is on its way the smart choice is not yet live and
  the dialog says it is checking. When `CanSmartShutdown` is false, the smart choice stays dead and the
  dialog shows `SmartShutdownRefusal` as given (the Gateway cannot be reached: the record must live off
  the machine); "Shut down and ignore all sessions" and Cancel still work. From the File menu, when
  `CanRestart` is false, the confirm stays dead and the dialog shows `RestartRefusal` as given. This is
  an addition to the merged dialog and its view model: keep it small, keep every existing dialog test
  green, and add tests for the three states (checking, may, may not with the reason) that drive the
  real window.
- **Cancel, Escape, the dialog's own close: nothing happens.** The window stays open, no session is
  touched.
- **Smart shutdown chosen:** build the `SmartShutdownRequest` (the purpose, the time allowed the owner
  picked), call `Start`, and put `ShutdownProgressView` with a `ShutdownProgressViewModel` on the run in
  place of the session view. When the run is over (the view model's finished event):
  - `Emptied`: close the application through the main window's existing close path, without asking
    again;
  - `RestartAccepted`: do nothing; the launcher stops the process;
  - `Cancelled`: take the progress screen away and put the session view back, at once;
  - `Refused`, `RestartRefused`, `Failed`: the owner must be able to READ the engine's reason before the
    session view comes back. Do it the smallest way that is true to `docs/VisualStyle.md` and write down
    what you chose.
  `Start` throws `InvalidOperationException` when a run is already under way: show its message, change
  nothing.
- **While a run is under way** a second press of the window close or of the menu item opens nothing and
  closes nothing: the progress screen is already the answer. The close is cancelled and logged.
- **Shut down and ignore all sessions chosen:** call `ShutDownIgnoringAllAsync` without blocking the
  interface thread, showing at once a plain "Ending your sessions..." state in place of the session view
  (no rows, no buttons; this is NOT the progress screen). Then, for the window close, close the
  application. For the File menu, read what the engine does for the purpose `Restart` on this path and
  do what is true to it; write down what you found. If `RecordWritten` is false the sessions are still
  ended (the owner chose to discard them); log `RecordRefusal`.
- **The operating system is shutting down** (`WindowClosingEventArgs.CloseReason` is
  `WindowCloseReason.OSShutdown`): NO dialog and no progress screen. Call `RecordAndLetEndAsync` with a
  token that gives up after five seconds, log what came back, and let the close carry on as it does
  today. Every other close reason goes through the dialog as above.
- **Remove `DrainDirectorDialog` and `CloseDialog`** (both files of each) and anything that only they
  used, once nothing calls them. Search `src` and `tools` for both names, including tests and comments,
  and leave none behind that claims they exist. Do not touch the Drain engine under
  `src/CcDirector.ControlApi/Drain/`; other doors still use it.
- You do NOT change anything under `src/CcDirector.ControlApi`, `src/CcDirector.Gateway*` or
  `src/CcDirector.Core`. If the engine cannot do something this mandate asks, do not reach into it:
  write it in the proof under "what the engine could not give me" and do the smaller true thing.
- Log as `docs/CodingStyle.md` says. Try-catch only at entry points (the two doors are entry points).
  No fallbacks. ASCII only, everywhere, including interface text. Do not define your own
  `InitializeComponent` in anything.

## Tests - the law of this phase

This pull request merges only with the headless window tests green. Tests go in
`src/CcDirector.Avalonia.Tests/SmartRestart/`, namespace `CcDirector.Avalonia.Tests.SmartRestart`, named
`Method_Scenario_Result`. Use a fake `ISmartShutdown` whose run is the existing test fake for
`ISmartShutdownRun`. Drive REAL windows and views with real clicks and keys; never hand-build a result
and assert on it. At least:

- zero sessions: no dialog is opened, and the close carries on;
- the count the dialog shows and the no-dialog decision come from the same list: one idle session is
  enough to open the dialog (this is the narrower-rule warning from the review);
- each door opens the dialog with its own words and hands the engine its own purpose;
- Cancel, Escape and the dialog's own close: the engine is never called, nothing is closed;
- the smart choice: `Start` gets the purpose and the time the owner picked (pick 30 minutes in the real
  dropdown), and the progress screen is what is shown;
- each of the six outcomes does what is written above, and the application is closed exactly once on
  `Emptied` and never on the others;
- a second close, and a second menu press, while a run is under way: nothing opens, nothing closes;
- the three states of the engine's check in the real dialog, with the refusal words shown as given, and
  ignore-all still working when the smart choice may not be used;
- ignore all: the engine's call is made once, the plain ending state is shown at once, and the close
  follows for the window close door;
- the operating system shutting down: no dialog, `RecordAndLetEndAsync` called once, the close carries on;
- a test that reads the source of `MainWindow.axaml.cs` and fails if it names `DrainDirectorDialog`,
  `CloseDialog` or "Drain this Director" - and prove it can fail.

For at least the zero-sessions test and the one-idle-session test, prove the test can fail: break the
code it guards, watch it go red, put it back, with a full build each time (never `--no-build`), and
write down both results. Commit BEFORE you mutate, so restoring cannot eat your work.

Pictures, written only when `SMART_RESTART_SCREENSHOT_DIR` names a folder, into
`docs/missions/smart-director-restart-2026-09-19/attachments/phase-2/` in your worktree: the dialog
while checking; the dialog with the smart choice refused and its reason; the plain ending state; the
progress screen after a failed run with whatever you built for reading the reason. LOOK at each one.

## Done

1. With a full build each time, in the foreground:
   `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` - read the
   COUNT (it is 46 before your work), state it; `dotnet test src/CcDirector.Avalonia.Tests` - state the
   count (645 before, on main without the engine); `dotnet build src/CcDirector.Avalonia` - 0 warnings,
   0 errors; and `dotnet build cc-director.sln` clean, because you removed two windows.
2. `docs/missions/smart-director-restart-2026-09-19/attachments/phase-2/swap-proof.md` in your worktree:
   what was built, the size of the change to `MainWindow.axaml.cs` in lines, the counts, what each new
   test proves in plain words, the revert proofs with their numbers, the pictures, every decision you
   made because nobody could be asked, what the engine could not give you, and what the proof does NOT
   cover (the real main window is not opened by any test; say so, and say what a person must try by
   hand on the isolated rig in phase 5).
3. Commit and push the branch. Do NOT open a pull request and do NOT merge: the work goes to a Reviewer
   first, and the engine must land on main before this can.
4. Last of all: `cc-devthrottle session report "<one paragraph: what you did, the counts, what you could not reach>"`.

## Rules that do not bend

- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", no robot
  emoji, in any commit, file or comment. ASCII only in everything. Plain English, no abbreviations.
- Never kill a running process. Never launch, restart, drain or stop a Director or a session, and never
  run this feature against a real Director. Headless tests only.
- If something is undecidable inside this mandate, decide the smaller way, write down what you decided
  and why in the proof, and carry on. Nobody can answer a question this turn.
