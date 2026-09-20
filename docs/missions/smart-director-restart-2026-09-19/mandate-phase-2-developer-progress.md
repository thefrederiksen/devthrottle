# Mandate - Smart Director Restart - Developer - the shutdown progress screen

You are a Developer on phase 2 of the Smart Director Restart mission. You were opened by the phase 2
Tech Lead (session number 120) and you report to it, never to the owner and never to the fleet. You
have no transcript; this file and the two files named below are your history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p2-progress`, branch
`smart-restart-p2-progress`, cut from `origin/main`. Work only there. The mission record lives in
ANOTHER worktree, `D:/ReposFred/devthrottle-smart-restart-p2`; read it there, never write to it.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. `D:/ReposFred/devthrottle-smart-restart-p2/docs/missions/smart-director-restart-2026-09-19/mission.md`
   - sections 4.4, 4.5, 5.3 items 4 to 8. It wins over this file.
3. `docs/CodingStyle.md` and `docs/VisualStyle.md` in your worktree.

## Your one task

Build the shutdown progress screen as a NEW view nobody calls yet. You do NOT touch
`MainWindow.axaml.cs`, `MainWindow.axaml`, `CloseDialog`, `DrainDirectorDialog`, or anything under
`src/CcDirector.ControlApi`. Another Developer puts it in place of the session view later, and phase 1
of this mission is building the engine beside you; you must not reach into the engine's files.

New files only, under `src/CcDirector.Avalonia/SmartRestart/` (namespace
`CcDirector.Avalonia.SmartRestart`). Another Developer is writing the Smart shutdown dialog in the same
folder in another worktree: every file name of yours starts with `ShutdownProgress` so the two never
collide.

- **The screen is a view (a `UserControl`), not a dialog**: the owner said it is shown "instead of" the
  sessions, in both cases (the smart shutdown and the ignore-all shutdown). It must also be hostable in
  a bare window, which is how your tests open it.
- **A small interface of your own shape, and a fake.** The real engine does not exist yet. Define the
  smallest interface the screen needs from a shutdown that is under way - for example: the sessions
  with their current state, a change notification, the time allowed and when it started, whether it is
  the smart or the ignore-all kind, and two requests, "shut down now" and "cancel and keep working".
  Keep it small and plain: phase 1 will publish the real interface and a later task adapts one to the
  other, so anything clever here is thrown away. Write a fake of it in the TEST project that a test can
  step by hand (move a session to a state, move the clock).
- **What the screen shows:**
  - one row per session: its name and its state, in these words exactly - asked, writing, handed over,
    shut down, interrupted, ended at the limit (mission 5.3 item 4). A session that could not take the
    request at all is shown as such with the reason the engine gave, not left looking "asked" (mission
    section 7, the wedged session);
  - a count: "4 of 9 shut down". Decide from the mission which states count as shut down and say which
    in your report ("shut down" and "ended at the limit" are gone; "handed over" is not gone yet);
  - the time left, counting down, as minutes and seconds. The clock is injected so a test moves it;
    no test sleeps;
  - two buttons: "Shut down now" and "Cancel and keep working". Each asks the interface once, then
    disables both and says what is happening ("Shutting down now..." / "Cancelling - bringing your
    sessions back..."), because a button that reports nothing reads as broken;
  - the finished state, when every session is gone: the count complete and the buttons gone;
  - the ignore-all kind: same rows and count, no time left, no "Cancel and keep working".
- State changes arrive from another thread. Every change to a bound collection goes through the
  interface thread (`Dispatcher.UIThread`), as `CLAUDE.md` rule 6 says.
- A view model holds everything the screen shows, so the words and counts are testable without a
  window. The view only binds.
- Do NOT define your own `InitializeComponent`. The old `DrainDirectorDialog` did, which skipped the
  generated code that connects named controls, and it threw on opening in every shipped build.
- Log as `docs/CodingStyle.md` says. No try-catch outside event handlers. No fallbacks. ASCII only,
  everywhere, including the interface text.

## Tests - the law of this phase

Every new window or view gets a headless test in `src/CcDirector.Avalonia.Tests` that OPENS it. Put
yours in a folder `SmartRestart/` with namespace `CcDirector.Avalonia.Tests.SmartRestart`, so this
filter finds them:

    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"

That filter matches nothing today and a filter that matches nothing exits green. Read the COUNT, never
the colour. Tests you owe, each `[AvaloniaFact]`, named `Method_Scenario_Result`:

- the view opens inside a window (`Show()`), and every named control the code-behind uses is not null.
  Prove it can fail: add your own `InitializeComponent` the way the old window did, watch the test go
  red, take it out, and write down both results;
- one row per session, each state drawn in its exact words, including the could-not-be-asked row;
- the count follows the fake as sessions go ("0 of 9", "4 of 9", "9 of 9");
- the time left follows the injected clock and never goes below zero;
- "Shut down now" asks the interface exactly once and both buttons disable; the same for "Cancel and
  keep working"; a second click does nothing;
- the ignore-all kind shows no time left and no cancel button;
- a change raised from a background thread lands in the rows without throwing.

Drive the real view's buttons and the real fake; do not hand-build a view model state and assert on it
where a button or the fake could produce it.

## Screenshots

An interface proves itself with screenshots rendered FROM the headless tests. The test project today
draws nothing (`UseHeadless` with headless drawing on, see `HeadlessTestApp.cs`), so a captured frame
would be blank. Make real rendering work for the test project (the Avalonia headless platform with Skia
and headless drawing off, then `CaptureRenderedFrame()`), WITHOUT breaking any existing test: run the
whole project before and after and write both counts in your report. The dialog Developer needs the
same thing; keep this change tiny and in `HeadlessTestApp.cs` and the project file only, so the second
one to merge has a trivial conflict.

Screenshots go, from a test that writes them only when an environment variable names the folder, to
`docs/missions/smart-director-restart-2026-09-19/attachments/phase-2/` in YOUR worktree, one per state
of the screen: just started (all asked), midway (a mix of every state, "4 of 9 shut down"), after the
two-thirds point with interrupted rows, a could-not-be-asked row, after "Shut down now", after "Cancel
and keep working", finished, and the ignore-all kind. Look at each picture yourself before you report;
a blank or unstyled picture is not proof.

## Done

1. The filter above green with a count you state, the whole `CcDirector.Avalonia.Tests` project green
   with counts before and after, `dotnet build` of `src/CcDirector.Avalonia` clean (warnings are errors).
2. Everything committed on your branch and pushed. Do NOT open the pull request and do NOT merge: the
   Tech Lead sends your code to a Reviewer first. Never pick your own reviewer.
3. `cc-devthrottle session report "<one paragraph: what you built, the counts, what you could not reach>"`.
4. Stay available: review findings come back to you as a file, and you answer every one.

## Rules that do not bend

- Nothing in the background, no sub-agents inside your session. Builds and tests run in the foreground.
- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any commit,
  file or comment. ASCII only in everything.
- Never kill a running process. Never launch, restart, drain or stop a Director or a session. Headless
  tests only.
- If something is undecidable inside this mandate:
  `cc-devthrottle session raise "<the question, with your recommendation>"` and carry on with the rest.
