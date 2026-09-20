# Review - Smart Director Restart, phase 2, task 1: the Smart shutdown dialog

Reviewer seat: opened by the phase 2 Tech Lead (session c6c50eeb). Reviewed commit `277498f16`
(branch `smart-restart-p2-dialog`). Nothing in this file is committed; the Tech Lead lands it.

## Scope

What I read:

- The whole change under review, `git diff origin/main...HEAD`: sixteen files. Twelve are new
  (the window, its code-behind, the view model, the door, the result, the session description, the
  session reader, the time option, and the two test files). One is changed
  (`src/CcDirector.Avalonia.Tests/HeadlessTestApp.cs`). Three are mission attachments (the
  developer's proof and four pictures). I confirmed the branch touches nothing else: `MainWindow`,
  `CloseDialog`, `DrainDirectorDialog` and everything under `src/CcDirector.ControlApi` are
  untouched, as the developer mandate requires.
- The developer mandate, the mission document sections 4.4, 5.3 items 1 to 3, 10.1 and 10.7, and
  `phase-1-interface.md` - all read from `origin/main` (this checkout is the fixed commit under
  review and is deliberately behind; every fact about shipped code below comes from
  `origin/main`, not from the working tree). The phase 1 types are already on `origin/main`
  (`src/CcDirector.ControlApi/SmartRestart/`), so the adoption question is about real code, not a
  plan.
- `docs/CodingStyle.md` and `docs/VisualStyle.md`.
- The developer's proof. I treated it as self-testimony and re-verified every claim I could reach
  myself (below).

What I ran, all in the foreground, nothing hidden:

- The filter: `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"`
  - 25 passed, 0 failed. Same as the Tech Lead's run.
- The whole project at the reviewed commit: 579 passed, 0 failed, test duration 1 minute 16 seconds.
- The whole project at the parent commit `0f084d60b` (the state before the change, built in a
  throwaway worktree I removed afterwards): 554 passed, 0 failed, test duration 1 minute 16
  seconds. This is my own answer to the "can the drawing change slow the 554 existing tests"
  question: it does not. The two runs take the same time to the second, with the 25 new tests
  costing no additional wall time.
- The developer's mutation claim, repeated myself in a throwaway worktree (my own worktree must
  not change): I added `private void InitializeComponent() => AvaloniaXamlLoader.Load(this);` to
  `SmartShutdownDialog.axaml.cs` the way the old `DrainDirectorDialog` did, ran the filter, and got
  exactly the developer's numbers: 18 failed, 7 passed of 25. The opening test failed. The failure
  surfaced first at the `Opened` handler (`BtnSmart.Focus()` on a control that was never connected),
  which is the old shipped defect exactly; had that handler not been there, the opening test's own
  not-null asserts would have caught it. Removed the worktree afterwards. The claim stands.
- The four committed pictures: I cannot view images in this seat this run, so I verified them
  another way. I re-ran the capture test with the screenshot folder variable set and compared
  bytes: all four committed picture files are byte-identical to what the test renders now, so they
  are genuine output of the capture test, not hand-made. The test itself asserts each frame holds
  the dialog background `#252526`, the panel background `#1E1E1E`, the accent button `#007ACC`, and
  more than fifty distinct colours (a blank frame cannot pass), so each picture is a drawn,
  styled dialog, not a blank frame.
- `dotnet build src/CcDirector.Avalonia`: 0 errors, 0 warnings (warnings are errors in that
  project).
- The commit messages and every new file: no agent or vendor name anywhere, plain ASCII only.

What I could not reach:

- The window's own title bar and its X button. Under the headless platform there is no window
  frame, so the X is driven as `Close()`. The developer says so plainly and I confirmed the test;
  the real X remains unproven until the wiring task opens this window inside the real application.
- Either door. Nothing calls this window yet, by design of this task, so no evidence exists that
  the File menu or the window close opens it. That is the next task.
- The running application's rendering. The pictures are drawn by Skia inside the test application
  with the application's own colour values, not by the shipped application process.
- Visual judgement of the pictures by eye. My seat cannot read images this run; my evidence is the
  byte comparison and the colour assertions described above. The developer did look at them and
  says so.

## Findings

