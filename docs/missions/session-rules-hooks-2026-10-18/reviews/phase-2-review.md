# Session Rules part 2, phase 2 - review: agents manage rules

Reviewer seat, 2026-10-18, on devlinux. Reviewed `git diff origin/mission/session-rules-hooks...origin/mission/session-rules-hooks-p2`
(commits `60c5464f8` and `739507278`, 36 files). The reviewer wrote none of this and fixed nothing. The phase 2 proof was
treated as testimony, not evidence: every claim below was checked against the code or by running it.

## Verdict

Two defects, both small to fix, neither visible from the proof. The first should be fixed, or explicitly accepted by
the Architect, before this phase merges, because it is the exact gap the owner's decision D7 rests on. The second
should be fixed before several agents manage rules at once, which is what D7 is for. The rest are notes.

The sharp questions, answered first:

| Question | Answer |
|---|---|
| Can a session key reach anything beyond rules? | No. `SessionKeyGuard.IsRuleRoute` is an allow list of eight exact shapes, matched on lower-cased segments and verb together. The nine neighbouring shapes in the tests are refused, and so were `PUT`, `POST /gateway/rules/history` and `/gateway/rulesets` when I ran the guard. There is no other `/gateway/rules` route in the Gateway. |
| Can it reach another tenant's rules? | Not that I can find. The session's tenant comes from its registry row (`HostedTenantBoundary.ResolveRequestTenant`), the host enters that ambient scope, `GatewayDatabase.CreateContext()` throws on an invalid ambient tenant, and every read and write in the store goes through a context whose query filter is that tenant. A foreign rule id answers 404 on PATCH and `deleted: false` on DELETE. The store-level test with `tenant-b` holds. **Not proven live**: the live run was self-host, one tenant. |
| Is every change recorded with session, agent, time, before and after, including delete and promote? | Through the store, yes, and the live run shows it. Through anything else, no - see finding 1. A promote of a rule already live records nothing, which is right: nothing changed. |
| Can `change` promote a rule or bypass dry run? | No, at three layers: `ReadChange` refuses `state` and `promotedBy`; `SessionRuleStore.Change` always writes dry run and clears `PromotedBy`; the write gate refuses a modified live rule without a promotion grant. Each layer has a test. |
| Does every `cc-devthrottle rules` filter apply to `--json`, and does an unknown flag fail? | Yes. `list --state` is applied before the JSON is printed; every `history` filter is sent to the Gateway and the JSON is the Gateway's filtered answer. An unknown flag exits 2 on all seven commands (run here). |
| Where could a constant be substituted and the tests stay green? | `SessionRuleEndpoints.MaxHistoryLimit` (500) and `DefaultHistoryLimit` (50): no test holds either; 5 and 1 would pass. `History` ordering has a tie the tests do not see - finding 3. |

## Findings

### 1. The write gate does not tie a rule write to a history row - the record is store-only (medium)

**Claim in the code.** `SessionRuleChangeEntity`: "A row is added in the SAME save as the change it describes, so a rule
that was changed and a history that does not say so cannot both exist." `SessionKeyGuard.IsRuleRoute`: "every write is
recorded in `session_rule_changes` ... in the same save."

**What the code does.** `GatewayDbContext.GuardRuleWrites` checks a rule's tenant, its validity, that a new rule is
in dry run, and that a live rule carries a grant. It never asks whether a `SessionRuleChangeEntity` is being added
alongside. A deleted rule is not inspected by the gate at all.

**Proof.** A throwaway test (written, run, deleted; not committed): create a rule through the store, then open a
context, set `DailyCap = 2` and a new `Instruction` on the row, `SaveChanges()`. It saved. `store.History(ruleId)`
returned one row, the create. The same for `ctx.SessionRules.Remove(row); SaveChanges()`: the rule is gone, the
history has only the create. Both passed on the first run, on `739507278`.

**The harm.** The owner allowed agents to change rules on the ground that every change is recorded. That ground is
exactly as wide as `SessionRuleStore`. The September mission's own comment at the top of the store says why this
matters: "THIS IS THE FRONT DOOR. It is NOT the only door ... the claim was made TRUE rather than deleted" by moving
the check into `SaveChanges`, "which every route to the table ends at". This change puts a new claim in the same
words and does not put it in the same place. The next writer to the table - a repair script, a migration, a future
route - changes a rule with no record, and the comment on the entity tells the reader that cannot happen.

**Shape of a fix** (for the Delivery Lead, not the reviewer): in `GuardRuleWrites`, for every `SessionRuleEntity`
entry that is Added, Modified or Deleted, require an Added `SessionRuleChangeEntity` in the same tracker with that
`RuleId` and the matching action, and refuse the save otherwise; plus a test that writes a rule straight through the
context without a row and watches it refused. Then the comment is true.

### 2. `PATCH` merges over a snapshot read outside the store lock: the second of two overlapping changes reverts the first, and the record blames the wrong session (low to medium)

**What the code does.** `SessionRuleEndpoints` reads `current = store.Get(id)` outside the store's lock, calls
`SessionRuleWire.ReadChange(current, body)` to lay the caller's fields over that snapshot, and then
`store.Change(...)` writes **every** field from the merged draft. The store's "before" is read correctly inside the
lock, but the values written come from the stale snapshot.

**Proof.** A throwaway test, deterministic, no race needed (written, run, deleted): session A reads the rule;
session B changes the instruction through the store and is recorded; session A then sends `{"dailyCap": 2}` merged
over its snapshot, exactly as the route does. Result: `dailyCap` is 2 **and the instruction is back to the
original** - B's change is gone - and A's history row shows `before` with B's instruction and `after` with the
original, so the record says A reverted the instruction. A never named it. Passed on `739507278`.

