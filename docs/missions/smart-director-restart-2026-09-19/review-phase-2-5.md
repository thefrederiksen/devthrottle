# Review - Smart Director Restart - phase 2 - task 3 second round: the close still asks with no engine

Reviewer seat, opened by the phase 2 Tech Lead (session c6c50eeb). Worktree
`D:/ReposFred/devthrottle-smart-restart-p2-review5`, detached checkout of `43ac8c3e8` (branch
`smart-restart-p2-swap`). No tracked file in this worktree was changed by this review. The throwaway
worktree this review built its mutation proof in was removed afterwards; this worktree was untouched
throughout and `git status` is clean.

## SCOPE

What I reviewed: `git diff abb232669 HEAD`, two commits (`da62648b0`, `43ac8c3e8`), answering finding 1 of
`review-phase-2-4.md` under the Tech Lead's ruling (mandate for the answering Developer, step 2). The
whole swap was reviewed in the first round and was NOT reviewed again. Files read in full:
`SmartShutdownCoordinator.cs`, `SmartShutdownViewModel.cs`, `SmartShutdownDialog.axaml` and
`SmartShutdownDialog.axaml.cs`, `SmartShutdownCoordinatorFakes.cs` (the rig), the changed hunks of
`SmartShutdownCoordinatorTests.cs` with the test file around them, the changed hunks of the proof, the
answers file `review-phase-2-4-answers.md` (read, not trusted; every claim in it that my scope touches
was checked against the code), the wiring and the `OnClosing` body of `MainWindow.axaml.cs` (lines 6604
to 6683), and the commit that came in from main (`f20bbd33b`, the session reader change, to check the
claimed count arithmetic).

What I ran, all in the foreground, all in this worktree except the mutation:

- `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"`:
  **95 passed, 0 failed, 0 skipped** - the same count the Tech Lead and the Developer reported.
- `dotnet test src/CcDirector.Avalonia.Tests` (whole project): **694 passed, 0 failed, 0 skipped** - the
  same count the Developer reported.
- **The revert proof, reproduced by me, not taken from the answers file.** A throwaway worktree was cut
  at a literal path outside this checkout, the old silent carry-on was put back in
  `SmartShutdownCoordinator.HandleWindowClosing` there (no engine: log and return false), and the same
  filter run: **6 failed, 89 passed** - the same six test names the proof's table D lists, the first of
  them the one my mandate asked to see fail
  (`HandleWindowClosing_NoEngineAndOneIdleSession_OpensTheDialogWithTheSmartChoiceDeadAndTheReasonShown`,
  red on its `Assert.True` that the close was cancelled). Restoring needs no statement from me: this
  worktree was never touched, and the throwaway worktree was removed.

What I could not reach, and did not pretend to:

- The real main window. No test opens it, and I did not either; "the close carries on through the main
  window's existing close path, which ends the sessions" is TRUE by reading `MainWindow.OnClosing`
  (when the coordinator returns false the rest of the body runs, and it calls `OnShutdown`, which ends
  the sessions and exits the process), but it is a reading, not a run. This is the standing gap the
  answers file itself names, with the manual check planned in phase 5 on the isolated rig; it is not new
  to this round and I add nothing to it.
- The real control service failing to start. The tests hand the coordinator a factory that returns
  nothing; the real `ControlApiHost` failure was not produced.
- The operating system shutting down with no engine. Read, not driven; this diff does not touch that
  path (it still records nothing and carries on, logged).
- No manual run of the feature was made against a real Director; my mandate forbids it.

## FINDINGS

None. Every question my mandate asked is answered and true, and I found no path this fix opened by
which the window closes unasked or can never close.

## WHAT I CHECKED AND FOUND TRUE

- **The close now asks.** `HandleWindowClosing`, with sessions running and no engine: the close is
  cancelled, the same dialog opens, and the log line says so. The first new test proves it against the
  real dialog: title "Smart shutdown", "1 session is running", no "checking" line (nothing is being
  asked, so `ApplyNoEngine` runs BEFORE the dialog is shown and the checking line never appears), the
  confirm dead, the amber panel visible with the plain sentence "This Director's control service
  did not start..." - the exact words are asserted against the sentence written out, not a constant -
  and ignore-all and Cancel live.
- **Ignore-all with no engine closes the application exactly once and calls no engine.** The choice
  needs no engine call, so the flow sets the stage to Idle and calls the same `CloseTheApplication` the
  other paths use: `_closeAllowed` is set, the main window's own close path runs, and the close that
  comes back through `HandleWindowClosing` carries on without a second dialog - the test asserts the
  close happened once, the dialog count stays at one, and `AssertTheEngineWasNeverCalled` proves no
  check, no start, no ignore-all and no record was written down on the engine. Read against the real
  window: `Close` is the window's own, `OnClosing` asks the coordinator, the coordinator says carry on
  because the close was asked for, and the rest of `OnClosing` ends the sessions. The owner was asked
  exactly once.
- **Cancel, Escape and the dialog's own close leave everything as it was.** All three come back as
  Cancelled: the stage returns to Idle, nothing was shown in place of the session view, nothing closed,
  no engine call, and the next close asks again (the test drives a second close and asserts a second
  dialog). Enter takes nothing: the confirm is disabled and the default button does not fire it, which
  the Enter test drives with a real key press.
- **The zero-sessions close is still silent**, with and without an engine: the session count is read
  before the engine is, so the close carries on with no dialog (test).
- **No new unasked-close path and no never-close trap.** The only new call that closes the application
  follows an explicit choice in the dialog. A smart shutdown chosen with no engine is thrown as a
  defect in the flow and caught at its door: the stage returns to Idle, the failure is shown, and the
  window stays open and closable. Every failure path leaves the stage at Idle; the dialog always offers
  Cancel and ignore-all; the operating system shutdown path is untouched. The unused
  cancellation source created on the no-engine branch holds nothing to let go of, by the class's own
  comment about sources with no timer and no wait handle.
- **The tests drive the real dialog and would go red if the defect came back.** The rig builds a real
  `SmartShutdownDialog` shown over a real window; clicks are real button click events; Escape and Enter
  are real key presses through the headless platform; only the engine and the application-close counter
  are fakes, and the counter stands for the window the tests cannot open. My own mutation run proved
  the red: the old silent carry-on put back makes exactly the six tests the proof names fail, and the
  mandated first of them fails on the assertion that the close was cancelled.
- **The counts and the arithmetic reproduce**: 95 on the filter, 694 on the whole project, and the
  "one came in with main" test is the session reader change in `f20bbd33b`, which touches
  `SmartShutdownSessionReaderTests.cs`.

END OF REVIEW
