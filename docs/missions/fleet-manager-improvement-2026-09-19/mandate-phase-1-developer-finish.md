# Fleet Manager Improvement - phase 1 task 1 - finish mandate for the Developer

20 September 2026. From the Delivery Lead (session 4e6fa57d). You report to the Delivery Lead. This
file is your whole mandate. Your seat is Developer. The owner has asked for phase 1 merged and
deployed today.

## Where you are

This worktree, `D:\ReposFred\devthrottle-fmi-p1-gateway`, branch
`fleet-manager-improvement/p1-gateway`, is the branch of open pull request 3198 (issue #3177). The
work is already built, and a Reviewer has already read the whole change and given the verdict: the
design is right, the build is honest, nothing found blocks the merge on correctness.

The review is committed on the record branch. Read it in full before you touch anything:

    git show origin/fleet-manager-improvement/p1-record:docs/missions/fleet-manager-improvement-2026-09-19/reviews/phase-1-task-1-review.md

Also read, in this order: `docs/missions/fleet-manager-improvement-2026-09-19/MISSION.md` (it wins
where anything disagrees), the repository's `CLAUDE.md` - especially the built-in skill rule - and
`docs/CodingStyle.md`.

## Your task, and only this

1. **Fix finding 1, the one thing blocking the merge.** The shipped `fleet-manager` skill,
   `src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md` at the rows for the owner's page
   and the walkthrough, still says each "refuses a session key". This change makes both sentences
   false: a raised session key may read the page and the walkthrough. Fix both rows in the same
   words the placement row above them already uses ("refuses those routes to every session key that
   is not raised"). Obey the built-in skill rule: edit the one source under `Skills/Content`, and
   regenerate the `.claude/skills/...` copy from it if one exists, rather than hand-editing the copy.
   There is a test that fails when the two bodies differ - run it.

2. **Answer every finding in writing.** Write
   `docs/missions/fleet-manager-improvement-2026-09-19/reviews/phase-1-task-1-answers.md` on THIS
   branch, one short paragraph per finding 1 to 9 plus the reviewer's terminology question, each
   accepted or declined with the reason. Findings 3, 4, 6, 7, 8, 9 and the terminology wording are
   FOLLOW-UPS by the Delivery Lead's decision - decline them here with that reason and say so
   plainly; the owner is filing them as work items. Do not fix them on this branch. Finding 2, the
   unpaid full gate, is the Delivery Lead's to answer - record that it is being carried as a work
   item, not paid here.

3. **Prove it and push.** Run the two suites that can decide this change quickly: the Gateway unit
   suite, and the raised session host tests. Both must be green. Append what you ran and the counts
   to `docs/missions/fleet-manager-improvement-2026-09-19/proofs/phase-1-task-1-developer.md`. Then
   commit and push to `fleet-manager-improvement/p1-gateway`.

**Do not merge, do not deploy, do not touch any other branch or worktree, and do not start anything
from phase 2 onward.** The Delivery Lead merges and deploys.

## When you are done

`cc-devthrottle session report "..."` saying the fix is pushed, the answers are written, and the
counts from the two suites. If something here is genuinely undecidable,
`cc-devthrottle session raise "..."` and carry on with the rest.

No assistant's name on any commit, comment or document - no trailer, no footer. Plain English, no
abbreviations.
