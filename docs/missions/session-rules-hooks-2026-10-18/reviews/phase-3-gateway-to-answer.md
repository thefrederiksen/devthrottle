# For Developer A: the Gateway review, to answer

The review is `reviews/phase-3-gateway-review.md`. Answer every item below, accepted or declined with the
reason, in `reviews/phase-3-gateway-answers.md` in YOUR worktree (commit it with the fixes). First pull the
phase branch into yours: `git merge mission/session-rules-hooks-p3` (the plan was amended).

1. Finding 1 (live screen firing labelled "Would have acted"): the Tech Lead asks you to fix it - the label
   must be true for a live rule whose keystroke did not land, an unconfirmed send, and an incomplete intent.
   Your call how (outcome prefix or recording the rule state on the firing); say which and why. Test each case.
2. Observation 1: the list page must not load every firing's full row to count today's. Use a count/newest
   query (or an equivalent bounded read). The rule page's firing record may stay complete for now; say so.
3. Observation 4: make `Overrides` internal if it is public only for tests.
4. Observation 5 and the plan amendment: `changedLabel` names the action ("Created by", "Changed by",
   "Promoted by"), "session" in lower case in `changedLabel` and `sessionLabel`.
5. Plan amendment: P1 carries `previewSuggestions` { machines, repos }, the same lists as P3 `suggestions`.
6. Observation 7: fill the test gaps it lists (screen firings on the rule page incl. `BottomOf` and the cut,
   a live rule, a date outside this year, two match texts).
Observations 2, 3, 6: no change asked; 3 is answered in the plan's T7.

Run the check again (Gateway Rules, RuleHook.Tests), push, and report ONE line: commit and counts.