**The harm.** D7 is several agents managing rules at once. Two sessions editing different fields of one rule within
the same window lose one edit silently, and the history - the thing that is supposed to settle "who changed this" -
names the wrong session for the reversion. The `wasLive` flag in the answer is read from the same stale snapshot.

**Shape of a fix:** do the merge inside `SessionRuleStore` under `_gate` (hand the store the fields to change, not a
fully merged draft), or carry the snapshot's `UpdatedUtc` into `Change` and refuse with "the rule changed since you
read it" when it differs.

### 3. History order has an unseen tie (low)

`History` orders by `OccurredUtc` only. `SessionRuleChangeHistoryTests.Delete_RecordsTheRuleBeforeAndTheHistoryOutlivesTheRule`
creates and deletes at the same fixed `Now` and asserts `[delete, create]`. That passes on SQLite because rows with
equal keys come back in insertion order, which SQLite does not promise and PostgreSQL does not do. Two changes to one
rule in the same timestamp can read back in the wrong order on the hosted Gateway, and the test would not know.
Not run on PostgreSQL here. A tie-break on a monotonic column would close it.

### 4. The unnamed-caller wording states something false (low)

`SessionRuleEndpoints.UnnamedCaller` is `"unnamed (this Gateway checks no sign-in)"`. On self-host the shared machine
token is accepted as a Bearer or as the raw-token cookie (`AuthMiddleware.AuthenticateRequest`, the MH-2 comments),
and such a caller is authenticated by that token and still named nobody. The history row then says the Gateway
checks no sign-in, which it did. The same caller is refused on promote with "no caller the Gateway could name"
(pre-existing). Say what is true: the shared machine token names no one. Not run live.

### 5. Comments and refusals still say "a person" promotes (note)

`SessionRuleStore.Promote` (null grant), `GuardRuleWrites` (modified live rule) and `RulePromotionGrant.FromAuthenticatedRequest`
still say "a rule is moved out of dry run by a person". Since D7 a session does it too, and the grant's own header
says so. An agent that meets one of these refusals is told something the product no longer holds.

### 6. The proof's "negative control" is not one (note)

The first run met a stale binary with the allow-list line disabled and every call was refused. That shows what
main does today, which the mission already states; it is not a control for the new routes. The live run was on a
self-host Gateway with one tenant, so the tenant question above rests on reading the code and on the store test,
not on a live call from a second account.

### 7. Small things, no action required

- `history --count 501` is refused by the Gateway and exits 1, while `--count 0` is a usage error and exits 2. The
  command does not know the ceiling. Harmless; noted for consistency.
- The stored snapshots escape apostrophes and non-ASCII as `\u` sequences (default `System.Text.Json` encoder). The
  wire re-parses them, so a reader never sees it; a direct reader of the table does.
- Promote and the shared `Refused` helper answer 400 for "no such rule" on promote but 404 on PATCH and GET. Pre-existing
  shape for promote.

## What holds, checked

- The eight allowed shapes and the nine refused ones in `SessionKeyGuardRuleRoutesTests`, plus my own extra shapes.
- A change that changes nothing is refused and records nothing; a refused create, change or delete records nothing.
- The history is append only through the context (edit and remove refused) and through bulk SQL (interceptor covers
  the new table; `RulesBulkSqlGuardTests` names the new entity).
- `SessionRuleBody.Of` round-trips through the create reader, including an input argument and a literal that looks
  like one, and `Of_EveryFieldTheCreateReaderReads_IsInTheBody` reads the draft's own properties, so phase 1's new
  fields cannot be silently dropped by a change.
- The promote caller is now read from the item the middleware actually writes; the old `Items["DeviceKeyId"]` was
  set by nothing in production. The proof's decision 3 is correct and the fix is right.
- Both migrations create the same table; `Before` and `After` are unbounded text on both providers.
- `rules history --session me` uses `CC_SESSION_ID`, which the proof shows matches the stored session id form.

## Scope: what I read, what I ran, what I could not reach

**Read.** The whole diff except the two generated `*.Designer.cs` files (6,000 lines each), of which I read the
hand-written migrations and the snapshot diff stats. Also, on the branch: `SessionRuleStore` in full, `GuardRuleWrites`,
`RulePromotionGrant`, `SessionRuleRecordRules.CheckRule`, `AuthMiddleware` (the accept paths and the session-key branch),
`HostedTenantBoundary.ResolveRequestTenant`, the host's tenant-scope middleware and `GatewayDatabase.CreateContext`,
`SessionKeyGuard.Check` (segment lower-casing), and mission sections 4 to 8.

**Ran, on `739507278`, devlinux, .NET SDK 10.0.301, Python 3.12.3:**

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules\|FullyQualifiedName~SessionKeyGuard\|FullyQualifiedName~ContextLessRouteCensus"` | 990 passed, 0 failed |
| `pytest tools/cc-devthrottle/tests/test_rule_ops.py` | 29 passed |
| `pytest tools/cc-devthrottle/tests/test_help_and_errors_axi.py` | 368 passed |
| `cc-devthrottle rules <each of the seven commands> --bogus`, `history --count 0`, `history --action rename` | exit 2 each |
| Two throwaway probe tests for findings 1 and 2 | both confirmed the defect; files deleted, tree clean |

**Could not reach.** PostgreSQL (the hosted store) - the migration was read, not applied. A hosted, two-tenant live run
of the new routes. Windows and macOS. The parked `Gateway.Tests` suite, including `CensusRouteTenancyProbeTests`, which
this machine's file-watch limit makes unreliable. The live Director rig from the proof - I did not rebuild it; the
hosted Gateway runs main, so a session-key call there says nothing about this diff.
