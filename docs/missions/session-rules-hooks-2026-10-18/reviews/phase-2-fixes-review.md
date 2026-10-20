# Session Rules part 2, phase 2 - review of the fixes

Reviewer seat, 2026-10-19, on devlinux. Reviewed the branch `mission/session-rules-hooks-p2` at `9ad213c8c` (the fixes
commit, "answer the review") against `reviews/phase-2-review.md` and `reviews/phase-2-answers.md`. The reviewer wrote
none of this and fixed nothing. The answers file was treated as testimony: every "fixed" was checked by reading the
code, running its test, taking the fix out and watching the test fail, then putting the fix back. The tree was left
clean after every revert (`git status` empty).

## Verdict

**MERGE.** Every finding of the first review is either fixed with a test that fails without the fix, or answered with a
reason that holds. The rebase onto `origin/main` changed nothing of substance, and the regenerated migrations agree with
the model on both providers. What I found is below: one coverage limit worth recording (finding 2's test does not reach
the route), one overclaim in the answers about which tenancy test covers the new routes, and notes. None blocks.

## Each answer, checked

| # | First review said | Answer said | What I found |
|---|---|---|---|
| 1 | The write gate does not tie a rule write to a history row | Fixed in the gate, three tests | **Fixed.** `GatewayDbContext.GuardRuleWrites` now refuses an Added, Modified or Deleted rule with no Added history row for that rule id and a matching action. With that block removed, exactly its three tests fail (`RuleChangedThroughTheContext_WithNoHistoryRow_IsRefusedByTheWriteGate`, `RuleRemovedThroughTheContext_WithNoHistoryRow_IsRefusedByTheWriteGate`, `RuleChangedThroughTheContext_WithARowForADifferentAction_IsRefused`) and the other 618 pass. The first review's probe (change a rule straight through the context, save) is now refused with "no record of who did it", and the rule is unchanged. |
| 2 | `PATCH` merges over a snapshot read outside the store lock | Fixed: the merge runs inside the lock | **Fixed in the code; the test covers the store, not the route.** `SessionRuleStore.Change` takes a merge function and calls it under `_gate` with the rule as stored; the route passes `stored => SessionRuleWire.ReadChange(stored, changes)`, and `wasLive` is the state read under the same lock. The store test reproduces the first review's sequence and holds. But when I put the route back to the defective shape - `_ => SessionRuleWire.ReadChange(current, changes)`, merging over the route's own earlier read - all 621 rules tests still pass. See finding A. |
| 3 | History order has an unseen tie | Fixed: a per-account sequence number | **Fixed.** Each history row carries `Sequence`, one more than the account's last, computed under the store lock in `ChangeRow`, and `History` orders by it; the three indexes end in `Sequence`. With the order put back on `OccurredUtc`, `History_WhenTheClockStepsBack_IsStillNewestChangeFirst` fails, and so does the existing `Delete_RecordsTheRuleBeforeAndTheHistoryOutlivesTheRule` - so the tie the first review predicted for PostgreSQL showed on SQLite here too. The sequence is deliberately not unique; the reason given (two Gateway processes during a deploy must not lose a change) is sound. |
| 4 | The unnamed-caller wording states something false | Fixed: the shared token is named as the shared token | **Fixed, and the live path agrees.** `ActorOf` falls back to `MachineTokenCaller` when `AuthMiddleware.AuthenticatedCredentialItemKey` is on the request and `CallerOf` named nobody. In the middleware, `Accept` sets that item for the machine token and for a device key, and `AcceptSession` sets it for a session key; the device and the session are named by `CallerOf` first, so the fallback reaches only the machine token. With the fallback reverted, `ActorOf_TheSharedMachineToken_IsNamedAsTheMachineTokenNotAsUnchecked` fails. |
| 5 | Refusals still say "a person" promotes | Fixed: three refusals reworded | **Fixed.** The three refusals in `SessionRuleStore.Promote`, `GuardRuleWrites` and `RulePromotionGrant.FromAuthenticatedRequest` say "a person or an agent session". Some comments still say a person alone - note D. |
| 6 | The proof's "negative control" is not one | Accepted; the proof is corrected; no live two-account run | **Sound, with one overclaim.** The proof now calls the stale-binary run what it is and lists the missing second account under "What this does not prove". A live two-account run is not done, and the reason given (the self-host rig has one account) is true. But the answer and the proof both say the parked `CensusRouteTenancyProbeTests` "executes the rule routes against two accounts": it executes `GET /gateway/rules/{id}`, `DELETE`, `/firings` and the `POST` seed. It does not call `PATCH`, promote or `/history`. For the routes this phase added, the tenant claim rests on the store test and on reading the query filter - note C. |
| 7 | Small things | Count ceiling, snapshot escaping, promote 404 fixed | **All three fixed.** `rules history --count 501` is a usage error (exit 2) before any call; with the ceiling check taken out of `rule_ops.py`, `test_history_countAboveTheGatewaysCeiling_isAUsageErrorAndAsksNothing` fails. Snapshots use the relaxed encoder; with the default encoder put back, `Snapshot_KeepsAnApostropheAsItself` fails. Promote answers 404 for a missing rule, before the grant is read; with that line removed, `Promote_ARuleThatDoesNotExist_Is404LikeEveryOtherRuleRoute` fails with 400. |
| - | Untested constants | Two tests, each asserting the value | **Fixed.** With `DefaultHistoryLimit` changed to 5 and `MaxHistoryLimit` to 100, both tests fail on the behaviour (5 rows back, 500 refused), not only on the asserted number. |

