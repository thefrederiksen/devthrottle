Read `brief-template.md` beside this file and follow it, with this difference in the work under review:

- **PIECE:** phase 1 meeting phase 2 (#4267) on main. Three commits in the checkout you were opened in:
  `d90c269da` (the merge of main, conflicts resolved in `SessionRuleEndpoints.cs`, `SessionRuleWire.cs`
  and `SessionRuleStore.cs`), `0b6caa724` (ruling P18 built - read P18 at the end of `phase-1-plan.md`),
  and `b39fb4203` (a merge of two unrelated main commits). Review the resolution against both parents
  (`git merge-tree --write-tree` against each is a good tool) and `git show 0b6caa724` in full. The
  regenerated migration `AddSessionRuleKindsAndHookFirings` now follows phase 2's `AddSessionRuleChanges`.
- **Questions for this piece:** did the resolution keep both phases' behaviour - can phase 2's change
  history be bypassed by any phase 1 write path (the hook firings route, the hook rule create), and does
  every phase 1 field appear in a change's before and after? Does a change meet every refusal a create
  meets (P4, P7)? Is `kind` really immutable through every route? Can a SESSION key now promote a hook
  rule, read or write W1 or W5, or record a firing? Does `cc-devthrottle rules` create, show and change
  a hook rule correctly, and does its `--json` keep its shape (the AXI rules in `docs/axi-standard.md`)?
  Is the migration the same schema change, after the right predecessor, on both providers?
- Run the tool's tests too (`tools/cc-devthrottle`; the CI job "Tool contracts (Python)" in
  `.github/workflows/ci.yml` shows how they are run).
- **Write the review to:**
  `/home/soren/ReposFred/devthrottle-session-rules-p1/docs/missions/session-rules-hooks-2026-10-18/reviews/phase-1-phase-2-integration-review.md`
