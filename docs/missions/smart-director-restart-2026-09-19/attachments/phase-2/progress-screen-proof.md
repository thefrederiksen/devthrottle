# Proof - the smart shutdown progress screen, rebuilt against the real run

Smart Director Restart, phase 2, task 2, second round: the answer to `review-phase-2-2.md`. Every
finding and what was done about it is in `../../review-phase-2-2-answers.md`.

## What the screen is now

A view, `CcDirector.Avalonia.SmartRestart.ShutdownProgressView`, that is given one
`CcDirector.ControlApi.SmartRestart.ISmartShutdownRun` and a `TimeProvider`, and nothing else. Nobody
calls it yet; a later task puts it in place of the session view.

- The engine words, the screen lays out. `CountLabel`, `PhaseLabel`, `Note`, each row's `StateLabel`
  and `Detail`, and the result's `Detail` are drawn exactly as given. The screen has no sentence of
  its own for a state, a phase or a count, and it computes no count.
- The screen's own work: a colour for each of the ten states, an indent for a row that has an
  `OwnerSessionId`, the time left against `LimitUtc`, and the sentence "No reason was given." when a
  `NotDelivered` row arrives with no `Detail` (drawn and logged, as the reviewer cleared it).
- An enumeration value the screen was never taught (a state or a phase) throws.
- Each snapshot replaces what is shown. A row is one immutable engine record; a row whose record
  changed in any way is replaced whole, so nothing from an older snapshot can survive inside it.
- "Shut down now" is live only while `CanShutDownNow`, "Cancel and keep working" only while
  `CanCancel`. A press goes dead BEFORE the call is sent and stays dead for the life of the screen.
- The end of the run: the result's last snapshot and its `Detail` are shown, both buttons and the time
  left are hidden, and ONE event, `ShutdownProgressViewModel.Finished`, carries the result to the
  caller. The screen closes nothing.
- Every change to anything bound happens on the interface thread. A change raised by the engine is
  always posted, never applied in place, so snapshots land in the order they were raised.
- The view model is disposable. It lets go of the run's `Changed` event, takes its continuation off
  the run's `Completion` task and stops its one-second clock. The view disposes it in
  `OnDetachedFromVisualTree`. It also lets go by itself when the run completes.
- The invented interface (`IShutdownProgressSource`) and the ignore-all kind are gone.

## The counts

Rebased onto `origin/main` at `912340ed8`. Every run below was in the foreground with a full build,
never `--no-build`.

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 21 passed, 0 failed |
| `dotnet test src/CcDirector.Avalonia.Tests` (the whole project) | 620 passed, 0 failed, 1 minute 1 second |
| `dotnet build src/CcDirector.Avalonia` | 0 warnings, 0 errors |

The reviewer measured the whole project at `912340ed8` without this branch at 599. 599 and these 21
make 620.

`Avalonia.Skia` is NOT referenced by the test project any more: the project file is the same as on
`origin/main`. The pictures still draw (the picture test fails on a blank frame, and I looked at all
eight), so Skia does arrive through the application project, as the dialog branch found.
`HeadlessTestApp.cs` was taken byte for byte from `origin/smart-restart-p2-dialog`. The application
brushes it adds are not read by this screen, so there was nothing to agree with.

## What each test proves, in plain words

All twenty-one open the REAL view inside a window. Button tests click with the mouse, because a real
click does not reach a disabled button and a raised event does. The labels in the snapshots are
deliberately odd ("the engine says: ...") so a label seen on screen can only have come from the
snapshot.

1. `Show_InsideAWindow_ConnectsEveryNamedControl` - the view opens and all nine named controls are
   connected.
2. `Snapshot_TheEnginesLabels_AppearWordForWord` - count, phase, note, state and detail appear exactly
   as sent. The count label says "7 of 3" over one row, so a screen that counted would show otherwise.
3. `Snapshot_ASecondOne_ReplacesEverythingTheFirstShowed` - the second snapshot drops a row, reorders,
   renames, clears a detail and the note; no text block left in the window holds a word of the first.
4. `Rows_AllTenStates_DrawTheEnginesWordsEachWithAColour` - all ten states (the test first asserts
   there are ten) draw their label with a solid colour; the `NotDelivered` row with no detail says
   "No reason was given."
5. `UnknownValues_AStateOrAPhase_Throw` - state 99 and phase 99 are refused.
6. `Rows_ASessionUnderALead_IsDrawnBeneathItAndIndented` - rows are in snapshot order and the row
   under a lead starts at least 20 pixels to the right of its lead, measured in the window.
7. `Buttons_TheEngineSaysCancelMayNotBeUsed_CancelIsDeadAndAClickCallsNothing` - at the limit both
   buttons are drawn and dead; real clicks call nothing; the phase words do not move (finding 2a).
8. `Buttons_EachFollowsItsOwnPermission` - one button live and the other dead, then the reverse.
9. `ShutDownNow_Pressed_CallsOnceAndIsDeadBeforeTheNextSnapshot` - the fake stays silent, so only the
   screen can have killed the button; a second click sends nothing; a later snapshot that says the
   button may be used does not bring it back; the screen says nothing of its own.
10. `CancelAndKeepWorking_Pressed_CallsOnceAndIsDeadBeforeTheNextSnapshot` - the same for cancel.
11. `ShutDownNow_TheEngineAnswers_BothButtonsGoDeadAndTheEnginesWordsShow` - the fake answers as the
    engine does (both permissions go); both buttons die and the engine's phase words show.
