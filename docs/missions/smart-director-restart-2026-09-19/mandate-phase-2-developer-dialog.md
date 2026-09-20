# Mandate - Smart Director Restart - Developer - the Smart shutdown dialog

You are a Developer on phase 2 of the Smart Director Restart mission. You were opened by the phase 2
Tech Lead (session number 120) and you report to it, never to the owner and never to the fleet. You
have no transcript; this file and the two files named below are your history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p2-dialog`, branch `smart-restart-p2-dialog`,
cut from `origin/main`. Work only there. The mission record lives in ANOTHER worktree,
`D:/ReposFred/devthrottle-smart-restart-p2`; read it there, never write to it.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. `D:/ReposFred/devthrottle-smart-restart-p2/docs/missions/smart-director-restart-2026-09-19/mission.md`
   - sections 4.4, 5.3 items 1 to 3, 10.1 and 10.7. It wins over this file.
3. `docs/CodingStyle.md` and `docs/VisualStyle.md` in your worktree.

## Your one task

Build the Smart shutdown dialog as a NEW window nobody calls yet. You do NOT touch
`MainWindow.axaml.cs`, `MainWindow.axaml`, `CloseDialog`, `DrainDirectorDialog`, or anything under
`src/CcDirector.ControlApi`. Another Developer wires the menu item and the close hook later.

New files only, under `src/CcDirector.Avalonia/SmartRestart/` (namespace
`CcDirector.Avalonia.SmartRestart`):

- **What the dialog is given.** A small, plain description of the sessions, of your own shape, built by
  the caller: for each session its display name, whether it is working or waiting, and whether it has a
  question box open for the owner. The window must not reach into `SessionManager` or the engine. Add
  one small static helper that builds this description from a list of `Session` objects, reading
  `Session.ActivityState` and `Session.PendingInteraction` (see
  `src/CcDirector.Core/Sessions/PendingInteraction.cs`). Note for your proof: that file says the
  property is currently never populated by the product; build and test the reading anyway, with a
  description you construct in the test, and say so plainly.
- **What the dialog gives back.** One result: smart shutdown with the time allowed, or shut down and
  ignore all sessions, or cancelled. Closing the window by its own X is cancelled. Cancel does nothing.
- **What the dialog shows**, in this order:
  - the number of sessions ("9 sessions are running");
  - the review, in code, no model: the count working against waiting ("4 working, 5 waiting"), and,
    only when there is at least one, the sessions with a question box open for the owner, by name,
    under the words "Answer these first?";
  - smart shutdown as the default, always - the default button, and the choice that Enter takes -
    explained in plain words that say all of this: your sessions are shut down nicely; each writes a
    short handover of what it was doing and what is left; they get ten minutes (the number follows the
    setting); whatever is still running after that is shut down for them; you can start the sessions
    again when the Director comes back. Add one short plain sentence on why a restart is worth doing
    (mission 10.7: a handover compresses a session down to what is left to do). Nothing nags;
  - one setting, the time allowed: 5, 10, 15, 30 minutes, an hour; default 10;
  - the other choice: "Shut down and ignore all sessions", visibly the lesser choice, with one plain
    sentence saying the sessions are ended at once and no handovers are written;
  - Cancel.
- The title and the words say "Smart shutdown" when opened from the window close and "Smart Restart"
  when opened from the File menu (mission 10.1): the caller passes which door it came from, and the
  only difference is the title and the confirm button's words.
- A view model holds everything the window shows, so the words and the counts are testable without a
  window. The window only binds.
- Do NOT define your own `InitializeComponent`. The old `DrainDirectorDialog` did, which skipped the
  generated code that connects named controls, and it threw on opening in every shipped build. Let the
  generated one stand.
- Log as `docs/CodingStyle.md` says (`FileLog.Write("[ClassName] Method: ...")`). No try-catch outside
  event handlers. No fallbacks. ASCII only, everywhere, including the interface text.
- The dialog never appears with zero sessions; that is the caller's rule, but the view model must
  refuse to be built for zero sessions with a clear exception rather than show "0 sessions".

## Tests - the law of this phase

Every new window gets a headless test in `src/CcDirector.Avalonia.Tests` that OPENS it. Put yours in a
folder `SmartRestart/` with namespace `CcDirector.Avalonia.Tests.SmartRestart`, so this filter finds
them:

    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"

That filter matches nothing today and a filter that matches nothing exits green. Read the COUNT, never
the colour. Tests you owe, each `[AvaloniaFact]`, named `Method_Scenario_Result`:

- the window opens (`Show()`), and every named control the code-behind uses is not null - this is the
  test the old window never had. Prove it can fail: add your own `InitializeComponent` the way the old
  window did, watch the test go red, take it out, and write down both results;
- the session count, the working and waiting counts, and the question box list appear as given; the
  question box section is absent when there are none;
- the default is smart shutdown with ten minutes; the dropdown holds exactly 5, 10, 15, 30, 60; the
  explanation's number follows the dropdown;
- each of the three results comes back from the right button, and the window's own X is cancelled;
- the two doors differ only in title and confirm words;
- the helper that reads `Session` objects reports working, waiting and an open question box correctly.

Do not hand-build a result and assert on it; drive the real window's buttons.

## Screenshots

An interface proves itself with screenshots rendered FROM the headless tests. The test project today
draws nothing (`UseHeadless` with headless drawing on, see `HeadlessTestApp.cs`), so a captured frame
would be blank. Make real rendering work for the test project (the Avalonia headless platform with Skia
and headless drawing off, then `CaptureRenderedFrame()`), WITHOUT breaking any existing test: run the
whole project before and after and write both counts in your report. Another Developer is doing the
progress screen beside you and needs the same thing; keep this change tiny and in `HeadlessTestApp.cs`
and the project file only, so the second one to merge has a trivial conflict.

Screenshots go, from a test that writes them only when an environment variable names the folder, to
`docs/missions/smart-director-restart-2026-09-19/attachments/phase-2/` in YOUR worktree: the dialog
from the X with no question boxes, the dialog from the File menu, the dialog with two question boxes
open, and the dialog with the time allowed changed to 30 minutes. Look at each picture yourself before
you report; a blank or unstyled picture is not proof.

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
