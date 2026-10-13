# Factory Seats, phase 2a - the brief store, its routes and its command: the proof

Date: 2026-10-13. Mission document: `docs/missions/factory-seats-2026-10-13/MISSION.md` (issue #3950).
Branch: `factory-seats/phase-2a-brief-store`. This phase has no screen, so there are no screenshots here; the
screens are phases 2b and 2c, and the phase 2 report with screenshots is theirs.

## What was built

- **A table, `factory_seat_briefs`**, by one Entity Framework migration (`AddFactorySeatBriefs`) and its PostgreSQL
  migration. Rows are keyed by account, factory, seat and version. Nothing existing reads it.
- **A store, `FactoryBriefStore`**, beside factory memory and shaped like it. A save adds a draft version and
  states the version it read; a stale save is refused with the newest version attached. A person publishes one
  version. Bringing an older version back publishes a copy of it as the newest version. A brief is written only
  for a registered seat of a registered factory. One version's text takes at most 65,536 bytes. Every version is
  kept.
- **Six routes** under `/factory-briefs/{factory}/{seat}`: read the published brief, read the newest version,
  history, save a draft, publish, bring back. Who is asking is read by `FactoryNaming.TryAct`, the gate a
  factory's schedules and triggers already pass.
- **The command line**: `cc-devthrottle factory brief get | history | set --file | publish | restore`, with its
  section in `docs/cli-reference.md`.

## The check, as run

Run on Linux, from a worktree of the branch, after rebasing onto `origin/main` at `a247c66c0`. The branch's
head for these runs was `4239a2e68`.

| Command | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~Cron\|FullyQualifiedName~Schedule"` | Passed: 1288, failed: 0, skipped: 0 |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"` | Passed: 48, failed: 0, skipped: 0 |
| `npm run typecheck` | Exit 0 |
| `npm test --workspace @devthrottle/client-core` | 163 files, 2016 tests passed |
| `npm test --workspace @devthrottle/cockpit` | 1111 passed, **1 failed** - see below |
| `python3 -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule"` | 906 passed, 1 skipped |
| `scripts/test-database.ps1` (the database tests the migration needs) | GREEN: 72 executed against a throwaway PostgreSQL, 0 failed, 0 skipped |

Also run, beyond the check: the whole `tools/cc-devthrottle/tests` suite (4377 passed, 2 skipped); the Gateway
unit tests for the session key guard, the migrations, tenancy and architecture (618 passed); and the guard in
`CcDirector.Core.UnitTests` that scans test files for dependence on the machine (13 passed).

Of the 1288 Gateway unit tests, 70 are new and are this phase's own: `FactoryBriefStoreTests` (35),
`FactoryBriefEndpointsTests` (17) and `FactoryBriefRoutesThroughThePipelineTests` (18). The command line has
30 new tests in `test_factory_brief_ops.py`.

### The one failure, and why it is not this phase's

`apps/cockpit/src/licenses/fixtures.test.ts` fails with "expected 28 to be 16". It compares the Licenses page's
fixtures with the examples in `missions/license-switching/cockpit-api.md`. This branch changes no file under
`apps/`, `packages/` or `missions/`; the branch differs from `origin/main` in 22 files, all listed by
`git diff origin/main --stat`.

### What had to be true of the machine for these runs

Three things about the machine the check ran on, none of them a change to the repository:

1. The Gateway test runs were given `DOTNET_hostBuilder__reloadConfigOnChange=false`. Existing tests that call
   `WebApplication.CreateBuilder()` each start a file watcher, and the machine's limit of 128 inotify instances for
   one user was already taken up by the Directors and sessions running on it. Without the variable, between 85 and
   119 existing tests failed with "The configured user limit (128) on the number of inotify instances has been
   reached"; none of them was a brief test.
2. `scripts/test-database.ps1` calls `powershell`, which Linux does not have. It was run with a two-line script
   named `powershell` on the path that starts `pwsh` with the same arguments.
3. The system Python has no `typer`, so the command line tests ran in a throwaway environment holding the
   tool's own declared dependencies and `pytest`.

## The refusals, by the answer that carries them

Each of these is asserted with an exact comparison in the test named, on the answer the route gives.

| Case | Status | The answer's `error` | Test |
|---|---|---|---|
| A session publishes | 403 | `Only a person publishes a seat's brief. A session of the factory may save a draft; the owner reads it and publishes it.` with `outcome` `OnlyAPersonPublishes` | `A_SESSIONS_PUBLISH_COMES_BACK_AS_403_WITH_THE_SENTENCE_AND_THE_OUTCOME_BY_NAME` |
| A session brings a version back | 403 | `Only a person publishes a seat's brief, and bringing an older version back publishes a copy of it. A session of the factory may save the text it wants as a new draft; the owner publishes it.` | the same test |
| A stale save | 409 | contains `is at version 2, not 1`, with `outcome` `Stale` and the whole newest version under `current` | `A_STALE_SAVE_IS_REFUSED_AND_THE_ANSWER_CARRIES_THE_CURRENT_VERSION`, and through the mapped route in `A_STALE_SAVE_COMES_BACK_AS_409_WITH_THE_CURRENT_VERSION_UNDER_current` |
| A session of another factory, on every one of the six routes | 403 | `this brief belongs to factory 'warmforward', so only a person or a session of 'warmforward' may change or run it` | `A_SESSION_OF_ANOTHER_FACTORY_MAY_NOT_READ_OR_WRITE_THIS_FACTORYS_BRIEF` |
| A seat that is not registered | 404 | contains `'night-watch' is not a seat of warmforward`, with `outcome` `SeatNotRegistered` | `A_BRIEF_FOR_A_SEAT_THAT_IS_NOT_REGISTERED_is_NOT_FOUND_on_a_write_and_on_a_read` |
| A brief over the cap | 400 | contains `at most 65536`, with `outcome` `TooLarge` | `A_brief_over_the_cap_is_a_BAD_REQUEST_that_says_the_cap` |
| The account's machine token writes | 403 | `a seat's brief is written by a person, or by a session of its factory` | `THE_MACHINE_TOKEN_MAY_READ_AND_MAY_NOT_WRITE_because_a_version_records_who_wrote_it` |

Another account never sees a brief: `ANOTHER_ACCOUNT_NEVER_SEES_A_BRIEF` (the store) and
`ANOTHER_ACCOUNT_NEVER_SEES_A_BRIEF_through_any_read` (the routes) register the same factory and seat ids in two
accounts, and the second account reads nothing and starts its own chain at version 1.

## Decisions made inside the brief, for the Implementation Lead to keep or replace

1. **Publishing marks a version; it does not copy it.** A version's text is never edited. The one change a stored
   row ever sees is the mark that it was published, set once. The published brief is the highest version that is
   published, so publishing never goes backwards, and an older draft is published by bringing it back.
2. **No version is ever pruned.** Factory memory keeps fifty versions or ninety days. The mission's goal 5 says
   every version is kept, so the brief store prunes nothing. Growth is bounded only by the cap on one version.
3. **The machine token and a Director may read and may not write.** `FactoryNaming.TryAct` lets them act on a
   factory's row. A version records who wrote it, a person or which session, and they are neither.
4. **The command line never looks up the version it sends.** `set` sends `--expected-version`, 0 by default.
5. **The cap is 65,536 bytes for one version's text**, and an empty brief is refused.
6. **History carries every version's whole text**, as factory memory's does.

## What this does NOT prove

- **No request went through the real Gateway host.** The routes are tested through their handlers and through
  the mapped endpoints in memory. Authentication in front of them, and the session key guard in front of that,
  are tested as their own units, never as one request through a running Gateway. The one thing known about the
  real host is that it starts with these routes mapped: `ContextLessRouteCensusTests` builds and starts a real
  `GatewayHost`, and it got as far as listing that host's routes.
- **A person's publish from the command line is not possible and was not tried.** The command line always sends
  a session's key, so `publish` and `restore` are refused by the Gateway. A person publishes from the screen that
  phase 2b builds.
- **Two replicas racing for one version were not run.** What is proven is that the database refuses a second row
  with the same key (`THE_DATABASE_ITSELF_REFUSES_A_SECOND_WRITER_OF_THE_SAME_VERSION`) and that a second store
  holding an older version is refused. The PostgreSQL run applies the migration and checks the collation of the
  key columns; no brief test runs against PostgreSQL.
- **The rest of the Gateway unit suite was not run to its end.** It was stopped at nine and a half minutes. In
  that time two tests failed, both in `Wingman` and both on Linux only: they make a save fail by holding a file
  open for exclusive use, which does not stop a writer on Linux. They fail the same way run alone.
- **One of the two `ContextLessRouteCensusTests` fails on this machine.** It expects `DELETE /exes/slots/{n}`,
  and the test's own comment says that route is mapped only on Windows. No brief route is in its list, and it is
  outside the mission's check.
- **The Cockpit's development proxy does not know `/factory-briefs` yet** (`apps/cockpit/vite.config.ts` lists
  `/factory-memory`). Phase 2b needs that line; nothing in this phase calls the routes from a browser.
