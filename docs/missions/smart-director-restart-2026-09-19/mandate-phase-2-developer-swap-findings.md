# Mandate - Smart Director Restart - Developer - the swap, review findings

You are a Developer on phase 2 of the Smart Director Restart mission. You were opened by the phase 2
Tech Lead (session c6c50eeb) and you report to it, never to the owner and never to the fleet. You have
no transcript; this file and the files it names are your history. The Developer who built the swap is
gone; you answer the review of its work.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p2-swap`, branch `smart-restart-p2-swap`. Work
only there. The mission record lives in ANOTHER worktree, `D:/ReposFred/devthrottle-smart-restart-p2`;
read it there, never write to it.

## ONE TURN ONLY - read twice

Nobody can wake you once your turn ends. No message reaches a stopped session. Finish EVERYTHING below,
push it, and write your answers file before your turn ends. There is no second round. Run everything in
the foreground; nothing in the background; no sub-agents. Never run `rm` (or any delete) on a path
built from a variable: a safety hook stops the command and asks the owner, which hangs your seat for
good. Literal paths only.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. In `D:/ReposFred/devthrottle-smart-restart-p2/docs/missions/smart-director-restart-2026-09-19/`:
   `review-phase-2-4.md` (the review, two findings), `mandate-phase-2-developer-swap.md` (what the first
   Developer was asked to build; its "Rules that do not bend" bind you), and `mission.md` section 4.4.
3. In your worktree: `docs/missions/smart-director-restart-2026-09-19/attachments/phase-2/swap-proof.md`,
   decision 6 above all.

## Your one task

1. **Bring the branch up to main.** The engine branch that was merged into this branch has since landed
   on main as a squash (pull request 3193). `git fetch origin`, then `git merge origin/main` (a merge, NOT
   a rebase). Wherever the merge conflicts in a file under `src/CcDirector.ControlApi`,
   `src/CcDirector.Gateway*`, `src/CcDirector.Core` or the engine's tests, take main's version whole
   (`git checkout --theirs <literal path>`): you own none of those files. Afterwards
   `git diff origin/main --stat` must show ONLY the swap's own files. If it shows an engine file, say so
   in the answers file and stop changing things there.
2. **Finding 1, ACCEPTED by the Tech Lead.** When the Director has no engine (the control service did not
   start, so the factory returns nothing) and sessions are running, the window close must still ASK. The
   owner said: if any sessions are running, show the number and ask. The ruling: open the SAME dialog.
   The smart choice is dead and the dialog says why in plain words (this Director's control service did
   not start, so a smart shutdown cannot run; the log has the reason). "Shut down and ignore all
   sessions" is live and, with no engine to call, lets the close carry on through the main window's
   existing close path, which ends the sessions, asked exactly once. Cancel, Escape and the dialog's own
   close do nothing. With zero sessions the close still carries on with no dialog. The File menu door
   keeps its sentence. Tests, driving the real dialog: no engine and one idle session opens the dialog
   with the smart choice dead and the reason shown; ignore-all there closes the application exactly once
   and calls no engine; cancel there closes nothing. Prove the first of them can fail (put the old
   silent carry-on back, full build, red; restore, full build, green) - commit BEFORE you mutate.
3. **Finding 2, ACCEPTED by the Tech Lead as a hold on the merge, with no change to the code.** The
   engine on main still refuses the restart purpose, ignore-all and the operating system record. This
   branch does not merge until the engine has all three. Write that in the answers file; do nothing else
   about it.
4. With a full build each time (never `--no-build`), in the foreground:
   `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` - read the
   COUNT (87 before your work) and state it; `dotnet test src/CcDirector.Avalonia.Tests` - state the
   count; `dotnet build cc-director.sln` - 0 warnings, 0 errors.
5. Update `swap-proof.md` (decision 6 is replaced; the new counts; the commit of main you merged). Write
   `docs/missions/smart-director-restart-2026-09-19/review-phase-2-4-answers.md` in YOUR worktree: each
   finding, accepted, with the reason and what was done; the decisions are the Tech Lead's, say so.
6. Commit and push. Do NOT open a pull request and do NOT merge.
7. Last of all: `cc-devthrottle session report "<one paragraph: what you did, the counts>"`.

## Rules that do not bend

- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", no robot
  emoji, in any commit, file or comment. ASCII only in everything. Plain English, no abbreviations.
- Log as `docs/CodingStyle.md` says. Try-catch only at entry points. No fallbacks.
- Never kill a running process. Never launch, restart, drain or stop a Director or a session. Headless
  tests only.
- If something is undecidable inside this mandate, decide the smaller way, write down what you decided
  and why in the answers file, and carry on. Nobody can answer a question this turn.
