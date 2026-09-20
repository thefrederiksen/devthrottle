# Mandate - Smart Director Restart, phase 3, Developer: finish the two windows of the way up

You are a Developer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-windows`, branch
`smart-restart/p3-way-up-windows`, pull request **#3208**, already carrying two finished windows and
45 tests. Another Developer built them; you finish them. Work only there. Never work in
`D:/ReposFred/devthrottle`.

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). FINISH INSIDE ONE TURN: build, run the checks, commit, push, and write your
   proof file before your turn ends. Nothing can answer a question you stop to ask. If you cannot
   finish, write how far you got into the proof file, commit and push that, and stop.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes.

## Read first

1. `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-windows.md` - what is already
   built, and in section 6 every decision behind it. Those decisions stand.
2. `docs/missions/smart-director-restart-2026-09-19/mandate-phase-3-developer-windows.md` - the rules
   the windows were built to. They still bind you, in particular: the client is dumb, keep
   `MainWindow.axaml.cs` tiny, every new window gets a headless test that OPENS it.
3. `docs/missions/smart-director-restart-2026-09-19/review-phase-3-1.md` and
   `review-phase-3-1-answers.md` - finding 3, which is why task 2 below exists.

## THE ENGINE HAS MOVED, AND IT IS NOW ON MAIN

The engine your windows call merged to `main` as **`217b79f63`** (pull request #3202). Your branch was
cut from an older commit of it, so the first thing you do is:

    git fetch origin
    git merge origin/main

Three things changed on the engine since your branch was cut. Only the first touches you:

1. **`WayUpHistorySeat` gained a sixth part, `WayUpReopenOffer? Reopen`.** It is non-null exactly for
   a seat that ended without a handover, null for every other seat. **Anything that builds a
   `WayUpHistorySeat` positionally must now pass it**, which is every test fake of yours that builds
   one. Expect the merge to stop compiling until you do.
2. `IWayUpGateway` gained a roster method. Nothing in a window touches that seam.
3. `AgentPluginLaunchMetadata` gained a required part. Nothing in a window touches it either.

Also read the new sentences on `IDirectorWayUp.ReopenAsync` and on
`ControlApiHost.CreateDirectorWayUp` before you write task 2. They answer a question you would
otherwise have to guess: **a fresh engine per window, per screen or per action is safe**, because the
once-only reopen claim is held by the process. And a reopen whose start FAILS keeps its claim until
this Director restarts, so a button that goes dead after a failure is deliberate and its refusal
sentence says so - do not build a retry around it.

## Your three tasks

### Task 1 - take the merge and make it build and pass again

As above. When it is done, re-measure both checks and write the numbers down.

### Task 2 - the history's reopen button, which is the whole point of finding 3

The first review found that the history TOLD the owner a saved conversation could be reopened and
gave him no way to do it, and gave him no honest words for it either. The engine fix put the offer
on the history seat. **Until the history window draws a button from it, that fix is invisible and the
history still makes a promise it does not keep.**

So: in `RestartHistoryWindow`, a seat that carries a `Reopen` offer gets a real button, worded by the
ENGINE - the same way the offer window already does it, and ideally through the same code, so the two
cannot drift apart. A seat whose offer says there is no conversation to reopen shows the engine's
sentence and NO button; a seat that carries no offer at all (it handed over, or it came back) shows
no button either.

This works on any record that holds such a seat, including a cancelled one and an ignore-all one -
the engine settled that, and the second Reviewer checked it. Do not add a rule of your own about
which records may show it.

Test it: a history record whose seats ended without a handover draws a button per seat; pressing one
reaches the engine naming THAT seat, off the interface thread, and shows the engine's answer; a seat
with no conversation gets the sentence and no button; and a button found by its own row rather than
by position, so the second seat's button can never reopen the first seat.

### Task 3 - THE FLAKY TEST, and it is yours

`WayUpOfferWindowTests.BtnReopen_Clicked_ReachesTheEngineWithItsOwnRowsSeat` passes 92 of 92 under the
`SmartRestart` filter every single time, and fails roughly one full run of the project in four. The
previous Developer saw it, reported it honestly, and could not name it. **The Tech Lead named it, by
running the whole project with `--logger "console;verbosity=normal"` until it fell over.** It takes
about 5 milliseconds when it fails and about 30 when it passes, which reads as a race between a click
that starts work on a thread pool thread and an assertion that does not wait for it.

Find the real cause and fix it. Do NOT paper over it with a sleep or a retry: a test that waits a
fixed time is the same defect with a longer fuse. Wait for the thing you actually care about - the
engine call having arrived - or make the click's work observable so the test can await it.

**A test that only passes under a filter is worse than no test**, because the filtered run is what a
gate reads. Prove the fix by running THE WHOLE PROJECT at least four times in a row and showing all
four counts. If you cannot make it fail before your fix, say so - and still fix the race you can read
in the code.

## What you may NOT do

- No change to `src/CcDirector.ControlApi`. If the engine cannot tell you something, STOP, write
  exactly what is missing into your proof file, and do everything else.
- No change to the Gateway or to any contract.
- Keep `MainWindow.axaml.cs` as it is - thirteen lines is right. Phase 2 holds a branch that renames
  the File menu item yours sits beside; do not touch that item.
- No second wording of anything. A conditional in a window that decides what a state MEANS belongs in
  the engine; say so in your proof file rather than writing it.

## The checks, and what they must say

    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"
    dotnet test src/CcDirector.Avalonia.Tests

Read the COUNT, never the colour. An ABORTED run prints "Passed" for whatever finished before it
died, so check each run reached its end. Before your changes, on the MERGED tree, the numbers to beat
are **92** on the filter and **691** on the whole project - and the whole-project run is the one that
catches task 3, so run it repeatedly and report every count, not the best one.

**Prove your own tests can fail.** Break one line of your task 2 work, show the red count, put it back
with `git checkout --`, and show green again on a FULL build - never a no-build run on the restore
run. Commit first so the restore cannot eat your work.

**The pictures.** Add one for the history with a reopen button on a seat that ended without a
handover, rendered the same way the seven existing ones are, committed under
`docs/missions/smart-director-restart-2026-09-19/attachments/phase-3/`. Assert it is really drawn, as
the existing picture test does.

## When it is done

1. Run the checks. Write every count down, including every repeat of the whole-project run.
2. Commit, plain English, no abbreviations, **sign nothing** - no "Co-authored-by", no "Generated
   with", no agent or vendor name. ASCII only: no Unicode, no emoji, no arrows, no tick marks.
3. Push. Pull request #3208 already exists and picks your commits up. **It can merge now** - the
   engine is on main - but do not merge it yourself and do not wait for the hosted checks.
4. Write `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-windows-2.md` on the same
   branch: the merge, the three tasks, the counts before and after, your revert proof numbers, what
   the new tests prove in plain words, the new picture, and what you could NOT reach. Say plainly
   what the cause of the flaky test turned out to be.
5. Then your turn may end.
