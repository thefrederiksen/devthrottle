# Review - Website Business Factory - the activity record (devthrottle pull request 3272)

Reviewer: the Reviewer seat on the mission "Website Business Factory", product track. Pull request 3272,
"the append-only factory activity record, behind a switch that is off by default", head read as the ref
`review-3272` (fetched from `pull/3272/head`; nothing was checked out into the shared working tree).

Reviewed against the Developer mandate
(`devthrottle_internal`, `origin/main:docs/missions/website-factory-agents-2026-09-21/MANDATE-developer-record.md`)
plus the one later change stated in my own mandate: **the outcome list gained `skipped`**.

## Verdict

One finding. Everything else the review was told to look hardest at holds.

## Scope - what I read

The whole diff (33 files) plus the surrounding code it plugs into, all at the pull request head:

- The switch: `src/CcDirector.Core/Configuration/FactoryAgentsConfig.cs`, its wiring in
  `GatewayHost.cs` (constructor override, `FactoryAgentsEnabled`, the `if (FactoryAgentsEnabled)` map).
- The table: `FactoryActivityEntity.cs`, `GatewayDbContext.cs` (mapping, tenant scope, indexes),
  the SQLite migration `20260921081600_AddFactoryActivity` and the PostgreSQL twin
  `20260921084515_AddFactoryActivity`, both `.cs` and `.Designer.cs`, and both model snapshots.
- The store: `src/CcDirector.Gateway/Factory/FactoryActivityRecord.cs`.
- The routes: `Api/FactoryActivityEndpoints.cs`, and the two `SessionKeyGuard.cs` additions
  (read allow-list entry and POST entry).
- The contracts: `src/CcDirector.Gateway.Contracts/FactoryActivityDtos.cs` (the outcome list lives here).
- The commands: `tools/cc-devthrottle/src/factory_ops.py`, the `cli.py` additions (the two verbs, the
  action catalogue entries), `docs/cli-reference.md`.
- The tests: `FactoryActivityRecordTests.cs` (22), `FactoryActivityRouteTests.cs` (3),
  `test_factory_ops.py` (12), and every existing test the diff had to move (the migration-order pins in
  `Gateway.Tests` and `Gateway.UnitTests`, the help census tests).
- The proof: `docs/missions/website-factory-agents-2026-09-21/proof/record/` - README, all three gate
  summaries.
- Surrounding code read for context at the same ref: `AuthMiddleware.CallingSession` /
  `RegisteringCredential`, `GatewayMintedKeyEntity`, `ActivityRetentionSweep` / `ActivityEventStore` and
  every `TenantScopedSweep` in the Gateway (the sweep inventory), `cc_shared/gateway.py` transport and
  `axi_cli.fail`, the governance-audit migration (the precedent for an append-only table's `Down`).
- The mandate's own documents in `devthrottle_internal` at `origin/main`: the Developer mandate,
  `PRODUCT-READ.md`, `GAPS-product.md`, and the trigger Developer's mandate (the consumer of this list).

## Scope - what I ran and what I could not reach

- I ran nothing. This seat reads; the gate is the Tech Lead's to run. No file was built, edited,
  committed, pushed or merged; no process was touched.
- I could not reach: a live Gateway (the switch's 404 behaviour is read in the code and the route test,
  not exercised by me), the hosted container's config path (the pull request itself declares it out of
  scope), and the two PostgreSQL migration-order tests in `Gateway.Tests` - the proof honestly records
  they were never run, and I had no run of my own either.

## The finding

### F1 - the outcome list is missing `skipped`, which the mandate has since added

Where: `src/CcDirector.Gateway.Contracts/FactoryActivityDtos.cs` - `FactoryActivityOutcome.All` lists ten
words (`started, allowed, asked, blocked, escalated, done, sent-back, nothing-to-do, paused, failed`) and
no `skipped`. The gap is pinned in four more places, so adding the word later means touching all of them:
`FactoryActivityRecordTests.Every_listed_outcome_is_accepted` asserts `Equal(10, All.Length)`;
`tools/cc-devthrottle/src/factory_ops.py` `OUTCOMES`; `docs/cli-reference.md`; and the proof README's
"all ten words".

The harm, and why it must change now: my mandate states the outcome list gained `skipped` after the
Developer mandate was written - so the list this pull request ships is one word behind what the mission
now requires. And the record's own design makes the gap bite on the mission's very next pull request:
the outcome is a closed list enforced at the FIRST write, the trigger mandate (item 8) says the trigger
writes its checks into this table, and a trigger must record a check that was skipped because the session
it started is still alive. When it tries, the Gateway answers 400 listing ten words without the one it
needs, `cc-devthrottle factory record` exits non-zero, and - by the rule this very pull request builds,
"write first, then act" - the business tool treats an expected, healthy condition as a failure. The
closed list exists to surface a wrong word at first write; here it is guaranteed to fire on the next
consumer. The word must land in this pull request, because the list is the contract the trigger
Developer builds against, and the count-pinning test will otherwise refuse it later as a surprise.

