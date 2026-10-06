# Review: phase A registry and goal number

**Verdict: CHANGES REQUESTED**

Reviewed commit `42f4834795bef46ef063120f99a323b77a0ecfb9` against `origin/main` (`e8f673ab9232fa77faf0e61c2b062ba08ec4cf32`).

## Findings

### [P1] Contain `goalFile` inside the factory folder before reading it

Location: `tools/cc-devthrottle/src/factory_registry_ops.py:128`

`read_manifest` joins `folder` and the manifest-controlled `goalFile`, then calls `is_file()` and `read_text()` before it proves that the result stays inside `folder`. A manifest can therefore name `../secret.txt` or an absolute path and make the command read those bytes into the request body. The Gateway later rejects the lexical `..`/absolute form, but the bytes have already left the computer. More seriously, a relative path through a symlink passes the Gateway's lexical `RelativePath` validation: I created `factory/GOAL.md` as a symlink to a sibling `secret.txt`, and `read_manifest` returned `goalFile: GOAL.md` with `goalText: TOP-SECRET`. That request is valid to the Gateway and persists the outside file's contents in the registry, where other sessions in the account can read it. This breaks the brief's promise that the named goal file is relative to the factory folder and creates a local-file disclosure path.

Resolve both paths before reading and refuse unless the resolved goal path is contained by the resolved factory folder; define deliberately how symlinks are treated.

### [P2] A valid non-ASCII goal filename makes a successful registration look failed

Location: `tools/cc-devthrottle/src/factory_registry_ops.py:167`

The Gateway accepts non-ASCII relative paths, but the command interpolates `goalFile` directly into the plain-output block. `axi_output.write_blocks` rejects non-ASCII rather than escaping it. With a real file named `G\u00d6AL.md` and a mocked successful Gateway response, `factory register` exited 1 with an unhandled `ValueError` and printed nothing. The registration has already succeeded at that point, so the caller is told failure after state changed and may retry or investigate the wrong problem. Render the filename through the AXI escaping helper before passing the block to `write_blocks`.

### [P2] The two read commands do not meet the AXI field and truncation contract

Locations: `tools/cc-devthrottle/src/factory_registry_ops.py:217`, `tools/cc-devthrottle/src/factory_registry_ops.py:289`

`factory list` always emits six fields per row and `factory goal-number show` always emits seven. Neither command exposes `--fields`, even though `docs/axi-standard.md` requires three or four default fields and `--fields` for additional fields. The goal-number command also prints every full `link` (accepted up to 2,048 characters) with no truncation hint or `--full`; at the default 20 posts, links alone can add roughly 40 KB to a routine read. The reproduced headers were `factories[1]{id,title,computer,ceo,seats,goal}:` and `goalNumbers[1]{asOf,value,unit,postedBy,postedAtUtc,link,id}:`. This defeats the token-efficiency requirement the brief explicitly assigns to these commands and gives callers no selective compact form.

Use three or four default fields, add validated `--fields` support for the rest, and truncate long free text with the required size hint plus `--full` access.

## Scope and evidence

Read:

- The full `origin/main...HEAD` inventory and every hand-authored production change for the contracts, entities, DbContext mapping, store, routes, host wiring, `SessionKeyGuard`, CLI, and CLI reference.
- The phase A brief, mission, plan, design report, repository guidance, and `docs/axi-standard.md`. The brief and plan are not present in the review worktree, so I read their copies in `D:/ReposFred/_wt/factories-screen/docs/missions/factories-screen-2026-10-06/`.
- The new store, route, guard, switch, command, and migration tests; both SQLite and PostgreSQL migration `Up`/`Down` files; and the generated snapshot/designer changes through their diffs and model-consistency tests.

Ran:

- `dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj` with filters covering `FactoryRegistryStoreTests`, `SessionKeyGuardTests`, `FactoryAgentsSwitchTests`, `FleetManagerLaterStepsMigrationChainTests`, and `GatewayHostBootSmokeTests`, with the unrelated workspace typecheck explicitly disabled: **404 passed, 1 skipped, 0 failed**. The skip was the env-gated real-PostgreSQL startup/migration fact.
- `uv run --isolated` at the declared Click 8.2.1 / Typer 0.16.1 floor for `tools/cc-devthrottle/tests/test_factory_registry_ops.py`: **21 passed**.
- Direct CLI probes for traversal/symlink reads, non-ASCII output, and the emitted list headers. Those probes reproduced all three findings above.
- `git diff --check origin/main...HEAD`: exit 0.

