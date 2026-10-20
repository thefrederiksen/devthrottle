# Session Rules part 2, phase 2 - answers to the review

Developer seat, 2026-10-19. Answers `reviews/phase-2-review.md` finding by finding. Every fix has a test, and each new
test was watched failing with its fix taken out before it was watched passing.

**The branch was rebased onto `origin/main`** (it was 27 commits behind) before the fixes. Main had added migrations
dated after this branch's, and finding 3 changes the history table anyway, so the old `AddSessionRuleChanges`
migration was dropped and regenerated once on top of main's (`20261020002835` SQLite, `20261020002913` PostgreSQL;
both snapshot diffs are pure additions). The commit ids in `phase-2-proof.md` are from before the rebase.

| # | Finding | Answer |
|---|---|---|
| 1 | The write gate does not tie a rule write to a history row | **Accepted, fixed.** |
| 2 | `PATCH` merges over a snapshot read outside the store lock | **Accepted, fixed.** |
| 3 | History order has an unseen tie | **Accepted, fixed.** |
| 4 | The unnamed-caller wording states something false | **Accepted, fixed.** |
| 5 | Refusals still say "a person" promotes | **Accepted, fixed.** |
| 6 | The proof's "negative control" is not one | **Accepted**; the proof is corrected. A live two-account run is not done - see below. |
| 7 | Small things | Count ceiling and stored escaping **fixed**; promote's 400 for a missing rule **fixed** (now 404). |
| - | Untested constants (`MaxHistoryLimit`, `DefaultHistoryLimit`) | **Accepted, fixed** with tests. |

## 1. Every rule write carries its history row - enforced in the gate

`GatewayDbContext.GuardRuleWrites` now refuses a save in which a rule is Added, Modified or Deleted without an Added
history row in the same save for that rule id and a matching action (create; change or promote; delete). The check runs
after every existing rule check, so the earlier refusals keep their own reasons. So the claim on
`SessionRuleChangeEntity` - a changed rule and a history that does not say so cannot both exist - is now true of every
route to the table, not of the store alone, and is held where the other rule invariants are.

Tests, in `SessionRuleChangeHistoryTests`:
`RuleChangedThroughTheContext_WithNoHistoryRow_IsRefusedByTheWriteGate` (the reviewer's probe, now refused, rule
unchanged), `RuleRemovedThroughTheContext_WithNoHistoryRow_IsRefusedByTheWriteGate` (the rule survives), and
`RuleChangedThroughTheContext_WithARowForADifferentAction_IsRefused` (a delete row does not account for a change). With
the new check disabled, all three failed.

## 2. The merge happens inside the store's lock, over the rule as stored

`SessionRuleStore.Change` now takes a merge, `Func<SessionRule, SessionRuleDraft>`, and calls it under `_gate` with the
rule as it is stored at that moment; the route passes `stored => SessionRuleWire.ReadChange(stored, body)`. Two sessions
changing different fields of one rule both keep their change, and each history row's before and after are that
session's own change. `wasLive` in the answer is now the state read under the same lock (`out stateBefore`), not the
earlier copy. The route's own read remains only to answer 404 early. `SessionRuleDraft` moved to the rules namespace so
the store can take it.

Tests: `Change_AfterAnotherSessionChangedADifferentField_KeepsBothChangesAndRecordsOnlyItsOwn` - the reviewer's
sequence (A reads, B changes the instruction, A sends only `dailyCap`) - now keeps B's instruction and A's cap, and A's
row shows B's instruction on both sides. With the old shape put back in the test (merging over A's earlier copy) it
failed. `Change_ALiveRule_ReportsTheStateItHadWhenTheChangeWasMade` holds `stateBefore`.

## 3. The history is ordered by a sequence number, not a timestamp

Each history row now carries `Sequence`, one more than the account's last, assigned under the store's lock; `History`
orders by it, and the three indexes are on `(tenant, rule | session | -, Sequence)`. Not unique on purpose: two Gateway
processes for a moment during a deploy could in principle number one change alike, and an ordering column that refused
a write would lose a change rather than mis-order it.

