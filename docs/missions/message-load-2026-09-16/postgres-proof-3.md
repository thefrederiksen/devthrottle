# Postgres proofs after the census line

Run on SOREN_NORTH, 2026-09-17, against `origin/mission/message-load` at `cecff3006`, in a detached worktree
(`D:\ReposFred\devthrottle-message-load-pg3`) cut from that ref, not the shared checkout. That head carries
`9f2f7b469` "test(postgres): the collation census names main's fleet_manager_marks.SessionId"; the line
`("fleet_manager_marks", "SessionId")` is present in `PostgresProviderProofTests.cs`. Docker was running
(`docker info` succeeded).

## What was run

Exactly the command recorded in `postgres-proof-2.md`. The script built its own PostgreSQL and destroyed it at the
end. Every suite outside the two below collected zero tests, as expected with this filter.

```
.\scripts\test-local.ps1 -Parked -Filter "FullyQualifiedName~PostgresProviderProofTests|FullyQualifiedName~DeviceCredentialImportPostgresTests|FullyQualifiedName~CallerSuppliedKeyUpgradePreservesRowsPostgresTests|FullyQualifiedName~EntitlementSchemaQualificationTests|FullyQualifiedName~PostgresRigIsPresentWhenRequiredTests|FullyQualifiedName~GatewaySessionConcurrencyPostgresTests|FullyQualifiedName~GatewayStatsWritePathPostgresTests|FullyQualifiedName~TheRejectedChainUpgradesToTipTests|FullyQualifiedName~HostedStatsServeTests|FullyQualifiedName~GatewayHostBootSmokeTests|FullyQualifiedName~HostedSchemaRefusesAnUnownedRowTests"
```

Exit code 1: "RESULT: FAILED in 1 project(s): CcDirector.Gateway.Tests".

## Totals

| Suite | Result file outcome | Total | Executed | Passed | Failed | Skipped |
|---|---|---|---|---|---|---|
| CcDirector.Gateway.Tests | Failed | 48 | 48 | 47 | 1 | 0 |
| CcDirector.Gateway.UnitTests | Completed | 15 | 14 | 14 | 0 | 1 |

The one skip is the same as before:
`GatewayHostBootSmokeTests.HostStartupPath_ResolvesAndAppliesPostgresMigrations_OnConfiguredPostgres` (gated on
`CC_GATEWAY_DB_CONNECTION`, which the script does not set).

## The one failure: the collation census, still red, one position further on

`CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres`
(line 275, the first assertion)

```
Assert.Equal() Failure: Collections differ (pos 14)
Expected: [..., ("fleet_messages", "RecipientSessionId"), ("fleet_messages", "SenderSessionId"), ("known_repositories", "MachineKey"), ("known_repositories", "PathKey"), ("mission_notes", "Key"), ...]
Actual:   [..., ("fleet_messages", "RecipientSessionId"), ("fleet_messages", "SenderSessionId"), ("fleet_outcomes", "Kind"), ("fleet_outcomes", "Status"), ("known_repositories", "MachineKey"), ...]
```

The census line did its job: position 10 (`fleet_manager_marks.SessionId`) now matches, and the comparison moved on
to position 14. There it meets two more explicit-"C" columns the list does not name: `fleet_outcomes.Kind` and
`fleet_outcomes.Status`. They also come from MAIN, from the same Fleet Manager change (#2997, commit `dc6d65476`):
`20260917090009_AddFleetManagerOutcomes` declares both with `collation: "C"`. The list on `origin/main` does not
carry them either (a `git grep fleet_outcomes origin/main` over `PostgresProviderProofTests.cs` finds nothing), so
this is main's gap, carried in by the merge, as with the previous one.

**Did the census run to its end?** No. It stopped at the first assertion again.

**Did the second assertion pass?** It **did not run**. xUnit stops at the first failed assertion, so "no gateway
column carries an explicit collation other than the default or C" is still unproven on this branch.

**Is anything else missing from the list?** Read from the source, not the run (the run cannot show past the first
difference): every `collation: "C"` column declared in the ten Postgres migrations dated 2026-09 was checked
against the list, and the only two absent are `fleet_outcomes.Kind` and `fleet_outcomes.Status`. So the likely fix
is two lines, `("fleet_outcomes", "Kind")` and `("fleet_outcomes", "Status")`, between the `fleet_messages` and
`known_repositories` entries. That is an expectation from reading migrations, not a proven green: migrations before
September and any `AlterColumn` collation were not re-checked, and only a rerun proves the census reaches its end.
This proof run changed no code.

## Everything else passed

All other 47 Gateway.Tests and all 14 executed Gateway.UnitTests passed, including
`PostgresProviderProofTests.Migrate_CreatesGatewaySchemaAndTables_OnRealPostgres`,
`PostgresProviderProofTests.PushSubscription_NaturalKeyByteOrdinalCollation_OnRealPostgres`,
`DeviceCredentialImportPostgresTests.Migration_AppliesClean_CreatesDeviceTables_WithByteOrdinalCollation_OnRealPostgres`,
and the `GatewayHostBootSmokeTests`.

## What this did NOT cover

The same gaps as `postgres-proof-2.md` stand: the fleet message store, doorbell and reply store tests are
SQLite-only (their harness has no PostgreSQL provider), none of the new EF queries has run on PostgreSQL, the
`CC_GATEWAY_DB_CONNECTION`-gated tests did not run, and the rest of the parked suites were not run.
