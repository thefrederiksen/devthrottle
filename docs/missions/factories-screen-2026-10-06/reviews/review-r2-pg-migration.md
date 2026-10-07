# Review - round 2, the PostgreSQL ArchiveFactories migration (pull request 3613)

Reviewed: branch `factories-screen/pg-migration`, head `fa4c7b1f3`, against `origin/main` at `ca403bdef`
(the merge base; main had not moved). Reviewer: a separate review session, read-only, no edits made.
Date: 2026-10-07.

## Verdict

**APPROVE.** No findings. The PostgreSQL migration is the column-for-column twin of the local one, the
PostgreSQL model snapshot and the migration's Designer agree with each other and with the entity, the
migration sorts and applies directly after the production chain's newest migration, Down removes exactly
what Up added, and nothing outside the migration project and the tests changed. The real-PostgreSQL proof
the pull request adds passed on a throwaway database built by the gate script on this machine.

## Scope

What I checked, and how:

1. **Column-for-column match with the local migration and the entity.** Read both migration files and the
   entity (`FactoryRegistryEntity`), plus the `HasMaxLength(256)` configuration in `GatewayDbContext`.
2. **Snapshot consistency.** Diffed the PostgreSQL snapshot against origin/main, and compared the Designer's
   `BuildTargetModel` body with the snapshot's `BuildModel` body byte for byte.
3. **Position in the production chain.** Listed both migration folders; read the chain tests the pull
   request updated.
4. **Down.** Read it.
5. **Nothing else changed.** `git diff --name-only origin/main...HEAD` filtered to anything outside
   `Migrations/` and `Tests/`.
6. **Ran the tests the pull request names**, with the gate script and its own throwaway PostgreSQL:
   `.\scripts\test-local.ps1 -Parked -Filter "FullyQualifiedName~Migration|FullyQualifiedName~PostgresTests|FullyQualifiedName~GatewayHostBootSmokeTests"`.

Not checked: whether the hosted database (the real production PostgreSQL) is at `AddTeamQuestionAnswers`
right now after the rollback to 43fe9b6. The chain tests prove the migration applies on top of
`AddTeamQuestionAnswers`; they cannot see production. I also did not re-run the pull request's "9 failures
with the PostgreSQL migration removed" measurement; I read the guard instead (see item 6 below).

## What I found, item by item

### 1. The PostgreSQL migration matches the local one and the entity

| Column | Entity | Local (SQLite) migration | PostgreSQL migration | PostgreSQL snapshot |
|---|---|---|---|---|
| `ArchivedAtUtc` | `DateTime?` | `TEXT`, nullable | `timestamp with time zone`, nullable | `DateTime?`, `timestamp with time zone` |
| `ArchivedBy` | `string?`, max length 256 | `TEXT`, maxLength 256, nullable | `character varying(256)`, maxLength 256, nullable | max length 256, `character varying(256)` |
| `ArchivedSchedulesJson` | `string?` | `TEXT`, nullable | `text`, nullable | `text` |

Same three names, same nullability (all nullable, no defaults, so a factory registered before the
migration keeps its row untouched), same max length on `ArchivedBy`. The PostgreSQL column types are the
Npgsql provider's standard mapping for those CLR types, and they are what the existing PostgreSQL columns
on the same table use (`RegisteredAtUtc` is `timestamp with time zone`, `RegisteredBy` is
`character varying(256)`). The migration names the `gateway` schema on every operation, as every other
PostgreSQL migration in the project does.

### 2. The snapshot is consistent

The snapshot diff against origin/main is exactly the three properties above, inserted in alphabetical
order inside the `FactoryRegistryEntity` block, and nothing else. The Designer file's model body is
identical to the snapshot's model body (a diff of the two method bodies is empty), so the migration
was generated from the model the snapshot now records, not hand-edited into place.
`GatewayHostBootSmokeTests.PostgresSnapshot_MatchesTheModel_..._WithoutDatabase` asserts
`HasPendingModelChanges()` is false for PostgreSQL, and it passed in my run.

### 3. It applies on top of the production chain

The newest PostgreSQL migration on origin/main is `20261006171258_AddTeamQuestionAnswers`. The new one is
`20261007052952_ArchiveFactories`, which sorts after it ordinally. The real-PostgreSQL proof migrates an
empty database to `AddTeamQuestionAnswers`, inserts a factory, applies `ArchiveFactories`, and checks the
three columns appear and the old row is intact; it passed. The chain tests on both providers
(`FleetManagerLaterStepsMigrationChainTests`, `FleetOutcomeStopIdentityMigrationTests`,
`GatewayHostBootSmokeTests`) now name `ArchiveFactories` as the last migration and passed.