Test: `History_WhenTheClockStepsBack_IsStillNewestChangeFirst` stamps the change and the delete five minutes before the
create; ordered by timestamp it reads create first. With the order put back on `OccurredUtc`, it failed - and so did the
existing `Delete_RecordsTheRuleBeforeAndTheHistoryOutlivesTheRule`, so the tie the reviewer described did show on SQLite
too. Not run on PostgreSQL.

## 4. A shared-token caller is named as the shared token

`SessionRuleEndpoints.ActorOf` now records an authenticated caller the pipeline could not name - the self-host shared
machine token - as `machine-token (the Gateway's shared token, which names no one)`, and keeps `unnamed (no credential
was checked on this request)` for a request that carried no checked credential at all. Tests:
`ActorOf_TheSharedMachineToken_IsNamedAsTheMachineTokenNotAsUnchecked`, `ActorOf_ARequestWithNoCheckedCredential_IsUnnamed`.
Promotion by the shared token is still refused (unchanged; that was not this finding).

## 5. The refusals say who may promote

The three refusals in `SessionRuleStore.Promote`, `GuardRuleWrites` and `RulePromotionGrant.FromAuthenticatedRequest`
now say a rule is moved out of dry run by "a person or an agent session" asking through an authenticated request. The
existing test that matches "person" still holds.

## 6. The proof's control, and the second account

Accepted. `phase-2-proof.md` now calls the first run what it is - today's behaviour on main, and evidence the session
held a session key - and names the real negative controls: the failure cases in the second run and the refused shapes
in `SessionKeyGuardRuleRoutesTests`. It also now lists, under "What this does not prove", that no call was made from a
second account. **Not done: a live two-account run.** It needs a hosted-mode Gateway with two enrolled accounts and a
session in each, which the self-host rig cannot give; the store test and the parked `CensusRouteTenancyProbeTests` (which
executes the rule routes against two accounts) are what stand on it. If the Delivery Lead wants that run, it belongs in
the mission's QA report against a hosted test Gateway.

## 7. The small things

- **`history --count`** now refuses anything outside 1 to 500 as a usage error (exit 2) before asking the Gateway, like
  `--count 0` already did. Tests: `test_history_countAboveTheGatewaysCeiling_isAUsageErrorAndAsksNothing`,
  `test_history_countAtTheCeiling_isSent`.
- **Stored snapshots** are written with the relaxed encoder, so an apostrophe or a non-ASCII letter is stored as
  itself. Test: `Snapshot_KeepsAnApostropheAsItself`.
- **Promote of a rule that does not exist** now answers 404 with the id, like GET and PATCH. Test:
  `Promote_ARuleThatDoesNotExist_Is404LikeEveryOtherRuleRoute`.

## The constants

`History_WithNoLimit_AnswersTheDefaultNumberNewestFirst` creates 51 rules and gets 50 back with no limit given;
`History_AboveTheCeiling_Is400AndAtTheCeiling_IsServed` asks for 501 (400) and 500 (200). Each also asserts the
constant's value, so a changed number is a failing test rather than a quiet one.

## Re-run, on the fixed tree (this commit), devlinux, .NET SDK 10.0.301

| Run | Result | Exit |
|---|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"` | 621 passed, 0 failed | 0 |
| Same project: `RulesTypeNothingGuardTests`, `RulesPromotionBoundaryGuardTests`, `RulesBulkSqlGuardTests`, every `SessionKeyGuard` and `Migration` test | 561 passed, 0 failed | 0 |
| `pytest tools/cc-devthrottle/tests/test_rule_ops.py tests/test_help_and_errors_axi.py` | 399 passed | 0 |
| `pytest tools/cc-devthrottle/tests` (the whole suite) | 5,085 passed, 2 skipped, 0 failed | 0 |

The Rules filter count rose from 508 to 621 because the rebase brought in main's tests whose names contain "Rules"
(Mail Desk, factory and team tests among them). The live rig run was not repeated: the routes, the command and the
session-key path it exercised are unchanged, and the changes here are inside the store and the gate, which the tests
above drive directly.
