# Proof - the two doors pressed by machine, in the real main window

Issue #3167, phase 5. Branch `smart-restart/door-tests`, cut from `origin/main` at `d1dc26286`.

## Which option was taken, and why

**Option A, the headless test that opens the real main window.** It was not blocked, and it took well
under the ninety minutes the mandate allowed.

The demo virtual machine (option B) was never started, and nothing on the owner's desktop was touched
at any point: every window in this work exists only inside the headless test application, which draws
into memory with Skia and has no desktop of its own.

The reason option A worked is that the test project already builds a real `MainWindow` headlessly -
`SessionRailWindowTests` has done so since ruling R20, and `HeadlessTestApp` carries the brushes from
`App.axaml` for exactly that purpose. So the gap was never that the window could not be opened; it was
only that nobody had opened it to press these two doors.

## What is new

One file: `src/CcDirector.Avalonia.Tests/SmartRestart/MainWindowDoorsTests.cs`, ten tests.

The test it is meant to replace as evidence -
`SmartShutdownCoordinatorTests.MainWindowSource_NamesNeitherOldWindow_AndCallsTheCoordinatorFromBothDoors` -
is deliberately LEFT IN PLACE. It reads the whole of `MainWindow.axaml.cs` for the three names of the
two windows this feature replaced, over every line of the file; the new menu test checks only the File
menu. The two do not overlap, so removing it would lose a check rather than remove a duplicate.

### Door one: File, Smart Restart

| Test | What it actually exercises |
|---|---|
| `FileMenu_SmartRestartClicked_ReachesTheCoordinator_AndItsAnswerReachesTheNotificationBar` | Builds the real `MainWindow`, walks the native menu the real `BuildNativeMenu` set with `NativeMenu.SetMenu`, finds the item headed `Smart Restart`, and clicks it through `INativeMenuItemExporterEventsImplBridge.RaiseClicked` - the same interface Avalonia's own Windows backend calls when the owner clicks it. Then reads the real window's own notification bar. The sentence it must carry ("Smart Restart is not available: this Director's control service did not start. The log says why.") is written in exactly one place in the product: the no-engine branch of `SmartShutdownCoordinator.OpenFromFileMenuAsync`. So the bar carrying it says the click arrived at the coordinator AND that the answer came back out through the window's own `ShowNotification`. |
| `FileMenu_SmartRestartClicked_WithSessionsOnTheRail_StillReachesTheCoordinator` | The same door with two sessions on the real rail, so the click is not only exercised on an empty Director. |
| `FileMenu_NamesSmartRestartOnce_AndNamesNeitherOldWindow` | The File menu has exactly one item headed `Smart Restart`, and no item whose header names the drain window this feature replaced. |

### Door two: the window closing

Both of these assert a DIFFERENCE rather than an answer, and that is the whole point of their shape.
"The close was not cancelled" is true of a window with no smart shutdown in it at all, so a test that
asserted only that would stay green with all fifteen lines deleted - a check whose pass condition is an
absence. What cannot be true of an unwired window is that the same window answers differently depending
on what it is asked.

| Test | What it actually exercises |
|---|---|
| `OnClosing_WithNothingRunningTheCloseCarriesOn_WithASessionRunningTheWindowCancelsIt` | One real `MainWindow`, its own `OnClosing` raised twice with `WindowCloseReason.WindowClosing`. Empty rail: not cancelled. One session working on the rail: cancelled. The difference can only come from the window handing its own rail to the coordinator and acting on the answer. |
| `OnClosing_TheRealCloseReasonIsHandedOver_UserCloseCancelsWhereOperatingSystemShutdownDoesNot` | Two real windows in the same state, one session working on each, `OnClosing` raised with `WindowClosing` on one and `OSShutdown` on the other. The user close is cancelled so the owner can be asked; the operating system shutting down is let straight through. A window that passed a constant, or never called the coordinator, gives the same answer twice and this goes red. |

`OnClosing` is `protected override` and Avalonia keeps the `WindowClosingEventArgs` constructor to its
own assembly, so the arguments are built the way Avalonia builds them and handed to the window's own
override. The other way in - `Show()` then `Close()` - is not available to this window here, and why is
in the section on what is still not proven.

A close that is LET THROUGH runs the window's own teardown, whose first statement
(`UpdateAllSessionHistoryTimestamps`) casts `Application.Current` to the real `App`, which the headless
test application is not. That `InvalidCastException` is asserted rather than swallowed, because it is
the positive evidence that the window carried on past the branch instead of returning. It is also the
reason nothing is written to the owner's own files: the cast is the first statement of that method,
before any load or save, so the test never reaches a write.

