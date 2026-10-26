# Step 1, relanded: proof

Mission "Licenses - Loud When Stranded". This relands step 1 (#4493, "a machine stranded on a full account is one
alarm"), which was reverted in #4512, and adds the test the Delivery Lead asked for before it could come back: a test
that starts the Gateway host with step 1 in it.

## Why it was reverted, and why it was not the cause

Production stopped answering HTTP after the deploy of 5ba96eb0a (step 1), deploy run 40397928305. A rollback to b6d6acb
restored it at 21:04. Main was reverted to be safe while the cause was unknown.

The production logs then cleared step 1. **The rolled-back b6d6acb container, which does not have step 1, logged the
same Npgsql `Timeout during reading attempt` at 21:05 to 21:06, and then `The connection pool has been exhausted`
(`Max Pool Size` 10).** The same database fault showed up on an image without step 1.

The investigation found nothing in step 1 that could cause it (`devthrottle_internal`,
`docs/missions/license-loud-switch-2026-10-26/reviews/step-1-startup-investigation.md`). On a throwaway PostgreSQL with
the 26 October data, the licenses minute check sends 16 queries on 5ba96eb0a and 15 on 86b37ace5, the commit before. It
takes about 10 to 13 milliseconds on both. The count stays flat from 8 to 60 machines and from 20,000 to 200,000 log
rows. There is no query per machine and no long transaction, and the check runs on a timer, first 60 seconds after
start.

## What this branch is

- `13954e683`: `git revert --no-edit 7c173f301`, which is the revert of the revert. Step 1 comes back exactly as it was
  reviewed.
- `src/CcDirector.Gateway.DatabaseTests/Licenses/LicenseHostStartPostgresTests.cs`: the new host-start test. It is in
  the database test project because it needs a real PostgreSQL, and `scripts\test-database.ps1` is the only thing that
  runs that project. Anywhere else it reports SKIPPED.
- `CapturedLog.cs` from `CcDirector.Gateway.Tests` is linked into the database test project, so the test can read what
  the minute check logs.

## What the host-start test proves

It starts the host production runs: `GatewayHostOptions.Hosted`, with the Gateway database on PostgreSQL through
`CC_GATEWAY_DB_CONNECTION`, the variable a hosted deployment sets. The host's own startup path opens and migrates that
database. The database is created for the run and dropped afterwards. The test sets up one account with the 26 October
picture in small. machine-a is on account A at 100% of its weekly limit and holds no sign-in for account B, which has
room, and nothing can approve a sign-in for B. machine-b is on B. Both Directors' stream connections are registered, so
the account is in the host's per-tenant pass. The check is `GatewayHost.RunLicenseSwitchCheckAsync`, which is what the
host's timer calls once a minute.

Each of these must be true:

1. `/healthz` answers 200 before the check.
2. The minute check finishes and writes no `FAILED` line from any license check.
3. The check did real work on the data. The stranded alarm is open for machine-a on account A, its one owner email went
   out, and the row is in PostgreSQL. A raw SQL count over the test's own connection shows it, with nothing under test
   in between.
4. A second check keeps the alarm open quietly, sends no second email, and fails nothing.
5. `/healthz` still answers 200 after both checks.

**Proof that it can fail.** I made `LicenseStrandedAlarm.Conditions` throw (`mutation-stranded-check-throws.txt`) and
the test went red on fact 2. The source was restored with `git checkout` and the test passed again on a fresh build
(`host-start-test.txt`). The test commit was made before the mutation, so the mutation could not get into the branch.

## Runs

| File | What | Result |
|---|---|---|
| `host-start-test.txt` | `scripts\test-database.ps1 -Filter "FullyQualifiedName~LicenseHostStartPostgresTests"`, on the restored source | GREEN: 1 executed, 1 passed, 0 skipped |
| `mutation-stranded-check-throws.txt` | the same, with the stranded check made to throw | RED, as it must be: `Assert.DoesNotContain()`, a license check logged FAILED |
| `licenses-filter.txt` | `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~CcDirector.Gateway.Tests.Licenses"` | 1,306 passed, 0 failed (1,259 without step 1) |
| `replay-step-3-on-reland.txt` | the step 3 replay (`LicenseStrandedReplayTests`, commit 1ad23b948), copied in and not committed | 11 passed, 2 failed, as expected |
| `database-tests-full.txt` | the whole database project, `scripts\test-database.ps1` | 84 passed, 3 failed: all three also fail on main without step 1, see below |
| `database-three-on-main-ee943d301.txt` | those three tests on origin/main (ee943d301, step 1 reverted) | the same 3 fail, with the same messages |

### The replay

Both `OutOfWeek_26October_AMachineThatCannotSwitch_IsToldByEmailPushAndBanner` cases (SOREN_NORTH and Mac-mini) pass.
Both `EightyOfWeek_25October_..._RaisesTheWarning` cases are red, because the 80% warning is step 2. This is the same
result step 1 had before it was reverted. Every other case passes, including the negative controls.

### Three database tests that already fail on main

The full database project has three failures. They fail the same way on origin/main at ee943d301, where step 1 is
reverted, so they do not come from this branch:

- `PostgresProviderProofTests.Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres`: the list of collated
  columns differs.
- `AddLicenseAlarmsPostgresTests.AddLicenseAlarms_AppliesOnPostgres_TheServiceOpensEmailsAndClosesAnAlarm_AndItsDownRemovesIt`:
  `Assert.Single()`, the collection was empty (line 123).
- `ArchiveFactoriesPostgresTests.ArchiveFactories_AppliesOnPostgres_TheRegistryListsArchivesAndRestores_AndItsDownRemovesTheColumns`:
  "Every factory has a Boss, and the manifest names no boss seat."

They are reported to the Tech Lead separately. This change does not try to fix them.

## Not run

`scripts\test-local.ps1` was not run on this branch. Its product code is byte for byte the step 1 that passed that gate
before it was reverted (see `../step-1/`). What is new is one test in the database project, which that gate does not run.
