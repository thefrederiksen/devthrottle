# Answers to the review of phase 2, task 2: the shutdown progress screen

The review is `review-phase-2-2.md`. The Developer who built the screen is gone; these answers are
from the Developer opened to answer the review. The decision on every finding - accepted, and how to
meet it - was made by the phase 2 Tech Lead in `mandate-phase-2-developer-progress-findings.md`. They
are the Tech Lead's decisions, not mine; what is mine is how each was carried out, and the smaller
decisions listed at the end. The proof is `attachments/phase-2/progress-screen-proof.md`.

One correction to the review, from the Tech Lead: the review says the phase 1 types had not landed.
They have (pull request 3182). The screen now builds against the real types in
`src/CcDirector.ControlApi/SmartRestart/`, not against the interface document. The engine behind them
has not landed, and the screen does not need it.

## Finding 1 - the view model never lets go of the source

**Accepted.** `ShutdownProgressViewModel` is now disposable. `Dispose` unsubscribes from the run's
`Changed` event, takes the screen's continuation off the run's `Completion` task, and stops the
one-second clock, which the view model now owns. The view calls it in `OnDetachedFromVisualTree`. A
snapshot already posted to the interface thread before the screen let go is dropped when it arrives.
The screen also lets go by itself the moment the run completes.

Proved by `Detached_TakenOutOfItsWindow_LetsGoOfTheRunAndStopsItsClock`, and proved able to fail: with
the unsubscribe taken out, 2 failed and 19 passed; put back, 21 passed.

## Finding 2 - the invented interface cannot carry the settled contract

**Accepted, all five parts.** `IShutdownProgressSource`, its session record, its seven-word state
enumeration and everything of its shape are deleted. The view model is given an `ISmartShutdownRun`
and nothing else from the engine. The test fake implements `ISmartShutdownRun`.

### 2a - the buttons cannot honour `CanShutDownNow` and `CanCancel`

**Accepted.** Each button is live only while the snapshot says it may be used. A press marks the
button dead BEFORE the run's method is called and it stays dead for the life of the screen, so a
second press sends nothing, whatever a later snapshot says. The screen no longer says "cancelling" or
"shutting down now"; those words arrive as the engine's `PhaseLabel`. The case the review names - the
limit reached, cancel meaningless - is test 7 and picture 5: the button is drawn, dead, and a real
click calls nothing.

### 2b - the screen words the states and the count itself

**Accepted.** `StateLabel`, `PhaseLabel` and `CountLabel` are drawn as given. `Describe`,
`DescribeStatus` and `IsGone` are deleted. The screen computes no count; test 2 sends a count label
that disagrees with its rows and the label is what shows.

### 2c - `Pending`, `KeptRunning` and `BroughtBack` have no representation

**Accepted.** The screen colours by the real ten-value enumeration and every value has a colour. A
value it was never taught throws, for a state and for a phase alike. Rows are in snapshot order and a
row with an `OwnerSessionId` is indented under its lead. After a cancel the count going down is simply
the engine's label, shown (picture 6).

### 2d - the engine's phase, note and completion have no home

**Accepted.** `PhaseLabel` and `Note` each have a line. `Completion` is awaited without blocking. When
it completes, the result's last snapshot and its `Detail` are shown as is, both buttons and the time
left are hidden, and ONE event, `ShutdownProgressViewModel.Finished`, carries the result to the
caller, on the interface thread. The screen closes nothing. All six outcomes are tested, and a failed
run is tested to leave no live button.

### 2e - the ignore-all kind has no engine source

**Accepted, by removal.** The Tech Lead's reading: the mission's "the progress screen that comes up in
both cases" (section 4.4) means both DOORS, the window close and the File menu, not both kinds of
shutdown; shutting down and ignoring all sessions "ends everything at once" (section 5.3 item 8) and
the engine gives it no run. `ShutdownProgressKind`, its picture and its tests are gone. This also
closes the first Developer's two open points about that kind.

## Points the reviewer cleared, and what became of them

- **"No reason was given."** Kept as cleared: drawn and logged when a `NotDelivered` row arrives with
  no `Detail`.
- **The thread proof.** Kept and widened against the new fake: it now records the thread of every row
  change, every property change and the `Finished` event, and asserts six named properties did change.
- **`HeadlessTestApp.cs` and `Avalonia.Skia`.** The file is now byte for byte the dialog branch's. The
  `Avalonia.Skia` reference is taken back out of the test project; the pictures still draw without it.
- **The clock.** Still clamped at zero and rounded up. It now reads `LimitUtc` from the snapshot and
  takes a `TimeProvider`. It shows only in `Asking`, `Collecting` and `Interrupting`.

## Smaller decisions I made inside the mandate, and why

Nobody could be asked this turn, so each was decided the smaller way.

1. **A `Completion` task that faults is thrown, not drawn.** The contract says it never faults for an
   expected end. Drawing a sentence of the screen's own for it would be the screen wording an outcome,
   and swallowing it would be a fallback. It is logged and thrown on the interface thread. Not tested.
2. **The view is shown once.** Leaving its window lets go of the run for good, so attaching the same
   view again throws with a message that says to build a new one, rather than showing a dead screen
   that looks alive. If the integration task finds the main window detaches and re-attaches its
   content in normal life, this is the line to revisit. Not tested.
3. **A press kills only the pressed button.** The other button follows the engine's next snapshot. The
   mandate says "the button goes dead", singular, and the engine ignores a request it would not honour.
4. **The result's `Final` snapshot is applied at completion**, so the screen ends agreeing with the
   result even if the last `Changed` was never raised. Tested (test 17).
5. **Every engine change is posted, never applied in place**, even when raised on the interface
   thread, so two snapshots can never land out of order.
6. **An unknown phase in the FIRST snapshot throws out of the constructor after the subscription was
   made.** The subscription is made before the first read on purpose (so no change is lost between the
   two), and this file has no try-catch outside entry points. The cost is one leaked delegate in a
   case that is already an engine defect. Written down rather than hidden.
7. **Picture 2 shows eight states, not ten.** The mandate asks for "every state present", but
   `KeptRunning` and `BroughtBack` exist only after a cancel and would make the midway picture show
   something no run can show. They are in picture 6, so all ten are drawn across the set, and test 4
   draws all ten in one window.
8. **`Mission` and `Role` are not drawn.** Session names already carry both by the fleet's naming
   convention, and the mandate lists the five labels to show.
9. **Two read-only members exist for the letting-go proof**: `SnapshotsReceived` and `IsClockRunning`.
   They are how "nothing is posted to the interface thread" and "stops its clock" are asserted as
   facts rather than inferred.

## What I did not touch

`MainWindow.axaml.cs`, `MainWindow.axaml`, `CloseDialog`, `DrainDirectorDialog`, the dialog's files,
and everything under `src/CcDirector.ControlApi`.

## A note on the rebase

The rebase onto `origin/main` (`912340ed8`) stopped after staging the first commit, because another
git process in this shared repository held a lock at that moment. No lock file was touched. The staged
commit was made with its original message and author (`git commit -C`), and the rebase then finished
cleanly. The branch's own files were checked to be unchanged by it.
