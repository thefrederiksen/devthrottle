# Handover - Smart Director Restart - Tech Lead, phase 1, third seat

You replace the second phase 1 Tech Lead (1c3174ba). It did nothing wrong: it ended its turn to wait
for its Developer's report, and on this Director a waiting session is never woken (product issue 3186:
the message doorbell is deferred for ever). The Delivery Lead (session 150) ended it. You report to the
Delivery Lead.

## THE RULE THAT KEEPS YOU ALIVE - read twice

NEVER end your turn while you are waiting for a session you opened. Nothing will wake you. No message,
no report and no raised hand reaches a session that has stopped. Instead WAIT IN THE FOREGROUND, inside
your turn: run one shell command that loops until the thing you wait for exists, pausing about a minute
between looks, for at most nine minutes per command, and run it again until it is there. Wait on an
ARTIFACT - a file, a pushed commit, a pull request state - or on `cc-devthrottle session workers`
showing the session no longer working. Example:

    until test -f <absolute path to the review file> ; do ping -n 61 127.0.0.1 > "$TEMP/wait.txt"; done

Never `run_in_background`. Your turn ends only when the phase is finished and reported. The Delivery
Lead is polling you and is not rung either, so write anything it must know into `phase-1-status.md` in
this folder as you go.

Write the same rule into every mandate you hand out: a Developer or Reviewer must finish everything,
push it, and write its proof or review FILE before its turn ends, because nobody can wake it for a
second round. A second round is a fresh session on a new mandate.

Second trap: a safety hook stops any shell command that runs `rm` on a path built from a variable and
asks the owner, which hangs the seat for good. Literal paths only. Put this in every mandate too.

## Read first

1. `mandate-phase-1.md` in this folder - your whole mandate. Everything in it still binds you.
2. `handover-phase-1-tech-lead-2.md` in this folder - where the phase stood one seat ago.
3. What the second seat left in this worktree, uncommitted, in this folder:
   `mandate-phase-1-developer-3-engine.md`, `mandate-phase-1-developer-4-cancel-ignore-restart.md`,
   `mandate-phase-1-reviewer-3.md`, `review-phase-1-2-answers.md`. They are its plan for the rest of the
   phase. Use them; correct them where they tell a session to report and stop.

## Where the phase stands (checked by the Delivery Lead)

Merged: 3172, 3182, 3181 (see the second handover).

Task 3, the smart shutdown run, is BUILT and PUSHED, not reviewed, no pull request: branch
`smart-restart/p1-engine`, worktree `D:/ReposFred/devthrottle-smart-restart-p1-engine`, three commits
on top of `origin/main`, proof beside the code. Its Developer (97ce1acd) says the check ran 517 passed,
0 failed, and flags one decision for you to look at first: a session that had already handed over but
is still present at the limit. Read its proof for that. That session is stopped and cannot be woken;
the Delivery Lead ends it. Findings on its work go to a fresh Developer you open (law 11).

The Delivery Lead's own run of the check on `origin/main` at 9f79e92dc: 487 passed, 0 failed.

## What is left

1. Run the check on the engine branch yourself. Send it to a Reviewer on a DIFFERENT agent
   (`--agent Codex`). Findings answered; merge the day you start.
2. Task 4 as the second seat planned it: cancel and keep working, ignore all, the restart. Same loop.
3. Tidy worktree `D:/ReposFred/devthrottle-smart-restart-p1-gate2` if it holds nothing unmerged, and the
   engine worktree once merged.
4. `phase-1-proof.md` merged, then `cc-devthrottle session report "<one paragraph>"` AND the same
   paragraph at the end of `phase-1-status.md`, since the report may never ring.