### 4. Down is correct

Down drops the same three columns on the same schema-qualified table, nothing more. The real-PostgreSQL
proof migrates back to `AddTeamQuestionAnswers`, checks the column count is zero and both factory rows
survive, then migrates forward again; it passed.

### 5. Nothing else changed

Eleven files: the migration, its Designer, the snapshot, one new test, and seven existing migration-chain
tests whose "newest migration" assertions moved forward by one. No product code, no documentation, no
scripts. The filter for files outside `Migrations/` and `Tests/` returned nothing.

### 6. The guard the brief asked about

The pull request is right that a guard already exists:
`GatewayHostBootSmokeTests.EverySqliteMigrationSinceTheBaseline_HasAPostgresTwin_AndTheOtherWayRound`
is on origin/main and compares the ordered set of migration names since the baseline on both providers.
It would have failed on pull request 3608's state (a SQLite `ArchiveFactories` with no PostgreSQL twin).
It lives in `CcDirector.Gateway.UnitTests`, which the default gate run parks, which is how 3608 merged
green. The pull request names this and proposes (not builds) a fix. That is the right size for an urgent
fix pull request; the proposal belongs in its own issue.

## Test results

Run on this machine, 2026-10-07 from 02:04 to 02:20 local time, Debug configuration, with the gate script's
own throwaway PostgreSQL (fsync on).

| Suite | Result |
|---|---|
| CcDirector.Gateway.UnitTests (filtered) | 42 passed, 1 skipped, 0 failed, 36 s |
| CcDirector.Core.Tests (filtered) | 21 passed, 0 failed |
| CcDirector.Gateway.Tests (filtered) | 36 passed, 0 failed, run stopped by me at 14 m 47 s (see below) |
| Ten default-run suites | 0 tests matched the filter, as expected |

The one skip is `HostStartupPath_ResolvesAndAppliesPostgresMigrations_OnConfiguredPostgres`, which
skips itself unless the runtime selector `CC_GATEWAY_DB_CONNECTION` points at a database; the pull request
reports the same skip. Skipped is not passed, and nothing in this review rests on that test.

**What I stopped, and why it does not change the verdict.** The Gateway.Tests filter matches 41 tests,
roughly 32 of which replay the whole PostgreSQL migration chain from an empty database. On this machine
tonight (about 130 `dotnet` processes, 4 GB free of 64, every DDL statement waiting on a disk sync) that was
on course for well over an hour, and the Lead asked me to bound it. I stopped my own test host after 36 of
the 41 had passed, including every test this pull request touches:

- `ArchiveFactoriesPostgresTests.ArchiveFactories_AppliesOnPostgres_TheRegistryListsArchivesAndRestores_AndItsDownRemovesTheColumns` - PASSED (the new real-PostgreSQL proof: migrate to before, insert, apply, list/register/archive/find/restore through the real store, Down, Up)
- `FleetOutcomeStopIdentityPostgresTests` and `TurnVerdictAnswerChoicePostgresTests` - PASSED (the two PostgreSQL chain tests the pull request edited)
- `AddTeamQuestionAnswersPostgresTests` - PASSED (the migration directly before the new one)

The five not reached are `AddTeamInvitationsPostgresTests`, one of the two `DeviceCredentialImportPostgresTests`,
the two `GatewayReadCutsPostgresTests`, and one more from the PostgreSQL set; none touches `factory_registry`,
and none was changed by this pull request. The pull request author reports 41 of 41 passed on their run. The
gate script marked Gateway.Tests FAILED because the host ended abnormally, not because any test failed: the
results file records 36 passed, 0 failed.

My run's throwaway PostgreSQL was destroyed by the script. A second rig container
(`cc-pg-stats-proof-runa370792e`, created 02:19) belongs to another run on this machine and I left it alone.

## Observations, not findings

- The gate's throwaway PostgreSQL runs with `fsync=on`, `synchronous_commit=on`, `full_page_writes=on`.
  For a database that is destroyed at the end of the run, `fsync=off` would make every chain replay several
  times faster at no cost to what the tests prove. That is a gate-script improvement, not this pull request.
- The PostgreSQL migration timestamp (`052952`) is about two hours after the SQLite one (`035034`). The
  twin guard matches on the name after the timestamp, so this is harmless, and it is how `AddFactoryRegistry`
  was made too.