## Findings

### A. Finding 2's test does not reach the route; the route's one line is unguarded (low, record it)

**What I ran.** The route put back to the defective shape - the merge lambda reading the route's own earlier
`current` instead of the `stored` rule the store hands it - and the whole rules filter: 621 passed, 0 failed. The fix
as committed is right by reading; the test `Change_AfterAnotherSessionChangedADifferentField_KeepsBothChangesAndRecordsOnlyItsOwn`
proves the store calls the merge under the lock with the stored rule, and that is the property that matters. The
answers file says the test failed "with the old shape put back in the test": that is a change to the test, not to the
product, and it is a weaker claim than the one the file's opening sentence makes ("each new test was watched failing
with its fix taken out").

**Why there is no better test to ask for.** The route's window is inside one request: `store.Get(id)` for the early 404,
then `store.Change`. Reproducing it through the route deterministically would need a seam that lets a test run a second
change between those two calls, and there is none. A timing test is forbidden by the test standard. So this is a known
limit, not a defect: a future edit that makes the route merge over `current` again would be green. Recording it here is
the mitigation.

### B. The gate proves a record exists and names who; it does not prove the record's before and after (note)

`GuardRuleWrites` requires a row with the right rule id and action, and `SessionRuleRecordRules.CheckChange` requires
`ChangedBy` and the right sides to be non-empty. Neither compares `Before` or `After` with the rule's actual snapshot. A
context built by hand can change a rule and record `{}` on both sides; the history then says who and when, but not
what. The store never does this, no comment claims otherwise, and the first review did not ask for it. If it is ever
wanted, the gate has the original values on the tracked entry and `SessionRuleBody.Snapshot` to compare with.

### C. The answers overstate what the tenancy probe covers (note)

See the table, finding 6. The new routes (`PATCH`, `GET /history`) read and write through a context whose query filter
is the request's tenant - `History` reads `ctx.SessionRuleChanges`, `Change` reads `ctx.SessionRules.FirstOrDefault` -
so the code is right. The sentence about `CensusRouteTenancyProbeTests` in the answers and in the proof should not be
read as a live two-account proof of these two routes.

### D. Comments that still say a person alone promotes (note)

`SessionRuleStore` lines 27 ("A person promotes it"), 262 ("Only a person does this"), 266 and 305, and
`GatewayDbContext` lines 56 and 105 ("a promotion a person asked for"). Comments, not refusals, so outside finding 5 as
written. An agent reading the store meets them before it meets the refusal.

### E. Not verified: a named-nobody hosted principal (note)

`ActorOf` records "no credential was checked on this request" whenever `CallerOf` names nobody and the credential item
is absent. A hosted principal that is authenticated but carries no name would land there too, and the sentence would be
false for it. I did not establish whether such a principal can occur; the hosted sign-in is not in this diff.

### F. Small things, no action required

- `docs/cli-reference.md` shows `history [--count N]` without the 1 to 500 range the `--help` now states.
- The `Sequence` column is numbered per account under one store's lock. Two `SessionRuleStore` instances over one
  database in one process could number alike; the Gateway builds one, and the tests that build several read nothing
  that depends on it.

