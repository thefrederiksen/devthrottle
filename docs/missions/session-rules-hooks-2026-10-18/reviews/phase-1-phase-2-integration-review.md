# Review - phase 1 meets phase 2 (#4267) on main

Reviewer seat "Session Rules - Reviewer - phase 1 meets phase 2", 2026-10-19. Reviewed in the worktree
`devthrottle-session-rules-p1-review-p2` at `b39fb4203`, which was 62 commits ahead of `origin/main` and 0
behind at the time of reading. The brief is `brief-phase-2-integration.md` beside this file.

## Verdict

**No defect found that requires a change.** The merge resolution keeps both phases' behaviour, ruling P18 is
built as written, and every test I could run is green. Five observations are listed at the end; none proves
harm, and none blocks the merge.

## What was reviewed

Three commits, each against both of its parents:

- `d90c269da`, the merge of main (phase 2) into phase 1. I ran `git merge-tree --write-tree` on the two parents
  (`7adcd7ad8` phase 1, `46ab20e2e` main) and diffed the automatic result against the committed merge. The
  committed merge differs from the automatic one only in the three conflicted files, the four phase 1 test
  files that had to pass an actor into the store, the two migration tests that name the predecessor, and the
  regenerated migration files. Nothing else was touched by hand.
- `0b6caa724`, ruling P18, read in full (13 files, 1,264 insertions).
- `b39fb4203`, the second merge of main. Its diff against its first parent is exactly main's own diff between
  `46ab20e2e` and `207133ed5` (six files, same totals), so it is a clean merge with no resolution.

The contract read against: `phase-1-plan.md` (P1-P18, W1-W5), `mission.md`, the repository rules file at its root, and
`docs/axi-standard.md` for the command line tool.

## The merge resolution, file by file

**`SessionRuleEndpoints.cs`.** Phase 1's create route called `CreateFromBody`; phase 2's read a draft and called
`store.Create` with an actor. The resolution keeps phase 1's kind-aware entry point and passes phase 2's actor
into it. The log line carries both the kind and who wrote it. The change, promote, delete and history routes
are phase 2's; the effective rules route and the hook firings route are phase 1's. Nothing from either side was
dropped.

**`SessionRuleWire.cs`.** Both `using` lines kept; `CreateFromBody` gained the actor and passes it to both store
creates.

**`SessionRuleStore.cs`.** `Create` takes the actor before the optional `appliesTo`, so phase 1's "absent means
everywhere" default survives. `CreateHook` takes the actor and now writes a `Create` history row in the same
save, which it did not on the phase 1 branch - without this the write gate's "every rule write carries its
history row" rule would have refused every hook create. In `Promote`, phase 1's "live hook rules arrive in
phase 4" refusal sits before phase 2's `before` snapshot and history row, so a refused promotion records
nothing.

**The migrations.** The phase 1 migration was regenerated as `20261020014625` (SQLite) and `20261020014715`
(PostgreSQL), both after phase 2's `AddSessionRuleChanges` (`20261020002835` and `20261020002913`). Both `Up`
methods add the same ten columns to `session_rules`, the same thirteen columns to `session_rule_firings`, and
the same unique index on tenant and decision id; the only differences are each provider's column types (text
and `"[]"` defaults on SQLite, `text[]`, `uuid[]` and `'{}'` defaults on PostgreSQL). Both Designer files and
both model snapshots carry `session_rule_changes`. Entity Framework's own check reports no pending model
changes for either provider (see "What was run").

## The brief's questions

**Can phase 2's change history be bypassed by any phase 1 write path?** No. The hook rule create now writes its
history row in the same save (`SessionRuleStore.cs:343`). The hook firings route writes firings, not rules,
and the firings table is not part of the change history by design. Behind every route, the write gate in
`GatewayDbContext.GuardRuleWrites` refuses any added, modified or deleted rule row whose save does not also add
a history row of the matching action (`GatewayDbContext.cs:201-220`). I found no write to `session_rules` that
goes round `SaveChanges`: the only direct set operation in the Gateway is the store's own `Remove`.

