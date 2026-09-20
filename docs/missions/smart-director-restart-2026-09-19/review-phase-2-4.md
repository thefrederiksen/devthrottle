# Review - Smart Director Restart - phase 2 - task 3: the swap of the two doors

Reviewer seat, opened by the phase 2 Tech Lead (session c6c50eeb). Worktree
`D:/ReposFred/devthrottle-smart-restart-p2-review4`, detached checkout of `52c5c2b54` (branch
`smart-restart-p2-swap`). Nothing in the worktree was changed by this review.

## SCOPE

What I reviewed: `git diff 55870f183 HEAD`, which is two commits (`af79c4d51`, `52c5c2b54`): the
coordinator, the surface, the dialog and view model additions, the main window wiring, the two removed
windows, the tests and fakes, the proof and the four pictures. Files read in full, line by line where the
mandate asked for it: `SmartShutdownCoordinator.cs`, `SmartShutdownSurface.axaml.cs`,
`SmartShutdownViewModel.cs`, `SmartShutdownDialog.axaml.cs`, `SmartShutdownSessionReader.cs`, both
changed hunks of `MainWindow.axaml.cs` and `MainWindow.axaml`, `SmartShutdownCoordinatorTests.cs`,
`SmartShutdownCoordinatorFakes.cs`, `ShutdownProgressFakes.cs`. Read to judge the call, not to review:
`DirectorSmartShutdown.cs`, `ISmartShutdown.cs`, `ControlApiHost.CreateSmartShutdown()`,
`ShutdownProgressView` and `ShutdownProgressViewModel`, `App.axaml.cs` (who can close the window, and when
the control service host can be null), the `ActivityState` list, the File menu around the new item. Also
read: the Developer mandate, `mission.md` (sections 4.4, 5.3 items 1 to 9, 7, 10.4, 10.5), the Developer's
proof, and the repository rules named in my mandate.

What I ran, all in the foreground:

- `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"`:
  **87 passed, 0 failed, 0 skipped** - the same count the Tech Lead reported.
- `dotnet test src/CcDirector.Avalonia.Tests` (whole project): **686 passed, 0 failed, 0 skipped** -
  the same count the Developer reported.
- Searches of `src` and `tools` for `DrainDirectorDialog`, `CloseDialog` and "Drain this Director".

What I could not reach, and did not pretend to:

- The four pictures. This seat cannot view images, so I verified only that they exist, are of real size,
  and are written by a test that asserts the frame is drawn. Their readability is the Developer's look,
  not mine. The dialog markup was read against the visual style guide's disabled-button rule by text only.
- The real `MainWindow` was never opened by any test, and I did not open it either (the mandate forbids
  running the feature against a real Director). Its wiring was read against the coordinator's constructor,
  which is exactly what the proof says its source test does; the gap stands as the proof states it.
- The real engine was never called by me. Every statement below about the engine is a reading of
  `DirectorSmartShutdown.cs` as merged at `55870f183`.
- `dotnet build cc-director.sln` was not re-run; the two test runs above built every project the change
  touches, and no file in `src` or `tools` names the removed windows.

## FINDINGS

1. **The window can close with live sessions and no question when the Director has no engine, and that
   was decided alone.** `SmartShutdownCoordinator.HandleWindowClosing`, lines 176 to 180: when the engine
   factory returns nothing, the close carries on with no dialog, no message, nothing. The factory
   (`MainWindow.axaml.cs`, line 6605) returns nothing exactly when `ControlApiHost` is null, which is a
   real state: `App.StartControlApi` catches the failure of `new ControlApiHost(...)` and the application
   carries on without it. In that state an owner with working sessions closes the window and they are
   ended by the ordinary close path, unasked - the one behaviour the mission's dictation rules out
   (section 4.4: if any sessions are running, show the number and ask). The File menu in the same state
   does say a sentence; the close says nothing to the owner, only to the log. The Developer chose this
   (proof, decision 6) because the alternative, cancelling a close that nothing could ever complete,
   traps the owner in the window, and that argument is real. I report it because the harm is real too and
   the mission does not cover the state; the Tech Lead should rule on it, not inherit it. A third way
   exists if the ruling wants one: a plain question with no smart choice in it, asked without the engine.

2. **This branch must not merge ahead of the finished engine, and the stated merge gate would not stop
   it.** Read against the engine merged into this branch at `55870f183`:
   `DirectorSmartShutdown.Start` throws `NotSupportedException` for purpose `Restart` (lines 126 to 132),
   `ShutDownIgnoringAllAsync` throws (lines 170 to 173), `RecordAndLetEndAsync` throws (lines 178 to 182).
   So at this commit: File, Smart Restart, confirmed always ends in the engine's "not built yet" sentence
   and nothing else; "Shut down and ignore all sessions" from either door always ends the same way, with
   nothing ended; an operating system shutdown records nothing. Only the window close with the smart
   choice runs end to end. I checked the Developer's claims about this and every one is true; the code
   handles each refusal honestly, shows the engine's own words, and changes nothing - and the proof says
   all of it under "what the engine could not give me". This is not a defect inside this diff and demands
   no change to it. It is a condition on the branch: the gate in mission section 8 for this pull request
   is "headless window tests green", and those tests are green precisely because they fake the engine - so
   the gate as written would let this branch merge with two of the three dialog choices dead. Whoever
   merges must hold it until the engine branch has grown the restart purpose, ignore-all and the
   operating system record, or the File menu door ships as a dead end.

