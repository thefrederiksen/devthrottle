# Mandate - Smart Director Restart - Tech Lead, phase 3 (the way up)

You are the Tech Lead for phase 3 of the Smart Director Restart mission. You were opened by the
Delivery Lead (session number 150) and you report to it, never to the owner and never to the fleet.
You have no transcript; this file and the files beside it are your history.

## THE RULE THAT KEEPS YOU ALIVE - read twice

On this Director a session that has ended its turn is never woken: the message doorbell does not ring
(product issue 3186). So NEVER end your turn while you are waiting for a session you opened. WAIT IN
THE FOREGROUND, inside your turn: one shell command that loops until the thing you wait for exists,
pausing about a minute between looks, at most nine minutes per command, run again until it is there.
Wait on an ARTIFACT - a file, a pushed commit, a pull request state - or on
`cc-devthrottle session workers` showing the session no longer working. Example:

    until test -f <absolute path to the review file> ; do ping -n 61 127.0.0.1 > "$TEMP/wait.txt"; done

Never `run_in_background`. The Delivery Lead polls you and is not rung either: write anything it must
know into `phase-3-status.md` in this folder, in your worktree, as you go.

Write the same rule into every mandate you hand out: a Developer or Reviewer finishes everything,
pushes it, and writes its proof or review FILE before its turn ends, because nobody can wake it for a
second round. A second round is a fresh session on a new mandate.

Second trap: a safety hook stops any shell command that runs `rm` on a path built from a variable and
asks the owner, which hangs the seat for good. Literal paths only. Put this in every mandate too.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Tech Lead".
2. `mission.md` in this folder, all of it. It wins over this file.
3. `phase-1-interface.md`, `phase-1-proof.md` and `phase-2-proof.md` in this folder - what the two
   phases before you built and what they could not reach.
4. `docs/CodingStyle.md` and `docs/VisualStyle.md`.

Mission issue: #3167.

## Your phase

Section 6, phase 3, and section 5.3 items 10, 11 and 14, with the rulings 10.2, 10.3 and 10.5:

- at start-up, once connected to the Gateway, the Director asks for workspaces whose Machine and
  DirectorName are its own, whose origin is a smart shutdown, and which hold at least one seat decided
  `restore` with no restored session id. A PRESENCE check on the record, never "no sessions are
  running". A record for another Director on the same machine is not offered; a record already brought
  back is not offered again; a record from "shut down and ignore all" or a cancelled one is never
  offered automatically;
- "A restart is available": when, how many, one row per mission head with a tick box, all ticked,
  leads first; Bring back, or Not now. Not now keeps it in the history;
- File, Restart history: every record for this Director, newest first, what came back and what did
  not, and a way to bring back later;
- a session ended at the limit, or that never answered, is NOT brought back automatically: listed as
  "ended without a handover", unticked, with one button that reopens its saved conversation with a
  first line telling it that it was stopped and must re-check before acting;
- the seed file the restore writes says only: you are a restored session, read this document, what
  changed while you were gone, verify state before acting. No rules of conduct;
- bring back goes through the existing restore (`DirectorRestore`), leads first, under the real owner.

TEST BEFORE YOU BUILD ON IT (mission document 10.3, and "Not verified by the Architect"): whether
reopening a saved conversation works on the current build, for Claude Code first, then Pi. Codex is
noted only - its driver ignores the conversation id, so a Codex session ended at the limit is offered
as a fresh blank session in its repository. Do this test on the isolated rig
(`scripts/restart-qa-rig.ps1`), never on a real Director, and write down exactly what you ran and saw.
If reopening does not work for an agent, that agent is "noted only" too, and you say so in the proof;
you do not build a second way round it.

MEASURE (mission document 4.5): how long a session interrupted mid-turn needs to write a handover,
on the rig, several runs. Three minutes is a guess. Report the numbers; the Delivery Lead decides
whether the two-thirds point moves.

Every new window gets a headless test in `src/CcDirector.Avalonia.Tests` that opens it, with
`SmartRestart` in its namespace or class name.

## The check you run yourself before accepting any work

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"

Read the COUNT, never the colour; a filter that matches nothing exits green. Baseline both on untouched
`origin/main` first and write the counts down. Screens prove themselves with screenshots rendered from
the headless tests, committed under `attachments/phase-3/` in this folder.

## How you work

- You never write code. Open Developers, one task each, as visible sessions:
  `cc-devthrottle session spawn <worktree path> --agent <agent> --controlled-by self --name "Director Restart - Developer - <task>" --prompt "Read <absolute path to a mandate file>. It is your whole mandate."`
  The prompt is ONE line pointing at a file. A long prompt never arrives.
- One worktree per concurrent workstream, cut from `origin/main`. Never two sessions in one tree at the
  same time. Never work in `D:/ReposFred/devthrottle` itself. Remove a worktree when its pull request
  merges. `MainWindow.axaml.cs` is very large and other missions edit it: keep the change to it small,
  put new code in new files, merge that pull request quickly.
- Send each Developer's code to a Reviewer running a DIFFERENT agent from the one that built it. The
  review is a file in this folder (`review-phase-3-<n>.md`) that states its scope. Findings are
  answered, every one, in `review-phase-3-<n>-answers.md`, by a fresh Developer on a mandate that names
  the findings.
- Every pull request goes all the way to merged on `origin/main` the day it opens, squash merge,
  branch deleted. Do not wait for the hosted checks to finish before starting the next task.
- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any
  commit, pull request, issue, comment or file. ASCII only in everything.
- Never destroy, deploy, send anything outward. Never restart, drain or stop a session that is not
  yours. Never run the feature against a real Director; tests and the isolated rig only.
- Shut down the Developers and Reviewers you opened once their work is merged.

## What you owe the Delivery Lead

`phase-3-proof.md` in this folder, merged: the check's commands, the counts before and after, what each
new test proves in plain words, the screenshots, the reopen test per agent, the handover timings, the
pull request numbers, and what you could not reach. Then `cc-devthrottle session report "<one
paragraph>"` AND the same paragraph at the end of `phase-3-status.md`.
