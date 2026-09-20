# Mandate - Smart Director Restart - Developer - the shutdown progress screen, review findings

You are a Developer on phase 2 of the Smart Director Restart mission. You were opened by the phase 2
Tech Lead (session c6c50eeb) and you report to it, never to the owner and never to the fleet. You have
no transcript; this file and the files it names are your history. The Developer who built the screen is
gone; you answer the review of its work.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p2-progress`, branch
`smart-restart-p2-progress`. Work only there. The mission record lives in ANOTHER worktree,
`D:/ReposFred/devthrottle-smart-restart-p2`; read it there, never write to it.

## ONE TURN ONLY - read twice

Nobody can wake you once your turn ends. No message reaches a stopped session. Finish EVERYTHING below,
push it, and write your answers file before your turn ends. There is no second round. Run everything in
the foreground; nothing in the background; no sub-agents. Never run `rm` (or any delete) on a path
built from a variable: a safety hook stops the command and asks the owner, which hangs your seat for
good. Literal paths only.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. `D:/ReposFred/devthrottle-smart-restart-p2/docs/missions/smart-director-restart-2026-09-19/review-phase-2-2.md`
   - the review. Two findings, the second in five parts.
3. `mandate-phase-2-developer-progress.md` in the same folder - what the first Developer was asked to
   build. Its "Rules that do not bend" bind you too. Where it and this file disagree, this file wins.
4. After `git fetch origin`: `git show origin/main:docs/missions/smart-director-restart-2026-09-19/phase-1-interface.md`
   and the real types, `src/CcDirector.ControlApi/SmartRestart/ISmartShutdownRun.cs` and
   `ISmartShutdown.cs` on `origin/main`. One correction to the review: it says these types have not
   landed. They have (pull request 3182). What has NOT landed is the engine behind them and
   `ControlApiHost.CreateSmartShutdown()`; you do not need either.
5. `docs/CodingStyle.md`, `docs/VisualStyle.md`, and rule 7 of the repository's `CLAUDE.md` (the client
   is dumb).

## Your one task

Rebase the branch onto `origin/main`, then rebuild the screen so that it reads the REAL run,
`CcDirector.ControlApi.SmartRestart.ISmartShutdownRun`, directly. Both findings are ACCEPTED by the
Tech Lead. Decisions already made, so you do not have to:

- **The invented interface goes.** Delete `IShutdownProgressSource` and everything of its shape. The
  view model is given an `ISmartShutdownRun` and nothing else from the engine. The test fake implements
  `ISmartShutdownRun`: a list of rows, a way to push a snapshot, a way to complete the run, and the two
  buttons flipping `CanShutDownNow` and `CanCancel` to false once used (interface document, section 4).
- **The engine words, the screen lays out (finding 2b, 2c, 2d).** Show `StateLabel`, `PhaseLabel`,
  `CountLabel`, `Detail` and `Note` exactly as given. The screen has NO sentence of its own for a state,
  a phase or a count, and computes no count. It may choose a COLOUR by `State`, and every one of the ten
  states, `Pending`, `KeptRunning` and `BroughtBack` included, has one. An unknown enumeration value
  throws; it is never drawn as something plausible. Rows are in the order the snapshot gives them (leads
  first); a row with an `OwnerSessionId` is indented under its lead. Each snapshot REPLACES what is
  shown; keep row objects by `SessionId` if you wish, but nothing from an older snapshot may survive.
- **The buttons obey the engine (finding 2a).** "Shut down now" is live only while `CanShutDownNow`,
  "Cancel and keep working" only while `CanCancel`. A press calls the run's method once and the button
  goes dead at once, before the next snapshot, so a second press sends nothing. The screen never says
  "cancelling" or "shutting down now" by itself: those words arrive as the engine's `PhaseLabel`.
- **The end of the run (finding 2d).** Await `Completion` without blocking. When it completes, show
  `SmartShutdownResult.Detail` as is, hide both buttons and the time left, and raise ONE event or
  callback carrying the result, so that the caller (a later task) can close the application on
  `Emptied` or put the session view back on `Cancelled`, `Refused`, `RestartRefused` and `Failed`. The
  screen itself closes nothing.
- **The time left** is the screen's own one-second clock against `LimitUtc`, never negative, shown only
  while the phase is `Asking`, `Collecting` or `Interrupting`. Take the clock as a `TimeProvider` or a
  function, so a test sets the time without sleeping.
- **The thread.** `Changed` is raised on an engine thread. Every change to anything bound happens on
  the interface thread. Keep the existing test that records which thread made each change, and make it
  prove this against the new fake.
- **Letting go (finding 1).** The view model is disposable: it unsubscribes from `Changed` and stops
  its clock. The view disposes it in `OnDetachedFromVisualTree`. A test proves it: after the view is
  taken out of its window, a snapshot pushed from the fake changes nothing, the fake reports zero
  subscribers, and nothing is posted to the interface thread. Prove that test can fail (take the
  unsubscribe out, watch it go red, put it back, full build each time) and write both results down.
- **The ignore-all kind goes (finding 2e), and with it the first Developer's two open points.** The
  mission's words "the progress screen that comes up in both cases" (section 4.4) mean both DOORS, the
  window close and the File menu, not both kinds of shutdown. Shutting down and ignoring all sessions
  "ends everything at once" (section 5.3 item 8) and the engine gives it no run. Remove
  `ShutdownProgressKind`, its picture and its tests.
- **"No reason was given."** stays as the reviewer cleared it: drawn and logged when a `NotDelivered` row
  arrives with no `Detail`.
- **`HeadlessTestApp.cs`.** The dialog branch makes the same change with other comment words and merges
  first. Make your file byte-identical to
  `git show origin/smart-restart-p2-dialog:src/CcDirector.Avalonia.Tests/HeadlessTestApp.cs` (if that
  adds application brushes your tests then need to agree with, agree with them). Take the
  `Avalonia.Skia` package reference back out of the test project file if the tests draw without it (the
  dialog branch proved Skia already arrives through the application project); keep it only if your
  pictures come out blank without it, and say which in the proof.
- Do NOT define your own `InitializeComponent`. Keep the opening test, and prove again that it fails
  when one is added.
- You do NOT touch `MainWindow.axaml.cs`, `MainWindow.axaml`, `CloseDialog`, `DrainDirectorDialog`, the
  dialog's files, or anything under `src/CcDirector.ControlApi`.

## Tests and pictures

Tests stay in `src/CcDirector.Avalonia.Tests/SmartRestart/`, namespace
`CcDirector.Avalonia.Tests.SmartRestart`. Drive the REAL view in a window with real mouse clicks; assert
on what the controls in the window hold. At least: the view opens and every named control is
connected; a snapshot's labels appear word for word; a second snapshot replaces the first; all ten
states draw; a lead's row and the row under it; each button is dead when the engine says it may not be
used (the limit reached: `CanCancel` false) and a click then calls nothing; one press, one call; the
time left against `LimitUtc` and never below zero; each of the six outcomes shows its `Detail` and
raises the finished event once; a failed run leaves no live button; the thread test; the letting-go
test.

Pictures, written only when `SMART_RESTART_SCREENSHOT_DIR` names a folder, to
`docs/missions/smart-director-restart-2026-09-19/attachments/phase-2/` in your worktree, replacing the
eight there (delete the old files by their literal names with `git rm`): just started; midway with every
state present and a lead with a session under it; after two thirds with interrupted rows; a
could-not-be-asked row with its reason; ending at the limit, with "Cancel and keep working" dead;
cancelling, with kept-running and brought-back rows; finished and emptied; failed, with the engine's
reason. LOOK at each picture yourself before you report. If you cannot view images, say so in the proof
and say what you checked instead.

## Done

1. With a full build each time (never `--no-build`), in the foreground:
   `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` - read the
   COUNT, state it; `dotnet test src/CcDirector.Avalonia.Tests` - state the count;
   `dotnet build src/CcDirector.Avalonia` - 0 warnings, 0 errors.
2. Rewrite `attachments/phase-2/progress-screen-proof.md`: what the screen is now, the counts with the
   commit of `origin/main` you rebased onto, what each test proves in plain words, both revert proofs
   with their numbers, the pictures, and what the proof does NOT cover.
3. Write `docs/missions/smart-director-restart-2026-09-19/review-phase-2-2-answers.md` in YOUR worktree:
   every finding and every part of finding 2, accepted or declined, with the reason and what was done.
   The decisions above are the Tech Lead's; say so. Commit it with the rest.
4. Commit and push (`git push --force-with-lease`, because of the rebase). Do NOT open a pull request
   and do NOT merge: the rebuilt screen goes to a Reviewer first.
5. Last of all: `cc-devthrottle session report "<one paragraph: what you did, the counts, what you could not reach>"`.

## Rules that do not bend

- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", no robot
  emoji, in any commit, file or comment. ASCII only in everything. Plain English, no abbreviations.
- Log as `docs/CodingStyle.md` says. No try-catch outside event handlers. No fallbacks.
- Never kill a running process. Never launch, restart, drain or stop a Director or a session. Headless
  tests only.
- If something is undecidable inside this mandate, decide the smaller way, write down what you decided
  and why in the answers file, and carry on. Nobody can answer a question this turn.