## WHAT I CHECKED AND FOUND TRUE

The mandate's questions, each answered from the code and the tests:

- **Every path through `OnClosing`.** The window cannot close unasked with sessions running except in the
  two designed cases (the operating system shutting down, and the close the flow itself asked for) and
  the one state in finding 1. It cannot become impossible to close: `_closeAllowed` is set only
  immediately before the flow's own close and is never left set by a cancel; a failed or refused run
  leaves the stage at `Idle` with the reason on screen and the doors working; an exception in the
  coordinator is caught at the door, logged, shown in the notification bar, and leaves the stage at
  `Idle`. A close while a run, an ending or a dialog is under way is cancelled and opens nothing (tests:
  `HandleWindowClosing_WhileARunIsUnderWay...`, `...WhileTheDialogIsOpen...`, and the ignore-all test's
  second-close assertion). The close after `Emptied` is asked exactly once
  (`RunFinished_Emptied_ClosesTheApplicationExactlyOnce...`, which also proves the returning close is not
  asked about again).
- **The main window wiring**, read line by line against the constructor: eight arguments, correct order,
  matching types; `ShowNotification` is `void ShowNotification(string)`; `Close` is the window's own;
  `TimeProvider.System` for the clock; the default five second limit for the operating system record. The
  session view is put back by visibility only, so the terminal and prompt are untouched and the view is
  usable after `Cancelled` (test: `RunFinished_Cancelled_PutsTheSessionViewBackAtOnce`).
- **The operating system shutting down.** No dialog, the record attempted inside a five second limit on a
  pool thread, and the close carries on whatever happens, including the engine throwing
  (`HandleWindowClosing_TheOperatingSystemIsShuttingDown...`, two tests). The wait does block the
  interface thread for at most the limit; that is the documented and, I judge, correct decision - the
  close that follows ends the process, so an unwaited record is never written, and the wait cannot stop
  the close because every failure path returns false.
- **One list.** The no-dialog decision, the count the dialog shows, the File menu's "nothing to shut
  down" and the operating system record all read `SmartShutdownSessionReader`, which counts every session
  whose state is not `Exited` (so an idle session and a `WaitingForPerm` session now count, where the old
  hook asked only about `Working` and `WaitingForInput`). The revert proof shows the one-idle-session test
  goes red when the narrow rule is put back.
- **The engine's check in the dialog.** The dialog opens before the engine is asked and never blocks the
  interface thread (`CheckIntoAsync` runs beside it); the refusal words are shown as given
  (`ApplyAvailability` copies them, and a refusal without a reason is thrown at the engine, not papered
  over); ignore-all and cancel stay live when the smart choice may not be used; a check that throws is
  shown as a dead confirm with the failure; an answer that arrives after the dialog closed is dropped.
- **Ignore all.** The interface thread is not blocked (the engine call is awaited); the call is made once
  (asserted `Assert.Single` in both door tests); from the File menu the coordinator passes purpose
  `Restart` and, on a successful result, keeps the Director up and says it was not restarted - which is
  written against the engine's contract. Against the engine as built today that success path cannot be
  reached; the throw path is tested with the engine's real words.
- **Zero sessions from the File menu.** The Developer's claim is true of the engine as built: `Start`
  refuses purpose `Restart` outright, a restart exists only as the end of a run, and a run is a shutdown of
  sessions, so with none there is nothing to ask. The one sentence shown is honest.
- **The tests.** They drive the real coordinator, real dialog (real clicks, real Escape, real close),
  real surface and real progress view; only the engine and the run are fakes, which is the engine's
  boundary, and the outcomes are stepped through the real progress view model's finished event, not
  handed to the coordinator. The source-reading test can fail: the finder is proved on all three old
  names, and the Developer's revert proof shows it red on a planted comment. The revert proofs for the
  zero-sessions and one-idle-session tests are recorded with numbers. I found no test that hand-builds a
  choice or a result the flow should have produced.
- **The two removed windows.** Nothing in `src` or `tools` names them or depended on them. Two lines
  remain by design and are documented: a test comment saying the old window was removed, and the summary
   "Drain this Director to a record on the Gateway" in `DirectorRestartCycle.cs`, which is about the
  drain engine, names no window, and sits in a project this task was forbidden to touch.
- **Blocked threads, swallowed errors, fallbacks.** None found beyond the documented operating system
  wait. Every catch logs; every failure reaches the owner in words; nothing invents a state to hide one.

END OF REVIEW
