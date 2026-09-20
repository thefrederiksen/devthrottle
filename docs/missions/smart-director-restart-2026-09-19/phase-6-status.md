# Status - Smart Director Restart, phase 6

Written by the Developer seat that answered review 1, standing in for the seat that built phase 6.

**Phase 6 is finished and merged.** Pull request 3228 squash merged to `main` as `c6f0ceebf` at
15:26:59 UTC on 20 September 2026, branch deleted.

## Where things stand

- The `director-restart` skill is **v6**, published 15:21:10 UTC on 20 September 2026. The published body
  and metadata are committed at `attachments/phase-6/director-restart-skill-v6.md` and `.json`; the v5 the
  Reviewer read is kept beside them, unchanged.
- The public page is `docs/public/features/10-smart-restart.md`, reached from `docs/public/index.json`,
  the overview matrix in `docs/public/features/01-overview.md`, and `docs/features/feature-inventory.yaml`.
- All four review findings are answered in `review-phase-6-1-answers.md`. The review is committed beside
  it, unchanged, as `review-phase-6-1.md`.

## What is left for somebody else

- **The product does not tell the owner when the record was not written** on "Shut down and ignore all
  sessions" with the Gateway unreachable. The documents now say so; the product still does not. That is a
  code change with a test, in a phase that owns code.
- **The page carries a dated note** saying the feature is not in a released version. It comes off the day
  Smart Restart is in a release. The note says so itself.
- **Nothing in this feature has been run against a real Director, a real Gateway or a real launcher.**
  The quality assurance phase is still ahead of the mission.
- **The page has no screenshots.** `/document-features` needs the running application and a foreground
  window; the inventory entries carry `screenshot: ""`, which the drift check accepts as "none yet".

## Report

Phase 6 is merged: pull request 3228 went to main as `c6f0ceebf` with its branch deleted. All four
findings of review 1 were accepted after I checked each one against origin/main myself: the public page
now opens with a note that Smart Restart is in no released version yet and why the File menu item may be
missing, the promise that the record is written on "Shut down and ignore all sessions" now carries its
condition in both the page and the skill (with the Gateway unreachable it is not written, the sessions
end anyway, and the owner is told nothing), and the handover folder pattern now matches DrainPaths. For
finding 4 I waited in the foreground for pull request 3208, the way up, which merged at 15:14 UTC; I
merged origin/main into the branch, read the start-up offer and the restart history window on main, and
replaced the page's dead end with a "Coming back up" section describing the offer, its two kinds of row,
Bring back and Not now, and File, Restart history as the way to do it later - so the page no longer names
a step it cannot explain. The skill is republished as v6 (15:21:10 UTC) and committed beside the v5 the
review read, byte for byte what the fleet now reads; it also corrects the question box section, which
pull request 3215 made true while this was open. The two new windows are in the feature inventory and the
overview matrix. Checks on the merged branch: Gateway.UnitTests Drain|Restart 606 passed, Core.UnitTests
RetiredMessagingWords 5 passed, check-inventory-drift OK at 40 source paths and 7 pages, and
Avalonia.Tests WayUp|RestartHistory 50 passed. What none of that covers, and what nobody has done yet:
this feature has still never been run against a real Director, a real Gateway or a real launcher.