### The things the window hands the coordinator

The coordinator is built lazily by `MainWindow.SmartShutdown` with eight arguments. These tests take
the real window's own coordinator and look at what it was given.

| Test | What it actually exercises |
|---|---|
| `TheSwapLambdas_PutTheSurfaceInPlaceOfTheSessionView_AndTakeItAwayAgain` | Runs the two swap actions off the real window's own coordinator and reads back the real `SmartShutdownHost` and the real `SessionViewGrid` of `MainWindow.axaml`: a real `SmartShutdownSurface` becomes the host's content, the host becomes visible, the session view is hidden; then the restore puts all three back. |
| `TheSessionsLambda_HandsOverTheWindowsOwnRail_AsItIsWhenItIsAsked` | Empty at first; a session added to the real rail AFTER the coordinator was built is in the next answer, so the lambda reads the roster each time rather than copying it once. |
| `TheEngineLambda_UnderTheHeadlessTestApplication_AnswersNoEngineRatherThanThrowing` | The engine lambda answers null here rather than throwing. Written as a test rather than only as prose so that a later change which DOES make an engine reachable turns it red and gets the deeper tests written. |
| `TheCloseAndMessageLambdas_AreThisWindowsOwnCloseAndItsOwnNotificationBar` | The coordinator holds THIS window's `Close` and THIS window's `ShowNotification`, by delegate target and method. |
| `TheDialogLambda_BelongsToThisWindow` | The dialog lambda's target is this window, so the dialog it builds would be owned by this window and not another. |

## The revert proofs

Eleven mutations were made to `src/CcDirector.Avalonia/MainWindow.axaml.cs`, one at a time. Each was
applied, the ten tests were run with a full build (never `--no-build`), and the source was restored
from bytes captured before the first mutation. The restore is in a `finally`, so no failure or stop
between mutation and restore can leave the source changed; after every batch the run asserted
`git status --porcelain` was clean for that file and printed
`RESTORED: MainWindow.axaml.cs is byte-identical to the commit again`.

The tests were committed BEFORE the first mutation (`700ec4aba`, `9387bd068`), so a restore could not
eat them.

| # | The wiring broken | Result | Which tests went red |
|---|---|---|---|
| 1 | The File menu item does nothing when clicked: `Item("Smart Restart", () => { })` | Failed 2, Passed 8 | `FileMenu_SmartRestartClicked_ReachesTheCoordinator_AndItsAnswerReachesTheNotificationBar`, `FileMenu_SmartRestartClicked_WithSessionsOnTheRail_StillReachesTheCoordinator` |
| 2 | The File menu has no `Smart Restart` item at all (the line deleted) | Failed 3, Passed 7 | the two above, plus `FileMenu_NamesSmartRestartOnce_AndNamesNeitherOldWindow` |
| 3 | `OnClosing` never asks the coordinator (the call left in the source but reached only on a close reason no test uses) | Failed 2, Passed 8 | `OnClosing_WithNothingRunningTheCloseCarriesOn_WithASessionRunningTheWindowCancelsIt`, `OnClosing_TheRealCloseReasonIsHandedOver_UserCloseCancelsWhereOperatingSystemShutdownDoesNot` |
| 4 | `OnClosing` hands over a constant instead of the real reason: `HandleWindowClosing(WindowCloseReason.OSShutdown)` | Failed 2, Passed 8 | the same two |
| 5 | The surface is never put in place of the session view: `surface => { }` | Failed 1, Passed 9 | `TheSwapLambdas_PutTheSurfaceInPlaceOfTheSessionView_AndTakeItAwayAgain` |
| 6 | The session view is never put back: `() => { }` | Failed 1, Passed 9 | the same one |
| 7 | The coordinator is handed an empty list instead of the rail | Failed 3, Passed 7 | `TheSessionsLambda_HandsOverTheWindowsOwnRail_AsItIsWhenItIsAsked` and both close-door tests |
| 8 | The dialog would be owned by some other window, not this one | Failed 1, Passed 9 | `TheDialogLambda_BelongsToThisWindow` |
| 9 | The engine lambda throws instead of answering | Failed 3, Passed 7 | `TheEngineLambda_UnderTheHeadlessTestApplication_AnswersNoEngineRatherThanThrowing` and both File menu click tests |
| 10 | The coordinator is not given this window's own `Close` | Failed 1, Passed 9 | `TheCloseAndMessageLambdas_AreThisWindowsOwnCloseAndItsOwnNotificationBar` |
| 11 | The coordinator is not given this window's own `ShowNotification` | Failed 3, Passed 7 | the same one, plus both File menu click tests |

