# Brief - Developer, round 2 track B: act on what waits, and archive a factory

You are a Developer on the Factories screen mission, round 2. Read, in this folder: `MANDATE-round-2-owner-feedback.md`
(the owner's words and the Factory Manager's findings - verify them against the live record), `PLAN.md` (all
decisions, including "Round 2"), and `MISSION.md`. The Implementation Lead (session b496c54d) opened you and is who
you report to. Your worktree: `D:/ReposFred/_wt/factories-screen-r2-actions`, branch `factories-screen/r2-actions`, cut
from origin/main. Another Developer builds track A (status reasons, FAILING clearing, PAUSED reason, the head's title)
in parallel in the same area - keep your changes tight and rebase onto origin/main before opening your pull request.
CLAUDE.md rule 7: every word, count and confirm sentence is folded on the Gateway.

## Your task: mandate items 3 and 4, in one pull request

1. **Waiting on you is actionable** on the factory page: each item with its text, when, which seat, a link to its
   evidence (when the row has one), and "Handled" (the existing handled mechanism). Order: newest and most important
   first (say your rule; escalated/needs-you before asked, then newest). Never hide or expire an item silently.
2. **Bulk clear, owner only:** one action "Mark everything older than 7 days as handled". The confirm says exactly how
   many items it will mark (counted on the Gateway). Each item gets its handled row as today, plus one activity row
   recording the owner's bulk act (who, when, how many). Refuse it for anything but the owner's own browser or phone
   (`FleetManagerOwnerDevice.Require`, as Talk does).
3. **Archive a factory, owner only:** "Archive factory" on the factory page. The confirm lists exactly what happens:
   it leaves the list; the Gateway schedules named in its registry seats are disabled (name them; none -> say none);
   all history, memory and the registry entry are kept. Recorded in the activity record as the owner's act.
   **Restore** undoes it (it re-enables only the schedules the archive disabled, and says which). A "Show archived"
   view on the Factories list. Archived factories are left out of the list and its status ordering.
4. Tests: the count and the bulk rows, the owner-only refusals (session key, Director device key, machine token),
   archive and restore including the schedule switching (with a fake schedule store), the list leaving out an
   archived factory, and Cockpit tests for the confirms (they show the Gateway's sentence verbatim).

Local gate plus your area's Gateway tests; say what you did not run (memory is short). Commit
`type(scope): description`, NO attribution. Push, `gh pr create`, then `cc-devthrottle message send b496c54d
"<one line>"`. Never deploy, never change live data, never archive or clear anything on the live Gateway.
