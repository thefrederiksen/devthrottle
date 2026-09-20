# Mandate - Smart Director Restart, phase 3, Developer: the two WINDOWS of the way up

You are a Developer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-windows`, branch
`smart-restart/p3-way-up-windows`. Work only there. Never work in `D:/ReposFred/devthrottle`.

**It is cut from the ENGINE BRANCH, not from main.** The way up engine you call is pull request
**#3202** on branch `smart-restart/p3-way-up-engine` (commit `0cda45788`), which is pushed, checked by
the Tech Lead and UNDER REVIEW - it is not on `main` yet. So it is already in your tree and you build
on the real types from the first minute. Two consequences you must plan for:

- the engine may CHANGE while you work, if the review finds something. When it does, the Tech Lead
  writes it into `docs/missions/smart-director-restart-2026-09-19/phase-3-status.md`. Read that file
  once before you open your pull request, and if the engine moved, merge it in and run your checks
  again;
- your pull request cannot merge before #3202 does. Open it anyway, say so in its text, and say so in
  your proof file. Do not wait for it inside your turn - you cannot be woken (see below).

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). FINISH EVERYTHING INSIDE ONE TURN: build it, run the checks, commit, push,
   open the pull request, and WRITE YOUR PROOF FILE - before your turn ends. If you stop half way to
   ask something, nothing will ever answer you. If you genuinely cannot finish, write how far you got
   into `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-windows.md`, commit, push, and
   then stop.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes.

## Read first

- `docs/missions/smart-director-restart-2026-09-19/mission.md`, section 5.3 items 10 and 11, and
  rulings 10.2, 10.3 and 10.4.
- `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-engine.md` - what the engine you
  call does and what it could not reach.
- `src/CcDirector.ControlApi/SmartRestart/` - the engine ITSELF. Read the real types. You build on
  them directly; you do NOT invent an interface of your own and you do not write a fake of the
  engine beyond what a test needs. Phase 2 built a screen on an invented interface and had to throw
  it away and build it again.
- `docs/VisualStyle.md` - every screen here must follow it.
- `src/CcDirector.Avalonia/SmartRestart/` and `src/CcDirector.Avalonia.Tests` - the way down dialog
  and progress screen from phase 2, and their headless tests. Copy their shape: they are the pattern
  this mission has already reviewed twice.

## What you build

### 1. "A restart is available" - the start-up window

Shown by the Director, by itself, once it is connected to the Gateway and the engine answers that
there is a record to offer. It shows what the engine computed and NOTHING it worked out for itself:

- when the shutdown was and how many seats are owed;
- one row per mission head, leads first, each with a tick box, all ticked;
- a seat that ended without a handover as its own row, unticked, with its own button that reopens
  its saved conversation (and, when the engine says the seat has no conversation id, the engine
  sentence saying so and no button);
- two answers: **Bring back** and **Not now**. Not now closes the window and writes nothing; the
  record stays in the history and is offered again next time.

**The client is dumb** (CLAUDE.md critical rule 7). Every word - the count, each row label, each
state, the sentence for a Codex seat, the refusal when the Gateway cannot be reached - comes from
the engine and is shown as given. You decide LAYOUT only. You may colour by a state; you may never
word by it. A test must prove that: give the window an engine answer whose label disagrees with its
own numbers and assert the window still shows the label it was given.

### 2. File, Restart history - the window

Every record for this Director, newest first: when, why, what kind of shutdown, and per seat what
came back and what did not - again all in the engine words. A record that still owes seats carries
the same bring-back offer the start-up window does, so a record answered "not now" can be brought
back later. That is the whole reason the history exists (the owner: "it could be that I accidentally
don't restart it right away and I want to restart it later").

### 3. The two small edits to `MainWindow`

`src/CcDirector.Avalonia/MainWindow.axaml.cs` is 7,000 lines, other missions are editing it, and
phase 2 is holding a branch that changes the File menu and `OnClosing`. So:

- KEEP YOUR CHANGE TO IT TINY. New code goes in new files under `src/CcDirector.Avalonia/SmartRestart/`.
  What lands in `MainWindow.axaml.cs` is one File menu item and one call at start-up, nothing else.
- Add the File menu item **Restart history...** beside the existing restart or drain item. Phase 2
  may have renamed the item next to it; read what is actually there and fit in.
- The start-up ask: follow the pattern already in that file. `WireGatewayStatusBox` /
  `TryAttachGatewayMonitor` already waits for the `ControlApiHost` to appear and then follows
  `GatewayMonitor`. Ask the engine ONCE, the first time that monitor reports Connected, off the user
  interface thread, and show the window only if the engine answers that there is something to offer.
  Never block the user interface thread and never show an empty window while the answer is on its way
  (CLAUDE.md rule 1).
- **A Director with no Gateway, or one whose engine refuses, shows NOTHING at start-up.** No dialog,
  no error box, no notification. A start-up that interrupts the owner to say it could not check is a
  worse product than one that stays quiet; the reason goes in the log and the answer is available
  from File, Restart history when he asks for it.
- Merge `origin/main` into your branch before you open the pull request, and again if it has moved.

## The check you run, and what it must say

    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"
    dotnet test src/CcDirector.Avalonia.Tests

Read the COUNT, never the colour: a filter that matches nothing exits green. The baseline the Tech
Lead measured on untouched `origin/main` = `ab2770c4a` is **47** on the filter and **646** on the
whole project, 0 failed in both - and the engine branch your tree starts from adds nothing to either,
because it added no Avalonia test. Confirm that yourself before you write a line: run both commands on
your tree UNTOUCHED and write the two numbers down. Your run afterwards must show both risen by the
number of tests you wrote, with no new failure.

**Every new window gets a headless test that OPENS it and asserts every named control is connected.**
That rule exists because the old `DrainDirectorDialog` defined its own `InitializeComponent`, which
skipped the generated code that connects named controls, and threw the moment anybody opened it - and
nothing caught it because no test ever opened the window. Put `SmartRestart` in the namespace or the
class name of every test you write, or the filter will not see it.

**Prove your tests can fail.** Do what phase 2 did: put the old window defect in on purpose (a
hand-written `InitializeComponent`), show the exact number of tests that go red, take it out, and show
them green again on a FULL build - never a no-build run on the restore run, because a no-build run
certifies the binary and not the source. Commit your work BEFORE you make the mutation so that putting
the code back cannot eat it.

**Screenshots.** Render them from the headless tests with real Skia, the way phase 2 did, and commit
them under `docs/missions/smart-director-restart-2026-09-19/attachments/phase-3/`. At least: the
start-up window with several mission heads and everything ticked; the same with a row that ended
without a handover, unticked, beside the others; the history window with more than one record,
including a cancelled one and an ignore-all one. Assert in a test that each picture is actually drawn
- not blank, carrying the window own colours - because nobody downstream may be able to view an image.

## What you may NOT do

- No change to the engine in `src/CcDirector.ControlApi`. If it cannot tell you something you need,
  STOP, write exactly what is missing into your proof file, and build everything else. The Tech Lead
  decides whether the engine changes; you do not reach into it.
- No change to the Gateway or to any contract.
- No second way of wording anything. If you find yourself writing a conditional in a window that
  decides what a state MEANS, it belongs in the engine - say so in your proof file.
- No fallbacks (CLAUDE.md rule 3).

## When it is built

1. Run both checks. Write the counts down.
2. Commit, plain English, no abbreviations, and **sign nothing**: no "Co-authored-by", no "Generated
   with", no agent or vendor name, anywhere. ASCII only, everywhere - no Unicode, no emoji, no
   arrows, no tick marks, in code, comments, commit messages and documents alike.
3. Push and open a pull request against `main` naming the mission issue #3167. Do not merge it
   yourself and do not wait for the hosted checks.
4. Write `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-windows.md` on the same
   branch: the commands, the counts before and after, the revert proof with its numbers, what each
   new test proves in plain words, the list of screenshots and what each one shows, every decision
   you made, and what you could NOT reach. Name the pull request number.
5. Then your turn may end.