The fix is the Developer's to make, not mine: add `skipped` to `FactoryActivityOutcome.All` (eleven
words), move the count pin to 11, and carry the word into `factory_ops.OUTCOMES`,
`docs/cli-reference.md`, and the proof README.

## What I looked hardest at and found holds

- **Append-only, everywhere in the path.** The store's public surface is `Append` and `Query` and
  nothing else, and the reflection test fails if that ever grows. There is no `Update`, no `Remove`, no
  `ExecuteDelete` and no `SaveChanges` on a tracked row anywhere in the store; a correction is a new
  row and the test serializes the corrected row before and after and proves it identical. No retention
  sweep touches the table: I walked every `TenantScopedSweep` in the Gateway (activity, session history,
  fleet messages, dev reports, push, cron) and each names its own store; the only generic iterations over
  the model (`ApplyCommonSubsetConventions`, the stats database's own migration) are model-building or a
  different database. `CorrectsId` and `SessionId` are value columns, not foreign keys, so no cascade
  path exists. The migrations' `Down` drops the table - I checked the precedent: the governance audit
  migration, the append-only table this one copies its discipline from, has the identical `Down`, and a
  deliberate migration rollback is an operator act, not a sweep; EF also requires `Down` to compose. Not
  a finding.
- **Exit code on every not-written path.** `factory record` exits 1 on a 404 (with a sentence naming the
  switch and the config key), on a refused row, on a 5xx, on an unreachable Gateway, on a missing
  session key (no request sent), and - the case most implementations miss - on a 2xx that carries no
  usable row id, which is treated as not recorded rather than success. `axi_cli.fail` raises
  `typer.Exit(1)`; there is no path through `factory_ops.record` that prints success without the
  Gateway's id. The Python tests drive each path against a real local HTTP server, not a mock of the
  transport.
- **The switch really unmaps the endpoints.** `GatewayHost` maps `FactoryActivityEndpoints` only inside
  `if (FactoryAgentsEnabled)`; off means the routes do not exist. Only a JSON boolean `true` turns it on
  (the string `"true"`, a missing block, `false` all leave it off - proven in six cases). The route test
  proves both verbs answer 404 off and 201/200 on, on the same paths, so the 404 is the switch and not a
  wrong path. The guard entries are inert while the route is unmapped.
- **Tenant isolation.** The entity is in `ApplyTenantScope`, the key is Gateway-minted
  (`GatewayMintedKeyEntity`, private setter, so no caller can present one), and the test proves account
  beta sees none of account alpha's rows and cannot correct one by its id - a cross-tenant id reads as
  "no such row", never an existence oracle.
- **Secrets and email text.** The row carries a capped sentence and a link by design; the 500-character
  cap is the enforcement the mandate asked for, the `What` text never enters the log (the append log
  line carries factory, agent, outcome and ids only), and no endpoint or command copies message bodies
  into a row. Nothing more was mandated, and nothing more is enforceable here.
- **Honest proof.** The gate summaries state the Launcher failures, the `Gateway.Tests` no-verdict run
  and the never-run PostgreSQL migration-order tests plainly, and do not dress a skip as a pass.

## What the finding count does not mean

One finding, within the scope above. The finding is a contract gap against the mandate as amended, not
a defect in what the pull request does build: everything it builds, it builds correctly as far as my
reading reaches.

## Developer's answer

**F1 - accepted and fixed.** `skipped` is now the eleventh outcome: a trigger check that did not start a
session because the one it started last is still running. What changed, all on branch
`wbf-activity-record`:

- `src/CcDirector.Gateway.Contracts/FactoryActivityDtos.cs` - new constant `FactoryActivityOutcome.Skipped
  = "skipped"`, added to `All` between `paused` and `failed` (eleven words). The Gateway's refusal and the
  outcome filter both read `All`, so both accept it and both list it.
- `src/CcDirector.Gateway.UnitTests/FactoryActivityRecordTests.cs` - the count pin in
  `Every_listed_outcome_is_accepted` moves from 10 to 11; the test still appends every word in `All`.
- `tools/cc-devthrottle/src/factory_ops.py` - `OUTCOMES` gains `skipped`, so `factory record --help`
  shows it.
- `docs/cli-reference.md` - the outcome list gains `skipped`, with one sentence saying what it means.
- The proof README - "all ten" becomes "all eleven" in both rows, and a re-run section records the
  results below.

Checks, with TEMP on drive D (drive C is full):
- `scripts/test-local.ps1`: all suites green except four tests, none in touched code. Two are the
  Launcher tests that read this machine's live restart signal. Two are caused by TEMP being on drive D:
  one Launcher test expects TEMP under `AppData\Local`, and one Reclaim test needs Windows short names,
  which drive D does not create.
- `dotnet test src\CcDirector.Gateway.UnitTests` in full: 6998 passed, 8 skipped, 0 failed.
- `tools/cc-devthrottle` Python tests in a scratch virtual environment (this machine's own click is below
  the tool's floor, as the first Developer recorded): 3483 passed, 3 skipped; `test_factory_ops.py` 12
  passed; the shared contract and output tests 136 passed.

Not run: the parked Gateway.Tests and Core.Tests, same as the first round.
