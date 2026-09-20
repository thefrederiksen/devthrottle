# Mandate - Smart Director Restart - Developer, phase 6: answer the review, then land it

You are a Developer on the Smart Director Restart mission. You were opened by the Delivery Lead (session
number 150) and you report to it. The Developer that wrote phase 6 is gone and cannot be woken, so under
the method you stand in for it: you answer every finding. You have no transcript; this file and the
files it names are your history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p6`, branch `smart-restart/p6-retire`, one
commit ahead of `origin/main`, pushed.

## Finish before you stop

On this Director a session that has ended its turn is never woken (product issue 3186). Do EVERYTHING
below before your turn ends. Never `run_in_background`; keep any one command under nine minutes. Never
run `rm` on a path built from a variable: a safety hook stops it and asks the owner, which hangs you for
good. Literal paths only. The account's weekly and monthly usage limits are nearly spent; if your screen
warns one is close, write where you are into
`docs/missions/smart-director-restart-2026-09-19/phase-6-status.md`, commit and push at once.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - "If you are a Developer", and laws 1, 4, 9, 11, 14.
2. In `docs/missions/smart-director-restart-2026-09-19/` in your worktree: `mandate-phase-6.md` (what was
   asked), `proof-phase-6.md` (what was built), `mission.md` (it wins over everything here).
3. The review:
   `D:/ReposFred/devthrottle-smart-restart-p6-review/docs/missions/smart-director-restart-2026-09-19/review-phase-6-1.md`.
   Four findings, by a Reviewer running a different agent. Read all of it, including "On the proof".

## Your task

1. Copy the review into your branch as
   `docs/missions/smart-director-restart-2026-09-19/review-phase-6-1.md`, unchanged.
2. Answer findings 1, 2 and 3 now in `review-phase-6-1-answers.md` beside it: accepted, with what you
   changed; or declined, with the reason. A Reviewer advises; it does not command. Check each finding
   against the code yourself before you accept it - the Reviewer may be wrong.
   **Finding 1 is the one that matters**: a public page must not tell a user to use something no
   released build has. Whatever you write there, a reader on today's build must not be misled.
3. **If you change the published skill, publish it again** (`cc-devthrottle skill push` / `publish`;
   read `--help` first) and say in the answers file what you published and when. The published copy and
   the copy in `attachments/phase-6/` must match: the review checked that, and so will the next one.
4. Finding 4 needs phase 3, which is being built now: the way up (the start-up offer and the restart
   history window) is in pull request 3208 on branch `smart-restart/p3-way-up-windows`. WAIT FOR IT IN
   THE FOREGROUND, inside your turn, then write the true answer:

       until gh pr view 3208 -R thefrederiksen/devthrottle --json state -q .state | grep -q MERGED ; do ping -n 61 127.0.0.1 > "$TEMP/wait.txt"; done

   Run that in pieces of under nine minutes and repeat it. While you wait, do steps 1 to 3. When it
   merges, `git fetch origin` and `git merge origin/main` into your branch, READ what the way up
   actually does on main, and make the page true about it. If it has not merged after about an hour,
   answer finding 4 against what is on main at that moment, say so plainly in the answers file, and
   carry on - do not wait for ever.
5. Run the checks the proof ran (the mission check, the retired-words sweep over `docs/public`, the docs
   drift check - the proof names them) and put your own counts in the answers file. Read the COUNT, never
   the colour.
6. Commit, push, open the pull request against `main` naming issue #3167, then squash merge it with the
   branch deleted. Do not wait for the hosted checks. If the merge is refused, write why into the
   answers file, push, and stop - force nothing.
7. Then `cc-devthrottle session report "<one paragraph>"` AND the same paragraph at the end of
   `phase-6-status.md`, pushed, since a report may never ring.

Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any commit,
pull request, comment or file. Before every commit and the pull request, search your text for those
words and strip them. ASCII only. Never deploy. Never restart, drain, stop or message a session that is
not yours.
