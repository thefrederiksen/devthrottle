# Mandate - Smart Director Restart - Reviewer, phase 2, task 3 second round: the close still asks with no engine

You are a Reviewer on the Smart Director Restart mission. You were opened by the Tech Lead of phase 2
(session c6c50eeb) and you report to it only. You read; you never build and never fix what you find.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p2-review5`, a detached checkout of `43ac8c3e8`
(branch `smart-restart-p2-swap`). Change NO tracked file in it.

## ONE TURN ONLY - read twice

Nobody can wake you once your turn ends. Finish everything and write the review FILE before your turn
ends. Foreground only; no sub-agents. Never run `rm` (or any delete) on a path built from a variable: a
safety hook stops the command and asks the owner, which hangs your seat for good.

## What you review - a SMALL change

    git diff abb232669 HEAD

Two commits answering finding 1 of
`D:/ReposFred/devthrottle-smart-restart-p2/docs/missions/smart-director-restart-2026-09-19/review-phase-2-4.md`:
with no engine (the control service did not start) and sessions running, the window close used to carry
on unasked. The ruling is in `mandate-phase-2-developer-swap-findings.md` in the same folder, step 2. The
Developer's answers are `docs/missions/smart-director-restart-2026-09-19/review-phase-2-4-answers.md` in
your worktree; do not trust them. The whole swap was reviewed already; do not review it again.

Look only at: does the close now ASK in that state, through the same dialog, with the smart choice dead
and the reason in plain words; does ignore-all there close the application exactly once with no engine
call; do Cancel, Escape and the dialog's own close leave everything as it was and the window closable
afterwards; is the zero-sessions close still silent; did the fix open any new path by which the window
closes unasked or can never close; would the new tests go red if the old silent carry-on came back, and
do they drive the real dialog.

You may run (the Tech Lead's run: filter 95 passed, whole project 694 passed):

    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"

## What you write

One file, `docs/missions/smart-director-restart-2026-09-19/review-phase-2-5.md`, in YOUR worktree, NOT
committed: your SCOPE first (what you read, ran, could not reach), then numbered findings. A finding must
prove the harm, with file and line. You may return nothing. The very last line must be exactly
`END OF REVIEW`.

Never sign it: no agent or vendor name anywhere. ASCII only. Plain English, no abbreviations. Never
restart, drain, stop or message a session that is not yours. When the file is written:
`cc-devthrottle session report "<one paragraph: how many findings, the worst one>"`.
