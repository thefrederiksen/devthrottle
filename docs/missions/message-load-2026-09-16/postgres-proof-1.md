# Postgres proofs for slice 1

Run on SOREN_NORTH, 2026-09-16, against `origin/mission/message-load` at `22eae03ad` (a detached worktree,
not the shared checkout). Docker Desktop was running (server 29.6.1).

## What was run

`scripts\test-local.ps1 -Parked` also runs every default suite and Core.Tests, so the run was narrowed with
`-Filter` to the test classes in the two parked Postgres suites (Gateway.Tests and Gateway.UnitTests) that
use the throwaway PostgreSQL. The script still provisioned the database itself, set the connection
variables, and destroyed it at the end. All other suites collected zero tests, as expected with this filter.

```
.\scripts\test-local.ps1 -Parked -Filter "FullyQualifiedName~PostgresProviderProofTests|FullyQualifiedName~DeviceCredentialImportPostgresTests|FullyQualifiedName~CallerSuppliedKeyUpgradePreservesRowsPostgresTests|FullyQualifiedName~EntitlementSchemaQualificationTests|FullyQualifiedName~PostgresRigIsPresentWhenRequiredTests|FullyQualifiedName~GatewaySessionConcurrencyPostgresTests|FullyQualifiedName~GatewayStatsWritePathPostgresTests|FullyQualifiedName~TheRejectedChainUpgradesToTipTests|FullyQualifiedName~HostedStatsServeTests|FullyQualifiedName~GatewayHostBootSmokeTests|FullyQualifiedName~HostedSchemaRefusesAnUnownedRowTests"
```

Exit code 0: "RESULT: all projects exited zero".

## Totals

| Suite | Result file outcome | Total | Executed | Passed | Failed | Skipped |
|---|---|---|---|---|---|---|
| CcDirector.Gateway.Tests | Completed | 48 | 48 | 48 | 0 | 0 |
| CcDirector.Gateway.UnitTests | Completed | 12 | 11 | 11 | 0 | 1 |

**Failures: none.** Nothing needed to be run again on origin/main.

## The proofs this slice needed

- Collation census: `PostgresProviderProofTests.Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres`
  **passed**. This is the test whose list the branch edited by hand to add `fleet_messages.MessageId`,
  `RecipientSessionId` and `SenderSessionId`, so the generated Postgres migration and that list agree on
  real PostgreSQL.
- Migrations: `PostgresProviderProofTests.Migrate_CreatesGatewaySchemaAndTables_OnRealPostgres` **passed**
  (the full migration set, including `AddFleetMessages`, applied cleanly and every mapped table landed in the
  `gateway` schema). The other four `PostgresProviderProofTests` also passed, as did
  `TheRejectedChainUpgradesToTipTests`, `CallerSuppliedKeyUpgradePreservesRowsPostgresTests` and
  `DeviceCredentialImportPostgresTests.Migration_AppliesClean_...`.

## What this did NOT cover

- The one skip, `GatewayHostBootSmokeTests.HostStartupPath_ResolvesAndAppliesPostgresMigrations_OnConfiguredPostgres`,
  and the whole `GatewayDatabaseLivePostgresProofTests` class (four tests) are gated on
  `CC_GATEWAY_DB_CONNECTION`, the hosted database's own variable. The test script does not set it, so they
  skip in a full `-Parked` run too; they are proofs against the live hosted database, not the throwaway one.
  A first run that named `GatewayDatabaseLivePostgresProofTests` in the filter exited 5 for exactly that
  reason (4 skipped, nothing executed for that term, 0 failures anywhere); the command above drops that term.
- The rest of the parked suites (all non-Postgres tests in Gateway.Tests, Gateway.UnitTests and Core.Tests)
  were not run.