## The rebase and the migrations

**The rebase changed nothing of substance.** The pre-rebase commits (`60c5464f8`, `739507278`) are still in the object
store. The diff of the code commit before the rebase against the diff after it, migrations aside, differs only in hunk
line numbers and context lines: the rules command group in `cli.py` now sits after the Mail Desk group that main added,
and `SHORT_HELP_LATER` gained `"maildesk"` from main beside this branch's `"rules"`. The proof commit is byte-identical.

**The migrations.** One `AddSessionRuleChanges` per provider (`20261020002835` SQLite, `20261020002913` PostgreSQL), both
later than main's newest (`20261019020017` and `20261019020053`). Both create `session_rule_changes` with `Sequence` and
the three indexes on it, plus the tenant index. Both snapshot diffs against main are pure additions (58 lines each, 0
removed). `FleetOutcomeStopIdentityMigrationTests` asserts `HasPendingModelChanges()` is false for each provider's
snapshot, and `GatewayHostBootSmokeTests` asserts it again after booting; both passed here. PostgreSQL was not
applied to a PostgreSQL database.

## What holds, checked

- Every other writer of `session_rules` was looked for: outside the store and the context, nothing in the Gateway
  touches `SessionRules` or `SessionRuleEntity` except `SessionRuleRecordRules.CheckRule`. `RecordFiring` reads the rule's
  state without tracking and writes only a firing. So the new gate check refuses nothing the product does today.
- The gate's new check runs after `CheckChange`, so a hand-written row with no `ChangedBy` is refused for that reason
  first; a record cannot satisfy the gate without naming who.
- `ChangeRow` is called inside `lock (_gate)` at all four sites (create, change, promote, delete), so the sequence is
  numbered under the lock as the answer says.
- A promote of an already-live rule saves nothing and so meets no gate check.
- `SessionRuleDraft` moved from the wire file to the rules namespace; nothing else referenced the old one.

## Scope: what I read, what I ran, what I could not reach

**Read.** The whole fixes commit except the two generated designer files, of which I read the migration attribute and
confirmed the `Sequence` column and the three indexes. On the branch: `SessionRuleStore` in full, `GuardRuleWrites` in full,
`SessionRuleEndpoints` in full, `SessionRuleWire.ReadChange`, `SessionRuleRecordRules.CheckChange`, `RulePromotionGrant.CallerOf`,
`AuthMiddleware.Accept` and `AcceptSession`, the tenant query filter, `CensusRouteTenancyProbeTests` (the rules section),
both hand-written migrations and both snapshot diffs, the proof diff and the answers.

**Ran, on `9ad213c8c`, devlinux, .NET SDK 10.0.301, Python 3.12.3 in a scratch environment (`uv`, with `cc_shared` and
`cc_storage` from the tree):**

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules\|FullyQualifiedName~SessionKeyGuard\|FullyQualifiedName~Migration"` | 1,146 passed, 0 failed |
| Same, `GatewayHostBootSmokeTests` and `FleetOutcomeStopIdentityMigrationTests` | 14 passed, 0 failed |
| `pytest tests/test_rule_ops.py` | 31 passed |
| `pytest tests` (the whole command-line suite) | 5,085 passed, 2 skipped, 0 failed (11 minutes 39 seconds) - the count the answers give |
| Revert: findings 3, 4, 7 (snapshot, promote 404) and both constants taken out together, rules filter | exactly the 7 expected tests failed, 614 passed; each failure message named its own mechanism |
| Revert: the gate's history-row check removed, rules filter | exactly the 3 expected tests failed, 618 passed |
| Revert: the route merging over its earlier read, rules filter | 0 failed, 621 passed - finding A |
| Revert: the count ceiling removed from `rule_ops.py`, `test_rule_ops.py` | 1 failed (the ceiling test), 30 passed |

Every revert was undone with `git checkout` and the tree checked clean before the next.

**Could not reach.** PostgreSQL - the migration was read and the snapshot held to the model, not applied. A hosted,
two-account live run of `PATCH` and `/history`. Windows and macOS. The parked `Gateway.Tests` suite, including
`CensusRouteTenancyProbeTests` and `ContextLessRouteCensusTests`. The live Director rig: the answers say it was not
re-run, and the routes a session key reaches are unchanged by the fixes, which I agree with from the diff.
