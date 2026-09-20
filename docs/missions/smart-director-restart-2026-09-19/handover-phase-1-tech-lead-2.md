# Handover - Smart Director Restart - Tech Lead, phase 1, second seat

You replace the first phase 1 Tech Lead (session 133). It did not fail at its work: it stopped for good
on a permission question that only the owner can answer, and the Delivery Lead (session 150) ended it
rather than call the owner. You report to the Delivery Lead.

## Read first

1. `mandate-phase-1.md` in this folder - your whole mandate. Everything in it still binds you.
2. `mission.md` and `phase-1-interface.md` in this folder.
3. The first seat's record, already on `origin/main` in this folder: `proof-phase-1-task-1.md`,
   `proof-phase-1-task-2.md`, `review-phase-1-1.md`, `review-phase-1-1-answers.md`,
   `review-phase-1-2.md`.

## Where the phase stands (checked by the Delivery Lead on `origin/main`)

Merged: pull request 3172 (the interface), 3182 (contract types, record marks, the two session verbs,
the new request text), 3181 (the restart cycle drains through the real drain, issue 3169).

Verify for yourself before relying on it: whether `review-phase-1-2.md` has its answers file on main. If
not, law 11 applies: every finding is answered by the Developer that built the work, or by a fresh one.

Not started: task 3, the engine itself - the smart shutdown run, two stages and the limit, shut down
now, cancel and keep working, ignore all. The first seat had written its mandate:
`mandate-phase-1-developer-3-engine.md` in this folder (not yet committed) and had cut the worktree
`D:/ReposFred/devthrottle-smart-restart-p1-engine` on branch `smart-restart/p1-engine`. Read that
mandate, check the worktree is at current `origin/main` and clean, then open the Developer on it.

Left over and yours to tidy: worktree `D:/ReposFred/devthrottle-smart-restart-p1-gate2` (detached; check
it holds nothing unmerged, then `git worktree remove` it), and five mandate files in this folder that
are untracked here but already on `origin/main`.

## The trap that ended the first seat - do not repeat it

A safety hook on this machine stops any shell command that runs `rm` on a path built from a variable
(`rm $M/$f`) and asks the owner. Nobody but the owner can answer, so the seat hangs for good. Never
write such a command. Delete files by LITERAL path, one per `rm`, or use `git clean` on a literal path,
or simply `git checkout --detach origin/main` after `git stash -u` is NOT acceptable either (no stashes
left behind) - literal paths only. Tell your Developers and Reviewers the same in their mandates.

## Also know

The hosted Gateway refuses a drain state it does not know. The new `ended-at-limit` mark therefore
needs a Gateway deploy before the real run in phase 5; the isolated rig is unaffected. The Delivery
Lead is carrying that. Nobody in this phase deploys anything.