12. `TimeLeft_AgainstTheEnginesLimit_CountsDownAndNeverGoesBelowZero` - 10:00, 6:40, 0:05, then 0:00
    three hours past the limit. The test sets the time; nothing sleeps.
13. `TimeLeft_ASnapshotWithAnotherLimit_IsReadAgainstTheNewLimit` - the limit is the engine's.
14. `TimeLeft_ByPhase_ShownOnlyWhileSessionsAreBeingGivenTime` - over all eight phases (asserted to be
    eight), the time left shows in `Asking`, `Collecting` and `Interrupting` and in no other.
15. `Completion_EachOfTheSixOutcomes_ShowsItsDetailAndRaisesFinishedOnce` - for each of the six
    outcomes (asserted to be six): the detail shows as sent, `Finished` is raised exactly once with
    that result, buttons and time left are hidden, and the window is still open.
16. `Completion_AFailedRun_LeavesNoLiveButtonAndLetsGoOfTheRun` - after `Failed` no button is visible
    or enabled, a press sends nothing, the run has no subscriber and the clock is stopped (finding 2d).
17. `Completion_TheResultsFinalSnapshot_IsWhatTheScreenEndsOn` - the last snapshot is handed over
    only inside the result, never pushed, and the screen ends showing it.
18. `Completion_ARunAlreadyOver_StillReachesACallerThatSubscribesAtOnce` - a `Refused` run that was
    over before the screen was built still raises `Finished` once.
19. `Changed_RaisedFromABackgroundThread_ChangesEverythingBoundOnTheInterfaceThread` - a snapshot and
    the completion are raised from another thread (asserted to be another thread). Every row change,
    every property change and the `Finished` event were recorded with the thread that made them; all
    were the interface thread, and six named properties are asserted to have changed at all.
20. `Detached_TakenOutOfItsWindow_LetsGoOfTheRunAndStopsItsClock` - finding 1, below.
21. `Capture_EveryMomentOfTheScreen_DrawsARealPicture` - eight frames, each with more than 100 distinct
    colours (a blank has a handful).

## Revert proof one - letting go (finding 1)

The test first pins three presences: one subscriber, a running clock, and one snapshot that DID reach
the screen while attached. Then the view is taken out of its window, and a snapshot and a completion
are raised from another thread. Asserted: zero subscribers, clock stopped, the count of change events
that reached the screen still 1 (so nothing was posted to the interface thread), and count, phase,
rows, result and `Finished` all untouched.

- With `_run.Changed -= OnRunChanged;` taken out, full build, the whole SmartRestart set:
  **2 failed, 19 passed** - this test and test 16, both "Expected: 0, Actual: 1" on the subscribers.
- Put back, full build: **21 passed, 0 failed**.

## Revert proof two - no hand-written InitializeComponent

- With `private void InitializeComponent() => AvaloniaXamlLoader.Load(this);` added to the view, full
  build, the whole SmartRestart set: **18 failed, 3 passed**. The opening test failed with
  "Assert.NotNull() Failure: Value is null".
- Taken out, full build: **21 passed, 0 failed**.

Both changes were restored from the commit with `git checkout`, the tree was checked to equal the
commit, and the final counts above were run after that.

## The pictures

In this folder, written by test 21 when `SMART_RESTART_SCREENSHOT_DIR` names a folder. I opened and
looked at all eight. The words in them are the TEST's stand-ins for the engine's labels; the real
engine's words may differ.

| File | What I saw |
|---|---|
| `progress-1-just-started.png` | "0 of 9", time left 10:00, leads "asked", the three sessions under leads indented and "not asked yet", both buttons live |
| `progress-2-midway-every-state.png` | the eight states a run can hold before a cancel, each in its colour, a note line, a reason under the could-not-be-asked row, two leads with sessions under them, 6:40 |
| `progress-3-after-two-thirds-interrupted.png` | three amber "interrupted" rows, the engine's two-thirds sentence, 3:05 |
| `progress-4-could-not-be-asked.png` | the reason drawn under the row in the engine's words |
| `progress-5-ending-at-the-limit-cancel-dead.png` | no time left; both buttons drawn dimmed and dead |
| `progress-6-cancelling.png` | blue "kept running" and green "brought back" rows, the count back down to "1 of 9", both buttons dead |
| `progress-7-finished-emptied.png` | the result's detail in bold, no buttons, no time left |
| `progress-8-failed.png` | the engine's reason in bold over rows still open, no buttons |

"Kept running" and "brought back" are not in picture 2 because they exist only after a cancel; they
are in picture 6, so all ten states are drawn across the set.

## What this proof does NOT cover

- The real engine. It has not landed; everything is against the real TYPES and a hand-stepped fake.
  The words, the order of rows and the honesty of `CanShutDownNow` and `CanCancel` are the engine's.
- The one-second clock firing by itself. No test sleeps; tests call the method the clock calls.
- The screen inside the main window, and what the caller does with `Finished`. A later task.
- A `Completion` task that faults. The contract says it never does for an expected end. The screen
  logs a fault and throws it on the interface thread; no test drives that.
- A view shown a second time after it left its window. It throws, by decision (answers file); no test
  drives that.
- Whether the pictures look right to the owner. I looked; that is one pair of eyes.
