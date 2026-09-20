# Mandate - Smart Director Restart - Developer, phase 1, task 4b: answer the review of cancel, ignore all and the restart purpose

You are a Developer on the Smart Director Restart mission. You were opened by the Tech Lead of phase 1
(session f42438de, the third seat) and you report to it, never to the owner and never to the fleet. You
have no transcript; this file is your history. The Developer that built task 4 is gone, so under law 11
of the method its review findings come to you, a fresh Developer.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p1-task4`, on branch
`smart-restart/p1-cancel-ignore-restart`, two commits on top of `origin/main` at `aa8b22912`. Work only
there. Never touch `D:/ReposFred/devthrottle`.

## THE RULE THAT DECIDES WHETHER YOUR WORK COUNTS

You get ONE turn. On this Director a session that has stopped is never woken: no message, no report and
no answer reaches it. So finish EVERYTHING - the code, the tests, the answers file, the commit and the
PUSH - before your turn ends. The Tech Lead is polling the pushed branch for the answers file, not for a
message. Never end your turn to wait for anything. If something is undecidable,
`cc-devthrottle session raise "<the question, with your recommendation>"`, then BUILD YOUR
RECOMMENDATION, carry on, and write the decision into the answers file.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. In `docs/missions/smart-director-restart-2026-09-19/` of your worktree: `review-phase-1-4.md` (the
   review you answer; it is untracked there, and you commit it unchanged), `proof-phase-1-task-4.md`,
   `phase-1-interface.md`, and `review-phase-1-3-answers.md` as the pattern for an answers file.
3. `docs/CodingStyle.md`.

## Your one task

Answer every finding of `review-phase-1-4.md` in a new file beside it, `review-phase-1-4-answers.md`:
accepted or declined, each with the reason. The Tech Lead's view, which you may overrule with a reason:

- **Finding 1 (a session that appears during "Shut down and ignore all" is neither recorded nor ended):
  ACCEPT, fix it.** After its end loop the ignore-all path asks once more what is live and ends what is
  left, the way the smart path already does in `EndSessionsThatAreNotSeatsAndCheckEmptyAsync`; the
  result's `Detail` names any session that was ended without being in the record, and any that would
  not end. One more pass, not a loop without a limit. A test on the rig: a session that appears after
  the record is written is ended, and the result says it was in no record. It must go red if the second
  pass is removed.
- **Finding 2 (a Gateway failure at the launcher ask ends the run `Failed` instead of `RestartRefused`):
  ACCEPT, fix it.** The Director is verifiably empty and the record stands; the contract wrote
  `RestartRefused` for exactly that. `AskLauncherAsync` is the run's own last step, so catching there is
  an entry point by this repository's rule, not a helper swallowing an error: catch what the launcher
  step throws, log it, and finish `RestartRefused` with the error's own words and the same
  standing-record sentence the null-client case already says. This is not a fallback: nothing is
  retried and nothing is hidden; the outcome names what happened. A test on the rig: a launcher step
  whose capability check throws gives `RestartRefused` with the reason, the record uncancelled. It must
  go red if the catch is removed.
- The review's notes under "Looked at and not counted as defects": record each as "noted, no change"
  with one line of reason. One of them you may fix if it is a few words: the busy message of
  `ShutDownIgnoringAllAsync` names a "Shut down now" button whatever is running, and the older drain has
  no such button.
- The stale comment the task 4 proof names in
  `Start_AcrossOneRealRun_EveryStateAndPhaseIsReached_AndEverySnapshotIsWhole` ("cancel is never offered
  because it is not built"): correct the COMMENT ONLY to the true reason (no restore is wired in that
  test's engine). Touch no assertion.

If you find yourself editing an EXISTING assertion to make it pass, stop: that is old behaviour changing.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

The Tech Lead's own run on `8d2101caf` merged with `origin/main`: 540 passed, 0 failed. Run it yourself
first on the untouched worktree, then after your change, built from source in the same command, never
`--no-build`. Read the COUNT, not the colour. Foreground only, never piped through `tail` or `head`.
Also run `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~RetiredMessagingWordsTests"`
(a repository-wide word sweep that reads your comments too) and build `src/CcDirector.Avalonia`. Do a
revert proof on EACH of your two new tests: commit first, mutate, REBUILD, run the WHOLE check, see red,
restore with `git checkout -- <literal path>`, REBUILD, see green.

## What you deliver, all pushed before your turn ends

1. The two fixes, the two tests, the comment - committed.
2. `review-phase-1-4.md` committed unchanged, and `review-phase-1-4-answers.md` beside it.
3. A short section appended to `proof-phase-1-task-4.md`: "After the review" - the counts before and
   after, each new test in one plain sentence, the revert proofs, what you could not reach.
4. `git push origin smart-restart/p1-cancel-ignore-restart`. Do NOT open a pull request. Do NOT rebase
   or force push.
5. Last of all: `cc-devthrottle session report "<one paragraph>"`.

## Rules

- No fallback programming. Every public method logs entry, exit and errors with `FileLog.Write`.
- Nothing in the background. No sub-agents inside your session.
- A safety hook on this machine stops any shell command that runs `rm` (or any delete) on a path built
  from a variable, such as `rm $folder/$file`, and asks the owner. Nobody but the owner can answer, so
  the seat hangs for good. Never write such a command. Delete files by LITERAL path, one per `rm`. Never
  leave a stash behind.
- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any
  commit, file or comment. ASCII only in everything. Plain English, no abbreviations.
- Never destroy, deploy, or send anything outward. Never restart, drain, stop or message a session
  that is not yours. Never run any of this against a real Director; tests only.
