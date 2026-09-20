# Phase 1 status - written by the Tech Lead as it goes (third seat, session f42438de)

The Delivery Lead polls this file. Newest entry last.

- 20 September 2026. Third seat started. Read the handover, the mandate, the second seat's plans and
  the task 3 proof. The engine branch `smart-restart/p1-engine` (`2d2abd541`) sits on top of
  `origin/main` with nothing behind it.
- My own run of the check on `2d2abd541`, built from source in a separate worktree
  (`D:/ReposFred/devthrottle-smart-restart-p1-review3`): 517 passed, 0 failed, 517 in all. Baseline on
  `origin/main` at `9f79e92dc` was 487. The Developer's count holds.
- The Developer's flagged decision (a session that had already handed over and is still present at the
  limit is ended but keeps "drained" and its handover): ACCEPTED. Mission 10.3 lists an ended-at-limit
  session as "ended without a handover"; marking a clean handover that way would hide it.
- Task 3 sent to a Reviewer on Codex. Waiting in the foreground on `review-phase-1-3.md`.
- Codex hit its usage limit (until 22 September 2026), so the Codex Reviewer was closed unused. Task 3 is with a Reviewer on Pi (session be0d6235), seated and reading. Waiting in the foreground on review-phase-1-3.md in D:/ReposFred/devthrottle-smart-restart-p1-review3.
- The Pi Reviewer returned review-phase-1-3.md: the check 517 passed on its own run, two narrow findings, neither touching safety (a Gateway client captured once gives false refusals after a settings change; a screen handler that blocks stalls the run and nothing says it must not). Both accepted. PHASE 2 SHOULD KNOW NOW: the handler of ISmartShutdownRun.Changed must return at once and dispatch to the user interface thread asynchronously (post, never a synchronous invoke), and should read Current after attaching. A fresh Developer (task 3b) is fixing finding 1 and writing that sentence into the contract. Waiting in the foreground on the pushed review-phase-1-3-answers.md.
- Task 3b (the review's findings) is pushed: finding 1 fixed with a test that watches the host, finding 2
  answered on the contract side (the sentence is now in phase-1-interface.md section 3 and on the event's
  comment), every note answered in review-phase-1-3-answers.md. My own run of the check on the engine
  branch MERGED with origin/main at 8b296fe48: 518 passed, 0 failed.
- The default local gate (scripts/test-local.ps1) on that merged tree shows three reds, NONE from the
  engine branch. (a) RetiredMessagingWordsTests: a comment from this mission's own task 1, already on
  main ("whichever message asked for it", DrainMessagesSmartShutdownTests.cs line 72) trips the word
  sweep, so main's default gate is red because of us. A small Developer (task 3c) is rewording it on the
  engine branch. (b) Two LauncherDeclaredCapabilitiesTests are red on UNTOUCHED origin/main at 8b296fe48
  on this machine (2 failed of 17, my own run); the engine branch does not touch the Launcher. FOR THE
  DELIVERY LEAD: that one is not this phase's; I have not looked for its cause.
- TASK 3 IS MERGED: pull request 3193, squash commit aa8b22912. My own runs on the merged result before
  merging: the check 518 passed, 0 failed; the retired words check 5 passed, 0 failed. The three sessions
  I opened for it (the Pi Reviewer, Developers 3b and 3c) and the unused Codex Reviewer are flagged for
  deletion. The engine worktree is deregistered and its branch deleted; its FOLDER
  D:/ReposFred/devthrottle-smart-restart-p1-engine still holds leftover build output that would not
  delete (in use) - safe to remove later, it is no longer a worktree. The gate2 worktree was already gone.
- Task 4 (cancel and keep working, ignore all, the operating system shutdown record, the restart
  purpose) opened on worktree D:/ReposFred/devthrottle-smart-restart-p1-task4, branch
  smart-restart/p1-cancel-ignore-restart. Waiting in the foreground on its pushed proof-phase-1-task-4.md.
- Task 4 Developer (b09a6891) raised two calls and is building on its recommendations. Both AGREED by
  the Tech Lead: (one) the single existing assertion that CanCancel is false in every snapshot pinned
  the "not built yet" placeholder, not older behaviour, so it changes to the new rule and the proof says
  so; (two) on a cancel every session already closed is brought back through the existing restore, by
  setting its decision to restore and keeping the session's own answer in the why.
- 03:21 to 06:00 (20 September 2026): the account's usage limit stopped BOTH this seat and the task 4
  Developer mid-work. Both resumed by themselves at 06:00; nothing was lost, the Developer's changes were
  uncommitted on disk and it carried on. Task 4 is still being built. Codex stays out until 22 September.
- Task 4 is BUILT and PUSHED: branch smart-restart/p1-cancel-ignore-restart at 8d2101caf, proof beside
  the code. My own run on it merged with origin/main: the check 540 passed, 0 failed (baseline 518); the
  retired words check 5 passed. The Developer found one thing phase 3 should know: a real Director cannot
  run the restore directly, because the Gateway grants the restore lease only at its restore door; the
  new class GatewaySmartShutdownBringBack asks that door for this Director's own id, and phase 3's
  "Bring back" will meet the same rule and can reuse it. Task 4 is now with a Reviewer on Pi; waiting in
  the foreground on review-phase-1-4.md.
- The Pi Reviewer returned review-phase-1-4.md: its own run 540 passed, two narrow findings (a session appearing during an ignore-all is neither recorded nor ended; a Gateway failure at the launcher ask shows Failed instead of RestartRefused). Both accepted. A fresh Developer (task 4b) is fixing them; waiting in the foreground on the pushed review-phase-1-4-answers.md.
- 20 September 2026, the Developer seat that stood in for the Tech Lead to finish task 4 and close the phase. PHASE 1 IS FINISHED AND MERGED. Task 4 - cancel and keep working, shut down and ignore all sessions, the operating system shutdown record, and the restart purpose - went to main with the answers to its review as pull request 3212, squash commit 09ae9c72d, branch deleted. Both findings of review-phase-1-4.md were already answered in code by the seat that died at 06:47 on the account's spend limit; I read that commit against each finding myself rather than trusting its message, and ran a revert proof on each: taking out the one further pass reddens the appears-after-the-record test and only it, taking out the launcher step's catch reddens both cases of the Gateway-that-dies theory and only them, and both restore green rebuilt. My own runs on the branch merged with origin/main at 217b79f63 - the mission's check 606 passed 0 failed, against 580 on untouched origin/main measured in a worktree of its own; the retired words check 5 passed; phase 2's Avalonia smart restart tests 47 passed; the Avalonia build clean. The merge needed one conflict resolved in GatewayClient.cs, where this branch and main had each added a method in the same place and both are kept whole, and one thing the compiler found, because phase 4 added PassDevReportsAsync to IRestoreGateway and the cancel tests' fake Gateway must answer it. phase-1-proof.md is merged beside the code: the check and its counts task by task, what each task built, the four reviews and their six findings with where each is answered, the pull request numbers 3172, 3181, 3182, 3193 and 3212, and what the phase could NOT reach - nothing in phase 1 was ever run against a real Gateway, a real Director or a real launcher; the new record marks need a Gateway deploy before a real Director can save them; the roster lag and the two windows around a late cancel are disclosed, unsolved and untested; and nothing outside the engine calls the ignore-all path yet, so the sentence it now returns about a session that was ended and is in no record is shown nowhere until whoever wires that button shows it.
