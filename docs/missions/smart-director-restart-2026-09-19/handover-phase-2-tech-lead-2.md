# Handover - Smart Director Restart - Tech Lead, phase 2, second seat

You replace the first phase 2 Tech Lead (session 120). It did nothing wrong: it ended its turn to wait
for its Developers' reports, and on this Director a waiting session is never woken (product issue 3186:
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

Never `run_in_background`. Your turn ends only when the phase is finished and reported, or when you
raise a question to the Delivery Lead - and the Delivery Lead is polling you, it is not rung either, so
write anything it must know into `phase-2-status.md` in this folder as you go.

Write the same rule into every mandate you hand out: a Developer or Reviewer must finish everything,
push it, and write its proof or review FILE before its turn ends, because nobody can wake it for a
second round. A second round is a fresh session on a new mandate.

Second trap: a safety hook stops any shell command that runs `rm` on a path built from a variable and
asks the owner, which hangs the seat for good. Literal paths only. Put this in every mandate too.

## Read first

1. `mandate-phase-2.md` in this folder - your whole mandate. Everything in it still binds you.
2. `mission.md` and `phase-1-interface.md` (on `origin/main`; fetch first) in this folder.
3. What the first seat left in this worktree, uncommitted: `mandate-phase-2-developer-dialog.md`,
   `mandate-phase-2-developer-progress.md`, `phase-2-proof.md` (a draft). This worktree is on branch
   `smart-restart-p2-record`.

## Where the phase stands (checked by the Delivery Lead)

Two Developers finished about 23:55 on 19 September. Both pushed; neither is reviewed or merged:

- the smart shutdown dialog: branch `smart-restart-p2-dialog`, worktree
  `D:/ReposFred/devthrottle-smart-restart-p2-dialog`, two commits, proof beside the code. Its session
  (d4f456b2) left "two judgements for whoever wires the doors" in its proof: read them.
- the shutdown progress screen: branch `smart-restart-p2-progress`, worktree
  `D:/ReposFred/devthrottle-smart-restart-p2-progress`, three commits, proof beside the code. Its
  session (141ad384) left "open points for the Tech Lead" in its proof: read them.

Both built against an interface of their own shape, as the mandate allowed. Phase 1's real interface is
merged now (pull request 3172, types in namespace `CcDirector.ControlApi.SmartRestart`, first types
merged in 3182). Adopting it is part of task 3 or a task of its own - your call.

Those two Developer sessions are stopped and cannot be woken. You cannot message them; you did not open
them. The Delivery Lead will end them. Law 11 then applies: findings on their work are answered by a
fresh Developer you open, on a mandate that names the finding.

Baseline, run by the Delivery Lead on `origin/main` at 9f79e92dc: the whole `CcDirector.Avalonia.Tests`
project, 554 passed, 0 failed.

## What is left

1. Run your check on each branch yourself (read the COUNT for the `SmartRestart` filter).
2. Send each to a Reviewer on a DIFFERENT agent from the one that built it (both were built with the
   default agent; use `--agent Codex`). The review is a file in this folder.
3. Findings answered, fixed by a fresh Developer where accepted; merge both, the day you start.
4. Task 3: swap the menu item and the close hook, the operating system shutting down, adopt the real
   interface, remove `DrainDirectorDialog` and `CloseDialog` (issue 3168). Merges only with the headless
   window tests green.
5. `phase-2-proof.md` merged, then `cc-devthrottle session report "<one paragraph>"` AND the same
   paragraph at the end of `phase-2-status.md`, since the report may never ring.