Could not reach:

- The authenticated `FactoryRegistryRouteTests` did not execute because the repository's per-user Gateway test lock was held by live process `14904`, owned by session `35ca0a83-0e1d-42d7-b80b-9eb1fef62098` in the implementation worktree. The lock reported that this run was queued rather than started; I stopped the waiting run and do not count it as route evidence.
- No configured `CC_GATEWAY_DB_CONNECTION` was available, so the real PostgreSQL migration/startup path did not execute. Model/snapshot and migration-chain checks did execute and passed.
- I did not run the full local/Parked suites or a live Gateway command. The findings above are independent reproductions on the changed CLI path; the tenant filter, gate wiring, and migration conclusions are limited to the source and tests named above.

## Round 2

**Verdict: APPROVED**

Re-reviewed follow-up commit `480ae6055a08634944dae470821b7a0c1c1cedbb` against the originally reviewed commit `42f4834795bef46ef063120f99a323b77a0ecfb9`, and checked the complete `origin/main...HEAD` scope. No new findings.

### Resolution of the first review

- **Contained goal-file read:** resolved. The CLI now rejects absolute and `..` paths before reading, resolves both the factory folder and final goal-file target, and refuses a symlink whose final target is outside the folder. A symlink to a target inside the folder remains supported. The command sends nothing to the Gateway on every refusal.
- **Non-ASCII successful registration output:** resolved. The returned factory id and composed goal line are rendered through the ASCII-safe helpers before `write_blocks`; a registration whose goal file is `G\u00d6AL.md` now exits 0 and produces ASCII output.
- **AXI fields and truncation:** resolved. `factory list` defaults to `id,title,ceo,seats`; `factory goal-number show` defaults to `asOf,value,unit,postedBy`; both support validated, ordered `--fields`; `--fields` with `--json` is a usage error; and long goal values, units, and links carry the required length hint with `--full` access to the whole value. JSON remains the unchanged all-fields Gateway answer.
- **Goal-number tenant-key guard:** the follow-up also changes `FactoryGoalNumberEntity` to derive from `GatewayMintedKeyEntity` and removes the store-level `Id` assignment. This makes the existing single-Guid primary-key exception enforceable by the private-setter/minted-key guard, while preserving the tenant query filter and per-account read behavior. The database mapping is unchanged, so no migration change is required for this source-level correction.

### Round 2 scope and evidence

Read every hunk in `42f483479..480ae6055`, including the entity/store change, CLI signatures and implementation, new tests, usage-error harness change, and CLI reference; then rechecked the full `origin/main...HEAD` inventory and whitespace.

Ran:

- The two changed Python test files in an isolated environment at the declared Click 8.2.1 / Typer 0.16.1 floor: **624 passed**.
- A named focused rerun of the regression cases: **12 passed**, including five absolute/traversal inputs, an external symlink refusal, an internal symlink success, the non-ASCII goal filename, selected-field order, compact defaults, truncation/`--full`, and both `--fields`/`--json` conflicts. The refusal tests positively asserted that no Gateway call occurred.
- The targeted Gateway unit set for the registry, tenant guard, session-key guard, feature switch, migration chain, and boot smoke: **409 passed, 1 skipped, 0 failed**. The skip was the environment-gated real-PostgreSQL startup/migration fact.
- A verbose focused rerun of `TenantScopeGuardTests` and `FactoryRegistryStoreTests`: **45 passed**, including the minted-key guard and `GoalNumbers_AreThePostingAccountsOnly`.
- `git diff --check origin/main...HEAD`: exit 0.

The authenticated `FactoryRegistryRouteTests` still did not start: the same live process `14904` in session `35ca0a83-0e1d-42d7-b80b-9eb1fef62098` held the repository's per-user Gateway suite lock, so I stopped my queued run and do not count it as evidence. No configured PostgreSQL connection was available, and I did not exercise a live Gateway. Those gaps do not cover HTTP-host integration or a real PostgreSQL migration; the follow-up did not change the route or schema, and the corrected client, store, model guard, tenant behavior, and migration metadata were exercised by the named tests above.