1. **The dialog's `SmartShutdownResult` collides by name with the phase 1 type of the same name,
   and the wiring task will not compile until one of them is renamed or aliased.**
   `src/CcDirector.Avalonia/SmartRestart/SmartShutdownResult.cs` line 20 declares
   `SmartShutdownResult` in namespace `CcDirector.Avalonia.SmartRestart`.
   `origin/main` already carries a different `SmartShutdownResult` in
   `src/CcDirector.ControlApi/SmartRestart/ISmartShutdownRun.cs` line 154, namespace
   `CcDirector.ControlApi.SmartRestart`, and the Avalonia project references the ControlApi
   project (`CcDirector.Avalonia.csproj` line 42). The task that wires the dialog to the engine
   must use both namespaces in one file; the unqualified name `SmartShutdownResult` is then
   ambiguous and the file fails to compile. That is the break, and it is all that breaks: the two
   types describe different things (what the owner chose, versus how a run ended), the dialog's
   result maps cleanly onto the phase 1 interface (smart shutdown becomes a `SmartShutdownRequest`
   whose time allowed is one of `SmartShutdownTimes.Allowed` - the dropdown holds exactly those
   five; ignore-all becomes `ShutDownIgnoringAllAsync`; cancelled means do nothing), and the door
   maps to `SmartShutdownPurpose` in one line. Whoever does the adoption decides between an alias
   and a rename; I say only what breaks, not what I would prefer.

Nothing else. Within the stated scope this change is sound. The specific questions the mandate put
to me, answered:

- **Does the window open, and does the opening test fail when the generated
  `InitializeComponent` is replaced?** Yes and yes. No hand-written `InitializeComponent` exists in
  the window; the generated one stands; I re-ran the mutation myself and reproduced the developer's
  exact numbers (18 of 25 failed, the opening test red at the `Opened` handler,
  `SmartShutdownDialog.axaml.cs` line 36).
- **Do the results come from driving the real window?** Yes. Every result is read off the opened
  window after a real button click, a real Enter key press, or a real Escape key press. Enter is
  the smart shutdown (the confirm button is the default button and takes the focus on opening);
  Escape and the window's own `Close()` are cancelled. The close test changes the time allowed
  first, so a result quietly built from the view model instead of the window would have shown.
  The one hand-built thing is the input record, which the mandate says the caller builds.
- **Do the words say everything mission 5.3 item 2 requires?** Yes. All five required clauses are
  in the explanation text, the why sentence matches the mandate's own wording for mission 10.7,
  the number in the explanation follows the dropdown (tested at 30 minutes and at 1 hour), the
  dropdown holds exactly 5, 10, 15, 30 and 60 minutes with 10 the default, and "Shut down and
  ignore all sessions" is visibly the lesser choice: a grey, transparent, left-placed button with
  one dim sentence under it, against an accent-coloured confirm button with a full explanation
  panel. The two doors differ only in the title and the confirm button's words, and a test proves
  exactly one drawn text differs between them.
- **Is leaving exited sessions out of the reader right, or can it make the dialog count and the
  no-dialog decision disagree?** Leaving them out is right: a session whose process has exited is
  not running, has nothing to hand over, and mission 4.4 says the Director asks nothing when
  nothing is running. A caller that takes the no-dialog decision from this same reader cannot make
  the two disagree - the reader is the single source. The one thing the next task must know: the
  close hook it will replace (`MainWindow.axaml.cs` `OnClosing`, line 6625 on `origin/main`) uses a
  NARROWER rule today - `Working or WaitingForInput` only - which ignores idle, starting and
  waiting-for-permission sessions that this reader counts. If the wiring task keeps the old
  expression for the decision but this reader for the count, a director whose only session is
  idle would close silently while the reader would have counted one session running. The
  decision and the count must come from the same list. Within this change nothing is wired, so
  nothing breaks today; this is a warning for the next mandate, not a defect here.
- **Anything that blocks the user interface thread, swallows an error, or is a fallback?** No. The
  window does no input or output at all. The only try-catch is in the shared button handler
  (`CloseWith`), which logs and rethrows - an event handler boundary, which the rules allow, and
  rethrowing is the honest choice. The guard that ignores a null selection in the view model is
  commented and is the known behaviour of a dropdown whose items change, not a hidden failure.
  The reader throws on a null list; the view model throws on an empty list rather than showing
  "0 sessions", exactly as the mandate asked.
- **Can the `HeadlessTestApp.cs` change break or slow the 554 existing tests, on Windows or on the
  Linux and macOS runners?** Not on the evidence. All 554 still pass at the reviewed commit
  (579 with the new tests), and the whole-project duration is unchanged to the second (1 minute
  16 seconds before and after). The Linux and macOS continuous integration jobs never build this
  test project at all: the .NET build-and-test job runs on `windows-latest`, and the Linux and
  macOS matrix exists to prove the command line tool. The change is confined to
  `HeadlessTestApp.cs` as the mandate asked, and Skia arrives through the application project's
  existing reference to Avalonia.Desktop, so no project file changed.

One note I do not raise as a finding: the new test file uses the null-forgiving operator
(`.Text!`, `frame!`), which `docs/CodingStyle.md` section 3 forbids. The identical pattern
already sits on `origin/main` in the same test project (`SessionRailRowRenderTests.cs` line 83),
no harm is shown, and the guide's own exception list does not cover it; whether to clean the
pattern up is the Tech Lead's call, not a defect in this change.

END OF REVIEW
