# Postgres proofs for the merged branch

Run on SOREN_NORTH, 2026-09-17, against `origin/mission/message-load` at `b72f71ded` (the merge with main), in a
detached worktree cut from that ref, not the shared checkout. Docker was running (`docker info` reported server
29.6.1).

## What was run

The same command as `postgres-proof-1.md`: the parked run, narrowed with `-Filter` to the test classes in the two
parked suites that use the throwaway PostgreSQL. The script built its own PostgreSQL, set the connection
variables, and destroyed it at the end. Every other suite collected zero tests, as expected with this filter.

```
.\scripts\test-local.ps1 -Parked -Filter "FullyQualifiedName~PostgresProviderProofTests|FullyQualifiedName~DeviceCredentialImportPostgresTests|FullyQualifiedName~CallerSuppliedKeyUpgradePreservesRowsPostgresTests|FullyQualifiedName~EntitlementSchemaQualificationTests|FullyQualifiedName~PostgresRigIsPresentWhenRequiredTests|FullyQualifiedName~GatewaySessionConcurrencyPostgresTests|FullyQualifiedName~GatewayStatsWritePathPostgresTests|FullyQualifiedName~TheRejectedChainUpgradesToTipTests|FullyQualifiedName~HostedStatsServeTests|FullyQualifiedName~GatewayHostBootSmokeTests|FullyQualifiedName~HostedSchemaRefusesAnUnownedRowTests"
```

Exit code 1: "RESULT: FAILED in 1 project(s): CcDirector.Gateway.Tests".

## Totals

| Suite | Result file outcome | Total | Executed | Passed | Failed | Skipped |
|---|---|---|---|---|---|---|
| CcDirector.Gateway.Tests | Failed | 48 | 48 | 47 | 1 | 0 |
| CcDirector.Gateway.UnitTests | Completed | 15 | 14 | 14 | 0 | 1 |

(Gateway.UnitTests grew from 12 to 15 since the first proof: `GatewayHostBootSmokeTests` gained tests from main.)

## The one failure

`CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres`

```
Assert.Equal() Failure: Collections differ (pos 10)
Expected: [..., ("dictation_suggestion_verdicts", "Term"), ("fleet_messages", "MessageId"), ("fleet_messages", "RecipientSessionId"), ("fleet_messages", "SenderSessionId"), ...]
Actual:   [..., ("dictation_suggestion_verdicts", "Term"), ("fleet_manager_marks", "SessionId"), ("fleet_messages", "MessageId"), ("fleet_messages", "RecipientSessionId"), ...]
```

The live catalog has one explicit-"C" column the hand-kept list does not name: `fleet_manager_marks.SessionId`. It
comes from MAIN, not from this branch: `20260917090109_AddFleetManagerMarkHistory` (the Fleet Manager mission,
#2997) declares `SessionId` with `collation: "C"`, and the list in `PostgresProviderProofTests.cs` on
`origin/main` does not carry it either. The three `fleet_messages` columns this branch added are present in the
catalog, in order, right after it.

**Does it also fail on origin/main?** **Yes, identically.** A second detached worktree at `origin/main` `0b8e11f6c` ran
`.\scripts\test-local.ps1 -Parked -Filter "FullyQualifiedName~PostgresProviderProofTests"`: exit code 1,
Gateway.Tests total 6, passed 5, failed 1 - the same test, with the same message (position 10, expected
`fleet_messages.MessageId`, actual `fleet_manager_marks.SessionId`). This failure is main's, and the merge carried
it onto the branch; the branch did not introduce it.

The fix is one line in the list, `("fleet_manager_marks", "SessionId")`, and it belongs to whoever owns main's
Fleet Manager change; this proof run changed no code.

What the failure hides: xUnit stops at the first assertion, so (a) the collection comparison only reports the
first differing position - any further difference later in the list is not visible - and (b) the test's second
assertion, that no gateway column carries a collation other than the default or "C", did not run on this branch.

## The proofs this branch needed

- Migrations: `PostgresProviderProofTests.Migrate_CreatesGatewaySchemaAndTables_OnRealPostgres` **passed**. The
  whole Postgres migration set, including this branch's `AddFleetMessages` and `AddFleetMessageReplyMarks`
  interleaved with main's `AddFleetManagerOutcomes`, `AddFleetManagerMarkHistory`, `AddDevReports` and
  `AddTurnVerdictTraceRowAndClock`, applied from empty on real PostgreSQL and every mapped table landed in the
  `gateway` schema.
- `GatewayHostBootSmokeTests` **passed** (four executed): every SQLite migration since the baseline has a Postgres
  twin and the other way round, the Postgres snapshot matches the model, and the SQLite set applies from empty
  with no pending model change.
- Also passed: the other four `PostgresProviderProofTests`, `TheRejectedChainUpgradesToTipTests`, both
  `CallerSuppliedKeyUpgradePreservesRowsPostgresTests`, both `DeviceCredentialImportPostgresTests`,
  `GatewaySessionConcurrencyPostgresTests` (8), `HostedStatsServeTests` (8), `GatewayStatsWritePathPostgresTests`
  (15), `EntitlementSchemaQualificationTests` (4), `PostgresRigIsPresentWhenRequiredTests` (2 in each suite) and
  `HostedSchemaRefusesAnUnownedRowTests` (8).
- Collation census for this branch's columns: **not proven clean** - see the failure above. The three
  `fleet_messages` columns do carry "C" on real PostgreSQL; the census as a whole is red because of main's column.

## The fleet message store, doorbell and reply store tests against PostgreSQL: NOT RUN - the harness is SQLite-only

`FleetMessageStoreTests`, `FleetMessageReplyStoreTests` and `FleetDoorbellTests` (all in Gateway.UnitTests) open
their database through `GatewayDbTestHarness`. That harness has no Postgres provider: it writes a migrated SQLite
template file per test and opens `GatewayDatabase` over that file path.

`GatewayDatabase` does switch to PostgreSQL when the process-global `CC_GATEWAY_DB_CONNECTION` is set, and the
harness would then silently ignore its per-test file. That is not a supported test mode: every test in the process
would share ONE PostgreSQL database with no per-test isolation, so results would mix one test's rows into another's
and mean nothing. The Postgres proof classes do not use that path either; they use `PostgresProofDatabase`
(`CC_GATEWAY_TEST_PG_CONNECTION`) and build their own contexts. So these store tests were not run against
PostgreSQL, and no harness was invented to do it.

## What this did NOT cover

- On PostgreSQL, none of the new EF queries has run: `FleetMessageStore`'s `MarkRungMany` (`ExecuteUpdate`),
  `UnreadForScheduling`, `MarkStuckWithNotices`, `MarkReplyOverdueWithNotices`, the inbox read with the 24-hour
  window and 200 cap, the duplicate key lookup, and the workspace restore marks. They are proven on SQLite only.
  Only the schema they run against is proven on PostgreSQL.
- The one skip, `GatewayHostBootSmokeTests.HostStartupPath_ResolvesAndAppliesPostgresMigrations_OnConfiguredPostgres`,
  and `GatewayDatabaseLivePostgresProofTests` are gated on `CC_GATEWAY_DB_CONNECTION`, the hosted database's own
  variable, which the script does not set; they were not run.
- The rest of the parked suites (all non-Postgres tests in Gateway.Tests, Gateway.UnitTests and Core.Tests) were
  not run.
