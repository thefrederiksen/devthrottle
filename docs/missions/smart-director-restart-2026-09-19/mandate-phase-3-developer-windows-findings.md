# Mandate - Smart Director Restart, phase 3, Developer: close the two test gaps in the way up windows

You are a Developer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-windows`, branch
`smart-restart/p3-way-up-windows`, pull request **#3208**. Work only there. Never work in
`D:/ReposFred/devthrottle`.

**This is a SMALL task: two tests and a merge.** Do them well and stop. Do not improve anything else.

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). FINISH INSIDE ONE TURN: build, run the checks, commit, push, write your
   answer file. Nothing can answer a question you stop to ask.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes.

## Read first

- `docs/missions/smart-director-restart-2026-09-19/review-phase-3-3.md` - the review. It found NO
  defect in the windows; read its section "What I attacked and failed to break" and do not undo any
  of it.
- `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-windows-2.md` - what is there now.

## Task 1 - merge main again

    git fetch origin
    git merge origin/main

Main has moved past this branch. The Reviewer checked and expects it to be clean - no file those
commits change is a file this branch changes - but check rather than assume. If it conflicts, resolve
by taking both sides and say exactly what you did.

## Task 2 - press the history's Bring back button (review finding 1)

`RestartHistoryWindow.axaml.cs`, `BtnBringBack_Click` and the `await ViewModel.LoadAsync()` after the
offer window closes. The button is drawn and the offer it builds is tested by calling `BuildOffer()`
directly, but **no test ever clicks it**, so the click handler and the re-read of the history
afterwards are held by nothing. Delete the re-read and every test stays green.

Write a test that clicks the real button on the real opened window and proves the history re-reads
itself afterwards - so that a record which has just been brought back stops saying it still owes
seats. That is the behaviour the re-read exists for, and a window that keeps saying a record owes
seats minutes after the owner brought them back is a window that lies.

If opening a modal dialog over a window is awkward under the headless platform, make the seam small
and honest rather than testing something adjacent: say in your answer file exactly what the test
drives and what it does not.

## Task 3 - give the offer window's reopen button two buttons to choose between (review finding 2)

`WayUpOfferWindowTests.BtnReopen_Clicked_ReachesTheEngineWithItsOwnRowsSeat` has a fixture with
exactly ONE reopenable row. So the rule it claims to hold - that the button carries its own row -
cannot be shown failing on that window: most ways of finding a row by position would find the same
row and the test would stay green. **A test whose fixture cannot distinguish the right answer from
the wrong one is not holding the rule.**

The history window already has the strong version: two reopenable seats, press the SECOND, the engine
is told about the second. Give the offer window the same. Then prove it: break the handler so it
finds its row by position instead of from its own row, show the test go red, put it back, and show it
green on a FULL build.

## What you may NOT do

- No change to `src/CcDirector.ControlApi`, to the Gateway, or to any contract.
- No change to `MainWindow.axaml.cs`.
- No new feature, no refactor, no tidying. Two tests and a merge.

## The checks

    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"
    dotnet test src/CcDirector.Avalonia.Tests
    dotnet build cc-director.sln

Read the COUNT, never the colour, and check each run reached its end - an ABORTED run prints "Passed"
for whatever finished before it died. On this branch head the Tech Lead and the Reviewer each measured
**145** on the filter and **744** on the whole project, 0 failed, and a clean build; the Tech Lead ran
the whole project five times in a row at 744 and the Reviewer three more times. **Run the whole
project at least three times yourself and report every count**, because this branch has already
carried one test that passed under a filter and failed one full run in ten.

Commit your work BEFORE any mutation so a restore cannot eat it, and never use a no-build run on the
restore run.

## When it is done

1. Run the checks. Write every count down.
2. Commit, plain English, no abbreviations, **sign nothing** - no "Co-authored-by", no "Generated
   with", no agent or vendor name. ASCII only: no Unicode, no emoji, no arrows, no tick marks.
3. Push. Pull request #3208 picks your commits up. Do not merge it and do not wait for the hosted
   checks.
4. Write `docs/missions/smart-director-restart-2026-09-19/review-phase-3-3-answers.md` on the same
   branch: the merge, one section per finding, what each new test drives and what it does not, the
   counts, and your revert proof numbers.
5. Then your turn may end.
