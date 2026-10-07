# Brief - Developer, round 2 track A: every status explains itself

You are a Developer on the Factories screen mission, round 2. Read, in this folder: `MANDATE-round-2-owner-feedback.md`
(the owner's words and what the Factory Manager found - VERIFY those findings against the live record, do not trust
them), `PLAN.md` (all decisions, including "Round 2"), and `MISSION.md`. The Implementation Lead (session b496c54d)
opened you and is who you report to. Your worktree: `D:/ReposFred/_wt/factories-screen-r2-status`, branch
`factories-screen/r2-status`, cut from origin/main. Another Developer builds track B (waiting items, bulk clear,
archive) in parallel in the same area (`FactoriesScreenFold`, `apps/cockpit/src/factory/`) - keep your changes tight
and rebase onto origin/main before you open your pull request. CLAUDE.md rule 7: every word is folded on the Gateway.

## Your task: mandate items 1, 2, 5 and 6, in one pull request

1. **A reason under every non-RUNNING status** on the list (and on the factory page header): one short line, folded on
   the Gateway - e.g. "Sender: 4 keep pages answered 404, 12:02", "Waiting since 21 Sep: 63 items, newest ...",
   "Nothing scheduled". Clicking the status or the waiting count opens the matching items on the factory's page
   (FAILING -> the failed rows; NEEDS YOU -> the waiting items).
2. **FAILING clears when the failure is over.** A failed row stops counting when a LATER successful row exists for the
   same seat AND subject, or when the owner marks that failure handled (reuse the existing handled/correction
   mechanism in the activity record - a new row, never an edit). Check what "subject" and "successful" mean on real
   rows (`cc-devthrottle factory activity --factory website-business --json`), write the exact rule into PLAN.md
   under "Round 2", and test both clearing paths plus a failure that has NOT cleared.
3. **PAUSED says why:** "Nothing scheduled" for a factory with no schedule at all, distinct from "Schedules switched
   off" (name how many).
4. **The factory's head may have any title.** The registry's `ceoSeat` is the factory's head; the list button and the
   page say that seat's own name and role ("Talk to Ruth Calder", "CFO Ruth Calder"). Do not rename the manifest key
   (registered data depends on it) unless you also keep the old one working; say what you chose.

Tests for every rule (fold unit tests with real-shaped rows; Cockpit tests that the reason and the links render
verbatim). Local gate plus your area's Gateway tests; say what you did not run (memory is short). Commit
`type(scope): description`, NO attribution. Push, `gh pr create`, then `cc-devthrottle message send b496c54d
"<one line>"`. Never deploy, never change live data.