**Does every phase 1 field appear in a change's before and after?** Yes. `SessionRuleBody.Of` writes `kind`
and `appliesTo` (all four parts) for both kinds, and a hook rule's `event`, `tool`, `handler`, `match` and
`result`; `Snapshot` adds state and who promoted. The create, change, promote and delete history rows are all
built from `Snapshot`. Tests prove it for a hook create's after, a hook change's before and after (match,
applies-to, result, instruction), and a screen create's kind and applies-to.

**Does a change meet every refusal a create meets (P4, P7)?** Yes, by construction and by test. A change is the
rule's current body with the caller's fields laid over it, read back through `ReadDraft`, the same reader the
create uses, so the body-level refusals (a hook rule gaining `scope`, screen fields, checks or a ceiling; a
screen rule gaining a hook field) fire with the create's own reason. A field of the other kind is deliberately
let through the "is this a field a change can carry" check so that the create's reason is the one the caller
reads. Then the store writes the entity and runs `SessionRuleRecordRules.CheckRule`, the same one place the
create and the write gate run, which holds the applies-to shapes, a screen rule applying only everywhere, the
phase 1 event, tool, handler and result values, non-empty and non-blank match text, and now the ceilings on a
hook rule. The test file covers each of these refusals and asserts that nothing is recorded and the rule is
unchanged.

**Is `kind` really immutable through every route?** Through every route that exists, yes, at three layers: the
change reader refuses a body naming another kind (`RefuseAChangeOfKind`), the store refuses a merged draft of
another kind, and the write gate refuses a modified row whose `Kind` differs from its original value. Tests
cover all three, including a hook row rewritten as a well-formed screen rule straight through the context with
its history row. See observation 1 for the one shape the gate does not see.

**Can a session key promote a hook rule, read or write W1 or W5, or record a firing?** No. Promote: the route is
open to a session key (phase 2) and the store refuses a hook rule for anyone with "phase 4"; the test uses a
session actor and a session-minted grant and asserts no history row. W1 and W5: `SessionKeyGuard.IsRuleRoute`
now requires the third path segment to parse as a rule id before any `/gateway/rules/{id}...` shape is
allowed, so `effective` and `hook-firings` fall out of the rule surface on every verb; the rest of `IsAllowed`
has no entry for them. Nine verb and path combinations, including a trailing slash and mixed case, are
asserted refused through the public `Check`. A firing is only ever written by the hook firings route, which is
refused to a session key, and by the screen evaluator, which has no inbound request.

**Does `cc-devthrottle rules` create, show and change a hook rule correctly, and does `--json` keep its shape?**
Yes. `create` sends exactly the hook body (kind, instruction, appliesTo, event, tool, handler, match, result)
and does not add empty `checks` to a hook rule; `--applies-to` parses `everywhere` and key=value pairs with
`machines` and `exceptMachines` as lists, and a value that is not pairs is a usage error (exit 2) that posts
nothing. `change` sends only the fields given and has no `--kind`; a kind sent through `--body` is refused by
the Gateway and the tool exits 1 with the Gateway's reason. `list` gains `kind` in its default columns and shows
`-` as a hook rule's scope rather than claiming "all-sessions". `show` prints each kind's own fields. The
machine-readable output is unchanged: `list --json` still prints the Gateway's rules as a bare array, filtered
by `--state` and nothing else; `show`, `create` and `change` print the Gateway's answer. `--fields` with
`--json` stays a usage error. `docs/cli-reference.md` describes the new flags and says a kind cannot be
changed.

**Is the migration the same schema change, after the right predecessor, on both providers?** Yes, as described
above.

## What was run

All from the review worktree at `b39fb4203`, .NET 10 at `~/.dotnet`, `DOTNET_USE_POLLING_FILE_WATCHER=1`.

