# Proof - the Smart shutdown dialog (phase 2, Developer)

Branch `smart-restart-p2-dialog`, cut from `origin/main` at `0f084d60b`, zero commits behind when the work
started. Every command below was run in the foreground in that worktree on 19 September 2026.

## What was built

New files only, under `src/CcDirector.Avalonia/SmartRestart/`: the window `SmartShutdownDialog`, the view
model `SmartShutdownViewModel` that holds every word and count, the plain input `SmartShutdownSession`, the
door `SmartShutdownDoor`, the result `SmartShutdownResult`, the dropdown entry `SmartShutdownTimeOption`,
and the helper `SmartShutdownSessionReader` that builds the input from `Session` objects. Nothing calls the
window yet. `MainWindow`, `CloseDialog`, `DrainDirectorDialog` and `src/CcDirector.ControlApi` are untouched.

## The counts

| Run | Command | Result |
|---|---|---|
| Whole project, BEFORE any change | `dotnet test src/CcDirector.Avalonia.Tests` | 554 passed, 0 failed, 0 skipped |
| The filter | `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 25 passed, 0 failed, 0 skipped |
| Whole project, AFTER | `dotnet test src/CcDirector.Avalonia.Tests` | 579 passed, 0 failed, 0 skipped (554 + 25) |
| The application | `dotnet build src/CcDirector.Avalonia` | 0 warnings, 0 errors (warnings are errors) |

## Real drawing in the test project

`HeadlessTestApp.cs` only: `.UseSkia()` and `UseHeadlessDrawing = false`. The project file did NOT need a
change, because Skia already arrives through the application project's reference to Avalonia.Desktop. All
554 existing tests still pass with drawing on.

## The opening test can fail - both results

The test is `Show_GeneratedInitializeComponent_EveryNamedControlIsConnected`. The work was committed first,
so that restoring the file could not lose it.

1. MUTATED. Added to `SmartShutdownDialog.axaml.cs`, the way the old `DrainDirectorDialog` did:
   `private void InitializeComponent() => AvaloniaXamlLoader.Load(this);`
   Ran the filter with a full build. Result: **18 failed, 7 passed of 25.** The opening test failed with
   `System.NullReferenceException` at `SmartShutdownDialog.axaml.cs:line 36`, the `BtnSmart.Focus()` in the
   Opened handler, reached from `TopLevel.OnOpened`. That is the old defect exactly: the window throws on
   opening because the named control was never connected. The 7 that stayed green are the 6 session reader
   tests and the zero sessions test, none of which opens a window.
2. RESTORED. `git checkout` of that one file, confirmed the line was gone, ran the filter again with a full
   build (never `--no-build`, which would have tested the mutated binary). Result: **25 passed, 0 failed.**

## The pictures

Written by the test `CaptureRenderedFrame_TheFourPictures_AreDrawnNotBlank` when the environment variable
`SMART_RESTART_SCREENSHOT_DIR` names a folder. With or without the variable the test asserts each frame
holds the dialog background, the panel background, the accent button colour and more than 50 colours, so a
blank frame fails it. Each picture was looked at after it was written; all four are drawn and styled.

- `dialog-from-window-close-no-question-boxes.png` - confirm button says "Smart shutdown"
- `dialog-from-file-menu.png` - confirm button says "Smart Restart"
- `dialog-two-question-boxes-open.png` - "Answer these first?" with two sessions by name
- `dialog-time-allowed-30-minutes.png` - the dropdown and the explanation both say 30 minutes

## What this proof does NOT cover

- **The question box list will not appear in the running application today.** `Session.PendingInteraction`
  is never populated by the product (its own file says so). The helper reads it, and the test proves the
  reading by setting the private property by reflection on a real `Session`. That proves the reading, not
  that the product ever fills it.
- **The window title is not in the pictures.** A headless frame has no title bar. The two titles are
  asserted in `Show_TheTwoDoors_DifferOnlyInTitleAndConfirmWords`, which also proves that exactly one drawn
  text differs between the doors.
- **The window's own X is driven as `Close()`.** There is no window frame to press under the headless
  platform. Enter and Escape are driven as real key presses on the opened window.
- **Nobody opens the window yet**, so nothing here proves either door reaches it. That is the next
  Developer's task.
- The pictures are drawn by Skia under the Fluent dark theme with the application's brushes copied into the
  test application, not by the running application.

## Two judgements made inside the mandate

- **Working means Working or Starting**, the same two states `Session` itself counts as working. Idle,
  waiting for input and waiting for permission are waiting.
- **A session whose process has exited is left out of the description**, with a log line. It is not running,
  so counting it in "9 sessions are running" would be false. The caller wiring the doors should know the
  helper does this.
