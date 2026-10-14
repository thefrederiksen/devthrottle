# Factory Seats, phase 2a - the brief store, its routes and its command: the proof

Date: 2026-10-13. Mission document: `docs/missions/factory-seats-2026-10-13/MISSION.md` (issue #3950).
Branch: `factory-seats/phase-2a-brief-store`. This phase has no screen, so there are no screenshots here; the
screens are phases 2b and 2c, and the phase 2 report with screenshots is theirs.

**Updated after review 1.** The review (`reviews/review-1.md`) returned one finding: nothing bounded the growth of
a seat's history. It is accepted and answered in `reviews/answer-1.md`, and this proof is brought up to date with
what was built for it. Where an earlier statement here is replaced, it says so.

## What was built

- **A table, `factory_seat_briefs`**, by one Entity Framework migration (`AddFactorySeatBriefs`) and its PostgreSQL
  migration. Rows are keyed by account, factory, seat and version. Nothing existing reads it.
- **A store, `FactoryBriefStore`**, beside factory memory and shaped like it. A save adds a draft version and
  states the version it read; a stale save is refused with the newest version attached. A person publishes one
  version. Bringing an older version back publishes a copy of it as the newest version. A brief is written only
  for a registered seat of a registered factory. One version's text takes at most 65,536 bytes. Every version is
  kept, and nothing is ever pruned or deleted.
- **A bound on writing and on reading (review 1).** A session is refused another draft while 20 drafts wait above
  the seat's published version; a person's publish makes room, and a person is not bound. The history is read one
  page at a time, without any version's text, and a version's text is read one version at a time. Each version
  stores the size of its text (`TextBytes`), so the history never reads a text to measure it.
- **Seven routes** under `/factory-briefs/{factory}/{seat}`: read the published brief, read the newest version,
  one page of the history, read one version, save a draft, publish, bring back. Who is asking is read by
  `FactoryNaming.TryAct`, the gate a factory's schedules and triggers already pass.
- **The command line**: `cc-devthrottle factory brief get [--draft | --version N] | history [--count N]
  [--before N] | set --file | publish | restore`, with its section in `docs/cli-reference.md`.

## The check, as run

Run on Linux on 2026-10-13, from the worktree of the branch, after `origin/main` at `c60d272d4` was merged into
it (phase 1, pull request 3958, is in that merge; it merged without a conflict). The code for these runs is
commit `f4f7002ed`. Every command was run in the foreground, one after another.

The six commands of the mission document's section 7:

| Command | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~Cron\|FullyQualifiedName~Schedule"` | Passed: 1355, failed: 0, skipped: 0 |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"` | Passed: 46, failed: 0, skipped: 0 |
| `npm run typecheck` | Exit 0 |
| `npm test --workspace @devthrottle/client-core` | 166 files, 2058 tests passed |
| `npm test --workspace @devthrottle/cockpit` | 110 files, 1155 tests passed |
| `python -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule"` | 933 passed, 1 skipped |

Also run, beyond the check, because this change touches what they cover:

| Command | Result | Why |
|---|---|---|
| `scripts/test-database.ps1` | GREEN: 73 executed against a throwaway PostgreSQL, 0 failed, 0 skipped | The PostgreSQL migration gained a column; this applies it and checks the model has no pending change |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~FactoryBrief\|FullyQualifiedName~Migration\|FullyQualifiedName~SessionKeyGuard"` | Passed: 595, failed: 0 | The local migration and the session key guard both changed |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~GatewayHostBootSmoke\|FullyQualifiedName~Tenancy\|FullyQualifiedName~Architecture"` | Passed: 142, failed: 0 | `GatewayHostBootSmokeTests` is where the local snapshot is compared with the model |
| `python -m pytest tools/cc-devthrottle/tests -q` (the whole command line suite) | 4404 passed, 2 skipped | The commands gained options, and the help of every command is checked there |

The whole command line suite earned its place: its first run failed one test, because the new one-line summary of
`factory brief get` was 95 characters and the limit is 80. The summary was shortened and the suite run again, to
the result above.

Of the Gateway unit tests, 104 are this phase's own: `FactoryBriefStoreTests` (53), `FactoryBriefEndpointsTests`
(28) and `FactoryBriefRoutesThroughThePipelineTests` (23). Before review 1 there were 70. The command line has 44
tests in `test_factory_brief_ops.py`; before review 1 there were 30.

### The Cockpit failure reported earlier is gone

An earlier version of this proof reported one Cockpit test failing, `apps/cockpit/src/licenses/fixtures.test.ts`,
and said it was not this phase's. After merging `origin/main` the Cockpit suite passes whole, on a branch that
still changes no file under `apps/`, `packages/` or `missions/`.

### What had to be true of the machine for these runs

None of these is a change to the repository:

1. The version 10 .NET kit in `~/.dotnet` was put first on the path.
2. The `dotnet test` runs were given `DOTNET_hostBuilder__reloadConfigOnChange=false` and
   `DOTNET_USE_POLLING_FILE_WATCHER=1`. Existing tests that call `WebApplication.CreateBuilder()` each start a file
   watcher, and the machine's limit of 128 inotify instances for one user is taken up by the Directors and sessions
   running on it.
3. `scripts/test-database.ps1` calls `powershell`, which Linux does not have. It was run with a two-line script
   named `powershell` on the path that starts `pwsh` with the same arguments.
4. The system Python has no `typer`, so the command line tests ran in an existing environment holding the tool's
   own declared dependencies and `pytest` (`click` 8.5.0 there, so the eleven failures review 1 saw on `click`
   8.1.8 do not occur).

## The refusals, by the answer that carries them

Each of these is asserted with an exact comparison in the test named, on the answer the route gives.

| Case | Status | The answer's `error` | Test |
|---|---|---|---|
| A session publishes | 403 | `Only a person publishes a seat's brief. A session of the factory may save a draft; the owner reads it and publishes it.` with `outcome` `OnlyAPersonPublishes` | `A_SESSIONS_PUBLISH_COMES_BACK_AS_403_WITH_THE_SENTENCE_AND_THE_OUTCOME_BY_NAME` |
| A session brings a version back | 403 | `Only a person publishes a seat's brief, and bringing an older version back publishes a copy of it. A session of the factory may save the text it wants as a new draft; the owner publishes it.` | the same test |
| A stale save | 409 | contains `is at version 2, not 1`, with `outcome` `Stale` and the whole newest version under `current` | `A_STALE_SAVE_IS_REFUSED_AND_THE_ANSWER_CARRIES_THE_CURRENT_VERSION`, and through the mapped route in `A_STALE_SAVE_COMES_BACK_AS_409_WITH_THE_CURRENT_VERSION_UNDER_current` |
| A session of another factory, on every one of the seven routes | 403 | `this brief belongs to factory 'warmforward', so only a person or a session of 'warmforward' may change or run it` | `A_SESSION_OF_ANOTHER_FACTORY_MAY_NOT_READ_OR_WRITE_THIS_FACTORYS_BRIEF` |
| A seat that is not registered | 404 | contains `'night-watch' is not a seat of warmforward`, with `outcome` `SeatNotRegistered` | `A_BRIEF_FOR_A_SEAT_THAT_IS_NOT_REGISTERED_is_NOT_FOUND_on_a_write_and_on_a_read` |
| A session's draft while 20 wait above the published version | 409 | `the brief of 'boss' has 20 drafts waiting that nobody has published (versions 1 to 20), and a session may add a draft only while fewer than 20 are waiting. Nothing was saved, and no version was removed. Ask the owner to read the drafts and publish one: publishing makes room for more.` with `outcome` `TooManyDrafts` and the newest version under `current` | `A_SESSIONS_DRAFT_PAST_THE_LIMIT_IS_A_CONFLICT_THAT_SAYS_TO_ASK_THE_OWNER_TO_PUBLISH`, and through the mapped route in `A_SESSIONS_DRAFT_PAST_THE_LIMIT_COMES_BACK_AS_409_WITH_THE_OUTCOME_BY_NAME_and_a_page_size_that_is_not_one_as_400` |
| A page size that is not 1 to 100, for example `limit=101` | 400 | `limit is how many versions one page holds: a whole number from 1 to 100, not '101'` | `A_page_size_or_a_starting_version_that_is_not_one_is_REFUSED_in_words_never_quietly_replaced` |
| A starting version that is not one, for example `before=0` | 400 | `before is the version a page starts below: a whole number from 1, not '0'` | the same test |
| A version that does not exist | 404 | `version 9 of the brief of 'boss' does not exist; its newest version is 1` with `newestVersion` 1 | `A_version_that_does_not_exist_is_NOT_FOUND_and_says_which_is_newest` |
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
   every version is kept, so the brief store prunes nothing. *Replaced after review 1:* this decision used to end
   "growth is bounded only by the cap on one version". Growth is now also bounded by decision 7.
3. **The machine token and a Director may read and may not write.** `FactoryNaming.TryAct` lets them act on a
   factory's row. A version records who wrote it, a person or which session, and they are neither.
4. **The command line never looks up the version it sends.** `set` sends `--expected-version`, 0 by default.
5. **The cap is 65,536 bytes for one version's text**, and an empty brief is refused.
6. **The history is one page of versions without their text** (`limit` 1 to 100, 50 when not sent; `before`).
   *Replaced after review 1:* it used to carry every version's whole text, as factory memory's does. A version's
   text is read one version at a time. A page size that is not one is refused, never quietly replaced.
7. **A session may add a draft only while fewer than 20 wait above the published version** (review 1). What
   counts is every draft above the published version, whoever wrote it. Twenty is one constant.
8. **A person is not bound by that limit.** The person is the one who makes room by publishing; a person who was
   bound could correct twenty bad drafts only by first publishing one of them. A person's drafts still count.
9. **The size of a version's text is stored with it**, in a column added to this branch's own migration in place,
   because the table has not been merged or deployed.

## What this does NOT prove

- **No request went through the real Gateway host.** The routes are tested through their handlers and through
  the mapped endpoints in memory. Authentication in front of them, and the session key guard in front of that,
  are tested as their own units, never as one request through a running Gateway. The one thing known about the
  real host is that it starts with these routes mapped: `ContextLessRouteCensusTests` builds and starts a real
  `GatewayHost`, and it got as far as listing that host's routes.
- **A person's publish from the command line is not possible and was not tried.** The command line always sends
  a session's key, so `publish` and `restore` are refused by the Gateway. A person publishes from the screen that
  phase 2b builds.
- **The limit on waiting drafts bounds a session, not a person.** A person's device key is not bound, on purpose
  (decision 8). So a script holding a person's device key could still add versions without end. Nothing limits how
  often anyone may save.
- **The limit is one seat's, and there is no limit across seats.** The most that sessions can add between two acts
  of a person is 20 versions for each registered seat. Nothing caps the table as a whole.
- **Until phase 2b exists, nobody can make room.** Publishing needs a person, and a person has no surface before
  the brief panel. So a seat takes 20 drafts from sessions and then refuses the next, until phase 2b ships.
- **The limit was not run with two replicas racing.** That two replicas cannot both pass it is argued from the
  primary key - both would be writing the same next version - and not shown by a test with two processes.
- **The page query and the count of waiting drafts ran only on SQLite.** The PostgreSQL run applies the migration
  with the new column and finds no pending model change; no brief test, and so no page of a history, runs against
  PostgreSQL.
- **A database that applied this branch's migration before the size column was added does not have it.** The
  column was added to the migration in place. That is right for main and for the hosted Gateway, where the table
  has never existed. A local Gateway that somebody ran from this branch before commit `f4f7002ed` holds the table
  without the column and would fail on a save; its `factory_seat_briefs` table and that migration's row in the
  migrations history would have to be removed by hand. No such database is known.
- **Two replicas racing for one version were not run.** What is proven is that the database refuses a second row
  with the same key (`THE_DATABASE_ITSELF_REFUSES_A_SECOND_WRITER_OF_THE_SAME_VERSION`) and that a second store
  holding an older version is refused. The PostgreSQL run applies the migration and checks the collation of the
  key columns; no brief test runs against PostgreSQL.
- **The whole Gateway unit suite was not run.** The filtered runs above are what ran after review 1. The earlier
  Developer reported two `Wingman` tests and one of the two `ContextLessRouteCensusTests` failing on Linux for
  reasons that are the machine's and not the branch's; those were not run again and that report is not confirmed
  here.
- **The Cockpit's development proxy does not know `/factory-briefs` yet** (`apps/cockpit/vite.config.ts` lists
  `/factory-memory`). Phase 2b needs that line; nothing in this phase calls the routes from a browser.