Every one of the ten tests was shown red by at least one mutation, and no mutation broke the build
(each run was checked for `error CS` before its result was read - a mutation that did not compile would
turn every test red for the wrong reason).

Mutation 7 is worth reading twice: handing the coordinator an empty session list turns the two close
tests red as well, because the difference they assert comes from the rail. That is the contrast working
as intended.

## The counts

Both checks the mandate names, run on this branch with the source restored and a full build:

| Check | Before | After |
|---|---|---|
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 159 passed, 0 failed | **169 passed, 0 failed** |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` | 686 passed, 0 failed | **686 passed, 0 failed** |

159 + 10 = 169. The Gateway number is unchanged because nothing in this work touches the Gateway; the
only file changed is a new file in the Avalonia test project.

**One number disagrees with the mandate and is reported rather than smoothed over.** The mandate gives
the Delivery Lead's Gateway run as 637. Measured here on `d1dc26286` - the exact commit this branch was
cut from - the same filter selects and passes **686**. The count was 686 before this work and 686
after, so the difference is not caused by anything here; it is a disagreement about what that filter
selected when the Delivery Lead ran it, and I could not account for it from this worktree. It matters
only as a reminder that the figure to carry forward is 686, not 637. Nothing failed in either reading.

## What about those fifteen lines is STILL not proven by machine

This is the part the owner is deciding on, so it is said plainly and at length.

**1. There is never a real engine, so half of each door is unreached.** The window's engine lambda is
`(Application.Current as App)?.ControlApiHost?.CreateSmartShutdown()`, and the headless test
application is not `App`. It answers null and cannot be made to answer anything else without starting a
real Director. Everything downstream of "there is an engine" is therefore exercised only in
`SmartShutdownCoordinatorTests`, with a fake engine and a stand-in window, and never through the real
main window:

- the real Smart shutdown dialog opening over the real main window;
- a smart shutdown starting, the progress screen replacing the real session view, and the run finishing;
- the restart itself;
- the real window's `Close` being called by the flow when the run empties the Director.

**2. The real main window is never SHOWN.** Showing it raises its `Loaded` handler, whose first line
casts `Application.Current` to the real `App` and throws under the headless application. So the window
is built and driven but never displayed. Nothing here proves the swap or the dialog LOOKS right on a
shown main window; what the dialog and the progress screen draw is proved separately, in
`SmartShutdownDialogTests` and `ShutdownProgressScreenshotTests`, on windows of their own.

**3. Nothing presses a real mouse or a real key.** The File menu is drawn by Windows, not by Avalonia,
so there is no menu frame to click in a headless test; the item is clicked through the interface
Avalonia's Windows backend calls, one layer in from the owner's finger. If the item were exported to
the real Windows menu incorrectly, or not at all, these tests would not notice.

**4. The close is raised by us, not by Windows.** `OnClosing` is invoked directly with arguments built
the way Avalonia builds them. The chain from the owner pressing the X, or the machine shutting down,
through Windows and Avalonia to this method with the RIGHT `WindowCloseReason`, is Avalonia's and is not
exercised anywhere in this repository. In particular, **that Windows reports a real operating system
shutdown as `WindowCloseReason.OSShutdown` at all is unproven by machine**, here or anywhere else. The
five-second record path behind it is engine work and is proved with a fake engine in
`SmartShutdownCoordinatorTests`; that it is reached on a real shutdown is not.

**5. A close that is let through is never allowed to finish.** The teardown below the branch throws on
its first statement under the headless application. So these tests prove the branch does not swallow a
close it should let through, but not that the rest of `OnClosing` still does its work correctly
afterwards. That was true before this work as well; nothing here made it worse.

**6. `ControlApiHost.CreateSmartShutdown()` producing a working engine on a real Director** is not
touched by these tests.

**What a human press would add, then**: the real Windows menu, the real X button and the real machine
shutdown reaching these lines at all, with a real engine behind them - which is to say the dialog
actually appearing, the progress screen actually replacing the session view on a visible window, and the
Director actually restarting. The fifteen lines themselves - that the item exists and calls the
coordinator, that the close asks it and acts on the answer, that the reason is passed through, and that
the swap moves the real controls - now go red on a machine when they are broken.

## Defects found

None. No change was made to `src/CcDirector.Avalonia/MainWindow.axaml.cs` or to any product file; every
mutation above was applied only to run the tests against it and was restored from bytes immediately
afterwards. The only change on this branch is the new test file and this document.
