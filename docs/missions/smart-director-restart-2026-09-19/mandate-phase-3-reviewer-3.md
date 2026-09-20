# Mandate - Smart Director Restart, phase 3, Reviewer 3: the two WINDOWS of the way up

You are a Reviewer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

You did not write this code and you run a different agent from the two Developers that did.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-review3`, detached at commit
`2a1a210cf`. Work only there. **You change no product code.** Your output is one file.

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). Finish inside ONE turn and WRITE YOUR REVIEW FILE before your turn ends. A
   question you stop to ask is never answered; put it in the file instead.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes.

## What you are reviewing

Pull request **#3208**, branch `smart-restart/p3-way-up-windows`, at `2a1a210cf`. Two windows of the
way up and the start-up ask that shows one of them, built by two Developer seats and never yet
reviewed by anybody.

    git diff origin/main...HEAD

The engine they call (`IDirectorWayUp`, `WayUpWords`, `ControlApiHost.CreateDirectorWayUp`) is ALREADY
ON MAIN and has been reviewed twice. **Do not re-review the engine.** If you find a defect in it, say
so and label it outside your scope.

Read, in this order:

1. `docs/missions/smart-director-restart-2026-09-19/mission.md`, section 5.3 items 10 and 11 and
   rulings 10.2, 10.3 and 10.4 - what these windows are supposed to be.
2. `docs/missions/smart-director-restart-2026-09-19/mandate-phase-3-developer-windows.md` and
   `mandate-phase-3-developer-windows-2.md` - what the two Developers were told. Something they were
   told NOT to do and did is a finding.
3. `proof-phase-3-windows.md` and `proof-phase-3-windows-2.md` - what they claim. **Every claim is a
   claim to disprove.** Read section 6 of the first and the decisions in the second: those decisions
   stand unless they are wrong.
4. `docs/VisualStyle.md`.
5. The code.

## What to attack, in order of what it would cost to get wrong

1. **The dumb client rule** (`CLAUDE.md` critical rule 7). Find any word a window decides for itself,
   any state it interprets, any count it re-derives, any sentence it invents when the engine gave it
   none. An empty string where a sentence belongs is the same defect. Two places the Developers
   themselves flagged and the Tech Lead accepted - check both and say whether you agree: the button
   words ("Bring back", "Not now", "Close") and the window title are held as constants in the window,
   not in the engine; and `NoHostWayUp` supplies ONE reason string which it feeds INTO the engine's
   own wording function.
2. **The start-up ask.** It must ask ONCE, only when the connection is Connected, off the interface
   thread, and show NOTHING at all when the engine says nothing is waiting or refuses. A start-up
   that puts a box in front of the owner to say it could not check is a worse product than one that
   stays quiet. Can you find a path where it asks twice, asks early, blocks the interface thread, or
   shows something it should not?
3. **The reopen button.** It must name its OWN seat, never one found by position, and there must be
   no button at all for a seat the engine says has no conversation. Both windows draw one now; check
   they cannot drift - the second Developer put them behind one shared class, so check that is
   really true and not two copies.
4. **"Not now" must write nothing.** Check that closing, Escape and Not now all ask the engine for
   nothing, so the record survives to be offered again.
5. **Pressing twice.** Bring back must reach the engine once, not twice; running the restore twice
   would start a second copy of every session.
6. **The tests.** A test that hand-builds its input and never opens the real window proves nothing
   about the window. A check whose pass condition is an ABSENCE certifies a run that never happened.
   Are there rules the code holds that no test holds? Is any assertion the only holder of a rule?
7. **The flaky test.** `WayUpOfferWindowTests.BtnReopen_Clicked_ReachesTheEngineWithItsOwnRowsSeat`
   failed roughly one whole-project run in ten. The second Developer says the cause is a press that
   hands its work to a thread pool thread while the assertion did not wait, and that it fixed it by
   having the window remember the task its last press started. **Judge that fix**: is
   `WorkTheLastPressStarted` sound, is a failure still raised exactly once, and is any OTHER test in
   this branch racing the same way?

## Run the checks yourself

    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"
    dotnet test src/CcDirector.Avalonia.Tests

Read the COUNT, never the colour: a filter that matches nothing exits green, and an ABORTED run
prints "Passed" for whatever finished before it died - check each run reached its end. The Tech
Lead's own runs on this exact commit were **145** on the filter and **744** on the whole project,
0 failed, with the whole project run FIVE TIMES IN A ROW at 744 each time; `dotnet build
cc-director.sln` was 0 warnings and 0 errors. **Run the whole project at least three times yourself**
- one run says nothing about a race.

Then break ONE thing of your own choosing, show the red counts, put it back with `git checkout --`,
and show green again on a FULL build. Never a no-build run on the restore run: it certifies the
binary, not the source. If your mutation changes nothing, say so loudly.

## The pictures

Eight are committed under
`docs/missions/smart-director-restart-2026-09-19/attachments/phase-3/`. If you can open an image,
LOOK at them and say whether they show what the proofs say they show. If you cannot, say so plainly
rather than implying you did - the Tech Lead will look instead.

## What you owe

ONE file at `docs/missions/smart-director-restart-2026-09-19/review-phase-3-3.md`, in your own
worktree, committed and pushed on your own branch (`smart-restart/p3-review-3`), before your turn
ends. It must say: your scope and what you did NOT look at; your own counts, every run, and your own
revert proof numbers; each finding, ranked, with file and line, what it would do to a person, and how
sure you are; and what you could not check. **"No findings" is a real answer** - then say what you
attacked and failed to break, so the next reader knows what your pass covers.

Do not fix anything, do not open a pull request against the code, do not merge anything.

**Sign nothing**: no "Co-authored-by", no "Generated with", no agent or vendor name, anywhere in the
file or the commit. ASCII only - no Unicode, no emoji, no arrows, no tick marks.
