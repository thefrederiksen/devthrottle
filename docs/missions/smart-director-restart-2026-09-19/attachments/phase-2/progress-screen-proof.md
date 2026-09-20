# The shutdown progress screen - proof

Smart Director Restart, phase 2. Branch `smart-restart-p2-progress`, cut from `origin/main` at `0f084d60b`.
Written by the Developer who built the screen; every number below was read from a run on 19 September 2026.

## What was built

New files only, under `src/CcDirector.Avalonia/SmartRestart/`. Nobody calls the view yet.

- `ShutdownProgressSource.cs` - the small interface the screen reads (`IShutdownProgressSource`): the kind
  of shutdown, the time allowed, when it started, the sessions with their states, a change notification,
  and the two requests. A later task adapts the real engine to it.
- `ShutdownProgressViewModel.cs` - everything the screen shows: the rows, the count, the time left, the
  sentence saying what is happening, and which buttons are shown and live.
- `ShutdownProgressView.axaml` and its code-behind - a `UserControl` that only binds. It does not define its
  own `InitializeComponent`.

## Which states count as shut down

`shut down` and `ended at the limit`. Those sessions are gone. `handed over` does not count: the handover is
written and the session is still open. `interrupted` and `could not be asked` do not count either.

The screen is finished when every session is gone, unless the owner pressed "Cancel and keep working": the
sessions are then on their way back and the screen keeps saying so.

## The check

    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"

| Run | Result |
|---|---|
| The filter, before this work | matched nothing |
| The filter, now | 14 passed, 0 failed |
| The whole `CcDirector.Avalonia.Tests` project, before (headless drawing on) | 554 passed, 0 failed |
| The whole project, after (real Skia drawing, plus the 14 new tests) | 568 passed, 0 failed |
| `dotnet build src/CcDirector.Avalonia` | 0 warnings, 0 errors (warnings are errors) |

The tests open the real view in a window, click its buttons with the mouse, and step a fake shutdown by
hand. They assert on the text the controls in the window hold, not on a view model a test filled in. A mouse
click is used because a raised click event reaches a disabled button and a real click does not.

## Proofs that a test can fail

1. **A hand-written `InitializeComponent`.** Added
   `private void InitializeComponent() => AvaloniaXamlLoader.Load(this);` to the view, as the old
   `DrainDirectorDialog` had. Result: 14 failed, 0 passed. The open test failed on
   `Assert.NotNull() Failure: Value is null` at the first named control; the others failed on a null
   reference to a named control. Taken out again and rebuilt: 14 passed.
2. **The move to the interface thread.** Replaced the dispatch in `OnSourceChanged` with a direct call.
   The first form of the background-thread test stayed GREEN, because Avalonia does not throw when a bound
   collection changes on the wrong thread. The test was rewritten to record which thread every row change
   and count change was made on. With the dispatch removed again: 1 failed, 13 passed
   (`Assert.All() Failure ... Item: False`). Restored and rebuilt: 14 passed.

## The pictures

Drawn by real Skia from the headless tests, written only when `SMART_RESTART_SCREENSHOT_DIR` names a folder.
Each state is reached the way it is reached in life. Each picture was opened and looked at.

| File | State |
|---|---|
| `progress-1-just-started.png` | nine sessions, all asked, 10:00 left |
| `progress-2-midway.png` | every state at once, "4 of 9 shut down", a could-not-be-asked row with its reason |
| `progress-3-after-two-thirds-interrupted.png` | 3:05 left, three interrupted rows |
| `progress-4-could-not-be-asked.png` | the wedged session with the reason the engine gave |
| `progress-5-after-shut-down-now.png` | "Shutting down now...", both buttons disabled and still readable |
| `progress-6-after-cancel-and-keep-working.png` | "Cancelling - bringing your sessions back...", both disabled |
| `progress-7-finished.png` | "9 of 9 shut down", no buttons, no time left |
| `progress-8-ignore-all.png` | the ignore-all kind: no time left, no "Cancel and keep working" |

## What this does not cover

- The one-second timer firing by itself. No test sleeps; the tests call the same method the timer calls.
- The screen inside the main window. That is another Developer's task.
- The real engine. Only the fake has driven this screen.
- The pictures use the test application's theme, which has no dark variant set. The screen sets its own
  colours, so it draws dark, but the real application has not drawn it yet.

## Open points for the Tech Lead

- In the ignore-all kind nobody is asked anything, yet the only word the mission gives for a session that is
  still open is "asked". The picture shows "asked" because the fake starts there. The engine decides what it
  reports; if a word such as "waiting" is wanted it is one more state.
- The ignore-all kind keeps the "Shut down now" button, because the mandate removes only the time left and
  "Cancel and keep working". If ignore-all is always immediate the button has nothing to do there.
- A could-not-be-asked session that arrives with no reason is drawn as "No reason was given." and logged,
  rather than throwing on the interface thread in the middle of a shutdown.