| What | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules\|FullyQualifiedName~SessionRuleKindsMigrationTests\|FullyQualifiedName~SessionKeyGuard"` | 1,269 passed, 0 failed, 1 minute 35 seconds |
| `dotnet-ef migrations has-pending-model-changes --context GatewayDbContext` for SQLite (`src/CcDirector.Gateway`) | no changes since the last migration |
| The same for PostgreSQL (`src/CcDirector.Gateway.Migrations.Postgres`, `CC_GATEWAY_EF_PROVIDER=postgres`) | no changes since the last migration |
| `dotnet-ef migrations list` for PostgreSQL | last two: `AddSessionRuleChanges`, then `AddSessionRuleKindsAndHookFirings` |
| `pytest tests/test_rule_ops.py tests/test_help_and_errors_axi.py` from `tools/cc-devthrottle`, `FORCE_COLOR=1` | 410 passed |
| `pytest tests/ -q` from `tools/cc-devthrottle`, `FORCE_COLOR=1`, as the continuous integration job "Tool contracts (Python)" runs it | 5,109 passed, 2 skipped, 12 minutes 34 seconds |

The Python tests ran in a throwaway environment built with `uv` (pytest 9.1.1, typer 0.27.3, click 8.5.0),
which is the job's "latest" leg, not its "floor" leg. The Gateway build needed node on the path for its
`npm ci` step; that is an environment matter here, not a change in the work.

The working tree was clean before and after; nothing was edited.

## Scope - what this review does not cover

- **PostgreSQL migration applied to a real database.** `SessionRuleKindsPostgresTests` needs Docker and
  `scripts/test-database.ps1`; not run here. The Entity Framework model check above is the nearest proof.
- **The live request pipeline.** The session key refusals are proven through `SessionKeyGuard.Check`, which the
  authentication middleware consults; I did not send requests to a running Gateway.
- **Windows.** Nothing in these three commits is platform-specific, but nothing was run on Windows.
- **The Core and Director suites.** The three commits do not touch Core or the Director; the merge brought
  phase 1's Core and Director code across unchanged from the phase 1 branch, which had its own reviews.
- **The Developer's "seen failing" claim.** The commit says 38 of 44 Gateway tests and 11 command line tests
  failed before the change. The new test file does hold 44 cases. I did not revert the change to watch them
  fail; that claim stands as the Developer's own report.
- **The full `test-local.ps1` gate.** Not run here; it is the Tech Lead's step before the pull request.

## Observations - no change required

1. **The write gate's kind check sees the row's original value, which an attached detached row does not have**
   (`GatewayDbContext.cs:135`). Entity Framework sets a row's original values equal to its current values when
   a detached object is attached with `Update`, so a hand-built context that loaded nothing and attached a
   rewritten row would pass this check. No such route exists: the store loads the row it changes, and there is
   no `Update`, `Attach` or raw SQL against `session_rules` in the Gateway. The comment "no route, the store's
   change included, can do it" is true of every route that exists. Worth knowing if one is ever added.
2. **A refused hook promotion still spends the grant** (`SessionRuleStore.cs:524` runs before the kind check at
   line 539). The grant is minted per request and dies with it, so nothing can be replayed; it only means the
   "phase 4" refusal is reached by a grant that has already been consumed. No effect on any caller.
3. **A `match` sent as one string rather than a list is read as no match text.** `SessionRuleWire.Strings`
   answers an empty list for a non-array, so a change `{"match": "git push"}` is refused with "a match rule
   needs at least one piece of text" rather than "match is a list". It is refused and nothing is stored, which
   is the safe side; the reason could name the shape. This reader is phase 1's, not this piece's.
4. **The help and reference examples write `machines=devlinux;laptop` unquoted.** In a shell the `;` ends the
   command, so a reader who copies the example gets a usage error. Quoting the value in the example would save
   that. Cosmetic.
5. **The test for a hook row written round the store** (`HookRuleStoreTests`, merge change) now adds a `Create`
   history row with `After = "{}"` so the write gate lets the row through. That is the right way to test "the
   store reads a hook row", and it is also a reminder that the gate checks a history row exists, not that its
   `After` is the rule's real body. The store always writes the real body; only a hand-built context could do
   otherwise.
